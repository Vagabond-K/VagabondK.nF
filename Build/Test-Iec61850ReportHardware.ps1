#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][switch]$AllowDeviceChanges,
    [ValidateRange(2,1440)][int]$DurationMinutes = 30,
    [switch]$TypedControls,
    [switch]$TypedClient,
    [string]$DeviceAddress = '192.168.0.187',
    [string]$OutputPath = '',
    [string]$MSBuildPath = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe'
)
$ErrorActionPreference='Stop'
if (-not $AllowDeviceChanges) { throw 'Use only the isolated COM3 evaluation board.' }
if ($TypedClient -and -not $TypedControls) { throw '-TypedClient requires -TypedControls.' }
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([Net.IPAddress]::Parse($DeviceAddress).AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) { throw 'IPv4 required.' }
$restore=Join-Path $root 'bin\hardware\restore-Modbus.SerialServer.bin'
if ((Get-FileHash -LiteralPath $restore).Hash -ne '66C51145509667EA4019649750B42CAD47344CE76D9736994B9658E0F45226A7') { throw 'Restore checksum mismatch.' }
if ([IO.Ports.SerialPort]::GetPortNames() -notcontains 'COM3') { throw 'COM3 unavailable.' }
if (-not $OutputPath) { $OutputPath=Join-Path $root ('bin\iec61850-report-soak-'+(Get-Date -Format 'yyyyMMdd-HHmmss')) }
$output=[IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $output) { throw 'Choose a fresh output folder.' }
$null=New-Item -ItemType Directory -Path $output
$state=[ordered]@{Status='preparing';Restored=$false;Error=$null;DurationMinutes=$DurationMinutes;TypedControls=[bool]$TypedControls;TypedClient=[bool]$TypedClient;Epoch=0;LastMetrics=$null;NativePeer=$null}
$udp=$null; $process=$null; $peerProcess=$null; $changed=$false
function Save-State { $state | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'status.json') }
function U32([byte[]]$b,[int]$p) { return ([uint32]$b[$p] -shl 24) -bor ([uint32]$b[$p+1] -shl 16) -bor ([uint32]$b[$p+2] -shl 8) -bor [uint32]$b[$p+3] }
function Metrics([byte]$command=0) {
    $deadline=[Environment]::TickCount64+120000
    do {
        $q=[byte[]]@(77,82,83,49,$command); $null=$udp.Send($q,$q.Length)
        $remote=[Net.IPEndPoint]::new([Net.IPAddress]::Any,0)
        try { $b=$udp.Receive([ref]$remote) }
        catch [Net.Sockets.SocketException] {
            if ($_.Exception.SocketErrorCode -notin @([Net.Sockets.SocketError]::TimedOut,[Net.Sockets.SocketError]::ConnectionReset)) { throw }
            continue
        }
        if ($b.Length -lt 48 -or ($b[0..3] -join ',') -ne '77,82,83,49') { throw 'Invalid soak diagnostics.' }
        $m=[pscustomobject]@{Status=(U32 $b 4);Sessions=(U32 $b 8);Faults=(U32 $b 12);Reports=(U32 $b 16);
            Requests=(U32 $b 20);Heap=(U32 $b 24);Baseline=(U32 $b 28);Minimum=(U32 $b 32);Epoch=$state.Epoch}
        if ((U32 $b 36) -ne 0 -or (U32 $b 40) -ne 0x600d) { throw 'MCU debugger/magic mismatch.' }
        if ($m.Status -eq 3) { throw ('MCU failure: '+[Text.Encoding]::UTF8.GetString($b,48,$b.Length-48)) }
        return $m
    } while ([Environment]::TickCount64 -lt $deadline)
    throw 'Soak diagnostic timeout.'
}
function Check-Native {
    $peer=Join-Path $root 'bin\protocol-tools\iec61850-build\Release\VagabondKMmsPeer.exe'
    if ((& $peer --identity) -ne 'libiec61850 v1.6.2 ORIGINAL f0f797bf955da763353f7081c892a1ff6b55e5a1' -or $LASTEXITCODE -ne 0) { throw 'Unexpected native peer.' }
    $source=Join-Path $root 'bin\protocol-tools\libiec61850'
    $revision=& git -C $source rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $revision -ne 'f0f797bf955da763353f7081c892a1ff6b55e5a1') { throw 'Unexpected native source revision.' }
    & git -C $source diff --quiet $revision --
    if ($LASTEXITCODE -ne 0) { throw 'Independent native dependency modified.' }
    $state.NativePeer=[ordered]@{Revision=$revision;SHA256=(Get-FileHash -LiteralPath $peer).Hash}; Save-State
    return $peer
}
Save-State
try {
    $pc=Join-Path $root 'Tests\IEC61850\VagabondK.nF.IEC61850.Tests\VagabondK.nF.IEC61850.Tests.csproj'
    & dotnet build $pc -c Release --nologo -v quiet *> (Join-Path $output 'pc-build.log')
    if ($LASTEXITCODE -ne 0) { throw 'PC soak build failed.' }
    $app=if ($TypedClient) { 'VagabondK.nF.IEC61850.TypedClient.Hardware' } elseif ($TypedControls) { 'VagabondK.nF.IEC61850.Typed.Hardware' } else { 'VagabondK.nF.IEC61850.Report.Hardware' }
    $project=Join-Path $root "Tests\IEC61850\$app\$app.nfproj"
    & $MSBuildPath $project /t:Rebuild /p:Configuration=Release /warnaserror /v:quiet /nologo *> (Join-Path $output 'build.log')
    if ($LASTEXITCODE -ne 0) { throw 'MCU soak build failed.' }
    $built=Join-Path (Split-Path $project) 'bin\Release'
    $image=Join-Path $built "$app.bin"
    Copy-Item -LiteralPath $image -Destination (Join-Path $output 'deployed-image.bin')
    Get-ChildItem -LiteralPath $built -File | Where-Object { $_.Extension -in '.pe','.bin' } |
        ForEach-Object { [pscustomobject]@{Name=$_.Name;Bytes=$_.Length;SHA256=(Get-FileHash -LiteralPath $_.FullName).Hash} } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'images.json')
    $pcSnapshot=Join-Path $output 'pc'
    Copy-Item -LiteralPath (Join-Path $root 'Tests\IEC61850\VagabondK.nF.IEC61850.Tests\bin\Release\net10.0') -Destination $pcSnapshot -Recurse
    $dll=Join-Path $pcSnapshot 'VagabondK.nF.IEC61850.Tests.dll'
    $epochs=if ($TypedControls) { 1 } else { 2 }
    for ($epoch=1;$epoch -le $epochs;$epoch++) {
        $state.Epoch=$epoch; $state.Status='deploying'; Save-State
        $changed=$true
        for ($attempt=1;$attempt -le 3;$attempt++) {
            & (Join-Path $env:USERPROFILE '.dotnet\tools\nanoff.exe') --nanodevice --deploy --serialport COM3 --image $image --suppressnanoffversioncheck *> (Join-Path $output "deploy-$epoch-$attempt.log")
            if ($LASTEXITCODE -eq 0) { break }
            if ($attempt -eq 3) { throw 'Soak deployment failed.' }
            Start-Sleep -Seconds 10
        }
        if ($udp) { $udp.Dispose() }
        $udp=[Net.Sockets.UdpClient]::new(); $udp.Client.ReceiveTimeout=2000; $udp.Connect($DeviceAddress,20005)
        $m=Metrics
        if ($m.Status -ne 0 -or $m.Sessions -ne 0) { throw 'MCU application did not restart cleanly.' }
        if ($TypedClient) {
            $peer=Check-Native
            $local=$udp.Client.LocalEndPoint.Address.ToString()
            $start=[Diagnostics.ProcessStartInfo]::new($peer,"typedserver 18105 $local")
            $start.UseShellExecute=$false; $start.RedirectStandardInput=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
            $peerProcess=[Diagnostics.Process]::Start($start)
            $ready=$peerProcess.StandardOutput.ReadLineAsync()
            if (-not $ready.Wait(10000) -or $ready.Result -ne 'READY') { throw 'Typed native server not ready.' }
            $null=Metrics 1
            $state.Status='running'; Save-State
            $deadline=[Environment]::TickCount64+240000
            do {
                Start-Sleep -Milliseconds 250
                $m=Metrics; $state.LastMetrics=$m; Save-State
                $m | ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $output 'measurements.jsonl')
            } while ($m.Status -ne 2 -and [Environment]::TickCount64 -lt $deadline)
            if ($m.Status -ne 2) { throw "Typed MCU Client exceeded 240-second bound after $($m.Sessions) connections." }
            if ($m.Status -ne 2 -or $m.Sessions -ne 8 -or $m.Reports -ne 96 -or $m.Requests -lt 500 -or
                $m.Faults -ne 0 -or $m.Baseline -eq 0 -or $m.Minimum+512 -lt $m.Baseline) { throw 'Typed MCU Client coverage/heap mismatch.' }
            $peerProcess.StandardInput.WriteLine('quit')
            if (-not $peerProcess.WaitForExit(10000) -or $peerProcess.ExitCode -ne 0) { throw 'Typed native server exit failed.' }
            $peerProcess.StandardOutput.ReadToEnd() | Set-Content -LiteralPath (Join-Path $output 'native-server.log')
            $peerProcess.StandardError.ReadToEnd() | Set-Content -LiteralPath (Join-Path $output 'native-server-error.log')
            $peerProcess.Dispose(); $peerProcess=$null
            Write-Output "TYPED MCU CLIENT sessions=$($m.Sessions) enhancedCompletions=$($m.Reports) checks=$($m.Requests) heap=$($m.Heap) baseline=$($m.Baseline) minimum=$($m.Minimum)"
            continue
        }
        $null=Metrics 1; Start-Sleep -Seconds 1
        $seconds=$DurationMinutes*30
        $arguments=if ($TypedControls) { @("`"$dll`"",'--typed-controls-host',$DeviceAddress,'18104') } else { @("`"$dll`"",'--report-soak',$DeviceAddress,'18104',"$seconds") }
        $process=Start-Process dotnet -ArgumentList $arguments -PassThru -NoNewWindow `
            -RedirectStandardOutput (Join-Path $output "client-$epoch.log") -RedirectStandardError (Join-Path $output "client-$epoch-error.log")
        Start-Sleep -Seconds 1
        if ($process.HasExited -and -not $TypedControls) { throw 'Soak client exited before readiness.' }
        $state.Status='running'; Save-State
        $deadline=[Environment]::TickCount64+($seconds+120)*1000L
        do {
            $m=Metrics; $state.LastMetrics=$m; Save-State
            $m | ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $output 'measurements.jsonl')
            if ($m.Baseline -gt 0 -and $m.Minimum+512 -lt $m.Baseline) { throw 'Post-warmup retained heap decreased by more than 512 bytes.' }
            if ([Environment]::TickCount64 -ge $deadline) { throw 'Soak process exceeded duration bound.' }
            Start-Sleep -Seconds 5
        } while (-not $process.HasExited)
        $process.WaitForExit()
        $expected=if ($TypedControls) { 'TYPED CONTROL HOST PASS' } else { 'REPORT SOAK PASS' }
        if ($process.ExitCode -ne 0 -or (Get-Content -LiteralPath (Join-Path $output "client-$epoch.log") -Raw) -notmatch $expected) { throw "Soak client failed in epoch $epoch." }
        $process.Dispose(); $process=$null
        if ($TypedControls) {
            $peer=Check-Native
            $process=Start-Process $peer -ArgumentList @('typedclient','18104',$DeviceAddress) -PassThru -NoNewWindow `
                -RedirectStandardOutput (Join-Path $output 'native-typed.log') -RedirectStandardError (Join-Path $output 'native-typed-error.log')
            if (-not $process.WaitForExit(30000)) { throw 'Native typed MCU timeout.' }
            if ($process.ExitCode -ne 0 -or (Get-Content -LiteralPath (Join-Path $output 'native-typed.log') -Raw) -notmatch 'NATIVE TYPED PASS') { throw 'Native typed MCU failed.' }
            $process.Dispose(); $process=$null
        }
        Start-Sleep -Seconds 1
        $m=Metrics
        if ($TypedControls) {
            if ($m.Sessions -ne 9 -or $m.Faults -ne 0 -or $m.Reports -ne 108 -or $m.Baseline -eq 0 -or $m.Minimum+512 -lt $m.Baseline) { throw 'Incomplete MCU typed control coverage/retained heap.' }
        }
        elseif ($m.Sessions -lt 7 -or $m.Faults -lt 2 -or $m.Reports -lt 300 -or $m.Baseline -eq 0) { throw 'Incomplete MCU soak coverage.' }
        $state.LastMetrics=$m; Save-State
        $null=Metrics 2
        $deadline=[Environment]::TickCount64+10000
        do { Start-Sleep -Milliseconds 100; $m=Metrics } while ($m.Status -ne 2 -and [Environment]::TickCount64 -lt $deadline)
        if ($m.Status -ne 2) { throw 'MCU soak stop failed.' }
        $state.LastMetrics=$m; Save-State
        Write-Output "REPORT MCU SOAK epoch=$epoch sessions=$($m.Sessions) reports=$($m.Reports) faults=$($m.Faults) heap=$($m.Heap) baseline=$($m.Baseline) minimum=$($m.Minimum)"
    }
    $state.Status='completed'
}
catch { $state.Status='failed'; $state.Error=$_.ToString(); throw }
finally {
    if ($process) { if (-not $process.HasExited) { Stop-Process -Id $process.Id; $process.WaitForExit() }; $process.Dispose() }
    if ($peerProcess) { if (-not $peerProcess.HasExited) { Stop-Process -Id $peerProcess.Id; $peerProcess.WaitForExit() }; $peerProcess.Dispose() }
    if ($udp) { $udp.Dispose() }
    if ($changed) {
        Start-Sleep -Seconds 5
        try {
            try { & (Join-Path $PSScriptRoot 'Restore-ModbusHardware.ps1') -LogPath (Join-Path $output 'restored-modbus.log') }
            catch {
                Write-Warning "Restore failed; retrying once: $_"; Start-Sleep -Seconds 5
                & (Join-Path $PSScriptRoot 'Restore-ModbusHardware.ps1') -LogPath (Join-Path $output 'restored-modbus-retry.log')
            }
            $state.Restored=$true
        }
        catch { $state.Status='failed'; $state.Error="$($state.Error)`nRestore: $_"; Save-State; throw }
    }
    Save-State
}
if ($TypedClient) { Write-Output "MMS TYPED CLIENT HARDWARE PASS: original server, eight connections; restored original Modbus; $output" }
elseif ($TypedControls) { Write-Output "MMS TYPED HARDWARE PASS: PC/native clients, nine connections; restored original Modbus; $output" }
else { Write-Output "MMS REPORT HARDWARE SOAK PASS: restarted application once; restored original Modbus; $output" }
