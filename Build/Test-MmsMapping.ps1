param(
    [string]$MSBuildPath = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe',
    [string]$NuGetPath = 'nuget',
    [string]$FeedPath = ''
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
foreach ($configuration in @('Debug', 'Release')) {
    & dotnet run -c $configuration --project (Join-Path $root 'Tests\MMS\VagabondK.nF.MMS.Mapping.Tests\VagabondK.nF.MMS.Mapping.Tests.csproj')
    if ($LASTEXITCODE -ne 0) { throw "$configuration mapping tests failed." }
    foreach ($protocol in $(if (-not $FeedPath) { @('MMS', 'IEC61850') })) {
        & $MSBuildPath (Join-Path $root "Samples\$protocol\VagabondK.nF.$protocol.Mapping\VagabondK.nF.$protocol.Mapping.nfproj") `
            /t:Rebuild "/p:Configuration=$configuration" /warnaserror /v:quiet /nologo
        if ($LASTEXITCODE -ne 0) { throw "$protocol $configuration generated MCU build failed." }
    }
}
if (-not $FeedPath) { Write-Output 'MMS/IEC MAPPING TEST PASS: PC and MCU builds; package tests not selected.'; return }
$feed = [IO.Path]::GetFullPath($FeedPath)
Add-Type -AssemblyName System.IO.Compression.FileSystem
$consumer = Join-Path $root 'Packaging\VagabondK.nF.MMS.Mapping.PackageSmoke'
foreach ($protocol in @('MMS', 'IEC61850')) {
    $generator = "VagabondK.nF.$protocol.Mapping"
    $packageFile = Join-Path $feed "$generator.0.1.0-preview.1.nupkg"
    if (-not (Test-Path -LiteralPath $packageFile)) {
        if ($protocol -eq 'IEC61850') { continue }
        throw "Package missing: $packageFile"
    }
    $zip = [IO.Compression.ZipFile]::OpenRead($packageFile)
    try {
        $dlls = @($zip.Entries | Where-Object FullName -like '*.dll' | ForEach-Object FullName)
        if ($dlls.Count -ne 1 -or $dlls[0] -ne "analyzers/dotnet/cs/$generator.dll" -or
            @($zip.Entries | Where-Object { $_.FullName -like 'lib/*' -or $_.FullName -like '*.pe' }).Count) {
            throw 'Generator package contains runtime assets or dependencies.'
        }
        $stream = $zip.GetEntry($dlls[0]).Open()
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $actual = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
        finally { $stream.Dispose(); $sha.Dispose() }
        if ($actual -ne (Get-FileHash -LiteralPath (Join-Path $root "Source\$protocol\$generator\bin\Release\netstandard2.0\$generator.dll")).Hash) {
            throw 'Stale generator package.'
        }
    }
    finally { $zip.Dispose() }
    & (Join-Path $PSScriptRoot 'Test-MappingDependencies.ps1') -Name $generator -FeedPath $feed -NuGetPath $NuGetPath
    foreach ($role in @('Client', 'Server')) {
        $packages = Join-Path $consumer ('bin\restore-' + [Guid]::NewGuid().ToString('N'))
        $names = @($generator, "VagabondK.nF.$protocol.$role")
        foreach ($name in $names) {
            & $NuGetPath install $name -Version '0.1.0-preview.1' -Source ($feed + ';' + (Join-Path $root 'packages')) `
                -OutputDirectory $packages -NonInteractive -NoHttpCache -DirectDownload -Verbosity quiet
            if ($LASTEXITCODE -ne 0) { throw "$name offline package restore failed." }
        }
        $opposite = if ($role -eq 'Client') { 'Server' } else { 'Client' }
        if (@(Get-ChildItem -LiteralPath $packages -Directory | Where-Object {
            $_.Name -match "\.$opposite\.0\." -or $_.Name -like '*CodeAnalysis*'
        }).Count) { throw 'Unexpected package role or Roslyn dependency.' }
        $id = [Guid]::NewGuid().ToString('N')
        $output = Join-Path $consumer "bin\package-$id"
        & $MSBuildPath (Join-Path $consumer 'VagabondK.nF.MMS.Mapping.PackageSmoke.nfproj') /t:Rebuild /p:Configuration=Release `
            "/p:SmokeProtocol=$protocol" "/p:SmokeRole=$role" "/p:SmokePackagesPath=$packages\" `
            "/p:OutputPath=bin\package-$id\" "/p:IntermediateOutputPath=obj\package-$id\" /warnaserror /v:quiet /nologo
        if ($LASTEXITCODE -ne 0) { throw "$protocol $role package-only MCU build failed." }
        if (-not (Test-Path -LiteralPath (Join-Path $output 'VagabondK.nF.MMS.Mapping.PackageSmoke.bin'))) { throw 'MCU image missing.' }
        if (@(Get-ChildItem -LiteralPath $output | Where-Object {
            $_.Name -match "\.$opposite\.(dll|pe)$" -or $_.Name -eq "$generator.dll" -or $_.Name -like '*Generator*' -or
            $_.Name -like '*CodeAnalysis*' -or $_.Name -like 'System.Net*'
        }).Count) { throw 'Unselected runtime or desktop DLL leaked into MCU output.' }
        Write-Output "$protocol $role MAPPING PACKAGE PASS: offline analyzer-only generator and role-only MCU image."
    }
}
