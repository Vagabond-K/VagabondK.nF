#Requires -Version 7.0
# 기존의 격리된 COM3/COM6 배선에서만 실행하며, 반드시 이전에 저장한 Modbus 이미지로 복원합니다.
param()
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$logs = Join-Path $root 'bin\dnp3-hardware'
$null = New-Item -ItemType Directory -Force -Path $logs
$restore = Join-Path $PSScriptRoot 'Restore-ModbusHardware.ps1'
try {
    & (Join-Path $PSScriptRoot 'Test-Dnp3IndependentPeer.ps1') -Transport Serial -PeerRole Master -Implementation GoNumeric
    & (Join-Path $PSScriptRoot 'Test-Dnp3IndependentPeer.ps1') -Transport Serial -PeerRole Outstation -Implementation GoNumeric
}
finally {
    & $restore -LogPath (Join-Path $logs 'restored-modbus-go-numeric.log')
}