#Requires -Version 7.0
param([string]$CMakePath = '')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$source = Join-Path $root 'bin\protocol-tools\libiec61850'
$output = Join-Path $root 'bin\protocol-tools\iec61850-build'
$revision = 'f0f797bf955da763353f7081c892a1ff6b55e5a1'
if (-not (Test-Path -LiteralPath $source)) {
    & git clone --quiet --depth 1 --branch v1.6.2 https://github.com/mz-automation/libiec61850.git $source
    if ($LASTEXITCODE -ne 0) { throw 'libiec61850 download failed.' }
}
$actual = & git -C $source rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $actual -ne $revision) { throw 'Unexpected libiec61850 revision.' }
& git -C $source diff --quiet $revision --
if ($LASTEXITCODE -ne 0) { throw 'Independent peer dependency was modified.' }
if (-not $CMakePath) {
    $command = Get-Command cmake -ErrorAction SilentlyContinue
    if ($command) { $CMakePath = $command.Source }
    else {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        $vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if (-not $vs) { throw 'Visual Studio C++ tools required.' }
        $CMakePath = Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
    }
}
& $CMakePath -S (Join-Path $root 'Tests\IEC61850\VagabondK.nF.IEC61850.ValidationTools') -B $output -A x64 "-DLIBIEC61850_SOURCE=$source" -Wno-dev
if ($LASTEXITCODE -ne 0) { throw 'MMS peer configure failed.' }
& $CMakePath --build $output --config Release --target VagabondKMmsPeer --parallel 2 -- /verbosity:quiet
if ($LASTEXITCODE -ne 0) { throw 'MMS peer build failed.' }
& (Join-Path $output 'Release\VagabondKMmsPeer.exe') --identity
if ($LASTEXITCODE -ne 0) { throw 'MMS peer identity failed.' }
