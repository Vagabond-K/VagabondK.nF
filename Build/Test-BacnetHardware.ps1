#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$DeviceAddress = '192.168.0.187',
    [ValidateRange(14,1000000)][int]$Iterations = 256,
    [Parameter(Mandatory)][switch]$AllowDeviceChanges,
    [string]$MSBuildPath = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe'
)
$ErrorActionPreference = 'Stop'
if (-not $AllowDeviceChanges) { throw 'Only use the isolated COM3 evaluation board.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$logs = Join-Path $root ('bin\bacnet-hardware-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$null = New-Item -ItemType Directory -Path $logs
$restore = Join-Path $PSScriptRoot 'Restore-ModbusHardware.ps1'
$nanoff = Join-Path $env:USERPROFILE '.dotnet\tools\nanoff.exe'
if ((Get-FileHash -LiteralPath (Join-Path $root 'bin\hardware\restore-Modbus.SerialServer.bin')).Hash -ne
    '66C51145509667EA4019649750B42CAD47344CE76D9736994B9658E0F45226A7') { throw 'Restore checksum mismatch.' }
$changed = $false
try {
    & $MSBuildPath (Join-Path $root 'Samples\BACnet\VagabondK.nF.BACnet.IPHost\VagabondK.nF.BACnet.IPHost.nfproj') `
        /t:Rebuild /p:Configuration=Release /p:BacnetHardwareTest=true /v:minimal /nologo
    if ($LASTEXITCODE -ne 0) { throw 'BACnet MCU build failed.' }
    & dotnet build (Join-Path $root 'Tests\BACnet\VagabondK.nF.BACnet.Interop\VagabondK.nF.BACnet.Interop.csproj') -c Release --nologo -v:q
    if ($LASTEXITCODE -ne 0) { throw 'Independent BACnet client build failed.' }
    $image = Join-Path $root 'Samples\BACnet\VagabondK.nF.BACnet.IPHost\bin\Release\VagabondK.nF.BACnet.IPHost.bin'
    Copy-Item $image (Join-Path $logs 'host.bin')
    $changed = $true
    & $nanoff --nanodevice --deploy --serialport COM3 --image $image --suppressnanoffversioncheck
    if ($LASTEXITCODE -ne 0) { throw 'BACnet deployment failed.' }
    & dotnet (Join-Path $root 'Tests\BACnet\VagabondK.nF.BACnet.Interop\bin\Release\net10.0\VagabondK.nF.BACnet.Interop.dll') `
        --bacnet-hardware $DeviceAddress $Iterations *>&1 | Tee-Object (Join-Path $logs 'independent.log')
    if ($LASTEXITCODE -ne 0) { throw 'Independent BACnet MCU validation failed.' }
}
finally {
    if ($changed) { & $restore -LogPath (Join-Path $logs 'restored-modbus.log') }
}
Write-Output "BACNET HARDWARE COMPLETE: original Modbus restored; $logs"
