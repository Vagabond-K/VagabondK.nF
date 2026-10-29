param(
    [string]$MSBuildPath = '',
    [string]$NuGetPath = 'nuget',
    [string]$FeedPath = '',
    [ValidateSet('Modbus','DNP3','BACnet','SNMP','MMS','IEC61850','MQTT','Sparkplug')][string]$LibrarySet = 'Modbus'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $FeedPath) { $FeedPath = Join-Path $PSScriptRoot ('..\bin\nuget\' + $LibrarySet.ToLowerInvariant()) }
$feed = [IO.Path]::GetFullPath($FeedPath)
$version = '0.1.0-preview.1'
[xml]$projects = Get-Content -LiteralPath (Join-Path $root 'VagabondK.nF.proj')
$libraries = @($projects.Project.ItemGroup.ChildNodes | Where-Object { $_.LocalName -eq "${LibrarySet}Library" -or ($LibrarySet -eq 'IEC61850' -and $_.LocalName -eq 'MMSLibrary') })
$names = @($libraries | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_.Include) })
$hashes = @(Import-Csv -LiteralPath (Join-Path $feed 'sha256.csv'))
if ($hashes.Count -ne $names.Count) { throw 'Incomplete package checksum manifest.' }
foreach ($name in $names) {
    $library = $libraries | Where-Object { [IO.Path]::GetFileNameWithoutExtension($_.Include) -eq $name }
    $libraryDirectory = Split-Path (Join-Path $root $library.Include)
    $file = Join-Path $feed "$name.$version.nupkg"
    $expectedHash = @($hashes | Where-Object Package -eq "$name.$version.nupkg")
    if ($expectedHash.Count -ne 1 -or
        $expectedHash[0].SHA256 -ne (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash) {
        throw "Package checksum mismatch: $name"
    }
    $zip = [IO.Compression.ZipFile]::OpenRead($file)
    try {
        $assets = @($zip.Entries | Where-Object { $_.FullName.StartsWith('lib/') } | ForEach-Object FullName)
        $expected = @("lib/$name.dll", "lib/$name.pe", "lib/$name.pdbx", "lib/$name.xml")
        if (@(Compare-Object $expected $assets).Count -ne 0) { throw "Unexpected packaged assets: $name" }
        foreach ($extension in @('dll', 'pe', 'pdbx', 'xml')) {
            $entry = $zip.GetEntry("lib/$name.$extension")
            $stream = $entry.Open()
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $actualHash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
            finally { $stream.Dispose(); $sha.Dispose() }
            $builtHash = (Get-FileHash -LiteralPath (Join-Path $libraryDirectory "bin\Release\$name.$extension")).Hash
            if ($actualHash -ne $builtHash) { throw "Stale packaged asset: $name.$extension" }
        }
    }
    finally { $zip.Dispose() }
}
$consumerName = if ($LibrarySet -eq 'Sparkplug') { 'VagabondK.nF.MQTT.Sparkplug.PackageSmoke' } else { "VagabondK.nF.$LibrarySet.PackageSmoke" }
$consumer = Join-Path $root "Packaging\$consumerName"
$packages = Join-Path $consumer ('bin\restore-' + [Guid]::NewGuid().ToString('N'))
$sources = $feed + ';' + (Join-Path $root 'packages')
foreach ($name in $names) {
    & $NuGetPath install $name -Version $version -Source $sources -OutputDirectory $packages -NonInteractive -NoHttpCache -DirectDownload -Verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw "Package dependency restore failed: $name" }
}
if (-not $MSBuildPath) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Specify -MSBuildPath for Visual Studio MSBuild.' }
    $MSBuildPath = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' |
        Select-Object -First 1
    if (-not $MSBuildPath) { throw 'Visual Studio MSBuild was not found.' }
}
$imagePath = Join-Path $consumer "bin\Release\$consumerName.bin"
if ($LibrarySet -notin @('MMS', 'IEC61850')) {
    & $MSBuildPath (Join-Path $consumer "$consumerName.nfproj") /t:Rebuild /p:Configuration=Release "/p:SmokePackagesPath=$packages\" /v:minimal /nologo
    if ($LASTEXITCODE -ne 0) { throw 'Package-only consumer build failed.' }
}
$restored = @(Get-ChildItem -LiteralPath $packages -Directory | ForEach-Object Name)
$foreign = @($restored | Where-Object { $_ -like 'VagabondK.nF.*' -and $names -notcontains ($_ -replace '\.\d+\.\d+\.\d+.*$', '') })
if ($foreign.Count -ne 0) { throw "Standalone $LibrarySet restored foreign packages: $($foreign -join ', ')" }
if ($LibrarySet -eq 'DNP3') {
    foreach ($role in @('Master', 'Outstation', 'Tcp', 'Serial')) {
        & $MSBuildPath (Join-Path $consumer "$consumerName.nfproj") /t:Rebuild /p:Configuration=Release "/p:Dnp3Role=$role" "/p:SmokePackagesPath=$packages\" /v:minimal /nologo
        if ($LASTEXITCODE -ne 0) { throw "Standalone DNP3 $role-only consumer build failed." }
    }
}
if ($LibrarySet -in @('MMS', 'IEC61850')) {
    $runId = [Guid]::NewGuid().ToString('N')
    foreach ($role in @('All', 'Core', 'Client', 'Server', 'Tcp', 'ServerTcp')) {
        $roleOutput = Join-Path $consumer "bin\package-smoke-$runId\$role"
        & $MSBuildPath (Join-Path $consumer "$consumerName.nfproj") /t:Rebuild /p:Configuration=Release "/p:SmokeRole=$role" "/p:IecRole=$role" "/p:SmokePackagesPath=$packages\" `
            "/p:OutputPath=$roleOutput\" "/p:IntermediateOutputPath=obj\package-smoke-$runId\$role\" /v:minimal /nologo
        if ($LASTEXITCODE -ne 0) { throw "Standalone $LibrarySet $role consumer build failed." }
        $roleImage = Join-Path $roleOutput "$consumerName.bin"
        if (-not (Test-Path -LiteralPath $roleImage)) { throw "Missing $LibrarySet $role deployment image." }
        if ($role -eq 'All') { $imagePath = $roleImage }
        $forbidden = @()
        if ($role -in @('Core', 'Server', 'Tcp', 'ServerTcp')) {
            $forbidden += @('VagabondK.nF.MMS.Client','VagabondK.nF.IEC61850.Client')
        }
        if ($role -in @('Core', 'Client', 'Tcp')) {
            $forbidden += @('VagabondK.nF.IEC61850.Server','VagabondK.nF.MMS.Server')
        }
        if ($role -in @('Core', 'Client', 'Server')) {
            $forbidden += @('VagabondK.nF.MMS.TCP','System.Net')
        }
        if ($LibrarySet -eq 'MMS') { $forbidden += @('VagabondK.nF.IEC61850','VagabondK.nF.IEC61850.Client','VagabondK.nF.IEC61850.Server') }
        foreach ($assembly in $forbidden) {
            if (Test-Path -LiteralPath (Join-Path $roleOutput "$assembly.pe")) { throw "$LibrarySet $role image contains forbidden assembly: $assembly" }
        }
    }
}
if (-not (Test-Path -LiteralPath $imagePath)) {
    throw 'Consumer deployment image was not generated.'
}
Write-Output "PACKAGE TEST PASS: $($names.Count) $LibrarySet package assets, offline dependencies, package-only consumer image. Restored to: $packages"
