#Requires -Version 7.0
# PC loopback: 실제 생성 MMS/IEC 61850 바인딩 ↔ 독립 libiec61850 v1.6.2 peer(원본 무수정) 양방향 상호운용 시험입니다.
# MMS 일부 리포트는 peer 래퍼의 직접 인코딩이며 외부 라이브러리 인코더 검증과 구분합니다.
[CmdletBinding()]
param(
    [ValidateRange(1, 50)][int]$Iterations = 3,
    [string]$OutputPath = ''
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputPath) { $OutputPath = Join-Path $root ('bin\mapping-interop-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$output = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $output) { throw 'Choose a fresh output folder.' }
$null = New-Item -ItemType Directory -Path $output
$processes = [Collections.Generic.List[Diagnostics.Process]]::new()
# 장시간 서버의 stderr(native 시각 추적)는 시작부터 비워 파이프가 차서 멈추지 않게 합니다.
$errors = @{}
function Start-Tool([string]$file, [string]$arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($file, $arguments)
    $start.UseShellExecute = $false; $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($start); $processes.Add($process)
    $errors[$process.Id] = $process.StandardError.ReadToEndAsync()
    $ready = $process.StandardOutput.ReadLineAsync()
    if (-not $ready.Wait(15000) -or $ready.Result -ne 'READY') { throw "Not ready: $arguments" }
    return $process
}
function Complete-Tool([Diagnostics.Process]$process, [string]$name, [int]$milliseconds) {
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = if ($errors.ContainsKey($process.Id)) { $errors[$process.Id] } else { $process.StandardError.ReadToEndAsync() }
    if (-not $process.WaitForExit($milliseconds)) { throw "$name exceeded its bound." }
    $process.WaitForExit()
    $text = $stdout.Result + $stderr.Result
    $text | Set-Content -LiteralPath (Join-Path $output "$name.log")
    if ($process.ExitCode -ne 0) { throw "$name failed: $text" }
    return $text
}
function Run-Tool([string]$file, [string]$arguments, [string]$name, [int]$milliseconds) {
    $start = [Diagnostics.ProcessStartInfo]::new($file, $arguments)
    $start.UseShellExecute = $false; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($start); $processes.Add($process)
    return Complete-Tool $process $name $milliseconds
}
try {
    & (Join-Path $PSScriptRoot 'Build-Iec61850ValidationPeer.ps1') *> (Join-Path $output 'peer-build.log')
    $identity = Get-Content -LiteralPath (Join-Path $output 'peer-build.log') | Where-Object { $_ -match '^libiec61850 v1\.6\.2 ORIGINAL f0f797bf955da763353f7081c892a1ff6b55e5a1$' }
    if (-not $identity) { throw 'Unmodified libiec61850 peer build failed.' }
    $peer = Join-Path $root 'bin\protocol-tools\iec61850-build\Release\VagabondKMmsPeer.exe'
    $pc = Join-Path $root 'Tests\MMS\VagabondK.nF.MMS.Mapping.Tests\VagabondK.nF.MMS.Mapping.Tests.csproj'
    & dotnet build $pc -c Release --nologo -v quiet *> (Join-Path $output 'pc-build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Mapping PC fixture build failed.' }
    $dll = Join-Path $root 'Tests\MMS\VagabondK.nF.MMS.Mapping.Tests\bin\Release\net10.0\VagabondK.nF.MMS.Mapping.Tests.dll'
    $bound = 60000 * $Iterations + 30000

    # 1) 독립 native 서버 ↔ 생성 IEC/MMS Client 바인딩
    $server = Start-Tool $peer 'mapserver 18191 127.0.0.1'
    $iec = Run-Tool 'dotnet' "`"$dll`" --interop-iec-client 127.0.0.1 18191 $Iterations" 'generated-iec-client' $bound
    if ($iec -notmatch "INTEROP IEC CLIENT PASS iterations=$Iterations ") { throw 'Generated IEC client verdict missing.' }
    $mms = Run-Tool 'dotnet' "`"$dll`" --interop-mms-client 127.0.0.1 18191 $Iterations" 'generated-mms-client' $bound
    if ($mms -notmatch "INTEROP MMS CLIENT PASS iterations=$Iterations ") { throw 'Generated MMS client verdict missing.' }
    $server.StandardInput.WriteLine('quit')
    $text = Complete-Tool $server 'native-server' 15000
    if ($text -notmatch "NATIVE MAP SERVER PASS writes=$(4 * $Iterations) reports=$(6 * $Iterations) commands=$(11 * $Iterations)") { throw "Native server side-effect count mismatch: $text" }

    # 2) 생성 IEC/MMS Server 바인딩 ↔ 독립 native Client
    foreach ($kind in 'iec', 'mms') {
        $server = Start-Tool 'dotnet' "`"$dll`" --interop-$kind-server 18192 $Iterations"
        for ($n = 1; $n -le $Iterations; $n++) {
            $text = Run-Tool $peer "map$($kind)client 18192 127.0.0.1" "native-$kind-client-$n" 60000
            if ($text -notmatch "NATIVE MAP $($kind.ToUpper()) CLIENT PASS writes=$(2 * $n) base=$(2 * $n - 2)") { throw "Native $kind client verdict mismatch: $text" }
        }
        $text = Complete-Tool $server "generated-$kind-server" 30000
        if ($text -notmatch "INTEROP $($kind.ToUpper()) SERVER PASS sessions=$(2 * $Iterations) faults=$Iterations writes=$(2 * $Iterations)") { throw "Generated $kind server verdict mismatch: $text" }
        $reports = [int]([regex]::Match($text, 'reports=(\d+)').Groups[1].Value); $requests = [int]([regex]::Match($text, 'requests=(\d+)').Groups[1].Value)
        if (($kind -eq 'iec' -and $reports -lt 2 * $Iterations) -or ($kind -eq 'mms' -and $reports -ne $Iterations) -or $requests -lt 20 * $Iterations) { throw "Generated $kind server report/request coverage mismatch: $text" }
    }
    Write-Output "MAPPING INTEROP PC PASS iterations=$Iterations peer=libiec61850-v1.6.2-f0f797bf; $output"
}
finally {
    foreach ($process in $processes) {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force; $process.WaitForExit() }
        $process.Dispose()
    }
}