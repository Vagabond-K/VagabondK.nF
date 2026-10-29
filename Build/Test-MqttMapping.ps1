param(
    [ValidateSet('MQTT','Sparkplug')][string]$LibrarySet = 'MQTT',
    [string]$MSBuildPath = '',
    [string]$NuGetPath = 'nuget',
    [string]$FeedPath = ''
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$name = if ($LibrarySet -eq 'Sparkplug') { 'VagabondK.nF.MQTT.Sparkplug.Mapping' } else { 'VagabondK.nF.MQTT.Mapping' }
if (-not $FeedPath) { $FeedPath = Join-Path $root ('bin\nuget\' + $LibrarySet.ToLowerInvariant() + '-mapping') }
$feed = [IO.Path]::GetFullPath($FeedPath)
$version = '0.1.0-preview.1'
$zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $feed "$name.$version.nupkg"))
try {
    $dlls = @($zip.Entries | Where-Object FullName -like '*.dll' | ForEach-Object FullName)
    if ($dlls.Count -ne 1 -or $dlls[0] -ne "analyzers/dotnet/cs/$name.dll" -or
        @($zip.Entries | Where-Object { $_.FullName -like 'lib/*' -or $_.FullName -like '*.pe' }).Count -ne 0) {
        throw 'Generator must contain only its analyzer DLL, not MCU or Roslyn dependencies.'
    }
    if (-not $zip.GetEntry("build/$name.targets")) { throw 'Missing packages.config analyzer import.' }
    $stream = $zip.GetEntry("analyzers/dotnet/cs/$name.dll").Open()
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $actual = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
    finally { $stream.Dispose(); $sha.Dispose() }
    if ($actual -ne (Get-FileHash -LiteralPath (Join-Path $root "Source\MQTT\$name\bin\Release\netstandard2.0\$name.dll")).Hash) {
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
& (Join-Path $PSScriptRoot 'Test-Packages.ps1') -MSBuildPath $MSBuildPath -NuGetPath $NuGetPath -FeedPath $feed -LibrarySet $LibrarySet
$consumerName = "$name.PackageSmoke"
$consumer = Join-Path $root "Packaging\$consumerName"
$packages = Join-Path $consumer ('bin\restore-' + [Guid]::NewGuid().ToString('N'))
& $NuGetPath install $name -Version $version -Source ($feed + ';' + (Join-Path $root 'packages')) `
    -OutputDirectory $packages -NonInteractive -NoHttpCache -DirectDownload -Verbosity quiet
if ($LASTEXITCODE -ne 0) { throw "Offline mapping dependency restore failed: $name" }
& $MSBuildPath (Join-Path $consumer "$consumerName.nfproj") /t:Rebuild /p:Configuration=Release `
    "/p:SmokePackagesPath=$packages\" /warnaserror /nr:false /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw "$LibrarySet package-only mapping consumer build failed." }
$output = Join-Path $consumer 'bin\Release'
if (-not (Test-Path -LiteralPath (Join-Path $output "$consumerName.bin"))) {
    throw 'Consumer deployment image was not generated.'
}
$runtime = $name -replace '\.Mapping$', ''
$foreign = @(Get-ChildItem -LiteralPath $output -Filter '*.dll' | Where-Object {
    $_.BaseName -like '*CodeAnalysis*' -or $_.BaseName -eq $name -or
    ($_.BaseName -like 'VagabondK.nF.*' -and $_.BaseName -notin @($runtime, $consumerName))
})
if ($foreign.Count -ne 0) { throw "Unselected runtime or desktop dependency leaked into MCU output: $($foreign.Name -join ', ')" }
Write-Output "MQTT MAPPING PACKAGE TEST PASS: $LibrarySet analyzer-only package, pinned runtime, offline MCU sample-model consumer; no device changes."
