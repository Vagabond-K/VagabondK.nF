#Requires -Version 7.0
param(
    [Parameter(Mandatory)][ValidateSet('Serial','Tcp')][string]$Transport,
    [ValidateSet('Master','Outstation')][string]$PeerRole = 'Outstation',
    [ValidateSet('OpenDnp3','GoNumeric')][string]$Implementation = 'OpenDnp3'
)
$ErrorActionPreference = 'Stop'
if ($Implementation -eq 'GoNumeric' -and $Transport -ne 'Serial') {
    throw 'GoNumeric hardware mode requires direct Serial; use Test-Dnp3ExtraPeers.ps1 for PC TCP.'
}
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$logs = Join-Path $root 'bin\dnp3-hardware'
$imageName = if ($Implementation -eq 'GoNumeric') {
    if ($PeerRole -eq 'Master') { 'Dnp3.NumericOutstation.bin' } else { 'Dnp3.NumericMaster.bin' }
} elseif ($PeerRole -eq 'Master') {
    if ($Transport -eq 'Serial') { 'Dnp3.Outstation-boundaries.bin' } else { 'Dnp3.TcpOutstation-boundaries.bin' }
} else {
    if ($Transport -eq 'Serial') { 'Dnp3.Master-independent.bin' } else { 'Dnp3.TcpMaster.bin' }
}
$image = Join-Path $logs $imageName
$nanoff = Join-Path $env:USERPROFILE '.dotnet\tools\nanoff.exe'
$toolFolder = Get-ChildItem (Join-Path $env:USERPROFILE '.dotnet\tools\.store\nanoff') -Recurse -File -Filter nanoFramework.Tools.DebugLibrary.Net.dll |
    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty DirectoryName
if (-not $toolFolder) { throw 'Install nanoff with its DebugLibrary first.' }
Add-Type -Path (Join-Path $toolFolder 'System.IO.Ports.dll')
Add-Type -Path (Join-Path $toolFolder 'nanoFramework.Tools.DebugLibrary.Net.dll')
$prefix = 'independent-' + $Transport.ToLowerInvariant() + '-' + $PeerRole.ToLowerInvariant()
if ($Implementation -eq 'GoNumeric') { $prefix = 'go-numeric-serial-' + $PeerRole.ToLowerInvariant() }
$stdout = Join-Path $logs ($prefix + '.log')
$stderr = Join-Path $logs ($prefix + '-error.log')
$debug = Join-Path $logs ($prefix + '-mcu.log')
& $nanoff --nanodevice --deploy --serialport COM3 --image $image --suppressnanoffversioncheck
if ($LASTEXITCODE -ne 0) { throw 'MCU image deployment failed.' }
$manager = [nanoFramework.Tools.Debugger.PortSerial.PortSerialManager]::new(
    $false, [System.Collections.Generic.List[string]]::new(), 1000)
