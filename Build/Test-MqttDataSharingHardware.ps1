#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][switch]$AllowDeviceChanges,
    [switch]$NormalSample,
    [switch]$Sparkplug,
    [switch]$Tahu,
    [string]$HostAddress='192.168.0.210',
    [string]$DeviceAddress='192.168.0.187',
    [string]$OutputPath='',
    [string]$MSBuildPath='C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe'
)
$ErrorActionPreference='Stop'
if (-not $AllowDeviceChanges) { throw 'Isolated COM3 evaluation board only.' }
if ($Tahu -and (-not $Sparkplug -or $NormalSample)) { throw 'Tahu requires -Sparkplug diagnostic mode, without -NormalSample.' }
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ($Tahu) {
    $module=Join-Path $root 'Tests\MQTT\VagabondK.nF.MQTT.DataSharing.Tests\Tahu\node_modules\sparkplug-client\index.js'
    if (-not (Test-Path -LiteralPath $module)) { throw 'Run npm ci --prefix .\Tests\MQTT\VagabondK.nF.MQTT.DataSharing.Tests\Tahu first.' }
    & node --version
    if ($LASTEXITCODE -ne 0) { throw 'Node.js is required for the Eclipse Tahu peer.' }
}
if ($HostAddress -ne '192.168.0.210') { throw 'Update the sample BrokerAddress before using another host.' }
if (-not (Get-NetIPAddress -AddressFamily IPv4 | Where-Object IPAddress -eq $HostAddress)) { throw 'Host LAN IP not configured.' }
$restore=Join-Path $root 'bin\hardware\restore-Modbus.SerialServer.bin'
if ((Get-FileHash -LiteralPath $restore).Hash -ne '66C51145509667EA4019649750B42CAD47344CE76D9736994B9658E0F45226A7') { throw 'Restore checksum mismatch.' }
if ([IO.Ports.SerialPort]::GetPortNames() -notcontains 'COM3') { throw 'COM3 unavailable.' }
if (-not $OutputPath) { $OutputPath=Join-Path $root ('bin\mqtt-sharing-'+(Get-Date -Format 'yyyyMMdd-HHmmss')) }
$output=[IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $output) { throw 'Fresh output directory required.' }
$null=New-Item -ItemType Directory -Path $output
$state=[ordered]@{Status='preparing';Mode=$(if ($Tahu) {'tahu'} elseif ($Sparkplug -and $NormalSample) {'sparkplug-normal'} elseif ($Sparkplug) {'sparkplug'} elseif ($NormalSample) {'normal'} else {'diagnostic'});Restored=$false;Error=$null}
$changed=$false; $peer=$null; $tail=$null; $errors=$null
function Save-State { $state | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'status.json') }
Save-State
try {
    & dotnet build (Join-Path $root 'Tests\MQTT\VagabondK.nF.MQTT.DataSharing.Tests\VagabondK.nF.MQTT.DataSharing.Tests.csproj') -c Release --nologo -v:q *>&1 |
        Tee-Object -FilePath (Join-Path $output 'pc-build.log')
    if ($LASTEXITCODE -ne 0) { throw 'PC build failed.' }
    $sampleName=if ($Sparkplug) {'MQTT.Sparkplug'} else {'MQTT'}
    $project=Join-Path $root "Samples\MQTT\VagabondK.nF.$sampleName.Mapping\VagabondK.nF.$sampleName.Mapping.nfproj"
    $instrumented=if ($NormalSample) {'false'} else {'true'}
    & $MSBuildPath $project /t:Rebuild /p:Configuration=Release "/p:MqttHardwareTest=$instrumented" /warnaserror /v:quiet /nologo *>&1 |
        Tee-Object -FilePath (Join-Path $output 'build.log')
    if ($LASTEXITCODE -ne 0) { throw 'MCU build failed.' }
    $image=Join-Path $root "Samples\MQTT\VagabondK.nF.$sampleName.Mapping\bin\Release\VagabondK.nF.$sampleName.Mapping.bin"
    Copy-Item -LiteralPath $image -Destination (Join-Path $output 'deployed-image.bin')
    $start=[Diagnostics.ProcessStartInfo]::new('dotnet')
    $arguments=@((Join-Path $root 'Tests\MQTT\VagabondK.nF.MQTT.DataSharing.Tests\bin\Release\net10.0\VagabondK.nF.MQTT.DataSharing.Tests.dll'))
    if ($Tahu) {
        $script=Join-Path $root 'Tests\MQTT\VagabondK.nF.MQTT.DataSharing.Tests\Tahu\peer.js'
        $arguments+=@('--tahu-hardware',$HostAddress,$DeviceAddress,$script)
    }
    elseif ($Sparkplug -and $NormalSample) { $arguments+=@('--sparkplug-peer',$HostAddress) }
    elseif ($Sparkplug) { $arguments+=@('--sparkplug-hardware',$HostAddress,$DeviceAddress) }
    elseif ($NormalSample) { $arguments+=@('--peer',$HostAddress) } else { $arguments+=@('--hardware',$HostAddress,$DeviceAddress) }
    foreach ($arg in $arguments) {
        $start.ArgumentList.Add($arg)
    }
    $start.UseShellExecute=$false
    $start.RedirectStandardInput=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $peer=[Diagnostics.Process]::Start($start)
    $errors=$peer.StandardError.ReadToEndAsync()
    $ready=$peer.StandardOutput.ReadLineAsync()
    while ($ready.Wait(15000) -and $ready.Result -match '^((SPARKPLUG|NATIVE MQTT) (CONTRACT PASS|LIBRARY PASS|PERF PC)|MQTT SAMPLE PASS:)') { $ready=$peer.StandardOutput.ReadLineAsync() }
    if (-not $ready.Wait(15000) -or ($NormalSample -and $ready.Result -notlike 'READY:*') -or
        (-not $NormalSample -and $ready.Result -ne 'READY')) { throw 'MQTT broker/test not ready.' }
    $tail=$peer.StandardOutput.ReadToEndAsync()
    $changed=$true
    for ($attempt=1; $attempt -le 3; $attempt++) {
        & (Join-Path $env:USERPROFILE '.dotnet\tools\nanoff.exe') --nanodevice --deploy --serialport COM3 --image $image --suppressnanoffversioncheck *>&1 |
            Tee-Object -FilePath (Join-Path $output "deploy-$attempt.log")
        if ($LASTEXITCODE -eq 0) { break }
        if ($attempt -eq 3) { throw 'MQTT deployment failed after three attempts.' }
        Write-Warning 'MQTT deployment failed; retrying after 20 seconds.'
        Start-Sleep -Seconds 20
    }
    $state.Status='running'; Save-State
    if (-not $NormalSample) { $peer.StandardInput.WriteLine('go'); $peer.StandardInput.Flush() }
    if (-not $peer.WaitForExit(240000)) { throw 'MQTT hardware test timed out.' }
    $text=$tail.GetAwaiter().GetResult()
    $errorText=$errors.GetAwaiter().GetResult()
    $text | Set-Content -LiteralPath (Join-Path $output 'peer.log')
    $errorText | Set-Content -LiteralPath (Join-Path $output 'peer-error.log')
    $passed=if ($Tahu) { $text -match 'TAHU HARDWARE PASS client=3.2.4' }
    elseif ($Sparkplug -and $NormalSample) {
        $text -match 'SPARKPLUG NORMAL PASS nativeData=30 birth/death/bidirectional=PASS'
    } elseif ($NormalSample) {
        ([regex]::Matches($text,'B -> A: Applied sequence=').Count -eq 30 -and
            $text -match 'B -> A: Applied sequence=30 externalTemperatureDeciC=280 sourceRunning=False' -and
            $text -match 'B available=True' -and $text -match 'MQTT DATA SHARING PASS')
    } elseif ($Sparkplug) { $text -match 'SPARKPLUG HARDWARE PASS types=4 native-mqtt/bidirectional/birth/alias/partial/stale/gap/device-death/reconnect/binary-will=PASS' }
    else { $text -match 'MQTT HARDWARE PASS sourceToBoard=68 boardToSource=2 rejects=\d+ freshness/reconnect=PASS' }
    if ($peer.ExitCode -ne 0 -or -not $passed) {
        throw "MQTT hardware failed: $errorText"
    }
    $state.Status='completed'
}
catch { $state.Status='failed'; $state.Error=$_.ToString(); throw }
finally {
    $cleanup=[Collections.Generic.List[string]]::new()
    if ($peer) {
        try {
            if (-not $peer.HasExited) {
                if ($Tahu) { $peer.Kill($true) } else { Stop-Process -Id $peer.Id }
                if (-not $peer.WaitForExit(5000)) { throw 'PC peer did not stop.' }
            }
            if ($tail) { $tail.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $output 'peer.log') }
            if ($errors) { $errors.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $output 'peer-error.log') }
        } catch { $cleanup.Add($_.ToString()) }
        finally { $peer.Dispose() }
    }
    if ($changed) {
        Start-Sleep -Seconds 5
        try {
            try { & (Join-Path $PSScriptRoot 'Restore-ModbusHardware.ps1') -LogPath (Join-Path $output 'restored-modbus.log') }
            catch {
                Start-Sleep -Seconds 5
                & (Join-Path $PSScriptRoot 'Restore-ModbusHardware.ps1') -LogPath (Join-Path $output 'restored-modbus-retry.log')
            }
            $state.Restored=$true
        } catch { $cleanup.Add($_.ToString()) }
    }
    if ($cleanup.Count) { $state.Status='failed'; $state.Error=($state.Error,($cleanup -join "`n") | Where-Object { $_ }) -join "`n" }
    Save-State
    if ($cleanup.Count) { throw ($cleanup -join "`n") }
}
$label=if ($Sparkplug) {'SPARKPLUG'} else {'MQTT'}
Write-Output "$label SHARING HARDWARE PASS: original Modbus restored; $output"
