#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][switch]$AllowDeviceChanges,
    [switch]$SerialValidation,
    [switch]$SerialSoak,
    [switch]$SerialCrc,
    [switch]$TcpRecovery,
    [ValidateRange(1,1440)][double]$DurationMinutes = 480,
    [ValidateRange(1,1000)][int]$FaultEveryCycles = 10,
    [ValidateRange(0,60)][int]$CyclePauseSeconds = 5,
    [string]$OutputPath = '',
    [string]$MSBuildPath = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe'
)
$ErrorActionPreference = 'Stop'
if (-not $AllowDeviceChanges) { throw 'Use only the isolated COM3 evaluation board.' }
if ($SerialSoak -and -not $SerialValidation) { throw 'SerialSoak requires SerialValidation.' }
if ($SerialCrc -and (-not $SerialValidation -or $SerialSoak)) { throw 'SerialCrc requires fixed SerialValidation mode.' }
if ($TcpRecovery -and ($SerialValidation -or $SerialSoak -or $SerialCrc)) { throw 'TcpRecovery is TCP-only fixed mode.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$restore = Join-Path $root 'bin\hardware\restore-Modbus.SerialServer.bin'
if ((Get-FileHash -LiteralPath $restore).Hash -ne '66C51145509667EA4019649750B42CAD47344CE76D9736994B9658E0F45226A7') { throw 'Restore checksum mismatch.' }
if ([IO.Ports.SerialPort]::GetPortNames() -notcontains 'COM3') { throw 'COM3 unavailable.' }
if ($SerialValidation -and [IO.Ports.SerialPort]::GetPortNames() -notcontains 'COM6') { throw 'COM6 unavailable.' }
$native = Join-Path $root 'bin\protocol-tools\master-build\Release\VagabondKDnp3Outstation.exe'
if ((& $native --decoder-identity) -ne 'DECODER ORIGINAL' -or $LASTEXITCODE -ne 0) { throw 'Unmodified OpenDNP3 required.' }
if (-not $OutputPath) { $OutputPath = Join-Path $root ('bin\dnp3-master-soak-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$output = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $output) { throw 'Choose a fresh output folder.' }
$null = New-Item -ItemType Directory -Path $output
$state = [ordered]@{
    Status='preparing'; ProcessId=$PID; RequestedMinutes=$DurationMinutes
    StartedUtc=$null; DeadlineUtc=$null; FinishedUtc=$null; Cycles=0; Faults=0; PeerRestarts=0; TcpDrops=0
    Restored=$false; Error=$null; LastMetrics=$null
    Transport=$(if ($SerialValidation) { 'Serial' } else { 'Tcp' })
    Mode=$(if ($TcpRecovery) { 'TcpRecovery' } elseif ($SerialSoak -or -not $SerialValidation) { 'Timed' } else { 'Fixed' })
    SerialAckTimeouts=0; SerialResponseTimeouts=0; SerialPartialExpiries=0
    SerialCrcCases=0; SerialCrcErrors=0
    TcpRecoveryCases=0; TcpCrcErrors=0; TcpPartialExpiries=0
}
function Save-State {
    $temporary = Join-Path $output 'status.tmp'
    $state | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $temporary
    Move-Item -LiteralPath $temporary -Destination (Join-Path $output 'status.json') -Force
}
Save-State
$escapedRoot = [Security.SecurityElement]::Escape($root)
$extraDefines = ''
$extraItems = ''
if ($SerialValidation -or $TcpRecovery) {
    $extraDefines = if ($SerialValidation) { ';DNP3_MASTER_MAPPING_SERIAL' } else { ';DNP3_TCP_RECOVERY' }
    $extraItems = @"
    <Reference Include="nanoFramework.Hardware.Esp32"><HintPath>`$(PackagesPath)nanoFramework.Hardware.Esp32.1.6.42\lib\nanoFramework.Hardware.Esp32.dll</HintPath></Reference>
    <Reference Include="System.IO.Ports"><HintPath>`$(PackagesPath)nanoFramework.System.IO.Ports.1.1.142\lib\System.IO.Ports.dll</HintPath></Reference>
    <ProjectReference Include="$escapedRoot\Source\DNP3\VagabondK.nF.DNP3.Serial\VagabondK.nF.DNP3.Serial.nfproj" />
    <ProjectReference Include="$escapedRoot\Source\DNP3\VagabondK.nF.DNP3.Outstation\VagabondK.nF.DNP3.Outstation.nfproj" />
    <Compile Include="$escapedRoot\Tests\DNP3\VagabondK.nF.DNP3.Mapping.Hardware\MasterSerialValidation.cs" />
    <Compile Include="$escapedRoot\Tests\DNP3\VagabondK.nF.DNP3.Mapping.Hardware\SerialCrcValidation.cs" />
"@
}
$project = @"
<Project ToolsVersion="Current" DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <PropertyGroup><ProjectGuid>{310E1601-9B23-4A51-B344-100000000116}</ProjectGuid></PropertyGroup>
  <Import Project="$escapedRoot\Build\Library.props" />
  <Import Project="$escapedRoot\Build\Network.props" />
  <PropertyGroup><OutputType>Exe</OutputType><DefineConstants>`$(DefineConstants);DNP3_MASTER_MAPPING_SOAK$extraDefines</DefineConstants></PropertyGroup>
  <ItemGroup>
    <Reference Include="mscorlib"><HintPath>`$(PackagesPath)nanoFramework.CoreLibrary.1.17.11\lib\mscorlib.dll</HintPath><Private>true</Private></Reference>
    <ProjectReference Include="$escapedRoot\Source\DNP3\VagabondK.nF.DNP3.Mapping\VagabondK.nF.DNP3.Mapping.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" SkipGetTargetFrameworkProperties="true" />
    <ProjectReference Include="$escapedRoot\Source\DNP3\VagabondK.nF.DNP3\VagabondK.nF.DNP3.nfproj" />
    <ProjectReference Include="$escapedRoot\Source\DNP3\VagabondK.nF.DNP3.Master\VagabondK.nF.DNP3.Master.nfproj" />
    <ProjectReference Include="$escapedRoot\Source\DNP3\VagabondK.nF.DNP3.TCP\VagabondK.nF.DNP3.TCP.nfproj" />
    <Compile Include="$escapedRoot\Samples\DNP3\VagabondK.nF.DNP3.MasterMapping\Program.cs" />
    <Compile Include="$escapedRoot\Samples\DNP3\VagabondK.nF.DNP3.MasterMapping\ReceivedData.cs" />
    <Compile Include="$escapedRoot\Tests\DNP3\VagabondK.nF.DNP3.Mapping.Hardware\MasterValidation.cs" />
    <Compile Include="$escapedRoot\Tests\DNP3\VagabondK.nF.DNP3.Mapping.Hardware\MasterSoak.cs" />
$extraItems
  </ItemGroup>
  <Import Project="$escapedRoot\Build\Library.targets" />
</Project>
"@
$path = Join-Path $output 'Dnp3MasterSoak.nfproj'
[IO.File]::WriteAllText($path, $project)
$changed = $false
$peer = $null
$evictor = $null
$rawSerial = $null
$rawTcp = $null
$udp = $null
$script:sequence = 0U
$script:prefix = $null
$script:tail = $null
$script:errors = $null
$script:peerNumber = 0
function Start-Peer {
    $arguments = if ($SerialValidation) { 'COM6 soak' } else { '20000 soak 192.168.0.210' }
    $start = [Diagnostics.ProcessStartInfo]::new($native, $arguments)
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $script:peer = [Diagnostics.Process]::Start($start)
    $script:errors = $peer.StandardError.ReadToEndAsync()
    $script:prefix = [Collections.Generic.List[string]]::new()
    do {
        $line = $peer.StandardOutput.ReadLineAsync()
        if (-not $line.Wait(10000) -or $null -eq $line.Result) { throw 'Outstation readiness failed.' }
        $prefix.Add($line.Result)
    } while ($line.Result -ne 'READY')
    $script:tail = $peer.StandardOutput.ReadToEndAsync()
    $script:peerNumber++
}
function Stop-Peer {
    if (-not $peer) { return }
    try {
        if (-not $peer.HasExited) {
            $peer.StandardInput.WriteLine('quit'); $peer.StandardInput.Flush()
            if (-not $peer.WaitForExit(5000)) { throw 'Outstation shutdown timed out.' }
        }
        if ($peer.ExitCode -ne 0) { throw "Outstation exit code: $($peer.ExitCode)" }
    }
    finally {
        if (-not $peer.HasExited) { Stop-Process -Id $peer.Id; $peer.WaitForExit() }
        if ($tail) { ($prefix -join "`n") + "`n" + $tail.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $output "peer-$peerNumber.log") }
        if ($errors) { $errors.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $output "peer-$peerNumber-error.log") }
        $peer.Dispose()
        $script:peer = $null
    }
}
function Read-U32([byte[]]$bytes, [int]$p) {
    return ([uint32]$bytes[$p] -shl 24) -bor ([uint32]$bytes[$p+1] -shl 16) -bor ([uint32]$bytes[$p+2] -shl 8) -bor [uint32]$bytes[$p+3]
}
function Invoke-Mcu([byte]$command, [string]$fault = '') {
    $script:sequence++
    $request = [byte[]]@(68,77,83,49,0,0,0,0,$command)
    for ($i = 0; $i -lt 4; $i++) { $request[4+$i] = [byte](($sequence -shr (24-$i*8)) -band 255) }
    $deadline = [Environment]::TickCount64 + $(if ($SerialValidation) { 300000 } else { 180000 })
    $injected = $false
    $remote = [Net.IPEndPoint]::new([Net.IPAddress]::Any,0)
    while ([Environment]::TickCount64 -lt $deadline) {
        if ($peer -and $peer.HasExited) { throw 'Independent Outstation exited unexpectedly.' }
        $null = $udp.Send($request,$request.Length)
        try { $reply = $udp.Receive([ref]$remote) }
        catch [Net.Sockets.SocketException] {
            if ($_.Exception.SocketErrorCode -notin @([Net.Sockets.SocketError]::TimedOut,[Net.Sockets.SocketError]::ConnectionReset)) { throw }
            continue
        }
        if ($reply.Length -lt 88 -or ($reply[0..3] -join ',') -ne '68,77,83,49') { throw 'Invalid soak response.' }
        if ((Read-U32 $reply 4) -ne $sequence) { continue }
        $status = Read-U32 $reply 8
        if ($status -eq 3) { throw ('MCU soak failure: ' + [Text.Encoding]::UTF8.GetString($reply,88,$reply.Length-88)) }
        if ($status -eq 1) {
            if (-not $fault) { throw 'Unexpected armed fault response.' }
            if (-not $injected) {
                if ($fault -eq 'restart') { Stop-Peer }
                elseif ($fault -eq 'drop') {
                    # OpenDNP3의 CloseExisting은 데이터베이스를 바꾸지 않고 준비된 MCU 연결만 끊습니다.
                    $script:evictor = [Net.Sockets.TcpClient]::new()
                    $evictor.Connect('192.168.0.210',20000)
                }
                elseif ($fault -eq 'partial') {
                    if ($peer) { throw 'Stop the Outstation before raw UART injection.' }
                    if (-not $rawSerial -or -not $rawSerial.IsOpen) { throw 'Raw UART must be open before arming the partial frame test.' }
                    $prefixBytes = [byte[]]@(5,100,20)
                    $rawSerial.Write($prefixBytes,0,3)
                }
                elseif ($fault -eq 'tcp-recovery') {
                    if ($peer) { throw 'Raw TCP ownership required.' }
                    $script:rawTcp = [Net.Sockets.Socket]::new(
                        [Net.Sockets.AddressFamily]::InterNetwork,[Net.Sockets.SocketType]::Stream,[Net.Sockets.ProtocolType]::Tcp)
                    $rawTcp.Connect('192.168.0.187',20003)
                    $rawTcp.SendTimeout = 3000
                    $kind = ($command - 6) % 4
                    if ($kind -eq 3) {
                        if ($rawTcp.Send([byte[]]@(5,100,20)) -ne 3) { throw 'Incomplete TCP prefix send.' }
                        Start-Sleep -Milliseconds 1200
                    }
                    $inputBytes = [SerialCrcValidation]::Input(($command -lt 10), $kind)
                    for ($offset = 0; $offset -lt $inputBytes.Length;) {
                        $sent = $rawTcp.Send($inputBytes,$offset,$inputBytes.Length-$offset,[Net.Sockets.SocketFlags]::None)
                        if ($sent -eq 0) { throw 'TCP input send made no progress.' }
                        $offset += $sent
                    }
                }
                elseif ($fault -eq 'crc') {
                    if ($peer -or -not $rawSerial -or -not $rawSerial.IsOpen) { throw 'Raw UART ownership required for CRC injection.' }
                    $receivingMaster = $command -lt 9
                    $kind = ($command - 6) % 3
                    $inputBytes = [SerialCrcValidation]::Input($receivingMaster, $kind)
                    Start-Sleep -Milliseconds 100
                    $rawSerial.Write($inputBytes, 0, $inputBytes.Length)
                }
                else { throw 'Invalid fault mode.' }
                $injected = $true
            }
            continue
        }
        if ($status -ne 2 -or ($fault -and -not $injected)) { throw 'Soak completion/fault injection mismatch.' }
        if ($evictor) { $evictor.Dispose(); $script:evictor = $null }
        $values = [uint32[]]::new(18)
        for ($i = 0; $i -lt 18; $i++) { $values[$i] = Read-U32 $reply (16 + $i*4) }
        if ($values[13] -ne 0 -or $values[15] -ne 0x600D) { throw 'Debugger/completion mismatch.' }
        $script:lastSerialRetries = Read-U32 $reply 12
        return ,$values
    }
    throw "MCU soak command timed out: sequence=$sequence command=$command fault=$fault"
}
function Record([uint32[]]$v, [string]$phase) {
    $record = [ordered]@{
        Utc=[DateTime]::UtcNow.ToString('o'); Phase=$phase; Sequence=$sequence; Cycles=$v[0]
        Checks=$v[1]; Measurements=$v[2]; Events=$v[3]; Unsolicited=$v[4]; Generation=$v[5]
        Faults=$v[6]; LastFaultKind=$v[7]; Heap=$v[8]; MinimumHeap=$v[9]; MaximumHeap=$v[10]
        BaselineHeap=$v[11]; DeviceSeconds=$v[12]
        SerialRetries=$script:lastSerialRetries; FrameTimeouts=$v[14]
        CrcErrors=$v[16]; DiscardedBytes=$v[17]
    }
    $record | ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $output 'measurements.jsonl')
    $state.LastMetrics = $record
    $state.Cycles = [int]$v[0]
    $state.Faults = [int]$v[6]
    Save-State
    Write-Output ($record | ConvertTo-Json -Compress)
}
try {
    & dotnet run -c Release --project (Join-Path $root 'Tests\DNP3\VagabondK.nF.DNP3.Mapping.Tests\VagabondK.nF.DNP3.Mapping.Tests.csproj') *>&1 |
        Tee-Object -FilePath (Join-Path $output 'pc-tests.log')
    if ($LASTEXITCODE -ne 0) { throw 'PC mapping/soak regression failed.' }
    if ($SerialCrc -or $TcpRecovery) {
        Add-Type -Path (Join-Path $root 'Tests\DNP3\VagabondK.nF.DNP3.Mapping.Tests\bin\Release\net10.0\VagabondK.nF.DNP3.Mapping.Tests.dll')
    }
    & $MSBuildPath $path /t:Rebuild /p:Configuration=Release /warnaserror /v:quiet /nologo *>&1 |
        Tee-Object -FilePath (Join-Path $output 'build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Soak MCU build failed.' }
    $image = Join-Path $output 'bin\Release\Dnp3MasterSoak.bin'
    Get-FileHash -LiteralPath $image | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'image.json')
    Start-Peer
    $changed = $true
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        & (Join-Path $env:USERPROFILE '.dotnet\tools\nanoff.exe') --nanodevice --deploy --serialport COM3 --image $image --suppressnanoffversioncheck *>&1 |
            Tee-Object -FilePath (Join-Path $output "deploy-$attempt.log")
        if ($LASTEXITCODE -eq 0) { break }
        if ($attempt -eq 3) { throw 'Soak deployment failed.' }
        Write-Warning 'Deployment failed; retrying after 10 seconds.'
        Start-Sleep -Seconds 10
    }
    $udp = [Net.Sockets.UdpClient]::new()
    $udp.Client.ReceiveTimeout = 3000
    $udp.Connect('192.168.0.187',20002)
    $values = Invoke-Mcu 0
    Record $values 'ready'
    $state.StartedUtc = [DateTime]::UtcNow.ToString('o')
    if ($TcpRecovery) {
        $state.Status = 'running'
        $values = Invoke-Mcu 1
        Record $values 'tcp-warmup'
        Stop-Peer
        for ($command = 6; $command -le 13; $command++) {
            $before = $values
            $values = Invoke-Mcu $command 'tcp-recovery'
            $kind = ($command-6)%4
            $expected = if ($kind -eq 3) { 0 } elseif ($kind -eq 2) { 6 } else { 4 }
            if ($values[6] -ne $before[6]+1 -or $values[7] -ne $command+1 -or
                $values[16] -ne $expected -or $values[2] -ne $before[2] -or
                $values[14] -ne $(if ($kind -eq 3) { 1 } else { 0 })) { throw 'TCP recovery totals mismatch.' }
            $wire = [IO.MemoryStream]::new()
            try {
                $buffer = [byte[]]::new(1024)
                $rawTcp.ReceiveTimeout = 3000
                while (($count = $rawTcp.Receive($buffer)) -gt 0) { $wire.Write($buffer,0,$count) }
                $bytes = $wire.ToArray()
                [SerialCrcValidation]::CheckOutput(($command -lt 10), $bytes)
                [IO.File]::WriteAllBytes((Join-Path $output "tcp-recovery-$command.bin"),$bytes)
            } finally { $wire.Dispose(); $rawTcp.Dispose(); $rawTcp = $null }
            $state.TcpRecoveryCases++
            $state.TcpCrcErrors += $values[16]
            $state.TcpPartialExpiries += $values[14]
            Record $values "tcp-recovery-$command"
        }
        Start-Peer
        $values = Invoke-Mcu 3
        $state.PeerRestarts++
        $before = $values
        $values = Invoke-Mcu 1
        if ($values[0] -ne $before[0]+1 -or $values[2] -ne $before[2]+1456) { throw 'TCP final recovery totals mismatch.' }
        Record $values 'tcp-final-recovery'
        if ($state.TcpRecoveryCases -ne 8 -or $state.TcpCrcErrors -ne 28 -or
            $state.TcpPartialExpiries -ne 2) { throw 'TCP recovery coverage incomplete.' }
    } elseif ($SerialValidation) {
        $state.Status = 'running'
        Save-State
        function Serial-Cycle([string]$phase) {
            if (Test-Path -LiteralPath (Join-Path $output 'stop.request')) { throw 'Operator requested an early stop; restoring original app.' }
            $previous = $script:values
            $script:values = Invoke-Mcu 1
            if ($values[0] -ne $previous[0]+1 -or $values[3] -ne $previous[3]+1344 -or
                $values[4] -ne $previous[4]+448 -or $values[2] -ne $previous[2]+1400) { throw 'Serial cycle totals mismatch.' }
            Record $values $phase
            if ($values[11] -ne 0 -and $values[8] -lt $values[11]) { throw 'Serial normal-cycle retained heap grew.' }
        }
        function Serial-Fault([int]$kind) {
            $before = $script:values
            if ($kind -eq 0 -or $kind -eq 1) {
                $command = if ($kind -eq 0) { 2 } else { 5 }
                $expectedRetries = if ($kind -eq 0) { 2 } else { 0 }
                $script:values = Invoke-Mcu $command 'restart'
                if ($values[7] -ne $kind+4 -or $script:lastSerialRetries -ne $expectedRetries) { throw 'Serial timeout/retry contract failed.' }
                if ($kind -eq 0) { $state.SerialAckTimeouts++ } else { $state.SerialResponseTimeouts++ }
                $phase = if ($kind -eq 0) { 'serial-ack-timeout' } else { 'serial-response-timeout' }
                $expectedMeasurements = $before[2]+56
            } elseif ($kind -eq 2) {
                Stop-Peer
                # COM6 소유권을 바꾸기 전에 취소된 네이티브 I/O 자원을 해제합니다.
                Start-Sleep -Seconds 1
                $script:rawSerial = [IO.Ports.SerialPort]::new('COM6',9600,[IO.Ports.Parity]::Even,8,[IO.Ports.StopBits]::One)
                $rawSerial.RtsEnable = $true; $rawSerial.DtrEnable = $true
                $rawSerial.ParityReplace = 0
                $rawSerial.WriteTimeout = 1000
                for ($attempt = 1; $attempt -le 3; $attempt++) {
                    try { $rawSerial.Open(); break }
                    catch {
                        if ($_.Exception.GetBaseException() -isnot [UnauthorizedAccessException] -or $attempt -eq 3) { throw }
                        Write-Warning "COM6 handover not ready (attempt $attempt): $_"
                        Start-Sleep -Seconds 1
                    }
                }
                $script:values = Invoke-Mcu 4 'partial'
                if ($values[7] -ne 6 -or $values[14] -ne 1) { throw 'Serial partial frame expiry contract failed.' }
                $state.SerialPartialExpiries++
                $phase = 'serial-partial-expiry'
                $expectedMeasurements = $before[2]
                $rawSerial.Dispose()
                $script:rawSerial = $null
                Start-Sleep -Seconds 1
            } else { throw 'Invalid Serial fault kind.' }
            if ($values[6] -ne $before[6]+1 -or $values[0] -ne $before[0] -or
                $values[2] -ne $expectedMeasurements -or $values[3] -ne $before[3] -or
                $values[4] -ne $before[4]) { throw 'Serial fault totals mismatch.' }
            Record $values $phase
            Start-Peer
            $script:values = Invoke-Mcu 3
            $state.PeerRestarts++
            Save-State
        }
        if ($SerialCrc) {
            Serial-Cycle 'serial-cycle'
            Serial-Cycle 'serial-cycle'
            Stop-Peer
            Start-Sleep -Seconds 1
            $rawSerial = [IO.Ports.SerialPort]::new('COM6',9600,[IO.Ports.Parity]::Even,8,[IO.Ports.StopBits]::One)
            $rawSerial.RtsEnable = $true; $rawSerial.DtrEnable = $true; $rawSerial.ParityReplace = 0
            $rawSerial.WriteTimeout = 1000
            $rawSerial.Open()
            for ($command = 6; $command -le 11; $command++) {
                if (Test-Path -LiteralPath (Join-Path $output 'stop.request')) { throw 'Operator requested early stop.' }
                $rawSerial.DiscardInBuffer()
                $before = $values
                $values = Invoke-Mcu $command 'crc'
                $expected = if (($command - 6) % 3 -eq 2) { 6 } else { 4 }
                if ($values[6] -ne $before[6]+1 -or $values[7] -ne $command+1 -or
                    $values[16] -ne $expected -or $values[2] -ne $before[2]) { throw 'CRC fault totals mismatch.' }
                $wire = [byte[]]::new($rawSerial.BytesToRead)
                $received = 0
                while ($received -lt $wire.Length) { $received += $rawSerial.Read($wire,$received,$wire.Length-$received) }
                [SerialCrcValidation]::CheckOutput(($command -lt 9), $wire)
                $role = if ($command -lt 9) { 'master' } else { 'outstation' }
                $kind = @('header','data','mixed')[($command-6)%3]
                [IO.File]::WriteAllBytes((Join-Path $output "crc-$role-$kind.bin"), $wire)
                $state.SerialCrcCases++
                $state.SerialCrcErrors += $values[16]
                Record $values "serial-crc-$role-$kind"
            }
            $rawSerial.Dispose(); $rawSerial = $null
            Start-Sleep -Seconds 1
            Start-Peer
            $values = Invoke-Mcu 3
            $state.PeerRestarts++
            Serial-Cycle 'serial-final-recovery'
            if ($state.SerialCrcCases -ne 6 -or $state.SerialCrcErrors -ne 28) { throw 'CRC coverage incomplete.' }
        } elseif ($SerialSoak) {
            $clock = [Diagnostics.Stopwatch]::StartNew()
            $state.DeadlineUtc = [DateTime]::UtcNow.AddMinutes($DurationMinutes).ToString('o')
            Save-State
            while ($clock.Elapsed.TotalMinutes -lt $DurationMinutes) {
                Serial-Cycle 'serial-cycle'
                if ($values[0] -ge 2 -and $values[0] % $FaultEveryCycles -eq 0) {
                    Serial-Fault ($state.Faults % 3)
                }
                Start-Sleep -Seconds $CyclePauseSeconds
            }
            if ($state.SerialAckTimeouts -lt 1 -or $state.SerialResponseTimeouts -lt 1 -or
                $state.SerialPartialExpiries -lt 1) {
                throw 'Duration ended without all three Serial fault modes; use a longer duration or shorter fault interval.'
            }
            Serial-Cycle 'serial-final-recovery'
        } else {
            Serial-Cycle 'serial-cycle'
            Serial-Cycle 'serial-cycle'
            Serial-Fault 0
            Serial-Cycle 'serial-recovery'
            Serial-Fault 1
            Serial-Cycle 'serial-recovery'
            Serial-Fault 2
            Serial-Cycle 'serial-final-recovery'
            if ($values[0] -ne 5 -or $values[6] -ne 3) { throw 'Serial final totals mismatch.' }
        }
    } else {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $state.DeadlineUtc = [DateTime]::UtcNow.AddMinutes($DurationMinutes).ToString('o')
    $state.Status = 'running'
    Save-State
    while ($clock.Elapsed.TotalMinutes -lt $DurationMinutes) {
        if (Test-Path -LiteralPath (Join-Path $output 'stop.request')) { throw 'Operator requested an early stop; restoring original app.' }
        $previous = $values
        $values = Invoke-Mcu 1
        if ($values[0] -ne $previous[0]+1 -or $values[3] -ne $previous[3]+1344 -or
            $values[4] -ne $previous[4]+448 -or $values[2] -ne $previous[2]+1456) { throw 'Cycle totals mismatch.' }
        Record $values 'cycle'
        if ($values[11] -ne 0 -and $values[8] -lt $values[11]) { throw 'Post-warmup retained managed heap grew.' }
        if ($values[0] -ge 2 -and $values[0] % $FaultEveryCycles -eq 0) {
            $fault = if (($state.Faults % 2) -eq 0) { 'restart' } else { 'drop' }
            $beforeFaults = $values[6]
            $values = Invoke-Mcu 2 $fault
            if ($values[6] -ne $beforeFaults+1 -or $values[7] -notin @(1,2,3)) { throw 'Expected request/session failure was not observed.' }
            Record $values $fault
            if ($fault -eq 'restart') {
                Start-Peer
                $values = Invoke-Mcu 3
                $state.PeerRestarts++
            } else { $state.TcpDrops++ }
            Save-State
        }
        Start-Sleep -Seconds $CyclePauseSeconds
    }
    if ($state.PeerRestarts -lt 1 -or $state.TcpDrops -lt 1) { throw 'Duration ended without both fault modes; use a longer duration or shorter fault interval.' }
    # 지정 시간이 막 지났더라도 마지막 장애 주입 뒤의 복구는 확인합니다.
    $previous = $values
    $values = Invoke-Mcu 1
    if ($values[0] -ne $previous[0]+1 -or $values[3] -ne $previous[3]+1344 -or
        $values[4] -ne $previous[4]+448 -or $values[2] -ne $previous[2]+1456) { throw 'Final recovery totals mismatch.' }
    Record $values 'final-recovery'
    if ($values[8] -lt $values[11]) { throw 'Final retained heap grew.' }
    }
    $state.Status = 'completed'
}
catch {
    $state.Status = 'failed'
    $state.Error = $_.ToString()
    throw
}
finally {
    $cleanupErrors = [Collections.Generic.List[string]]::new()
    if ($udp) { $udp.Dispose() }
    if ($evictor) { $evictor.Dispose() }
    if ($rawSerial) { $rawSerial.Dispose() }
    if ($rawTcp) { $rawTcp.Dispose() }
    try { Stop-Peer } catch { $cleanupErrors.Add($_.ToString()) }
    if ($changed) {
        $state.Status = if ($state.Status -eq 'completed') { 'restoring' } else { $state.Status }
        try { Save-State } catch { $cleanupErrors.Add($_.ToString()) }
        Start-Sleep -Seconds 5
        try {
            try { & (Join-Path $PSScriptRoot 'Restore-ModbusHardware.ps1') -LogPath (Join-Path $output 'restored-modbus.log') }
            catch {
                Write-Warning "Restore failed; retrying once: $_"
                Start-Sleep -Seconds 5
                & (Join-Path $PSScriptRoot 'Restore-ModbusHardware.ps1') -LogPath (Join-Path $output 'restored-modbus-retry.log')
            }
            $state.Restored = $true
        }
        catch { $cleanupErrors.Add($_.ToString()) }
    }
    if ($cleanupErrors.Count) {
        $state.Status = 'failed'
        $state.Error = ($state.Error,($cleanupErrors -join "`n") | Where-Object { $_ }) -join "`n"
    } elseif ($state.Status -eq 'restoring') { $state.Status = 'completed' }
    $state.FinishedUtc = [DateTime]::UtcNow.ToString('o')
    Save-State
    if ($cleanupErrors.Count) { throw ($cleanupErrors -join "`n") }
}
Write-Output "DNP3 MASTER SOAK PASS: original Modbus restored; $output"
