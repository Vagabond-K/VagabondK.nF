#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][switch]$AllowDeviceChanges,
    [switch]$MalformedRecovery,
    [string]$DeviceAddress = '192.168.0.187',
    [string]$OutputPath = '',
    [string]$MSBuildPath = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe'
)
$ErrorActionPreference='Stop'
if (-not $AllowDeviceChanges) { throw 'Use only the isolated COM3 evaluation board.' }
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$address=[Net.IPAddress]::Parse($DeviceAddress)
if ($address.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) { throw 'Device IPv4 required.' }
$restore=Join-Path $root 'bin\hardware\restore-Modbus.SerialServer.bin'
if ((Get-FileHash -LiteralPath $restore).Hash -ne '66C51145509667EA4019649750B42CAD47344CE76D9736994B9658E0F45226A7') { throw 'Restore checksum mismatch.' }
if ([IO.Ports.SerialPort]::GetPortNames() -notcontains 'COM3') { throw 'COM3 unavailable.' }
$native=Join-Path $root 'bin\protocol-tools\iec61850-build\Release\VagabondKMmsPeer.exe'
if ((& $native --identity) -ne 'libiec61850 v1.6.2 ORIGINAL f0f797bf955da763353f7081c892a1ff6b55e5a1' -or $LASTEXITCODE -ne 0) { throw 'Unexpected native peer.' }
$source=Join-Path $root 'bin\protocol-tools\libiec61850'
$revision=& git -C $source rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $revision -ne 'f0f797bf955da763353f7081c892a1ff6b55e5a1') { throw 'Unexpected native revision.' }
& git -C $source diff --quiet $revision --
if ($LASTEXITCODE -ne 0) { throw 'Independent MMS source modified.' }
if (-not $OutputPath) { $OutputPath=Join-Path $root ('bin\iec61850-server-hardware-'+(Get-Date -Format 'yyyyMMdd-HHmmss')) }
$output=[IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $output) { throw 'Choose a fresh output folder.' }
$null=New-Item -ItemType Directory -Path $output
$state=[ordered]@{Status='preparing';Restored=$false;Error=$null;Device=$DeviceAddress;Mode=$(if ($MalformedRecovery) { 'MalformedRecovery' } else { 'Full' });LastMetrics=$null}
$changed=$false; $udp=$null; $process=$null
function Save-State { $state | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'status.json') }
function U32([byte[]]$b,[int]$p) { return ([uint32]$b[$p] -shl 24) -bor ([uint32]$b[$p+1] -shl 16) -bor ([uint32]$b[$p+2] -shl 8) -bor [uint32]$b[$p+3] }
function Metrics([byte]$command=0) {
    $deadline=[Environment]::TickCount64+120000
    while ([Environment]::TickCount64 -lt $deadline) {
        $q=[byte[]]@(77,83,83,54,$command)
        $null=$udp.Send($q,$q.Length)
        $remote=[Net.IPEndPoint]::new([Net.IPAddress]::Any,0)
        try { $b=$udp.Receive([ref]$remote) }
        catch [Net.Sockets.SocketException] {
            if ($_.Exception.SocketErrorCode -notin @([Net.Sockets.SocketError]::TimedOut,[Net.Sockets.SocketError]::ConnectionReset)) { throw }
            continue
        }
        if ($b.Length -lt 144 -or ($b[0..3] -join ',') -ne '77,83,83,54') { throw 'Invalid Server diagnostic datagram.' }
        $status=U32 $b 4
        if ($status -eq 3) { throw ('MCU failure: '+[Text.Encoding]::UTF8.GetString($b,144,$b.Length-144)) }
        $v=[uint32[]]::new(34)
        for($i=0;$i -lt 34;$i++) { $v[$i]=U32 $b (8+4*$i) }
        if ($v[19] -ne 0x600d -or $v[16] -ne 0) { throw 'Server magic/debugger mismatch.' }
        return [pscustomobject]@{Status=$status;Values=$v}
    }
    throw 'Server diagnostic timeout.'
}
function Wait-Step([int]$step) {
    $deadline=[Environment]::TickCount64+30000
    do {
        $result=Metrics
        if ($result.Values[0] -gt $step) { throw 'Unexpected Server step count.' }
        $finalStep=if ($MalformedRecovery) { 28 } else { 15 }
        if ($result.Values[0] -eq $step -and ($step -ne $finalStep -or $result.Status -eq 2)) {
            $v=$result.Values
            $record=[ordered]@{Step=$v[0];Checks=$v[1];Reads=$v[2];Malformed=$v[3];Timeouts=$v[4];Heap=$v[5];BaselineHeap=$v[6]
                MinimumHeap=$v[7];MaximumHeap=$v[8];SessionBytes=$v[9];AssociationMilliseconds=$v[10]
                ReadTotalMilliseconds=$v[11];ReadMaximumMilliseconds=$v[12];Associations=$v[13]
                TimeoutWaitMilliseconds=$v[14];TimeoutTotalMilliseconds=$v[15];ModelSequence=$v[17];Status=$result.Status
                ControlValidations=$v[20];ControlExecutions=$v[21];ControlFailures=$v[22]
                ProcessingTotalMilliseconds=$v[23];ProcessingMaximumMilliseconds=$v[24];IoTotalMilliseconds=$v[25]
                EnhancedValidations=$v[26];EnhancedExecutions=$v[27];EnhancedTerminations=$v[28]
                SboEnhancedValidations=$v[29];SboEnhancedExecutions=$v[30];SboEnhancedTerminations=$v[31]
                DataRequests=$v[32];Reports=$v[33]}
            $state.LastMetrics=$record
            $record | ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $output 'measurements.jsonl')
            Save-State
            Write-Output ($record | ConvertTo-Json -Compress)
            return
        }
        Start-Sleep -Milliseconds 100
    } while ([Environment]::TickCount64 -lt $deadline)
    throw "Server step $step timeout."
}
function Native-Read([int]$sequence, [switch]$Stress, [switch]$Enhanced, [switch]$SboEnhanced, [switch]$SboStress, [switch]$Data) {
    $arguments=if ($Data) { "dataclient 18103 $DeviceAddress" } elseif ($SboStress) { "sboenhancedstress 18103 $DeviceAddress 256" } elseif ($SboEnhanced) { "sboenhancedclient 18103 $DeviceAddress" } elseif ($Enhanced) { "enhancedclient 18103 $DeviceAddress" } elseif ($Stress) { "controlstress 18103 $DeviceAddress 256" } else { "controlverify 18103 $DeviceAddress $sequence" }
    $start=[Diagnostics.ProcessStartInfo]::new($native,$arguments)
    $start.UseShellExecute=$false; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $script:process=[Diagnostics.Process]::Start($start)
    $out=$process.StandardOutput.ReadToEndAsync(); $err=$process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(120000)) { throw 'Native read timeout.' }
    $text=$out.GetAwaiter().GetResult(); $errorText=$err.GetAwaiter().GetResult()
    $text | Set-Content -LiteralPath (Join-Path $output "native-$sequence.log")
    $errorText | Set-Content -LiteralPath (Join-Path $output "native-$sequence-error.log")
    $expected=if ($Data) { 'NATIVE DATA REPORT PASS' } elseif ($SboStress) { 'NATIVE SBO ENHANCED STRESS PASS repetitions=256' } elseif ($SboEnhanced) { 'NATIVE SBO ENHANCED PASS' } elseif ($Enhanced) { 'NATIVE ENHANCED PASS' } elseif ($Stress) { 'NATIVE CONTROL STRESS PASS repetitions=256 operations=512' } else { "NATIVE SERVER VERIFY PASS sequence=$sequence scalar=30 access=10" }
    if ($process.ExitCode -ne 0 -or $text -notmatch $expected) { throw "Native read failed: $errorText" }
    $process.Dispose(); $script:process=$null
}
Save-State
try {
    & dotnet run -c Release --project (Join-Path $root 'Tests\IEC61850\VagabondK.nF.IEC61850.Tests\VagabondK.nF.IEC61850.Tests.csproj') -- --interop $native *>&1 |
        Tee-Object -FilePath (Join-Path $output 'pc-tests.log')
    if ($LASTEXITCODE -ne 0) { throw 'PC Server interop failed.' }
    $project=Join-Path $root 'Tests\IEC61850\VagabondK.nF.IEC61850.Server.Hardware\VagabondK.nF.IEC61850.Server.Hardware.nfproj'
    & $MSBuildPath $project /t:Rebuild /p:Configuration=Release /warnaserror /v:quiet /nologo *>&1 |
        Tee-Object -FilePath (Join-Path $output 'build.log')
    if ($LASTEXITCODE -ne 0) { throw 'MCU Server build failed.' }
    $built=Join-Path $root 'Tests\IEC61850\VagabondK.nF.IEC61850.Server.Hardware\bin\Release'
    $image=Join-Path $built 'VagabondK.nF.IEC61850.Server.Hardware.bin'
    Copy-Item -LiteralPath $image -Destination (Join-Path $output 'deployed-image.bin')
    $sizes=[ordered]@{ImageBytes=(Get-Item -LiteralPath $image).Length;SHA256=(Get-FileHash -LiteralPath $image).Hash
        CorePEBytes=(Get-Item -LiteralPath (Join-Path $built 'VagabondK.nF.IEC61850.pe')).Length
        ServerPEBytes=(Get-Item -LiteralPath (Join-Path $built 'VagabondK.nF.IEC61850.Server.pe')).Length
        MmsCorePEBytes=(Get-Item -LiteralPath (Join-Path $built 'VagabondK.nF.MMS.pe')).Length
        MmsServerPEBytes=(Get-Item -LiteralPath (Join-Path $built 'VagabondK.nF.MMS.Server.pe')).Length
        TcpPEBytes=(Get-Item -LiteralPath (Join-Path $built 'VagabondK.nF.MMS.TCP.pe')).Length
        AppPEBytes=(Get-Item -LiteralPath (Join-Path $built 'VagabondK.nF.IEC61850.Server.Hardware.pe')).Length
        PeerSHA256=(Get-FileHash -LiteralPath $native).Hash}
    $sizes | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'sizes.json')
    $changed=$true
    for ($attempt=1;$attempt -le 3;$attempt++) {
        & (Join-Path $env:USERPROFILE '.dotnet\tools\nanoff.exe') --nanodevice --deploy --serialport COM3 --image $image --suppressnanoffversioncheck *>&1 |
            Tee-Object -FilePath (Join-Path $output "deploy-$attempt.log")
        if ($LASTEXITCODE -eq 0) { break }
        if ($attempt -eq 3) { throw 'Server deployment failed.' }
        Write-Warning 'Deployment failed; retrying in 10 seconds.'
        Start-Sleep -Seconds 10
    }
    $udp=[Net.Sockets.UdpClient]::new(); $udp.Client.ReceiveTimeout=2000; $udp.Connect($DeviceAddress,20004)
    $ready=Metrics
    if ($ready.Status -ne 0) { throw 'Server not in initial state.' }
    $state.Status='running'; Save-State
    if ($MalformedRecovery) {
        $null=Metrics 2
        $assembly=Join-Path $root 'Tests\IEC61850\VagabondK.nF.IEC61850.Tests\bin\Release\net10.0\VagabondK.nF.IEC61850.Tests.dll'
        for ($scenario=0;$scenario -lt 14;$scenario++) {
            & dotnet $assembly --hardware-fault $DeviceAddress $scenario (Join-Path $output "fault-$scenario.bin") *>&1 |
                Tee-Object -FilePath (Join-Path $output "fault-$scenario.log")
            if ($LASTEXITCODE -ne 0) {
                $diagnostic=Metrics
                throw "Raw fault peer failed: $scenario; MCU status=$($diagnostic.Status), step=$($diagnostic.Values[0])."
            }
            Wait-Step (2*$scenario+1)
            if ($state.LastMetrics.Malformed -ne [Math]::Min($scenario+1,13) -or
                $state.LastMetrics.ControlExecutions -ne (2*$scenario + $(if ($scenario -eq 13) { 1 } else { 0 }))) {
                throw 'Malformed input/control execution count mismatch.'
            }
            Native-Read ($scenario+1)
            Wait-Step (2*$scenario+2)
        }
        $last=$state.LastMetrics
        if ($last.Status -ne 2 -or $last.Malformed -ne 13 -or $last.Associations -ne 14 -or
            $last.Reads -ne 1344 -or $last.ControlExecutions -ne 29 -or $last.ControlValidations -ne 29) {
            throw 'Malformed recovery totals mismatch.'
        }
        $records=Get-Content -LiteralPath (Join-Path $output 'measurements.jsonl') | ForEach-Object { $_ | ConvertFrom-Json }
        foreach ($record in $records | Select-Object -Skip 2) {
            if ($record.Heap -lt $records[1].Heap) { throw 'Heap retained after malformed recovery warmup.' }
        }
    } else {
    $null=Metrics 1
    Native-Read 1; Wait-Step 1
    Native-Read 2; Wait-Step 2
    foreach ($cycle in @(0,1)) {
        $socket=[Net.Sockets.TcpClient]::new()
        try {
            $socket.Connect($address,18103)
            $bad=[Convert]::FromHexString('0400001611E00000000100C00107C2020001C1020001')
            $socket.GetStream().Write($bad,0,$bad.Length)
            Wait-Step (3+$cycle*3)
        }
        finally { $socket.Dispose() }
        $socket=[Net.Sockets.TcpClient]::new()
        try {
            $socket.Connect($address,18103)
            Wait-Step (4+$cycle*3)
        }
        finally { $socket.Dispose() }
        Native-Read (3+$cycle); Wait-Step (5+$cycle*3)
    }
    Native-Read 5 -Stress; Wait-Step 9
    Native-Read 6 -Enhanced; Wait-Step 10
    Native-Read 7 -SboEnhanced; Wait-Step 11
    Native-Read 8 -SboStress; Wait-Step 12
    $dataHeap=0
    for ($dataCycle=1;$dataCycle -le 3;$dataCycle++) {
        Native-Read (8+$dataCycle) -Data; Wait-Step (12+$dataCycle)
        if ($dataCycle -eq 1) { $dataHeap=$state.LastMetrics.Heap }
        elseif ($state.LastMetrics.Heap -lt $dataHeap) { throw 'Data/report retained heap grew.' }
    }
    $last=$state.LastMetrics
    if ($last.Status -ne 2 -or $last.Reads -ne 2503 -or $last.Associations -ne 11 -or $last.Malformed -ne 2 -or
        $last.Timeouts -ne 2 -or $last.ModelSequence -ne 4 -or $last.ControlValidations -ne 524 -or
        $last.ControlExecutions -ne 522 -or $last.ControlFailures -ne 2 -or $last.EnhancedValidations -ne 4 -or
        $last.EnhancedExecutions -ne 3 -or $last.EnhancedTerminations -ne 4 -or $last.SboEnhancedValidations -ne 522 -or
        $last.SboEnhancedExecutions -ne 259 -or $last.SboEnhancedTerminations -ne 260 -or
        $last.DataRequests -ne 27 -or $last.Reports -ne 3) { throw 'Server final totals mismatch.' }
    $records=Get-Content -LiteralPath (Join-Path $output 'measurements.jsonl') | ForEach-Object { $_ | ConvertFrom-Json }
    if ($last.Heap -lt $records[3].Heap -or $records[6].Heap -lt $records[3].Heap -or
        $last.Heap -lt $records[10].Heap) { throw 'Heap retained after stress/fault recovery.' }
    }
    $state.Status='completed'
}
catch { $state.Status='failed';$state.Error=$_.ToString();throw }
finally {
    $cleanup=[Collections.Generic.List[string]]::new()
    if ($udp) { $udp.Dispose() }
    if ($process) {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id; $process.WaitForExit() }
        $process.Dispose()
    }
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
Write-Output "MMS SERVER HARDWARE PASS: restored original Modbus; $output"
