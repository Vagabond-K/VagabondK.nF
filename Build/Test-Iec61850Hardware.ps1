#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][switch]$AllowDeviceChanges,
    [switch]$UseTryRead,
    [switch]$MalformedRecovery,
    [switch]$TimeoutComparison,
    [string]$HostAddress = '192.168.0.210',
    [string]$DeviceAddress = '192.168.0.187',
    [ValidateRange(1024,65535)][int]$Port = 18102,
    [string]$OutputPath = '',
    [string]$MSBuildPath = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe'
)
$ErrorActionPreference = 'Stop'
if (-not $AllowDeviceChanges) { throw 'Use only the isolated COM3 evaluation board.' }
if ($MalformedRecovery -and $TimeoutComparison) { throw 'Choose MalformedRecovery or TimeoutComparison, not both.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$ip = [Net.IPAddress]::Parse($HostAddress)
if ($ip.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or $HostAddress -eq '0.0.0.0' -or $HostAddress -eq '127.0.0.1') { throw 'Explicit LAN IPv4 required.' }
if (-not (Get-NetIPAddress -AddressFamily IPv4 | Where-Object IPAddress -eq $HostAddress)) { throw 'Host LAN address is not configured.' }
$restore = Join-Path $root 'bin\hardware\restore-Modbus.SerialServer.bin'
if ((Get-FileHash -LiteralPath $restore).Hash -ne '66C51145509667EA4019649750B42CAD47344CE76D9736994B9658E0F45226A7') { throw 'Restore checksum mismatch.' }
if ([IO.Ports.SerialPort]::GetPortNames() -notcontains 'COM3') { throw 'COM3 unavailable.' }
$native = Join-Path $root 'bin\protocol-tools\iec61850-build\Release\VagabondKMmsPeer.exe'
if (-not (Test-Path -LiteralPath $native)) { throw 'Build-Iec61850ValidationPeer.ps1 required.' }
if ((& $native --identity) -ne 'libiec61850 v1.6.2 ORIGINAL f0f797bf955da763353f7081c892a1ff6b55e5a1' -or $LASTEXITCODE -ne 0) { throw 'Unexpected independent MMS peer.' }
$source = Join-Path $root 'bin\protocol-tools\libiec61850'
$revision = & git -C $source rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $revision -ne 'f0f797bf955da763353f7081c892a1ff6b55e5a1') { throw 'Unexpected peer revision.' }
& git -C $source diff --quiet $revision --
if ($LASTEXITCODE -ne 0) { throw 'Independent MMS dependency modified.' }
if (-not $OutputPath) { $OutputPath = Join-Path $root ('bin\iec61850-hardware-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$output = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $output) { throw 'Choose a fresh output folder.' }
$null = New-Item -ItemType Directory -Path $output
$state = [ordered]@{ Status='preparing'; Restored=$false; Error=$null; Host=$HostAddress; Device=$DeviceAddress; Port=$Port; Mode=$(if ($TimeoutComparison) { 'TimeoutComparison' } elseif ($MalformedRecovery) { 'MalformedRecovery' } else { 'Full' }); ReadApi=$(if ($UseTryRead) { 'TryRead' } else { 'Read' }); LastMetrics=$null }
function Save-State { $state | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'status.json') }
Save-State
$peer = $null; $tail = $null; $errors = $null; $peerNumber = 0
$udp = $null; $changed = $false
$script:sequence = 0U
function Start-Peer([string]$mode='writeserver') {
    $start = [Diagnostics.ProcessStartInfo]::new($native,"$mode $Port $HostAddress")
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $script:peer = [Diagnostics.Process]::Start($start)
    $script:peerNumber++
    $script:errors = $peer.StandardError.ReadToEndAsync()
    $line = $peer.StandardOutput.ReadLineAsync()
    if (-not $line.Wait(10000) -or $line.Result -ne 'READY') { throw 'Independent MMS server not ready.' }
    $script:tail = $peer.StandardOutput.ReadToEndAsync()
}
function Start-FaultPeer([int]$scenario) {
    $assembly=Join-Path $root 'Tests\IEC61850\VagabondK.nF.IEC61850.Tests\bin\Release\net10.0\VagabondK.nF.IEC61850.Tests.dll'
    $start=[Diagnostics.ProcessStartInfo]::new('dotnet')
    foreach ($argument in @($assembly,'--hardware-client-fault',$HostAddress,"$Port","$scenario",(Join-Path $output "fault-$scenario.bin"))) {
        $start.ArgumentList.Add($argument)
    }
    $start.UseShellExecute=$false
    $start.RedirectStandardInput=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $script:peer=[Diagnostics.Process]::Start($start)
    $script:peerNumber++
    $script:errors=$peer.StandardError.ReadToEndAsync()
    $line=$peer.StandardOutput.ReadLineAsync()
    if (-not $line.Wait(10000) -or $line.Result -ne 'READY') { throw 'Malformed response peer not ready.' }
    $script:tail=$peer.StandardOutput.ReadToEndAsync()
}
function Stop-Peer {
    if (-not $peer) { return }
    try {
        if (-not $peer.HasExited) {
            $peer.StandardInput.WriteLine('quit'); $peer.StandardInput.Flush()
            if (-not $peer.WaitForExit(5000)) { throw 'MMS server shutdown timed out.' }
        }
        if ($peer.ExitCode -ne 0) { throw "MMS server exit code $($peer.ExitCode)" }
    }
    finally {
        if (-not $peer.HasExited) { Stop-Process -Id $peer.Id; $peer.WaitForExit() }
        if ($tail) { 'READY' + "`n" + $tail.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $output "peer-$peerNumber.log") }
        if ($errors) { $errors.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $output "peer-$peerNumber-error.log") }
        $peer.Dispose(); $script:peer = $null; $script:tail = $null; $script:errors = $null
    }
}
function Read-U32([byte[]]$bytes, [int]$p) {
    return ([uint32]$bytes[$p] -shl 24) -bor ([uint32]$bytes[$p+1] -shl 16) -bor ([uint32]$bytes[$p+2] -shl 8) -bor [uint32]$bytes[$p+3]
}
function Invoke-Mcu([byte]$command, [switch]$StopWhenArmed) {
    $script:sequence++
    $request = [byte[]]@(77,77,72,58,0,0,0,0,$command,0,0,0,0,($Port -shr 8),($Port -band 255))
    $hostBytes=$ip.GetAddressBytes()
    [Array]::Copy($hostBytes,0,$request,9,4)
    for ($i=0;$i -lt 4;$i++) { $request[4+$i]=[byte](($sequence -shr (24-$i*8)) -band 255) }
    $deadline=[Environment]::TickCount64+180000
    $stopped=$false
    while ([Environment]::TickCount64 -lt $deadline) {
        if ($peer -and $peer.HasExited) { throw 'Independent MMS server exited unexpectedly.' }
        $null=$udp.Send($request,$request.Length)
        $remote=[Net.IPEndPoint]::new([Net.IPAddress]::Any,0)
        try { $reply=$udp.Receive([ref]$remote) }
        catch [Net.Sockets.SocketException] {
            if ($_.Exception.SocketErrorCode -notin @([Net.Sockets.SocketError]::TimedOut,[Net.Sockets.SocketError]::ConnectionReset)) { throw }
            continue
        }
        if ($reply.Length -lt 208 -or ($reply[0..3] -join ',') -ne '77,77,72,58') { throw 'Invalid MCU diagnostics.' }
        if ((Read-U32 $reply 4) -ne $sequence) { continue }
        $status=Read-U32 $reply 8
        if ($status -eq 3) { throw ('MCU failure: '+[Text.Encoding]::UTF8.GetString($reply,208,$reply.Length-208)) }
        if ($status -eq 1) {
            if (-not $StopWhenArmed) { throw 'Unexpected armed response.' }
            if (-not $stopped) { Stop-Peer; $stopped=$true }
            continue
        }
        if ($status -ne 2 -or ($StopWhenArmed -and -not $stopped)) { throw 'MCU completion mismatch.' }
        $v=[uint32[]]::new(48)
        for ($i=0;$i -lt 48;$i++) { $v[$i]=Read-U32 $reply (16+$i*4) }
        if ($v[13] -ne 0 -or $v[15] -ne 0x600d) { throw 'MCU debugger/magic mismatch.' }
        return ,$v
    }
    throw "MCU command timeout: $command"
}
function Record([uint32[]]$v,[string]$phase) {
    $record=[ordered]@{ Phase=$phase; Utc=[DateTime]::UtcNow.ToString('o'); Cycles=$v[0]; Checks=$v[1]; Values=$v[2]; AccessErrors=$v[3]
        CycleMilliseconds=$v[4]; PeerStopFaults=$v[5]; SilentTimeouts=$v[6]; TimeoutMilliseconds=$v[7]
        Heap=$v[8]; BaselineHeap=$v[9]; MinimumHeap=$v[10]; MaximumHeap=$v[11]; TimeoutWaitMilliseconds=$v[12]; ClientAndChannelBytes=$v[14]
        AssociationMilliseconds=$v[16]; ReadTotalMilliseconds=$v[17]; ReadMaximumMilliseconds=$v[18]; TimedReads=$v[19]
        ScalarReadTotalMilliseconds=$v[20]; ScalarReadMaximumMilliseconds=$v[21]; AccessErrorTotalMilliseconds=$v[22]; AccessErrorMaximumMilliseconds=$v[23]
        DiscoveryPages=$v[24]; AttributeQueries=$v[25]; ServiceErrors=$v[26]; CompositeRequests=$v[27]
        WriteSuccesses=$v[28]; WriteDenials=$v[29]; WriteReadbacks=$v[30]; ControlCycles=$v[31]; EnhancedCycles=$v[32]; SboEnhancedCycles=$v[33]; DataCycles=$v[34]
        MalformedResponses=$v[35]; LostResponses=$v[36]; FaultOperateTransmissions=$v[37]; RefusedReplays=$v[38]
        TimeoutComparisons=$v[39]; TimeoutResults=$v[40]; TimeoutExceptions=$v[41]; TimeoutMode=$v[42]
        OperationMilliseconds=$v[43]; ChannelReadMilliseconds=$v[44]; ChannelWriteMilliseconds=$v[45]
        BeforeReadMilliseconds=$v[46]; AfterReadMilliseconds=$v[47] }
    $state.LastMetrics=$record
    $record | ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $output 'measurements.jsonl')
    Save-State
    Write-Output ($record | ConvertTo-Json -Compress)
}
function Cycle([string]$phase) {
    $before=$script:values
    $script:values=Invoke-Mcu $(if ($UseTryRead) { 4 } else { 1 })
    if ($values[0] -ne $before[0]+1 -or $values[2] -ne $before[2]+60 -or $values[3] -ne $before[3]+20) { throw 'Cycle totals mismatch.' }
    Record $values $phase
    if ($values[9] -ne 0 -and $values[8] -lt $values[9]) { throw 'Post-warmup retained heap grew.' }
}
try {
    & dotnet run -c Release --project (Join-Path $root 'Tests\IEC61850\VagabondK.nF.IEC61850.Tests\VagabondK.nF.IEC61850.Tests.csproj') -- --interop $native *>&1 |
        Tee-Object -FilePath (Join-Path $output 'pc-tests.log')
    if ($LASTEXITCODE -ne 0) { throw 'PC MMS regression failed.' }
    $project=Join-Path $root 'Tests\IEC61850\VagabondK.nF.IEC61850.Hardware\VagabondK.nF.IEC61850.Hardware.nfproj'
    & $MSBuildPath $project /t:Rebuild /p:Configuration=Release /warnaserror /v:quiet /nologo *>&1 |
        Tee-Object -FilePath (Join-Path $output 'build.log')
    if ($LASTEXITCODE -ne 0) { throw 'MCU image build failed.' }
    $built=Join-Path $root 'Tests\IEC61850\VagabondK.nF.IEC61850.Hardware\bin\Release'
    $image=Join-Path $built 'VagabondK.nF.IEC61850.Hardware.bin'
    $size=[ordered]@{ ImageBytes=(Get-Item -LiteralPath $image).Length; SHA256=(Get-FileHash -LiteralPath $image).Hash
        AppPEBytes=(Get-Item -LiteralPath (Join-Path $built 'VagabondK.nF.IEC61850.Hardware.pe')).Length
        CorePEBytes=(Get-Item -LiteralPath (Join-Path $built 'VagabondK.nF.IEC61850.pe')).Length
        MmsCorePEBytes=(Get-Item -LiteralPath (Join-Path $built 'VagabondK.nF.MMS.pe')).Length
        MmsClientPEBytes=(Get-Item -LiteralPath (Join-Path $built 'VagabondK.nF.MMS.Client.pe')).Length
        IecClientPEBytes=(Get-Item -LiteralPath (Join-Path $built 'VagabondK.nF.IEC61850.Client.pe')).Length
        TcpPEBytes=(Get-Item -LiteralPath (Join-Path $built 'VagabondK.nF.MMS.TCP.pe')).Length }
    $size | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'sizes.json')
    Copy-Item -LiteralPath $image -Destination (Join-Path $output 'deployed-image.bin')
    [ordered]@{ Identity=(& $native --identity); PeerSHA256=(Get-FileHash -LiteralPath $native).Hash
        RestoreSHA256=(Get-FileHash -LiteralPath $restore).Hash; AppSourceSHA256=(Get-FileHash -LiteralPath (Join-Path $root 'Tests\IEC61850\VagabondK.nF.IEC61850.Hardware\Program.cs')).Hash } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'images.json')
    Start-Peer
    $changed=$true
    for ($attempt=1;$attempt -le 3;$attempt++) {
        & (Join-Path $env:USERPROFILE '.dotnet\tools\nanoff.exe') --nanodevice --deploy --serialport COM3 --image $image --suppressnanoffversioncheck *>&1 |
            Tee-Object -FilePath (Join-Path $output "deploy-$attempt.log")
        if ($LASTEXITCODE -eq 0) { break }
        if ($attempt -eq 3) { throw 'MMS deployment failed.' }
        Write-Warning 'Deployment failed; retrying in 10 seconds.'
        Start-Sleep -Seconds 10
    }
    $udp=[Net.Sockets.UdpClient]::new()
    $udp.Client.ReceiveTimeout=2000
    $udp.Connect($DeviceAddress,20003)
    $values=Invoke-Mcu 0
    $state.Status='running'
    Record $values 'ready'
    if ($TimeoutComparison) {
        Cycle 'warmup'
        Cycle 'baseline'
        Stop-Peer
        for ($round=0;$round -lt 3;$round++) {
            foreach ($mode in @(0,1,2,3)) {
                Start-FaultPeer $(if ($mode -ge 2) { 12 } else { 13 })
                $values=Invoke-Mcu (22+$mode)
                Record $values "comparison-$round-$mode"
                Stop-Peer
                $scenario=if ($mode -ge 2) { 12 } else { 13 }
                $executions=if ($mode -ge 2) { 1 } else { 0 }
                $evidence=Get-Content -LiteralPath (Join-Path $output "peer-$peerNumber.log") -Raw
                if ($evidence -notmatch "MCU CLIENT FAULT PASS scenario=$scenario injections=1 executions=$executions operateTransmissions=$executions") {
                    throw 'Timeout comparison peer evidence missing.'
                }
                Start-Peer
                Cycle "comparison-recovery-$round-$mode"
                Stop-Peer
            }
        }
        if ($values[39] -ne 12 -or $values[40] -ne 6 -or $values[41] -ne 6 -or $values[0] -ne 14) {
            throw 'Timeout comparison totals mismatch.'
        }
        $records=Get-Content -LiteralPath (Join-Path $output 'measurements.jsonl') | ForEach-Object { $_ | ConvertFrom-Json }
        $samples=@($records | Where-Object { $_.Phase -match '^comparison-\d+-\d+$' })
        $summary=foreach ($mode in @(0,1,2,3)) {
            $group=@($samples | Where-Object TimeoutMode -eq $mode)
            if ($group.Count -ne 3) { throw 'Timeout comparison sample count mismatch.' }
            $timing=$group.OperationMilliseconds | Measure-Object -Average -Minimum -Maximum
            [ordered]@{Mode=$mode;Samples=$group.Count;AverageMilliseconds=$timing.Average
                MinimumMilliseconds=$timing.Minimum;MaximumMilliseconds=$timing.Maximum}
        }
        $summary | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'comparison.json')
    } elseif ($MalformedRecovery) {
        Cycle 'warmup'
        Cycle 'baseline'
        Stop-Peer
        for ($scenario=0;$scenario -lt 13;$scenario++) {
            Start-FaultPeer $scenario
            $values=Invoke-Mcu (9+$scenario)
            if ($values[35] -ne [Math]::Min($scenario+1,12) -or $values[36] -ne $(if ($scenario -eq 12) { 1 } else { 0 })) {
                throw 'Malformed response counters mismatch.'
            }
            Record $values "fault-$scenario"
            Stop-Peer
            $evidence=Get-Content -LiteralPath (Join-Path $output "peer-$peerNumber.log") -Raw
            $executions=if ($scenario -eq 12) { 1 } else { 0 }
            if ($evidence -notmatch "MCU CLIENT FAULT PASS scenario=$scenario injections=1 executions=$executions operateTransmissions=$executions") {
                throw 'Fault peer execution/no replay evidence missing.'
            }
            Start-Peer
            Cycle "recovery-$scenario"
            Stop-Peer
        }
        if ($values[0] -ne 15 -or $values[2] -ne 900 -or $values[3] -ne 300 -or $values[35] -ne 12 -or
            $values[36] -ne 1 -or $values[37] -ne 1 -or $values[38] -ne 1) { throw 'Client recovery final totals mismatch.' }
    } else {
    Stop-Peer
    Start-Peer 'silent'
    $values=Invoke-Mcu 3
    Stop-Peer
    $silentEvidence=Get-Content -LiteralPath (Join-Path $output "peer-$peerNumber.log") -Raw
    if ($silentEvidence -notmatch 'SILENT CR 0300001611E00000000100C0010AC1020001C2020001') { throw 'Silent peer COTP CR mismatch.' }
    if ($values[6] -ne 1 -or $values[12] -lt 2950 -or $values[12] -ge 3500 -or
        $values[7] -lt $values[12] -or $values[7] -ge $values[12]+4000) { throw 'Silent timeout mismatch.' }
    Record $values 'silent-timeout'
    Start-Peer
    Cycle 'warmup-after-timeout'
    Cycle 'baseline'
    Cycle 'repeat'
    $values=Invoke-Mcu 2 -StopWhenArmed
    if ($values[5] -ne 1 -or $values[2] -ne 181) { throw 'Peer stop fault mismatch.' }
    Record $values 'peer-stopped'
    Start-Peer
    Cycle 'peer-recovery'
    Cycle 'final-recovery'
    if ($values[0] -ne 5 -or $values[2] -ne 301 -or $values[3] -ne 100 -or $values[5] -ne 1 -or $values[6] -ne 1 -or $values[19] -ne 400 -or
        $values[24] -ne 15 -or $values[25] -ne 15 -or $values[26] -ne 5 -or $values[27] -ne 30 -or
        $values[28] -ne 30 -or $values[29] -ne 20 -or $values[30] -ne 20) { throw 'Final totals mismatch.' }
    Stop-Peer
    Start-Peer 'controlserver'
    for ($controlCycle=1;$controlCycle -le 3;$controlCycle++) {
        $values=Invoke-Mcu 5
        if ($values[31] -ne $controlCycle) { throw 'Control cycle total mismatch.' }
        Record $values "controls-$controlCycle"
    }
    Stop-Peer
    Start-Peer 'enhancedserver'
    for ($enhancedCycle=1;$enhancedCycle -le 3;$enhancedCycle++) {
        $values=Invoke-Mcu 6
        if ($values[32] -ne $enhancedCycle) { throw 'Enhanced cycle total mismatch.' }
        Record $values "enhanced-$enhancedCycle"
    }
    Stop-Peer
    Start-Peer 'sboenhancedserver'
    for ($sboCycle=1;$sboCycle -le 3;$sboCycle++) {
        $values=Invoke-Mcu 7
        if ($values[33] -ne $sboCycle -or $values[8] -lt $values[9]) { throw 'SBO enhanced cycle/heap mismatch.' }
        Record $values "sbo-enhanced-$sboCycle"
    }
    Stop-Peer
    Start-Peer 'dataserver'
    $dataHeap=0
    for ($dataCycle=1;$dataCycle -le 3;$dataCycle++) {
        $values=Invoke-Mcu 8
        if ($values[34] -ne $dataCycle) { throw 'Data/report cycle total mismatch.' }
        if ($dataCycle -eq 1) { $dataHeap=$values[8] }
        elseif ($values[8] -lt $dataHeap) { throw 'Data/report retained heap grew.' }
        Record $values "data-report-$dataCycle"
    }
    }
    $state.Status='completed'
}
catch { $state.Status='failed'; $state.Error=$_.ToString(); throw }
finally {
    $cleanup=[Collections.Generic.List[string]]::new()
    if ($udp) { $udp.Dispose() }
    try { Stop-Peer } catch { $cleanup.Add($_.ToString()) }
    if ($changed) {
        Start-Sleep -Seconds 5
        try {
            try { & (Join-Path $PSScriptRoot 'Restore-ModbusHardware.ps1') -LogPath (Join-Path $output 'restored-modbus.log') }
            catch {
                Write-Warning "Restore failed; retrying once: $_"
                Start-Sleep -Seconds 5
                & (Join-Path $PSScriptRoot 'Restore-ModbusHardware.ps1') -LogPath (Join-Path $output 'restored-modbus-retry.log')
            }
            $state.Restored=$true
        }
        catch { $cleanup.Add($_.ToString()) }
    }
    if ($cleanup.Count) { $state.Status='failed'; $state.Error=($state.Error,($cleanup -join "`n") | Where-Object { $_ }) -join "`n" }
    Save-State
    if ($cleanup.Count) { throw ($cleanup -join "`n") }
}
Write-Output "MMS HARDWARE PASS: original Modbus restored; $output"
