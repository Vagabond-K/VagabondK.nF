#Requires -Version 7.0
<#
.SYNOPSIS
    실제 TCP/UDP 루프백과 손상 입력을 반복하며 PC 프로세스 자원을 관측합니다.
.PARAMETER Seconds
    프로토콜당 워밍업을 제외한 최소 실행 시간입니다. 기본 300초이며 한 시나리오 회차만큼 초과할 수 있습니다.
.PARAMETER SampleSeconds
    전체 시나리오 회차가 끝난 뒤 GC 및 자원 표본을 남기는 간격입니다.
.PARAMETER Protocol
    시험할 프로토콜입니다. 기본값은 기존 6개이며 MQTT는 -Protocol MQTT로 선택합니다.
.PARAMETER OutputDirectory
    실행별 하위 디렉터리에 JSONL, 표준 출력/오류 및 summary.json을 저장합니다.
.PARAMETER MaxManagedGrowthBytes
    워밍업 후 기준값 대비 GC 후 관리 힙 증가 허용량입니다. 기본 8 MiB입니다.
.PARAMETER MaxHandleGrowth
    프로세스 핸들 증가 허용량입니다. 기본 32개입니다.
.PARAMETER MaxThreadGrowth
    프로세스 스레드 증가 허용량입니다. 기본 8개입니다.
#>
param(
    [ValidateRange(1, 86400)][int]$Seconds = 300,
    [ValidateRange(1, 86400)][int]$SampleSeconds = 10,
    [ValidateSet('Modbus', 'DNP3', 'BACnet', 'SNMP', 'MMS', 'IEC61850', 'MQTT')]
    [ValidateNotNullOrEmpty()]
    [string[]]$Protocol = @('Modbus', 'DNP3', 'BACnet', 'SNMP', 'MMS', 'IEC61850'),
    [string]$OutputDirectory,
    [ValidateRange(0, [long]::MaxValue)][long]$MaxManagedGrowthBytes = 8MB,
    [ValidateRange(0, [int]::MaxValue)][int]$MaxHandleGrowth = 32,
    [ValidateRange(0, [int]::MaxValue)][int]$MaxThreadGrowth = 8
)

$ErrorActionPreference = 'Stop'
if ($SampleSeconds -gt $Seconds) { throw 'SampleSeconds must not exceed Seconds.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'bin\soak' }
$run = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ("run-{0}-{1}" -f (Get-Date -Format 'yyyyMMdd-HHmmss'), [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($run) | Out-Null
Write-Host "PC soak results: $run"
$results = @()
foreach ($name in $Protocol | Select-Object -Unique) {
    $assembly = if ($name -eq 'MQTT') { 'VagabondK.nF.MQTT.DataSharing.Tests' } else { "VagabondK.nF.$name.Tests" }
    $project = Join-Path $root "Tests\$name\$assembly\$assembly.csproj"
    & dotnet build $project -c Release -v quiet --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $name. Results: $run" }
    $dll = Join-Path (Split-Path $project) "bin\Release\net10.0\$assembly.dll"
    $jsonl = Join-Path $run "$name.jsonl"
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @($dll, '--soak', $Seconds, $SampleSeconds, $jsonl,
        $MaxManagedGrowthBytes, $MaxHandleGrowth, $MaxThreadGrowth)) {
        $start.ArgumentList.Add([string]$argument)
    }
    $stdout = [IO.File]::Create((Join-Path $run "$name.stdout.log"))
    $stderr = [IO.File]::Create((Join-Path $run "$name.stderr.log"))
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $timedOut = $false
    $started = $false
    $clock = [Diagnostics.Stopwatch]::StartNew()
    try {
        Write-Host "==> $name ($Seconds seconds + warmup)" -ForegroundColor Cyan
        if (-not $process.Start()) { throw "Could not start $name." }
        $started = $true
        $copyOut = $process.StandardOutput.BaseStream.CopyToAsync($stdout)
        $copyErr = $process.StandardError.BaseStream.CopyToAsync($stderr)
        if (-not $process.WaitForExit(($Seconds + 120) * 1000)) {
            $timedOut = $true
            $process.Kill($true)
            $process.WaitForExit()
        }
        $copyOut.GetAwaiter().GetResult() | Out-Null
        $copyErr.GetAwaiter().GetResult() | Out-Null
        $code = $process.ExitCode
    }
    finally {
        if ($started -and -not $process.HasExited) {
            $process.Kill($true)
            $process.WaitForExit()
        }
        $process.Dispose()
        $stdout.Dispose()
        $stderr.Dispose()
    }
    $last = $null
    $sample = $null
    $reportError = $null
    $peakManagedGrowth = 0L
    if (Test-Path $jsonl) {
        foreach ($line in [IO.File]::ReadLines($jsonl)) {
            try { $last = $line | ConvertFrom-Json -ErrorAction Stop }
            catch [ArgumentException] {
                $reportError = $_.Exception.Message
                Write-Warning "Invalid JSONL in ${jsonl}: $reportError"
                break
            }
            if ($last.Event -eq 'sample') {
                $sample = $last
                $peakManagedGrowth = [Math]::Max($peakManagedGrowth, [long]$sample.ManagedGrowthBytes)
            }
        }
    }
    $normal = @($last.Scenarios | Where-Object Name -eq 'normal') | Select-Object -First 1
    $passed = -not $timedOut -and -not $reportError -and $code -eq 0 -and $last.Event -eq 'pass'
    $results += [pscustomobject]@{
        Protocol = $name
        Result = if ($passed) { 'PASS' } else { 'FAIL' }
        Seconds = [Math]::Round($clock.Elapsed.TotalSeconds, 1)
        ExitCode = $code
        TimedOut = $timedOut
        ReportError = $reportError
        NormalExchanges = $normal.Exchanges
        MaxExchangeMs = $normal.MaximumExchangeMilliseconds
        PeakManagedGrowthBytes = $peakManagedGrowth
        FinalManagedGrowthBytes = $sample.ManagedGrowthBytes
        FinalHandleGrowth = $sample.HandleGrowth
        FinalThreadGrowth = $sample.ThreadGrowth
        Report = $jsonl
    }
    $results | ConvertTo-Json -Depth 5 -AsArray | Set-Content (Join-Path $run 'summary.json') -Encoding utf8
    if (-not $passed) {
        Write-Host "FAILED: $name (exit=$code, timedOut=$timedOut); inspect $run" -ForegroundColor Red
        Get-Content (Join-Path $run "$name.stderr.log") -Tail 15 | Write-Host
    }
}
$results | Select-Object Protocol, Result, Seconds, NormalExchanges,
    @{n='MaxExchangeMs';e={[Math]::Round($_.MaxExchangeMs, 2)}},
    PeakManagedGrowthBytes, FinalHandleGrowth, FinalThreadGrowth |
    Format-Table -AutoSize | Out-String -Width 220 | Write-Host
if ($results.Result -contains 'FAIL') { exit 1 }
Write-Host "ALL $($results.Count) PC SOAK TESTS PASS: $run" -ForegroundColor Green
