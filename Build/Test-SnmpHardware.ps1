#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$DeviceAddress = '192.168.0.187',
    [ValidateRange(1,1000000)][int]$Iterations = 256,
    [Parameter(Mandatory)][switch]$AllowDeviceChanges,
    [string]$MSBuildPath = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe'
)
$ErrorActionPreference = 'Stop'
if (-not $AllowDeviceChanges) { throw 'Only use the isolated COM3 evaluation board.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$logs = Join-Path $root ('bin\snmp-hardware-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$null = New-Item -ItemType Directory -Path $logs
$restore = Join-Path $PSScriptRoot 'Restore-ModbusHardware.ps1'
$nanoff = Join-Path $env:USERPROFILE '.dotnet\tools\nanoff.exe'
if ((Get-FileHash -LiteralPath (Join-Path $root 'bin\hardware\restore-Modbus.SerialServer.bin')).Hash -ne
    '66C51145509667EA4019649750B42CAD47344CE76D9736994B9658E0F45226A7') { throw 'Restore checksum mismatch.' }
$changed = $false
try {
    & $MSBuildPath (Join-Path $root 'Samples\SNMP\VagabondK.nF.SNMP.UDPHost\VagabondK.nF.SNMP.UDPHost.nfproj') `
        /t:Rebuild /p:Configuration=Release /p:SnmpHardwareTest=true /v:minimal /nologo
    if ($LASTEXITCODE -ne 0) { throw 'SNMP MCU build failed.' }
    & dotnet build (Join-Path $root 'Tests\SNMP\VagabondK.nF.SNMP.Interop\VagabondK.nF.SNMP.Interop.csproj') -c Release --nologo -v:q
    if ($LASTEXITCODE -ne 0) { throw 'Independent SNMP client build failed.' }
    $image = Join-Path $root 'Samples\SNMP\VagabondK.nF.SNMP.UDPHost\bin\Release\VagabondK.nF.SNMP.UDPHost.bin'
    Copy-Item $image (Join-Path $logs 'host.bin')
    $changed = $true
    & $nanoff --nanodevice --deploy --serialport COM3 --image $image --suppressnanoffversioncheck
    if ($LASTEXITCODE -ne 0) { throw 'SNMP deployment failed.' }
    & dotnet (Join-Path $root 'Tests\SNMP\VagabondK.nF.SNMP.Interop\bin\Release\net10.0\VagabondK.nF.SNMP.Interop.dll') `
        --snmp-hardware $DeviceAddress $Iterations *>&1 | Tee-Object (Join-Path $logs 'independent.log')
    if ($LASTEXITCODE -ne 0) { throw 'Independent SNMP MCU validation failed.' }
}
finally {
    if ($changed) { & $restore -LogPath (Join-Path $logs 'restored-modbus.log') }
}
Write-Output "SNMP HARDWARE COMPLETE: original Modbus restored; $logs"
