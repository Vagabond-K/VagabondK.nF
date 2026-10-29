#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$DeviceAddress = '192.168.0.187',
    [ValidateRange(1,10000)][int]$Iterations = 128,
    [Parameter(Mandatory)][switch]$AllowDeviceChanges,
    [switch]$Robustness,
    [string]$MSBuildPath = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe'
)
$ErrorActionPreference = 'Stop'
if (-not $AllowDeviceChanges) { throw 'Only use the isolated COM3 evaluation board.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$restore = Join-Path $root 'bin\hardware\restore-Modbus.SerialServer.bin'
if ((Get-FileHash -LiteralPath $restore -Algorithm SHA256).Hash -ne '66C51145509667EA4019649750B42CAD47344CE76D9736994B9658E0F45226A7') {
    throw 'Original Modbus restore checksum mismatch.'
}
if ([IO.Ports.SerialPort]::GetPortNames() -notcontains 'COM3') { throw 'Evaluation board COM3 unavailable.' }
$logs = Join-Path $root ('bin\snmp-mapping-hardware-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$null = New-Item -ItemType Directory -Path $logs
$sample = Join-Path $root 'Samples\SNMP\VagabondK.nF.SNMP.Mapping'
$tests = Join-Path $root 'Tests\SNMP\VagabondK.nF.SNMP.Mapping.Tests'
$changed = $false
try {
    & dotnet run -c Release --project (Join-Path $tests 'VagabondK.nF.SNMP.Mapping.Tests.csproj') *>&1 |
        Tee-Object -FilePath (Join-Path $logs 'pc-tests.log')
    if ($LASTEXITCODE -ne 0) { throw 'SNMP mapping PC tests failed.' }
    $project = if ($Robustness) { Join-Path $root 'Tests\SNMP\VagabondK.nF.SNMP.Mapping.Hardware\Robustness.nfproj' }
        else { Join-Path $sample 'VagabondK.nF.SNMP.Mapping.nfproj' }
    & $MSBuildPath $project /t:Rebuild /p:Configuration=Release /warnaserror /v:quiet /nologo
    if ($LASTEXITCODE -ne 0) { throw 'SNMP mapping MCU build failed.' }
    $image = Join-Path $logs 'sample.bin'
    $built = Join-Path (Split-Path $project) ('bin\Release\' + [IO.Path]::GetFileNameWithoutExtension($project) + '.bin')
    Copy-Item -LiteralPath $built -Destination $image
    Get-FileHash -LiteralPath $image | Format-List | Out-File (Join-Path $logs 'image-hash.log')
    $changed = $true
    & (Join-Path $env:USERPROFILE '.dotnet\tools\nanoff.exe') --nanodevice --deploy --serialport COM3 --image $image --suppressnanoffversioncheck
    if ($LASTEXITCODE -ne 0) { throw 'SNMP mapping deployment failed.' }
    & dotnet (Join-Path $tests 'bin\Release\net10.0\VagabondK.nF.SNMP.Mapping.Tests.dll') --hardware $DeviceAddress $Iterations *>&1 |
        Tee-Object -FilePath (Join-Path $logs 'hardware.log')
    if ($LASTEXITCODE -ne 0) { throw 'SNMP mapping hardware verification failed.' }
    if ($Robustness) {
        & dotnet (Join-Path $tests 'bin\Release\net10.0\VagabondK.nF.SNMP.Mapping.Tests.dll') --robust-hardware $DeviceAddress *>&1 |
            Tee-Object -FilePath (Join-Path $logs 'robustness.log')
        if ($LASTEXITCODE -ne 0) { throw 'SNMP robustness hardware verification failed.' }
    }
}
finally {
    if ($changed) {
        Start-Sleep -Seconds 5
        try { & (Join-Path $PSScriptRoot 'Restore-ModbusHardware.ps1') -LogPath (Join-Path $logs 'restored-modbus.log') }
        catch {
            Write-Warning "Original Modbus restore failed; retrying once: $_"
            Start-Sleep -Seconds 5
            & (Join-Path $PSScriptRoot 'Restore-ModbusHardware.ps1') -LogPath (Join-Path $logs 'restored-modbus-retry.log')
        }
    }
}
Write-Output "SNMP MAPPING HARDWARE PASS: original Modbus restored; $logs"
