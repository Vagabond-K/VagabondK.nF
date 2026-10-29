#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$DeviceAddress = '192.168.0.187',
    [string]$PeerAddress = '192.168.0.210',
    [ValidateRange(1,10000)][int]$Iterations = 64,
    [switch]$NoDebugger,
    [ValidateSet(8,32,128)][int[]]$ScaleCounts = @(8,32,128),
    [ValidateRange(1,60)][int]$ResponseTimeoutSeconds = 2,
    [ValidateRange(0,3600)][int]$StabilitySeconds = 0,
    [ValidateRange(1,10)][int]$SlaveRuns = 1,
    [Parameter(Mandatory)][switch]$AllowDeviceChanges,
    [string]$MSBuildPath = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe'
)

$ErrorActionPreference = 'Stop'
if (-not $AllowDeviceChanges) { throw 'Only use the isolated COM3 evaluation board.' }
if ($SlaveRuns -gt 1 -and -not $NoDebugger) { throw 'Repeated Slave runs require -NoDebugger.' }
if ($StabilitySeconds -gt 0 -and (-not $NoDebugger -or $StabilitySeconds -lt 60)) {
    throw 'Stability tests require -NoDebugger and at least 60 seconds.'
}
if ($PeerAddress -ne '192.168.0.210') { throw 'The dedicated MCU fixture uses PC address 192.168.0.210; update its source before changing this address.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$fixture = Join-Path $root 'Tests\Modbus\VagabondK.nF.Modbus.Mapping.Hardware'
$project = Join-Path $fixture 'VagabondK.nF.Modbus.Mapping.Hardware.nfproj'
$python = Join-Path $fixture 'bin\python\Scripts\python.exe'
$peer = Join-Path $fixture 'peer.py'
$nanoff = Join-Path $env:USERPROFILE '.dotnet\tools\nanoff.exe'
$restore = Join-Path $root 'bin\hardware\restore-Modbus.SerialServer.bin'
if (-not (Test-Path -LiteralPath $python)) {
    throw 'Create the fixture bin\python venv and install its requirements.txt before running hardware tests.'
}
& $python -c "import pymodbus; assert pymodbus.__version__ == '3.11.3', pymodbus.__version__"
if ($LASTEXITCODE -ne 0) { throw 'Independent pymodbus 3.11.3 is required.' }
if ((Get-FileHash -LiteralPath $restore -Algorithm SHA256).Hash -ne '66C51145509667EA4019649750B42CAD47344CE76D9736994B9658E0F45226A7') {
    throw 'Original Modbus restore checksum mismatch.'
}
if ([IO.Ports.SerialPort]::GetPortNames() -notcontains 'COM3') { throw 'Evaluation board COM3 is unavailable.' }
if (@(Get-NetIPAddress -AddressFamily IPv4 | Where-Object IPAddress -eq $PeerAddress).Count -eq 0) { throw 'PC peer address is not assigned.' }
$logs = Join-Path $root ('bin\mapping-hardware-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$null = New-Item -ItemType Directory -Path $logs
@{ NoDebugger = [bool]$NoDebugger; ScaleCounts = $ScaleCounts; SlaveRuns = $SlaveRuns
    StabilitySeconds = $StabilitySeconds; ResponseTimeoutSeconds = $ResponseTimeoutSeconds; Iterations = $Iterations } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $logs 'invocation.json')
$images = @()
$changed = $false
$process = $null
if (-not $NoDebugger) {
    $toolFolder = Get-ChildItem (Join-Path $env:USERPROFILE '.dotnet\tools\.store\nanoff') -Recurse -File -Filter nanoFramework.Tools.DebugLibrary.Net.dll |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty DirectoryName
    if (-not $toolFolder) { throw 'nanoff DebugLibrary is required for MCU failure evidence.' }
    Add-Type -Path (Join-Path $toolFolder 'System.IO.Ports.dll')
    Add-Type -Path (Join-Path $toolFolder 'nanoFramework.Tools.DebugLibrary.Net.dll')
}
function Invoke-ObservedSlave([int]$Count) {
    $manager = [nanoFramework.Tools.Debugger.PortSerial.PortSerialManager]::new(
        $false, [System.Collections.Generic.List[string]]::new(), 1000)
    $device = $manager.AddDevice('COM3')
    $engine = $null
    $peerProcess = $null
    $source = 'MappingHardwareDebug'
    $debugLog = Join-Path $logs "slave-$Count-mcu.log"
    try {
        $device.CreateDebugEngine()
        $engine = $device.DebugEngine
        $engine.StopDebuggerOnConnect = $false
        if (-not $engine.Connect()) { throw 'MCU debugger connection failed.' }
        Register-ObjectEvent $engine -EventName OnMessage -SourceIdentifier $source | Out-Null
        $engine.RebootDevice([nanoFramework.Tools.Debugger.RebootOptions]::ClrOnly, $null) | Out-Null
        $arguments = @("`"$peer`"", 'slave', '--device', $DeviceAddress, '--points', "$Count", '--iterations', "$Iterations",
            '--output', "`"$(Join-Path $logs "slave-$Count.json")`"")
        $peerProcess = Start-Process -FilePath $python -ArgumentList $arguments -PassThru -NoNewWindow `
            -RedirectStandardOutput (Join-Path $logs "slave-$Count.log") -RedirectStandardError (Join-Path $logs "slave-$Count-error.log")
        $deadline = [DateTime]::UtcNow.AddSeconds(260)
        while (-not $peerProcess.HasExited -and [DateTime]::UtcNow -lt $deadline) {
            $event = Wait-Event -SourceIdentifier $source -Timeout 1
            if ($event) {
                foreach ($arg in $event.SourceArgs) {
                    if ($arg -is [string]) {
                        $arg | Add-Content -LiteralPath $debugLog
                        if ($arg.Contains('Uncaught exception')) { throw "MCU unhandled exception; see $debugLog" }
                    }
                }
                Remove-Event -EventIdentifier $event.EventIdentifier
            }
        }
        Get-Content -LiteralPath (Join-Path $logs "slave-$Count.log")
        if (-not $peerProcess.HasExited -or $peerProcess.ExitCode -ne 0) {
            throw "Independent MCU Slave test failed: $Count; see $logs"
        }
    }
    finally {
        Unregister-Event -SourceIdentifier $source -ErrorAction SilentlyContinue
        Get-Event -SourceIdentifier $source -ErrorAction SilentlyContinue | Remove-Event
        if ($peerProcess -and -not $peerProcess.HasExited) { Stop-Process -Id $peerProcess.Id -Force }
        if ($engine) { $engine.Stop($true) }
        $manager.DisposeDevice('COM3')
    }
}
function Invoke-UnobservedSlave([int]$Count, [int]$Run) {
    & $python -B $peer slave --device $DeviceAddress --points $Count --iterations $Iterations --timeout $ResponseTimeoutSeconds `
        --require-no-debugger --stability-seconds $StabilitySeconds --output (Join-Path $logs "slave-$Count-run-$Run.json") *>&1 |
        Tee-Object -FilePath (Join-Path $logs "slave-$Count-run-$Run.log")
    if ($LASTEXITCODE -ne 0) { throw "Unobserved MCU Slave test failed: $Count; see $logs" }
}
try {
    foreach ($count in $ScaleCounts) {
        & $MSBuildPath $project /t:Rebuild /p:Configuration=Release /p:MappingHardwareRole=Slave "/p:MappingScaleCount=$count" /warnaserror /v:quiet /nologo
        if ($LASTEXITCODE -ne 0) { throw "Slave scaling fixture build failed: $count" }
        $image = Join-Path $logs "slave-$count.bin"
        Copy-Item -LiteralPath (Join-Path $fixture 'bin\Release\VagabondK.nF.Modbus.Mapping.Hardware.bin') -Destination $image
        $pe = Get-Item -LiteralPath (Join-Path $fixture 'bin\Release\VagabondK.nF.Modbus.Mapping.Hardware.pe')
        $images += [PSCustomObject]@{ Points = $count; MappingPEBytes = $pe.Length; Image = $image }
    }
    & $MSBuildPath $project /t:Rebuild /p:Configuration=Release /p:MappingHardwareRole=Master /p:MappingScaleCount=8 /warnaserror /v:quiet /nologo
    if ($LASTEXITCODE -ne 0) { throw 'Master fixture build failed.' }
    $masterImage = Join-Path $logs 'master.bin'
    Copy-Item -LiteralPath (Join-Path $fixture 'bin\Release\VagabondK.nF.Modbus.Mapping.Hardware.bin') -Destination $masterImage
    $images | Export-Csv -LiteralPath (Join-Path $logs 'images.csv') -NoTypeInformation
    # 이 자체 시험은 실행 범위가 정해진 localhost 시험이며 아직 보드를 변경하지 않습니다.
    & $python $peer selftest --bind 127.0.0.1 --port 15021 --ready (Join-Path $logs 'selftest.ready') --output (Join-Path $logs 'selftest.json') *>&1 |
        Tee-Object -FilePath (Join-Path $logs 'selftest.log')
    if ($LASTEXITCODE -ne 0) { throw 'Independent peer self-test failed.' }
    foreach ($image in $images) {
        for ($run = 1; $run -le $SlaveRuns; $run++) {
            $changed = $true
            & $nanoff --nanodevice --deploy --serialport COM3 --image $image.Image --suppressnanoffversioncheck
            if ($LASTEXITCODE -ne 0) { throw "Slave fixture deployment failed: $($image.Points)" }
            if ($NoDebugger) { Invoke-UnobservedSlave -Count $image.Points -Run $run }
            else { Invoke-ObservedSlave -Count $image.Points }
        }
    }
    $ready = Join-Path $logs 'master.ready'
    $report = Join-Path $logs 'master.json'
    $arguments = @("`"$peer`"", 'master', '--bind', $PeerAddress, '--ready', "`"$ready`"", '--output', "`"$report`"")
    $process = Start-Process -FilePath $python -ArgumentList $arguments -PassThru -NoNewWindow `
        -RedirectStandardOutput (Join-Path $logs 'master.log') -RedirectStandardError (Join-Path $logs 'master-error.log')
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while (-not (Test-Path -LiteralPath $ready)) {
        if ($process.HasExited -or [DateTime]::UtcNow -gt $deadline) { throw 'Independent PC Slave failed to listen; check master logs.' }
        Start-Sleep -Milliseconds 200
    }
    if ($process.HasExited) { throw 'Independent PC Slave stopped before deployment.' }
    & $nanoff --nanodevice --deploy --serialport COM3 --image $masterImage --suppressnanoffversioncheck
    if ($LASTEXITCODE -ne 0) { throw 'Master fixture deployment failed.' }
    if (-not $process.WaitForExit(260000)) { throw 'MCU Master test timed out; check master logs.' }
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $report)) {
        throw 'Independent MCU Master validation failed; check master logs.'
    }
    Get-Content -LiteralPath (Join-Path $logs 'master.log')
}
finally {
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force }
    if ($changed) { & (Join-Path $PSScriptRoot 'Restore-ModbusHardware.ps1') -LogPath (Join-Path $logs 'restored-modbus.log') }
}
Write-Output "MAPPING HARDWARE PASS: original Modbus restored. Results: $logs"
