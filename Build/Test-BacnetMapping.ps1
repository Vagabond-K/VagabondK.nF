param(
    [string]$MSBuildPath = '',
    [string]$NuGetPath = 'nuget',
    [string]$FeedPath = ''
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $FeedPath) { $FeedPath = Join-Path $root 'bin\nuget\bacnet-mapping' }
$feed = [IO.Path]::GetFullPath($FeedPath)
$name = 'VagabondK.nF.BACnet.Mapping'
$version = '0.1.0-preview.1'
$zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $feed "$name.$version.nupkg"))
try {
    $dlls = @($zip.Entries | Where-Object FullName -like '*.dll' | ForEach-Object FullName)
    if ($dlls.Count -ne 1 -or $dlls[0] -ne "analyzers/dotnet/cs/$name.dll" -or
        @($zip.Entries | Where-Object { $_.FullName -like 'lib/*' -or $_.FullName -like '*.pe' }).Count -ne 0) {
        throw 'Generator must contain only its analyzer DLL, not MCU or Roslyn dependencies.'
    }
    $stream = $zip.GetEntry("analyzers/dotnet/cs/$name.dll").Open()
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $actual = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
    finally { $stream.Dispose(); $sha.Dispose() }
    if ($actual -ne (Get-FileHash -LiteralPath (Join-Path $root "Source\BACnet\$name\bin\Release\netstandard2.0\$name.dll")).Hash) {
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
& (Join-Path $PSScriptRoot 'Test-Packages.ps1') -MSBuildPath $MSBuildPath -NuGetPath $NuGetPath -FeedPath $feed -LibrarySet BACnet
$consumer = Join-Path $root 'Packaging\VagabondK.nF.BACnet.Mapping.PackageSmoke'
foreach ($binaryOnly in @('false','true')) {
    $packages = Join-Path $consumer ('bin\restore-' + [Guid]::NewGuid().ToString('N'))
    $names = @($name, 'VagabondK.nF.BACnet')
    foreach ($package in @($name)) {
        & $NuGetPath install $package -Version $version -Source ($feed + ';' + (Join-Path $root 'packages')) -OutputDirectory $packages -NonInteractive -NoHttpCache -DirectDownload -Verbosity quiet
        if ($LASTEXITCODE -ne 0) { throw "Offline dependency restore failed: $package" }
    }
    $foreign = @(Get-ChildItem -LiteralPath $packages -Directory |
        Where-Object { $_.Name -like 'VagabondK.nF.*' -and $names -notcontains ($_.Name -replace '\.\d+\.\d+\.\d+.*$', '') })
    if ($foreign.Count -ne 0) { throw "Unexpected runtime packages: $($foreign.Name -join ', ')" }
    & $MSBuildPath (Join-Path $consumer 'VagabondK.nF.BACnet.Mapping.PackageSmoke.nfproj') /t:Rebuild /p:Configuration=Release "/p:BinaryOnly=$binaryOnly" "/p:SmokePackagesPath=$packages\" /warnaserror /v:minimal /nologo
    if ($LASTEXITCODE -ne 0) { throw "BACnet package-only MCU build failed: binaryOnly=$binaryOnly" }
    $output = Join-Path $consumer "bin\Release\$binaryOnly"
    if (-not (Test-Path -LiteralPath (Join-Path $output 'VagabondK.nF.BACnet.Mapping.PackageSmoke.bin'))) {
        throw 'Consumer deployment image was not generated.'
    }
    if (@(Get-ChildItem -LiteralPath $output | Where-Object {
        $_.Name -eq "$name.dll" -or $_.Name -like '*Generator*' -or $_.Name -like '*CodeAnalysis*' -or
        $_.Name -like 'VagabondK.nF.Modbus*' -or $_.Name -like 'VagabondK.nF.SNMP*' -or
        $_.Name -like 'VagabondK.nF.DNP3*' -or $_.Name -like 'VagabondK.nF.BACnet.IP*'
    }).Count -ne 0) { throw 'Unselected runtime or desktop dependency leaked into MCU output.' }
}
Write-Output 'BACNET MAPPING PACKAGE TEST PASS: build-only analyzer, offline restore, core-only AV/BV and BV-only MCU consumers.'
