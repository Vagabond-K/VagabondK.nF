#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$LogPath)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$restore = Join-Path $root 'bin\hardware\restore-Modbus.SerialServer.bin'
if ((Get-FileHash $restore).Hash -ne '66C51145509667EA4019649750B42CAD47344CE76D9736994B9658E0F45226A7') {
    throw 'Original Modbus restore checksum mismatch.'
}
& (Join-Path $env:USERPROFILE '.dotnet\tools\nanoff.exe') --nanodevice --deploy --serialport COM3 `
    --image $restore --suppressnanoffversioncheck
if ($LASTEXITCODE -ne 0) { throw 'Original Modbus restore deployment failed.' }
Start-Sleep -Seconds 20
for ($attempt = 1; $attempt -le 4; $attempt++) {
    "RESTORE READINESS: read-only attempt $attempt." | Tee-Object -FilePath $LogPath -Append
    try {
        & (Join-Path $root 'Samples\Modbus\VagabondK.nF.Modbus.SerialServer\Test-SerialServer.ps1') -PortName COM6 -Iterations 1 *>&1 |
            Tee-Object -FilePath $LogPath -Append
        return
    }
    catch {
        if ($_.Exception.Message -notlike 'RTU timeout: received 0 of 3 bytes*' -or $attempt -eq 4) { throw }
        $message = "Restore not ready: $($_.Exception.Message) Waiting 5s for a quiet bus before read-only retry."
        $message | Tee-Object -FilePath $LogPath -Append | Write-Warning
        Start-Sleep -Seconds 5
    }
}
