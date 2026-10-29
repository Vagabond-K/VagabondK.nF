#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][switch]$AllowDeviceChanges,
    [ValidateRange(1,1440)][int]$DurationMinutes = 120,
    [string]$OutputPath = '',
    [string]$MSBuildPath = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe',
    [string]$GoPath = ''
)
$ErrorActionPreference = 'Stop'
if (-not $AllowDeviceChanges) { throw 'Use only the isolated COM3/COM6 evaluation wiring.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$restore = Join-Path $root 'bin\hardware\restore-Modbus.SerialServer.bin'
if ((Get-FileHash -LiteralPath $restore -Algorithm SHA256).Hash -ne
    '66C51145509667EA4019649750B42CAD47344CE76D9736994B9658E0F45226A7') {
    throw 'Restore image checksum mismatch.'
}
$ports = [IO.Ports.SerialPort]::GetPortNames()
if ($ports -notcontains 'COM3' -or $ports -notcontains 'COM6') { throw 'COM3 and COM6 must both be available.' }
if (-not $GoPath) {
    $goCommand = Get-Command go -ErrorAction SilentlyContinue
    if ($goCommand) { $GoPath = $goCommand.Source }
}
if (-not $GoPath -or -not (Test-Path -LiteralPath $GoPath)) { throw 'Go compiler not found; supply -GoPath.' }
if (-not $OutputPath) { $OutputPath = Join-Path $root ('bin\dnp3-outstation-serial-soak-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$output = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $output) { throw "Choose a fresh output folder: $output" }
$null = New-Item -ItemType Directory -Path $output

$state = [ordered]@{
    Status = 'preparing'; DurationMinutes = $DurationMinutes; StartedUtc = $null
    FinishedUtc = $null; Cycles = 0; Operations = 0; Measurements = 0
    Restored = $false; Error = $null; LastPeerLine = $null
}
function Save-State {
    $temporary = Join-Path $output 'status.tmp'
    $state | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $temporary
    Move-Item -LiteralPath $temporary -Destination (Join-Path $output 'status.json') -Force
}
Save-State

$escapedRoot = [Security.SecurityElement]::Escape($root)
$project = @"
<Project ToolsVersion="Current" DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <PropertyGroup><ProjectGuid>{310E1601-9B23-4A51-B344-100000000118}</ProjectGuid></PropertyGroup>
  <Import Project="$escapedRoot\Build\Library.props" />
  <Import Project="$escapedRoot\Build\Network.props" />
  <PropertyGroup><OutputType>Exe</OutputType><StartupObject>VagabondK.nF.DNP3.SerialHost.Program</StartupObject>
    <DefineConstants>`$(DefineConstants);DEBUG;DNP3_HARDWARE_TEST;DNP3_NUMERIC_TEST;DNP3_NUMERIC_SOAK</DefineConstants></PropertyGroup>
  <ItemGroup>
    <Reference Include="nanoFramework.Hardware.Esp32"><HintPath>`$(PackagesPath)nanoFramework.Hardware.Esp32.1.6.42\lib\nanoFramework.Hardware.Esp32.dll</HintPath></Reference>
    <Reference Include="System.IO.Ports"><HintPath>`$(PackagesPath)nanoFramework.System.IO.Ports.1.1.142\lib\System.IO.Ports.dll</HintPath></Reference>
    <Reference Include="System.IO.Streams"><HintPath>`$(PackagesPath)nanoFramework.System.IO.Streams.1.1.96\lib\System.IO.Streams.dll</HintPath></Reference>
    <Reference Include="nanoFramework.Runtime.Events"><HintPath>`$(PackagesPath)nanoFramework.Runtime.Events.1.11.39\lib\nanoFramework.Runtime.Events.dll</HintPath></Reference>
    <Reference Include="System.Threading"><HintPath>`$(PackagesPath)nanoFramework.System.Threading.1.1.52\lib\System.Threading.dll</HintPath></Reference>
    <ProjectReference Include="$escapedRoot\Source\DNP3\VagabondK.nF.DNP3\VagabondK.nF.DNP3.nfproj" />
    <ProjectReference Include="$escapedRoot\Source\DNP3\VagabondK.nF.DNP3.Master\VagabondK.nF.DNP3.Master.nfproj" />
    <ProjectReference Include="$escapedRoot\Source\DNP3\VagabondK.nF.DNP3.Outstation\VagabondK.nF.DNP3.Outstation.nfproj" />
    <ProjectReference Include="$escapedRoot\Source\DNP3\VagabondK.nF.DNP3.Serial\VagabondK.nF.DNP3.Serial.nfproj" />
    <Compile Include="$escapedRoot\Samples\DNP3\VagabondK.nF.DNP3.SerialHost\Program.cs" />
    <Compile Include="$escapedRoot\Samples\DNP3\VagabondK.nF.DNP3.SerialHost\NumericValidation.cs" />
    <Compile Include="$escapedRoot\Samples\DNP3\VagabondK.nF.DNP3.SerialHost\SerialValidation.cs" />
  </ItemGroup>
  <Import Project="$escapedRoot\Build\Library.targets" />
</Project>
"@
$projectPath = Join-Path $output 'Dnp3SerialOutstationSoak.nfproj'
[IO.File]::WriteAllText($projectPath, $project)

$peerRoot = Join-Path $root 'Tests\DNP3\VagabondK.nF.DNP3.ExtraPeers\go-peer'
$peerExe = Join-Path $output 'go-peer.exe'
$image = Join-Path $output 'bin\Release\Dnp3SerialOutstationSoak.bin'
$peerLog = Join-Path $output 'peer.log'
$peerError = Join-Path $output 'peer-error.log'
$debugLog = Join-Path $output 'mcu-debug.log'
$peer = $null
$engine = $null
$manager = $null
$changed = $false
$eventSource = 'Dnp3OutstationSoak'
$failure = $null
try {
    & dotnet run -c Release --project (Join-Path $root 'Tests\DNP3\VagabondK.nF.DNP3.Mapping.Tests\VagabondK.nF.DNP3.Mapping.Tests.csproj') *>&1 |
        Tee-Object -FilePath (Join-Path $output 'pc-tests.log')
    if ($LASTEXITCODE -ne 0) { throw 'DNP3 PC regression failed.' }

    Push-Location $peerRoot
    try {
        & $GoPath build -o $peerExe .
        if ($LASTEXITCODE -ne 0) { throw 'Independent Go Master build failed.' }
    }
    finally { Pop-Location }

    & $MSBuildPath $projectPath /t:Rebuild /p:Configuration=Release /warnaserror /v:quiet /nologo *>&1 |
        Tee-Object -FilePath (Join-Path $output 'build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Outstation soak MCU build failed.' }
    if (-not (Test-Path -LiteralPath $image)) { throw 'Outstation soak image was not generated.' }
    (Get-FileHash -LiteralPath $image -Algorithm SHA256).Hash |
        Set-Content -LiteralPath (Join-Path $output 'image.sha256')

    $toolFolder = Get-ChildItem (Join-Path $env:USERPROFILE '.dotnet\tools\.store\nanoff') -Recurse -File -Filter nanoFramework.Tools.DebugLibrary.Net.dll |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty DirectoryName
    if (-not $toolFolder) { throw 'nanoff DebugLibrary is unavailable.' }
    Add-Type -Path (Join-Path $toolFolder 'System.IO.Ports.dll')
    Add-Type -Path (Join-Path $toolFolder 'nanoFramework.Tools.DebugLibrary.Net.dll')

    $nanoff = Join-Path $env:USERPROFILE '.dotnet\tools\nanoff.exe'
    $changed = $true
    & $nanoff --nanodevice --deploy --serialport COM3 --image $image --suppressnanoffversioncheck *>&1 |
        Tee-Object -FilePath (Join-Path $output 'deploy.log')
    if ($LASTEXITCODE -ne 0) { throw 'Outstation soak deployment failed.' }

    $manager = [nanoFramework.Tools.Debugger.PortSerial.PortSerialManager]::new(
        $false, [System.Collections.Generic.List[string]]::new(), 1000)
    $device = $manager.AddDevice('COM3')
    $device.CreateDebugEngine()
    $engine = $device.DebugEngine
    $engine.StopDebuggerOnConnect = $false
    if (-not $engine.Connect()) { throw 'MCU debugger connection failed.' }
    Register-ObjectEvent $engine -EventName OnMessage -SourceIdentifier $eventSource | Out-Null

    $peer = Start-Process $peerExe -ArgumentList @('serial-master-soak','COM6',([string]($DurationMinutes * 60))) `
        -RedirectStandardOutput $peerLog -RedirectStandardError $peerError -PassThru -NoNewWindow
    $engine.RebootDevice([nanoFramework.Tools.Debugger.RebootOptions]::ClrOnly, $null) | Out-Null
    $readyDeadline = [DateTime]::UtcNow.AddSeconds(90)
    while ([DateTime]::UtcNow -lt $readyDeadline -and (Get-Content -LiteralPath $peerLog -Raw -ErrorAction SilentlyContinue) -notmatch 'READY Go numeric Master soak') {
        if ($peer.HasExited) { throw "Go Master exited before readiness: $($peer.ExitCode)" }
        Start-Sleep -Milliseconds 250
    }
    if ((Get-Content -LiteralPath $peerLog -Raw) -notmatch 'READY Go numeric Master soak') { throw 'Go Master readiness timed out.' }

    $state.Status = 'running'
    $state.StartedUtc = [DateTime]::UtcNow.ToString('o')
    Save-State
    $lastSave = [DateTime]::UtcNow
    while (-not $peer.HasExited) {
        if (Test-Path -LiteralPath (Join-Path $output 'stop.request')) { throw 'Operator requested early stop.' }
        $event = Wait-Event -SourceIdentifier $eventSource -Timeout 1
        if ($event) {
            foreach ($arg in $event.SourceArgs) {
                if ($arg -is [string]) {
                    Add-Content -LiteralPath $debugLog -Value $arg
                    if ($arg.Contains('Uncaught exception')) { throw "MCU exception: $arg" }
                }
            }
            Remove-Event -EventIdentifier $event.EventIdentifier
        }
        if ([DateTime]::UtcNow - $lastSave -ge [TimeSpan]::FromSeconds(30)) {
            $last = Get-Content -LiteralPath $peerLog -Tail 1 -ErrorAction SilentlyContinue
            $state.LastPeerLine = $last
            if ($last -match '^SOAK cycle=(\d+) operations=(\d+) measurements=(\d+)') {
                $state.Cycles = [int]$Matches[1]
                $state.Operations = [int]$Matches[2]
                $state.Measurements = [long]$Matches[3]
            }
            Save-State
            $lastSave = [DateTime]::UtcNow
        }
    }
    if ($peer.ExitCode -ne 0) { throw "Go Master failed with exit code $($peer.ExitCode)." }
    $peerOutput = Get-Content -LiteralPath $peerLog -Raw
    if ($peerOutput -notmatch 'GO RS485 NUMERIC MASTER SOAK PASS:') { throw 'Go Master soak completion marker missing.' }
    $debugDeadline = [DateTime]::UtcNow.AddSeconds(15)
    while ([DateTime]::UtcNow -lt $debugDeadline -and
        (Get-Content -LiteralPath $debugLog -Raw -ErrorAction SilentlyContinue) -notmatch 'DNP3 MCU NUMERIC OUTSTATION SOAK PASS:') {
        $event = Wait-Event -SourceIdentifier $eventSource -Timeout 1
        if ($event) {
            foreach ($arg in $event.SourceArgs) {
                if ($arg -is [string]) {
                    Add-Content -LiteralPath $debugLog -Value $arg
                    if ($arg.Contains('Uncaught exception')) { throw "MCU exception: $arg" }
                }
            }
            Remove-Event -EventIdentifier $event.EventIdentifier
        }
    }
    if ((Get-Content -LiteralPath $debugLog -Raw -ErrorAction SilentlyContinue) -notmatch
        'DNP3 MCU NUMERIC OUTSTATION SOAK PASS:') {
        throw 'MCU Outstation soak completion marker missing.'
    }
    $cycleRecords = [regex]::Matches($peerOutput, '(?m)^SOAK cycle=(\d+) operations=(\d+) measurements=(\d+)')
    if ($cycleRecords.Count -eq 0) { throw 'Go Master cycle metrics are missing.' }
    $finalCycle = $cycleRecords[$cycleRecords.Count - 1]
    $state.Cycles = [int]$finalCycle.Groups[1].Value
    $state.Operations = [int]$finalCycle.Groups[2].Value
    $state.Measurements = [long]$finalCycle.Groups[3].Value
    $last = Get-Content -LiteralPath $peerLog -Tail 1
    if ($last -notmatch 'operations=(\d+) measurements=(\d+)') { throw 'Final Go Master totals missing.' }
    $state.Operations = [int]$Matches[1]
    $state.Measurements = [long]$Matches[2]
    $state.Status = 'completed'
}
catch {
    $failure = $_
    $state.Status = 'failed'
    $state.Error = $_.ToString()
}
finally {
    if ($peer -and -not $peer.HasExited) {
        try { Stop-Process -Id $peer.Id; $peer.WaitForExit() }
        catch { $state.Error = ($state.Error, $_.ToString() | Where-Object { $_ }) -join "`n" }
    }
    if ($engine) {
        try { $engine.Stop($true) }
        catch { $state.Error = ($state.Error, $_.ToString() | Where-Object { $_ }) -join "`n" }
    }
    if ($manager) {
        try { $manager.DisposeDevice('COM3') }
        catch { $state.Error = ($state.Error, $_.ToString() | Where-Object { $_ }) -join "`n" }
    }
    Unregister-Event -SourceIdentifier $eventSource -ErrorAction SilentlyContinue
    if ($peer) {
        $peer.Dispose()
    }
    if ($changed) {
        if ($state.Status -eq 'completed') { $state.Status = 'restoring' }
        try { Save-State } catch { $state.Error = ($state.Error, $_.ToString() | Where-Object { $_ }) -join "`n" }
        Start-Sleep -Seconds 5
        try {
            & (Join-Path $PSScriptRoot 'Restore-ModbusHardware.ps1') -LogPath (Join-Path $output 'restored-modbus.log')
            $state.Restored = $true
        }
        catch {
            Write-Warning "Restore failed; retrying once: $_"
            Start-Sleep -Seconds 5
            try {
                & (Join-Path $PSScriptRoot 'Restore-ModbusHardware.ps1') -LogPath (Join-Path $output 'restored-modbus-retry.log')
                $state.Restored = $true
            }
            catch { $state.Error = ($state.Error, $_.ToString() | Where-Object { $_ }) -join "`n" }
        }
    }
    $state.FinishedUtc = [DateTime]::UtcNow.ToString('o')
    if ($state.Status -eq 'restoring' -and $state.Restored) { $state.Status = 'completed' }
    if (-not $state.Restored -and $changed) { $state.Status = 'failed' }
    Save-State
}
if ($failure) { throw $failure }
if ($state.Status -ne 'completed' -or -not $state.Restored) {
    throw "DNP3 Serial Outstation soak did not complete cleanly. See $output\status.json"
}
Write-Output "DNP3 SERIAL OUTSTATION SOAK PASS: $output"
