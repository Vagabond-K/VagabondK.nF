#Requires -Version 7.0
param([string]$CMakePath = '', [switch]$PatchedIeee)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$source = Join-Path $root 'bin\protocol-tools\opendnp3'
$output = Join-Path $root $(if ($PatchedIeee) { 'bin\protocol-tools\master-build-ieee' } else { 'bin\protocol-tools\master-build' })
$revision = '26b4c01e4839bbbda8866655e086471c4917ee53'
if (-not (Test-Path -LiteralPath $source)) {
    & git clone --quiet --depth 1 --branch 3.1.2 https://github.com/dnp3/opendnp3.git $source
    if ($LASTEXITCODE -ne 0) { throw 'OpenDNP3 source download failed.' }
}
$actual = & git -C $source rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $actual -ne $revision) { throw 'Unexpected OpenDNP3 revision; do not run unpinned test tools.' }
& git -C $source diff --quiet $revision --
if ($LASTEXITCODE -ne 0) { throw 'OpenDNP3 test dependency has local modifications.' }
if (-not $CMakePath) {
    $command = Get-Command cmake -ErrorAction SilentlyContinue
    if ($command) { $CMakePath = $command.Source }
    else {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        $vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if (-not $vs) { throw 'Visual Studio C++ tools are required.' }
        $CMakePath = Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
    }
}
$patchOption = if ($PatchedIeee) { 'ON' } else { 'OFF' }
& $CMakePath -S (Join-Path $root 'Tests\DNP3\VagabondK.nF.DNP3.ValidationTools') -B $output -A x64 `
    "-DOPENDNP3_SOURCE=$source" "-DCMAKE_POLICY_VERSION_MINIMUM=3.5" "-DVAGABONDK_NF_PATCHED_IEEE=$patchOption" -Wno-dev
if ($LASTEXITCODE -ne 0) { throw 'OpenDNP3 test master configure failed.' }
& $CMakePath --build $output --config Release --target VagabondKDnp3Master VagabondKDnp3Outstation --parallel 2 -- /verbosity:quiet
if ($LASTEXITCODE -ne 0) { throw 'OpenDNP3 test master build failed.' }
Write-Output "DNP3 VALIDATION MASTER: $(Join-Path $output 'Release\VagabondKDnp3Master.exe')"
if ($PatchedIeee) {
    & $CMakePath --build $output --config Release --target VagabondKDnp3Decoder --parallel 2 -- /verbosity:quiet
    if ($LASTEXITCODE -ne 0) { throw 'Patched IEEE decoder verification build failed.' }
    $decoderLog = Join-Path $output 'ieee-decoder-verification.log'
    & (Join-Path $output 'Release\VagabondKDnp3Decoder.exe') 2>&1 | Tee-Object -FilePath $decoderLog
    if ($LASTEXITCODE -ne 0) { throw 'Patched IEEE decoder verification failed.' }
    Write-Output "IEEE DECODER EVIDENCE: $decoderLog"
    Write-Output 'TEST-ONLY PATCHED IEEE BUILD; not interoperability with unmodified OpenDNP3.'
}
