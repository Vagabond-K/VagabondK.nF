param(
    [string]$MSBuildPath = '',
    [string]$NuGetPath = 'nuget',
    [string]$FeedPath = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $FeedPath) { $FeedPath = Join-Path $root 'bin\nuget\modbus-mapping' }
$feed = [IO.Path]::GetFullPath($FeedPath)
$name = 'VagabondK.nF.Modbus.Mapping'
$version = '0.1.0-preview.1'
$zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $feed "$name.$version.nupkg"))
try {
    $dlls = @($zip.Entries | Where-Object FullName -like '*.dll' | ForEach-Object FullName)
    if ($dlls.Count -ne 1 -or $dlls[0] -ne "analyzers/dotnet/cs/$name.dll" -or
        @($zip.Entries | Where-Object { $_.FullName -like 'lib/*' -or $_.FullName -like '*.pe' }).Count -ne 0) {
        throw 'Generator must contain only its analyzer DLL, not MCU or Roslyn dependencies.'
    }
    $entry = $zip.GetEntry("analyzers/dotnet/cs/$name.dll")
    $stream = $entry.Open()
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $actual = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
    finally { $stream.Dispose(); $sha.Dispose() }
    if ($actual -ne (Get-FileHash -LiteralPath (Join-Path $root "Source\Modbus\$name\bin\Release\netstandard2.0\$name.dll")).Hash) {
        throw 'Stale generator package.'
    }
}
finally { $zip.Dispose() }
& (Join-Path $PSScriptRoot 'Test-MappingDependencies.ps1') -Name $name -FeedPath $feed -NuGetPath $NuGetPath
if (-not $MSBuildPath) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $MSBuildPath = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' |
        Select-Object -First 1
    if (-not $MSBuildPath) { throw 'Specify -MSBuildPath for Visual Studio MSBuild.' }
}
& (Join-Path $PSScriptRoot 'Test-Packages.ps1') -MSBuildPath $MSBuildPath -NuGetPath $NuGetPath -FeedPath $feed -LibrarySet Modbus
$consumer = Join-Path $root 'Packaging\VagabondK.nF.Modbus.Mapping.PackageSmoke'
foreach ($role in @('Client', 'Server')) {
    foreach ($bitsOnly in @('false', 'true')) {
        $packages = Join-Path $consumer ('bin\restore-' + [Guid]::NewGuid().ToString('N'))
        $names = @($name, 'VagabondK.nF.Modbus', "VagabondK.nF.Modbus.$role")
        foreach ($package in $names) {
            & $NuGetPath install $package -Version $version -Source ($feed + ';' + (Join-Path $root 'packages')) -OutputDirectory $packages -NonInteractive -NoHttpCache -DirectDownload -Verbosity quiet
            if ($LASTEXITCODE -ne 0) { throw "Offline dependency restore failed: $package" }
        }
        $foreign = @(Get-ChildItem -LiteralPath $packages -Directory |
            Where-Object { $_.Name -like 'VagabondK.nF.*' -and $names -notcontains ($_.Name -replace '\.\d+\.\d+\.\d+.*$', '') })
        if ($foreign.Count -ne 0) { throw "Unexpected cross-role packages: $($foreign.Name -join ', ')" }
        & $MSBuildPath (Join-Path $consumer 'VagabondK.nF.Modbus.Mapping.PackageSmoke.nfproj') /t:Rebuild /p:Configuration=Release "/p:MappingRole=$role" "/p:MappingBitsOnly=$bitsOnly" "/p:SmokePackagesPath=$packages\" /warnaserror /v:minimal /nologo
        if ($LASTEXITCODE -ne 0) { throw "Mapping package-only $role / bits=$bitsOnly build failed." }
        $output = Join-Path $consumer "bin\Release\$role-$bitsOnly"
        $image = Join-Path $output 'VagabondK.nF.Modbus.Mapping.PackageSmoke.bin'
        if (-not (Test-Path -LiteralPath $image)) { throw 'Consumer deployment image was not generated.' }
        if ((Test-Path -LiteralPath (Join-Path $output "$name.dll")) -or @(Get-ChildItem -LiteralPath $output -Filter '*Generator*').Count -ne 0 -or
            @(Get-ChildItem -LiteralPath $output -Filter '*CodeAnalysis*').Count -ne 0) {
            throw 'Desktop generator leaked into MCU output.'
        }
        $other = if ($role -eq 'Client') { 'Server' } else { 'Client' }
        if ((Test-Path -LiteralPath (Join-Path $output "VagabondK.nF.Modbus.$other.dll")) -or
            (Test-Path -LiteralPath (Join-Path $output 'VagabondK.nF.Modbus.Data.dll'))) {
            throw 'Unselected runtime dependency leaked into MCU output.'
        }
    }
}
Write-Output 'MAPPING PACKAGE TEST PASS: build-only assets, offline restore, Client/Server and numeric/bool-only MCU images.'
