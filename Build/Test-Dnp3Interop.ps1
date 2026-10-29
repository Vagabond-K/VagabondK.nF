#Requires -Version 7.0
param(
    [ValidateRange(14, 1000000)][int]$Iterations = 1024,
    [ValidateRange(1, 2147483647)][int]$Seed = 20261023,
    [ValidateRange(1, 100)][int]$Repetitions = 3,
    [switch]$ReportKnownLimits,
    [switch]$PatchedIeee
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
& (Join-Path $PSScriptRoot 'Build-Dnp3ValidationMaster.ps1') -PatchedIeee:$PatchedIeee
$project = Join-Path $root 'Tests\DNP3\VagabondK.nF.DNP3.Interop\VagabondK.nF.DNP3.Interop.csproj'
$variant = if ($PatchedIeee) { 'master-build-ieee' } else { 'master-build' }
$decoder = if ($PatchedIeee) { 'patched-ieee' } else { 'original' }
$master = Join-Path $root "bin\protocol-tools\$variant\Release\VagabondKDnp3Master.exe"
$log = Join-Path $root ('bin\protocol-tools\verification-' + $decoder + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '.log')
"DECODER VARIANT: $decoder; patched results do not supersede original-stack failures." | Tee-Object -FilePath $log -Append
& dotnet build $project -c Release --nologo -v:q
if ($LASTEXITCODE -ne 0) { throw 'Protocol verification project build failed.' }
for ($i = 0; $i -lt $Repetitions; $i++) {
    $runSeed = [uint32]$Seed + [uint32]$i
    "REPETITION $($i + 1)/$Repetitions; seed=$runSeed; iterations=$Iterations" | Tee-Object -FilePath $log -Append
    $policy = if ($ReportKnownLimits) { 'report' } else { 'fail' }
    & dotnet run --project $project -c Release --no-build -- --master $master --decoder $decoder --stress $Iterations --seed $runSeed --known-limits $policy 2>&1 |
        Tee-Object -FilePath $log -Append
    if ($LASTEXITCODE -ne 0) { throw "Protocol verification failed. Evidence: $log" }
}
"PROTOCOL CHECKS COMPLETE; review KNOWN INTEROP LIMIT entries; repetitions=$Repetitions; iterations=$Iterations; log=$log" |
    Tee-Object -FilePath $log -Append
