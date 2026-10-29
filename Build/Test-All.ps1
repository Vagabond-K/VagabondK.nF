#Requires -Version 7.0
<#
.SYNOPSIS
    외부 피어, 직렬 포트 또는 장치 없이 실행할 수 있는 기본 PC 테스트 프로젝트를 모두 실행합니다.
.PARAMETER Filter
    프로젝트 이름에 대한 와일드카드 필터입니다. 예: '*DNP3*'.
.PARAMETER Configuration
    빌드 구성입니다. 기본값은 Release입니다.
#>
param(
    [string]$Filter = '*',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projects = @(
    'Modbus.Tests', 'Modbus.Data.Tests', 'Modbus.Mapping.Tests',
    'DNP3.Tests', 'DNP3.Mapping.Tests',
    'BACnet.Tests', 'BACnet.Mapping.Tests',
    'SNMP.Tests', 'SNMP.Mapping.Tests',
    'MMS.Tests', 'MMS.Mapping.Tests',
    'IEC61850.Tests', 'Soak.Tests',
    'MQTT.Mapping.Tests', 'MQTT.Sparkplug.Mapping.Tests', 'MQTT.DataSharing.Tests'
) | ForEach-Object { "VagabondK.nF.$_" } | Where-Object { $_ -like $Filter }
if ($projects.Count -eq 0) { throw "No test project matches '$Filter'." }

$results = foreach ($name in $projects) {
    $group = if ($name -eq 'VagabondK.nF.Soak.Tests') { 'Common' } else { $name.Split('.')[2] }
    $project = Join-Path $root "Tests\$group\$name\$name.csproj"
    Write-Host "==> $name" -ForegroundColor Cyan
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $output = & dotnet run -c $Configuration --project $project 2>&1 | ForEach-Object { "$_" }
    $code = $LASTEXITCODE
    $watch.Stop()
    if ($code -ne 0) { $output | Write-Host }
    $summary = @($output | Where-Object { $_ -match '\S' }) | Select-Object -Last 1
    [pscustomobject]@{
        Project = $name
        Result  = if ($code -eq 0) { 'PASS' } else { 'FAIL' }
        Seconds = [Math]::Round($watch.Elapsed.TotalSeconds, 1)
        Summary = $summary
    }
}

$results | Format-Table -AutoSize -Wrap | Out-String -Width 200 | Write-Host
$failed = @($results | Where-Object Result -ne 'PASS')
if ($failed.Count -ne 0) {
    Write-Host "FAILED: $($failed.Project -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host "ALL $($results.Count) TEST PROJECTS PASS" -ForegroundColor Green