$device = $manager.AddDevice('COM3')
$engine = $null
$process = $null
$passed = $false
$source = 'Dnp3IndependentDebug'
try {
    $device.CreateDebugEngine()
    $engine = $device.DebugEngine
    $engine.StopDebuggerOnConnect = $false
    if (-not $engine.Connect()) { throw 'MCU debugger connection failed.' }
    Register-ObjectEvent $engine -EventName OnMessage -SourceIdentifier $source | Out-Null
    $target = if ($Transport -eq 'Serial') { 'COM6' } elseif ($PeerRole -eq 'Master') { '192.168.0.187' } else { '192.168.0.210' }
    $tcpStation = $Transport -eq 'Tcp' -and $PeerRole -eq 'Master'
    if ($tcpStation) {
        $engine.RebootDevice([nanoFramework.Tools.Debugger.RebootOptions]::ClrOnly, $null) | Out-Null
        $networkDeadline = [DateTime]::UtcNow.AddSeconds(60)
        $listening = $false
        while ([DateTime]::UtcNow -lt $networkDeadline -and -not $listening) {
            $client = [System.Net.Sockets.TcpClient]::new()
            try {
                $connect = $client.ConnectAsync($target, 20000)
                if ($connect.Wait(1000) -and $client.Connected) { $listening = $true }
            }
            catch [System.AggregateException] { Write-Output 'Waiting for MCU Wi-Fi TCP listener.' }
            finally { $client.Dispose() }
            if (-not $listening) { Start-Sleep -Seconds 1 }
        }
        if (-not $listening) { throw 'MCU TCP listener was not ready.' }
    }
    $executable = 'dotnet'
    $fixture = Join-Path $root 'Tests\DNP3\VagabondK.nF.DNP3.SerialFixture\bin\Release\net10.0\VagabondK.nF.DNP3.SerialFixture.dll'
    $peerArguments = @($fixture, $target, ('Independent' + $PeerRole))
    if ($Implementation -eq 'GoNumeric') {
        $executable = Join-Path $root 'bin\protocol-tools\extra-peers\go-peer.exe'
        $role = if ($PeerRole -eq 'Master') { 'serial-master' } else { 'serial-station' }
        $peerArguments = @($role, 'COM6')
    }
    $process = Start-Process $executable -ArgumentList $peerArguments `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru -NoNewWindow
    $ready = [DateTime]::UtcNow.AddSeconds(5)
    while ([DateTime]::UtcNow -lt $ready -and (Get-Content $stdout -Raw) -notmatch 'READY') {
        if ($process.HasExited) { throw 'Independent Outstation failed to start.' }
        Start-Sleep -Milliseconds 50
    }
    if ((Get-Content $stdout -Raw) -notmatch 'READY') { throw 'Independent peer was not ready.' }
    if (-not $tcpStation) {
        $engine.RebootDevice([nanoFramework.Tools.Debugger.RebootOptions]::ClrOnly, $null) | Out-Null
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(240)
    $expected = if ($Transport -eq 'Serial') { 'DNP3 MCU MASTER PASS:' } else { 'DNP3 MCU TCP MASTER PASS:' }
    if ($Implementation -eq 'GoNumeric') {
        $expected = if ($PeerRole -eq 'Master') { 'DNP3 MCU NUMERIC OUTSTATION PASS:' } else { 'DNP3 MCU NUMERIC MASTER PASS:' }
    }
    $messages = [System.Collections.Generic.List[string]]::new()
    while ([DateTime]::UtcNow -lt $deadline) {
        $event = Wait-Event -SourceIdentifier $source -Timeout 1
        if ($event) {
            foreach ($arg in $event.SourceArgs) {
                if ($arg -is [string]) {
                    $messages.Add($arg)
                    if ($arg.Contains($expected)) { $passed = $true; Write-Output $arg }
                    if ($arg.Contains('Uncaught exception')) {
                        $messages | Set-Content -LiteralPath $debug
                        throw "MCU raised an unhandled exception. Evidence: $debug"
                    }
                }
            }
            Remove-Event -EventIdentifier $event.EventIdentifier
        }
        if ($passed -and $process.HasExited) { break }
        if ($Implementation -eq 'OpenDnp3' -and $PeerRole -eq 'Master' -and $process.HasExited) {
            $passed = $process.ExitCode -eq 0
            break
        }
        if ($process.HasExited -and $process.ExitCode -ne 0) { break }
    }
    $messages | Set-Content -LiteralPath $debug
    Get-Content $stdout -Tail 10
    Get-Content $stderr -Tail 20
    if (-not $passed) { throw "MCU completion not observed. Evidence: $debug" }
    if (-not $process.HasExited -or $process.ExitCode -ne 0) { throw 'Independent peer did not pass.' }
    if ($Implementation -eq 'GoNumeric') {
        $text = $messages -join "`n"
        $totals = if ($PeerRole -eq 'Master') { 'operations=12;' } else { 'measurements=1008;' }
        if (-not $text.Contains($totals) -or -not $text.Contains('CRCerrors=0; frameTimeouts=0')) {
            throw 'MCU numeric totals or zero CRC/frame-timeout requirement failed.'
        }
        $peerPass = if ($PeerRole -eq 'Master') { 'GO RS485 NUMERIC MASTER PASS:' } else { 'GO RS485 NUMERIC OUTSTATION PASS:' }
        if ((Get-Content $stdout -Raw) -notmatch [regex]::Escape($peerPass)) {
            throw 'Independent Go peer numeric completion marker missing.'
        }
    }
}
finally {
    Unregister-Event -SourceIdentifier $source -ErrorAction SilentlyContinue
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id }
    if ($engine) { $engine.Stop($true) }
    $manager.DisposeDevice('COM3')
}
