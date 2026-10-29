#Requires -Version 7.0
# 보드(COM3 전용)에서 실제 생성 MMS/IEC 61850 매핑 바인딩과 독립 libiec61850 v1.6.2 peer(원본 무수정)를 양방향으로 맞대는 시험입니다.
# MMS 일부 리포트는 peer 래퍼의 직접 인코딩이며 외부 라이브러리 인코더 검증과 구분합니다.
# 한 번 배포한 뒤 IEC Server → IEC Client → MMS Server → MMS Client 역할을 순서대로 실행하고, 종료 시 항상 원래 Modbus 이미지를 복원합니다.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][switch]$AllowDeviceChanges,
    [ValidateRange(1, 20)][int]$Iterations = 4,
    [string]$HostAddress = '192.168.0.210',
    [string]$DeviceAddress = '192.168.0.187',
    [string]$OutputPath = '',
    [string]$MSBuildPath = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe'
)
$ErrorActionPreference = 'Stop'
if (-not $AllowDeviceChanges) { throw 'Use only the isolated COM3 evaluation board.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$ip = [Net.IPAddress]::Parse($HostAddress)
if ($ip.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or $HostAddress -eq '0.0.0.0' -or $HostAddress -eq '127.0.0.1') { throw 'Explicit LAN IPv4 required.' }
if (-not (Get-NetIPAddress -AddressFamily IPv4 | Where-Object IPAddress -eq $HostAddress)) { throw 'Host LAN address is not configured.' }
$restore = Join-Path $root 'bin\hardware\restore-Modbus.SerialServer.bin'
if ((Get-FileHash -LiteralPath $restore).Hash -ne '66C51145509667EA4019649750B42CAD47344CE76D9736994B9658E0F45226A7') { throw 'Restore checksum mismatch.' }
if ([IO.Ports.SerialPort]::GetPortNames() -notcontains 'COM3') { throw 'COM3 unavailable.' }
if (-not $OutputPath) { $OutputPath = Join-Path $root ('bin\mapping-interop-hardware-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$output = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $output) { throw 'Choose a fresh output folder.' }
$null = New-Item -ItemType Directory -Path $output
$state = [ordered]@{ Status = 'preparing'; Role = $null; Iterations = $Iterations; Restored = $false; Error = $null; Metrics = $null; Results = @() }
$udp = $null; $changed = $false
$processes = [Collections.Generic.List[Diagnostics.Process]]::new()
$roles = @{ 1 = 'IEC Server'; 2 = 'IEC Client'; 3 = 'MMS Server'; 4 = 'MMS Client' }
function Save-State { $state | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'status.json') }
function U32([byte[]]$b, [int]$p) { return ([uint32]$b[$p] -shl 24) -bor ([uint32]$b[$p+1] -shl 16) -bor ([uint32]$b[$p+2] -shl 8) -bor [uint32]$b[$p+3] }
function Metrics([byte]$command = 0, [byte]$role = 0) {
    # 보드가 바빠 응답이 수신 제한 시간을 넘기면 늦은 응답이 소켓에 남습니다. 순번이 일치하는 응답만 받아들입니다.
    $script:sequence++
    $deadline = [Environment]::TickCount64 + 120000
    do {
        $s = [uint32]$script:sequence
        $query = [byte[]]@(77,73,79,49,$command,$role,[byte]$Iterations,[byte]($s -shr 24),[byte](($s -shr 16) -band 255),[byte](($s -shr 8) -band 255),[byte]($s -band 255))
        $null = $udp.Send($query, $query.Length)
        $resend = [Environment]::TickCount64 + 2000
        while ([Environment]::TickCount64 -lt $resend) {
            $remote = [Net.IPEndPoint]::new([Net.IPAddress]::Any, 0)
            try { $b = $udp.Receive([ref]$remote) }
            catch [Net.Sockets.SocketException] {
                if ($_.Exception.SocketErrorCode -notin @([Net.Sockets.SocketError]::TimedOut, [Net.Sockets.SocketError]::ConnectionReset)) { throw }
                continue
            }
            if ($b.Length -lt 68 -or (U32 $b 0) -ne 0x4d494f31 -or (U32 $b 36) -ne 0 -or (U32 $b 40) -ne 0x600d) { throw 'Invalid unobserved MCU diagnostics.' }
            if ((U32 $b 64) -ne $s) { $script:staleReplies++; continue }
            $m = [pscustomobject]@{ Status=(U32 $b 4); Sessions=(U32 $b 8); Faults=(U32 $b 12); Reports=(U32 $b 16); Requests=(U32 $b 20)
                Heap=(U32 $b 24); Baseline=(U32 $b 28); Minimum=(U32 $b 32); Role=(U32 $b 44); Writes=(U32 $b 48)
                Checks=(U32 $b 52); Rejected=(U32 $b 56); Applied=(U32 $b 60) }
            if ($m.Status -eq 3) { throw ('MCU error: ' + [Text.Encoding]::UTF8.GetString($b, 68, $b.Length - 68)) }
            return $m
        }
    } while ([Environment]::TickCount64 -lt $deadline)
    throw 'Interop diagnostic timeout.'
}
# native map server의 stderr 시각 추적(수신 write·보고 송신·연결)은 실행 내내 계속 비워야 파이프가 차서 peer가 멈추지 않으며,
# 실패로 강제 종료해도 finally에서 그대로 저장합니다.
$streams = @{}
function Start-Tool([string]$file, [string]$arguments, [string]$name) {
    $start = [Diagnostics.ProcessStartInfo]::new($file, $arguments)
    $start.UseShellExecute = $false; $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($start); $processes.Add($process)
    $streams[$process.Id] = [pscustomobject]@{ Name = $name; Out = $null; Err = $process.StandardError.ReadToEndAsync(); Saved = $false }
    return $process
}
function Read-Tool([Diagnostics.Process]$process) {
    $s = $streams[$process.Id]
    if (-not $s.Out) { $s.Out = $process.StandardOutput.ReadToEndAsync() }
    return $s
}
function Save-Tool($s) {
    $text = ''
    foreach ($task in $s.Out, $s.Err) { if ($task -and $task.Wait(3000)) { $text += $task.Result } }
    $text | Set-Content -LiteralPath (Join-Path $output "$($s.Name).log"); $s.Saved = $true
    return $text
}
function Complete-Tool([Diagnostics.Process]$process, [int]$milliseconds) {
    $s = Read-Tool $process
    if (-not $process.WaitForExit($milliseconds)) { throw "$($s.Name) exceeded its bound." }
    $process.WaitForExit()
    $text = Save-Tool $s
    if ($process.ExitCode -ne 0) { throw "$($s.Name) failed: $text" }
    return $text
}
function Check-Heap($m) {
    if ($m.Baseline -eq 0 -or $m.Minimum + 1024 -lt $m.Baseline) { throw "Post-warmup heap fell by more than 1024 bytes or baseline missing: $($m | ConvertTo-Json -Compress)" }
}
Save-State
try {
    & (Join-Path $PSScriptRoot 'Test-MappingInterop.ps1') -Iterations 1 -OutputPath (Join-Path $output 'pc-loopback') *> (Join-Path $output 'pc-loopback.log')
    if ((Get-Content -LiteralPath (Join-Path $output 'pc-loopback.log') -Raw) -notmatch 'MAPPING INTEROP PC PASS iterations=1 ') { throw 'PC loopback interop gate failed.' }
    $native = Join-Path $root 'bin\protocol-tools\iec61850-build\Release\VagabondKMmsPeer.exe'
    if ((& $native --identity) -ne 'libiec61850 v1.6.2 ORIGINAL f0f797bf955da763353f7081c892a1ff6b55e5a1' -or $LASTEXITCODE -ne 0) { throw 'Unexpected independent MMS peer.' }
    $source = Join-Path $root 'bin\protocol-tools\libiec61850'
    $revision = & git -C $source rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $revision -ne 'f0f797bf955da763353f7081c892a1ff6b55e5a1') { throw 'Unexpected peer revision.' }
    & git -C $source diff --quiet $revision --
    if ($LASTEXITCODE -ne 0) { throw 'Independent MMS dependency modified.' }
    # 실행 파일을 회차별 출력 폴더로 복사해 실행하면 Windows 방화벽이 새 경로를 "Query User" 규칙으로 막아 보드의 18105 접속이 거부됩니다.
    # 그래서 기존 Allow 규칙이 있는 고정 빌드 경로에서 실행하고, 출력 폴더에는 해시만 기록합니다.
    $nativePath = (Resolve-Path -LiteralPath $native).Path
    $filters = @(Get-NetFirewallApplicationFilter -ErrorAction Stop | Where-Object { $_.Program -eq $nativePath })
    $rules = @($filters | Get-NetFirewallRule | Where-Object { $_.Enabled -eq 'True' -and $_.Direction -eq 'Inbound' })
    if (-not @($rules | Where-Object { $_.Action -eq 'Allow' }).Count -or @($rules | Where-Object { $_.Action -eq 'Block' }).Count) {
        throw "Inbound firewall Allow rule required for $nativePath (board client roles connect to TCP 18105)."
    }
    [pscustomobject]@{ Path = $nativePath; SHA256 = (Get-FileHash -LiteralPath $nativePath).Hash } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'native-peer.json')

    $app = 'VagabondK.nF.MMS.MappingInterop.Hardware'
    $project = Join-Path $root "Tests\MMS\$app\$app.nfproj"
    & $MSBuildPath $project /t:Rebuild /p:Configuration=Release /warnaserror /v:quiet /nologo *> (Join-Path $output 'build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Interop MCU fixture build failed.' }
    $built = Join-Path (Split-Path $project) 'bin\Release'
    if (@(Get-ChildItem -LiteralPath $built -File | Where-Object {
        $_.Name -like '*CodeAnalysis*' -or $_.Name -eq 'VagabondK.nF.MMS.Mapping.dll' -or $_.Name -eq 'VagabondK.nF.IEC61850.Mapping.dll'
    }).Count) { throw 'Desktop analyzer leaked into MCU output.' }
    $image = Join-Path $built "$app.bin"
    Copy-Item -LiteralPath $image -Destination (Join-Path $output 'deployed-image.bin')
    Get-ChildItem -LiteralPath $built -File | Where-Object { $_.Extension -in '.pe', '.bin' } |
        ForEach-Object { [pscustomobject]@{ Name=$_.Name; Bytes=$_.Length; SHA256=(Get-FileHash -LiteralPath $_.FullName).Hash } } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'images.json')

    $state.Status = 'deploying'; Save-State
    $changed = $true
    & (Join-Path $env:USERPROFILE '.dotnet\tools\nanoff.exe') --nanodevice --deploy --serialport COM3 --image $image --suppressnanoffversioncheck *> (Join-Path $output 'deploy.log')
    if ($LASTEXITCODE -ne 0) { throw 'Interop MCU deployment failed.' }
    $script:sequence = 0; $script:staleReplies = 0
    $udp = [Net.Sockets.UdpClient]::new(); $udp.Client.ReceiveTimeout = 500; $udp.Connect($DeviceAddress, 20005)
    $m = Metrics
    if ($m.Status -ne 0 -or $m.Sessions -ne 0) { throw 'MCU did not restart cleanly.' }

    foreach ($role in 1, 2, 3, 4) {
        $name = $roles[$role]; $kind = if ($role -le 2) { 'iec' } else { 'mms' }
        $state.Role = $name; $state.Status = 'running'; Save-State
        if ($role % 2 -eq 1) {
            $m = Metrics 1 $role
            if ($m.Role -ne $role -or $m.Status -ne 1) { throw "$name did not start." }
            Start-Sleep -Milliseconds 500
            for ($n = 1; $n -le $Iterations; $n++) {
                $client = Start-Tool $native "map$($kind)client 18104 $DeviceAddress" "native-$kind-client-$n"
                $text = Complete-Tool $client 90000
                if ($text -notmatch "NATIVE MAP $($kind.ToUpper()) CLIENT PASS writes=$(2 * $n) base=$(2 * $n - 2)") { throw "Native $kind client verdict mismatch: $text" }
                $m = Metrics; $state.Metrics = $m; Save-State
                $m | ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $output 'measurements.jsonl')
            }
            $null = Metrics 2
            $deadline = [Environment]::TickCount64 + 15000
            do { Start-Sleep -Milliseconds 100; $m = Metrics } while ($m.Status -ne 2 -and [Environment]::TickCount64 -lt $deadline)
            if ($m.Status -ne 2) { throw "$name did not stop cleanly." }
            if ($m.Sessions -ne 2 * $Iterations -or $m.Faults -gt $Iterations -or $m.Writes -ne 2 * $Iterations -or $m.Requests -lt 20 * $Iterations) { throw "$name coverage mismatch: $($m | ConvertTo-Json -Compress)" }
            if ($kind -eq 'iec' -and $m.Reports -lt 2 * $Iterations) { throw "$name reports missing: $($m | ConvertTo-Json -Compress)" }
            if ($kind -eq 'mms' -and $m.Reports -ne $Iterations) { throw "$name InformationReport count mismatch: $($m | ConvertTo-Json -Compress)" }
        }
        else {
            $server = Start-Tool $native "mapserver 18105 $HostAddress" "native-server-$kind"
            $ready = $server.StandardOutput.ReadLineAsync()
            if (-not $ready.Wait(10000) -or $ready.Result -ne 'READY') { throw 'Independent native map server not ready.' }
            $null = Read-Tool $server
            $m = Metrics 1 $role
            if ($m.Role -ne $role -or $m.Status -ne 1) { throw "$name did not start." }
            $deadline = [Environment]::TickCount64 + 60000 * $Iterations + 60000
            do {
                Start-Sleep -Seconds 2
                $m = Metrics; $state.Metrics = $m; Save-State
                $m | ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $output 'measurements.jsonl')
                if ($server.HasExited) { throw 'Independent native map server exited unexpectedly.' }
                if ([Environment]::TickCount64 -ge $deadline) { throw "$name exceeded its bound." }
            } while ($m.Status -ne 2)
            $server.StandardInput.WriteLine('quit')
            $text = Complete-Tool $server 15000
            $commands = if ($kind -eq 'iec') { 4 * $Iterations } else { 7 * $Iterations }
            $reports = if ($kind -eq 'iec') { 0 } else { 6 * $Iterations }
            if ($text -notmatch "NATIVE MAP SERVER PASS writes=$(2 * $Iterations) reports=$reports commands=$commands") { throw "Native server side-effect mismatch: $text" }
            $applied = if ($kind -eq 'iec') { 3 * $Iterations } else { 2 * $Iterations }
            $rejected = if ($kind -eq 'iec') { $Iterations } else { 4 * $Iterations }
            if ($m.Sessions -ne $Iterations -or $m.Writes -ne 2 * $Iterations -or $m.Applied -ne $applied -or $m.Rejected -ne $rejected -or $m.Checks -lt 10 * $Iterations) { throw "$name coverage mismatch: $($m | ConvertTo-Json -Compress)" }
        }
        if ($Iterations -ge 3) { Check-Heap $m }
        $state.Results += [pscustomobject]@{ Role = $name; Metrics = $m }; Save-State
        Write-Output "MAPPING INTEROP MCU $name sessions=$($m.Sessions) faults=$($m.Faults) writes=$($m.Writes) reports=$($m.Reports) requests=$($m.Requests) checks=$($m.Checks) applied=$($m.Applied) rejected=$($m.Rejected) heap=$($m.Heap) baseline=$($m.Baseline) minimum=$($m.Minimum)"
    }
    $state.StaleReplies = $script:staleReplies; $state.Status = 'completed'
}
catch { $state.StaleReplies = $script:staleReplies; $state.Status = 'failed'; $state.Error = $_.ToString(); throw }
finally {
    foreach ($process in $processes) {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force; $process.WaitForExit() }
        $s = $streams[$process.Id]
        if ($s -and -not $s.Saved) { try { try { $null = Read-Tool $process } catch { }; $null = Save-Tool $s } catch { Write-Warning "Log save failed: $_" } }
        $process.Dispose()
    }
    if ($udp) { $udp.Dispose() }
    if ($changed) {
        Start-Sleep -Seconds 5
        try {
            try { & (Join-Path $PSScriptRoot 'Restore-ModbusHardware.ps1') -LogPath (Join-Path $output 'restored-modbus.log') }
            catch {
                Write-Warning "Restore failed; retrying once: $_"; Start-Sleep -Seconds 5
                & (Join-Path $PSScriptRoot 'Restore-ModbusHardware.ps1') -LogPath (Join-Path $output 'restored-modbus-retry.log')
            }
            $state.Restored = $true
        }
        catch { $state.StaleReplies = $script:staleReplies; $state.Status = 'failed'; $state.Error = "$($state.Error)`nRestore: $_"; Save-State; throw }
    }
    Save-State
}
Write-Output "MAPPING INTEROP HARDWARE PASS: IEC/MMS generated Server+Client vs libiec61850 v1.6.2 iterations=$Iterations; restored original Modbus; $output"