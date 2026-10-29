#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][switch]$AllowDeviceChanges,
    [switch]$Client,
    [string]$DeviceAddress = '192.168.0.187',
    [string]$OutputPath = '',
    [string]$MSBuildPath = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe'
)
$ErrorActionPreference = 'Stop'
if (-not $AllowDeviceChanges) { throw 'Use only the isolated COM3 evaluation board.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$restore = Join-Path $root 'bin\hardware\restore-Modbus.SerialServer.bin'
if ((Get-FileHash -LiteralPath $restore).Hash -ne '66C51145509667EA4019649750B42CAD47344CE76D9736994B9658E0F45226A7') { throw 'Restore checksum mismatch.' }
if ([IO.Ports.SerialPort]::GetPortNames() -notcontains 'COM3') { throw 'COM3 unavailable.' }
$role = if ($Client) { 'Client' } else { 'Server' }
if (-not $OutputPath) { $OutputPath = Join-Path $root ('bin\mapping-hardware-' + $role + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$output = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $output) { throw 'Choose a fresh output folder.' }
$null = New-Item -ItemType Directory -Path $output
$state = [ordered]@{ Status = 'preparing'; Role = $role; Epoch = 0; Restored = $false; Error = $null; Metrics = $null }
$udp = $null; $process = $null; $changed = $false
function Save-State { $state | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'status.json') }
function U32([byte[]]$b, [int]$p) { return ([uint32]$b[$p] -shl 24) -bor ([uint32]$b[$p+1] -shl 16) -bor ([uint32]$b[$p+2] -shl 8) -bor [uint32]$b[$p+3] }
function Metrics([byte]$command = 0) {
    $deadline = [Environment]::TickCount64 + 120000
    do {
        $query = [byte[]]@(77,82,83,49,$command); $null = $udp.Send($query,$query.Length)
        $remote = [Net.IPEndPoint]::new([Net.IPAddress]::Any,0)
        try { $b = $udp.Receive([ref]$remote) }
        catch [Net.Sockets.SocketException] {
            if ($_.Exception.SocketErrorCode -notin @([Net.Sockets.SocketError]::TimedOut,[Net.Sockets.SocketError]::ConnectionReset)) { throw }
            continue
        }
        if ($b.Length -lt 48 -or (U32 $b 0) -ne 0x4d525331 -or (U32 $b 36) -ne 0 -or (U32 $b 40) -ne 0x600d) { throw 'Invalid unobserved MCU diagnostics.' }
        $m = [pscustomobject]@{ Status=(U32 $b 4);Sessions=(U32 $b 8);Faults=(U32 $b 12);Reports=(U32 $b 16);Requests=(U32 $b 20);
            Heap=(U32 $b 24);Baseline=(U32 $b 28);Minimum=(U32 $b 32);Epoch=$state.Epoch;
            Iterations=0;IdleMs=0;ChangedMs=0;ApplyMs=0;BenchmarkHeapBefore=0;BenchmarkHeapAfter=0 }
        if ($m.Status -eq 3) { throw ('MCU error: '+[Text.Encoding]::UTF8.GetString($b,48,$b.Length-48)) }
        if ($b.Length -ge 72) {
            $m.Iterations=U32 $b 44; $m.IdleMs=U32 $b 48; $m.ChangedMs=U32 $b 52; $m.ApplyMs=U32 $b 56
            $m.BenchmarkHeapBefore=U32 $b 60; $m.BenchmarkHeapAfter=U32 $b 64
        }
        return $m
    } while ([Environment]::TickCount64 -lt $deadline)
    throw 'Mapping diagnostic timeout.'
}
Save-State
try {
    $pc = Join-Path $root 'Tests\MMS\VagabondK.nF.MMS.Mapping.Tests\VagabondK.nF.MMS.Mapping.Tests.csproj'
    & dotnet build $pc -c Release --nologo -v quiet *> (Join-Path $output 'pc-build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Mapping PC fixture build failed.' }
    $app = "VagabondK.nF.IEC61850.Mapping$role.Hardware"
    $project = Join-Path $root "Tests\IEC61850\$app\$app.nfproj"
    & $MSBuildPath $project /t:Rebuild /p:Configuration=Release /warnaserror /v:quiet /nologo *> (Join-Path $output 'build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Mapping MCU fixture build failed.' }
    $built = Join-Path (Split-Path $project) 'bin\Release'
    $opposite = if ($Client) { 'Server' } else { 'Client' }
    if (@(Get-ChildItem -LiteralPath $built -File | Where-Object {
        $_.Name -match "\.$opposite\.(dll|pe)$" -or $_.Name -like '*CodeAnalysis*' -or
        $_.Name -eq 'VagabondK.nF.IEC61850.Mapping.dll'
    }).Count) { throw 'Unselected role or desktop analyzer leaked into MCU output.' }
    $image = Join-Path $built "$app.bin"
    Copy-Item -LiteralPath $image -Destination (Join-Path $output 'deployed-image.bin')
    Get-ChildItem -LiteralPath $built -File | Where-Object { $_.Extension -in '.pe','.bin' } |
        ForEach-Object { [pscustomobject]@{Name=$_.Name;Bytes=$_.Length;SHA256=(Get-FileHash -LiteralPath $_.FullName).Hash} } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'images.json')
    $pcSnapshot = Join-Path $output 'pc'
    Copy-Item -LiteralPath (Join-Path $root 'Tests\MMS\VagabondK.nF.MMS.Mapping.Tests\bin\Release\net10.0') -Destination $pcSnapshot -Recurse
    $dll = Join-Path $pcSnapshot 'VagabondK.nF.MMS.Mapping.Tests.dll'
    for ($epoch = 1; $epoch -le 2; $epoch++) {
        $state.Epoch=$epoch; $state.Status='deploying'; Save-State
        $changed=$true
        & (Join-Path $env:USERPROFILE '.dotnet\tools\nanoff.exe') --nanodevice --deploy --serialport COM3 --image $image --suppressnanoffversioncheck *> (Join-Path $output "deploy-$epoch.log")
        if ($LASTEXITCODE -ne 0) { throw 'Mapping MCU deployment failed.' }
        if ($udp) { $udp.Dispose() }
        $udp=[Net.Sockets.UdpClient]::new(); $udp.Client.ReceiveTimeout=2000; $udp.Connect($DeviceAddress,20005)
        $m=Metrics
        if ($m.Status -ne 0 -or $m.Sessions -ne 0) { throw 'MCU did not restart cleanly.' }
        if ($Client) {
            $start=[Diagnostics.ProcessStartInfo]::new('dotnet',"`"$dll`" --mapping-client 18105")
            $start.UseShellExecute=$false; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
            $process=[Diagnostics.Process]::Start($start)
            $ready=$process.StandardOutput.ReadLineAsync()
            if (-not $ready.Wait(10000) -or $ready.Result -ne 'READY') { throw 'PC mapped Server not ready.' }
            $null=Metrics 1
        } else {
            $null=Metrics 1; Start-Sleep -Milliseconds 500
            $process=Start-Process dotnet -ArgumentList @("`"$dll`"",'--mapping-server',$DeviceAddress,'18104','16') -PassThru -NoNewWindow `
                -RedirectStandardOutput (Join-Path $output "peer-$epoch.log") -RedirectStandardError (Join-Path $output "peer-$epoch-error.log")
        }
        $state.Status='running'; Save-State
        $deadline=[Environment]::TickCount64+600000
        do {
            Start-Sleep -Seconds 2
            $m=Metrics; $state.Metrics=$m; Save-State
            $m | ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $output 'measurements.jsonl')
            if ($m.Baseline -gt 0 -and $m.Minimum+512 -lt $m.Baseline) { throw 'Post-warmup heap fell by more than 512 bytes.' }
            if ([Environment]::TickCount64 -ge $deadline) { throw 'Mapping epoch exceeded ten-minute bound.' }
        } while (-not $process.HasExited)
        $process.WaitForExit()
        if ($Client) {
            $process.StandardOutput.ReadToEnd() | Set-Content -LiteralPath (Join-Path $output "peer-$epoch.log")
            $process.StandardError.ReadToEnd() | Set-Content -LiteralPath (Join-Path $output "peer-$epoch-error.log")
        }
        $expected=if ($Client) { 'MAPPING CLIENT PEER PASS' } else { 'MAPPING HOST PASS' }
        if ($process.ExitCode -ne 0 -or (Get-Content -LiteralPath (Join-Path $output "peer-$epoch.log") -Raw) -notmatch $expected) { throw 'Mapping PC peer failed.' }
        $process.Dispose(); $process=$null
        Start-Sleep -Seconds 1; $m=Metrics
        $expectedSessions = if ($Client) { 16 } else { 17 }
        $expectedFaults = if ($Client) { 0 } else { 1 }
        if ($m.Sessions -ne $expectedSessions -or $m.Requests -lt 1000 -or $m.Reports -lt 16 -or $m.Faults -ne $expectedFaults -or $m.Baseline -eq 0) { throw 'Incomplete mapping coverage.' }
        if ($m.Iterations -ne 1000 -or $m.BenchmarkHeapBefore -eq 0 -or $m.BenchmarkHeapAfter+128 -lt $m.BenchmarkHeapBefore) { throw 'Missing benchmark or retained heap regression.' }
        if (-not $Client) {
            $null=Metrics 2
            $deadline=[Environment]::TickCount64+10000
            do { Start-Sleep -Milliseconds 100; $m=Metrics } while ($m.Status -ne 2 -and [Environment]::TickCount64 -lt $deadline)
        }
        if ($m.Status -ne 2) { throw 'Mapping MCU did not stop cleanly.' }
        $state.Metrics=$m; Save-State
        Write-Output "MAPPING MCU $role epoch=$epoch sessions=$($m.Sessions) requests=$($m.Requests) reports=$($m.Reports) heap=$($m.Heap) baseline=$($m.Baseline) minimum=$($m.Minimum)"
    }
    $state.Status='completed'
}
catch { $state.Status='failed'; $state.Error=$_.ToString(); throw }
finally {
    if ($process) { if (-not $process.HasExited) { Stop-Process -Id $process.Id; $process.WaitForExit() }; $process.Dispose() }
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
Write-Output "MAPPING $role HARDWARE PASS: two app lifetimes and 32 completed associations; restored original Modbus; $output"
