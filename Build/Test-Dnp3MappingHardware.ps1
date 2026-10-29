#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][switch]$AllowDeviceChanges,
    [string]$DeviceAddress = '192.168.0.187',
    [ValidateRange(1,10000)][int]$Iterations = 128,
    [ValidateRange(10,1000)][int]$Rounds = 100,
    [string]$OutputPath = '',
    [string]$MSBuildPath = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe'
)
$ErrorActionPreference = 'Stop'
if (-not $AllowDeviceChanges) { throw 'Only use the isolated COM3 evaluation board.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$restore = Join-Path $root 'bin\hardware\restore-Modbus.SerialServer.bin'
if ((Get-FileHash -LiteralPath $restore -Algorithm SHA256).Hash -ne '66C51145509667EA4019649750B42CAD47344CE76D9736994B9658E0F45226A7') {
    throw 'Original Modbus restore checksum mismatch.'
}
if ([IO.Ports.SerialPort]::GetPortNames() -notcontains 'COM3') { throw 'COM3 unavailable.' }
$native = Join-Path $root 'bin\protocol-tools\master-build\Release\VagabondKDnp3Master.exe'
if (-not (Test-Path -LiteralPath $native)) { throw 'Build the original OpenDNP3 master with Build-Dnp3ValidationMaster.ps1 first.' }
if ((& $native --decoder-identity) -ne 'DECODER ORIGINAL' -or $LASTEXITCODE -ne 0) { throw 'Original independent decoder required.' }
if (-not $OutputPath) { $OutputPath = Join-Path $root ('bin\dnp3-mapping-hardware-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$output = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $output) { throw "Choose a fresh output folder: $output" }
$null = New-Item -ItemType Directory -Path $output
$tests = Join-Path $root 'Tests\DNP3\VagabondK.nF.DNP3.Mapping.Tests'
$sample = Join-Path $root 'Samples\DNP3\VagabondK.nF.DNP3.Mapping'
& dotnet run -c Release --project (Join-Path $tests 'VagabondK.nF.DNP3.Mapping.Tests.csproj') *>&1 | Tee-Object -FilePath (Join-Path $output 'pc-tests.log')
if ($LASTEXITCODE -ne 0) { throw 'DNP3 mapping tests failed.' }
& $MSBuildPath (Join-Path $sample 'VagabondK.nF.DNP3.Mapping.nfproj') /t:Rebuild /p:Configuration=Release /warnaserror /v:quiet /nologo
if ($LASTEXITCODE -ne 0) { throw 'Sample MCU build failed.' }
Copy-Item -LiteralPath (Join-Path $sample 'bin\Release\VagabondK.nF.DNP3.Mapping.bin') -Destination (Join-Path $output 'sample.bin')
$escapedRoot = [Security.SecurityElement]::Escape($root)
$sizes = @()
foreach ($count in @(8,32,128)) {
    foreach ($capacity in @(16,128)) {
        $folder = Join-Path $output "$count-$capacity"
        $null = New-Item -ItemType Directory -Path $folder
        $source = [Text.StringBuilder]::new()
        $null = $source.AppendLine('using VagabondK.nF.DNP3; using VagabondK.nF.DNP3.Mapping; namespace Dnp3Scaling { [Dnp3Map] internal partial class Model {')
        $null = $source.AppendLine("public const int Count = $count; public const int Capacity = $capacity; public int Generation;")
        for ($i = 0; $i -lt $count; $i++) {
            $null = $source.AppendLine("[Dnp3Point(Dnp3PointType.Analog,$i,StaticVariation=6,EventVariation=8,Deadband=0.5,Flags=nameof(Quality),Timestamp=nameof(Time))] public double P$i;")
        }
        $null = $source.AppendLine('public byte Quality = 1; public long ExtraTime; public long Time { get { return Generation + ExtraTime; } }')
        $null = $source.AppendLine('public void ChangeLocal(int slot) { switch(slot) {')
        for ($i = 0; $i -lt 3; $i++) {
            $index = @(0, [int]($count / 2), [int]($count - 1))[$i]
            $null = $source.AppendLine("case $i`: P$index = -$([int](700 + $index)); break;")
        }
        $null = $source.AppendLine('case 5: P0 = -699.5; break; case 6: P0 = -699.25; break; default: throw new System.ArgumentOutOfRangeException("slot"); } }')
        $null = $source.AppendLine('private Dnp3CommandStatus Check(Dnp3Command command) { return Dnp3CommandStatus.Success; }')
        $null = $source.AppendLine('[Dnp3Control(41,0,Validate=nameof(Check))] private Dnp3CommandStatus Set(Dnp3Command command,bool selected) { Generation++;')
        for ($i = 0; $i -lt $count; $i++) { $null = $source.AppendLine("P$i = command.AnalogValue;") }
        $null = $source.AppendLine('return Dnp3CommandStatus.Success; } }}')
        [IO.File]::WriteAllText((Join-Path $folder 'Model.cs'), $source.ToString())
        Copy-Item -LiteralPath (Join-Path $root 'Tests\DNP3\VagabondK.nF.DNP3.Mapping.Hardware\Program.cs') -Destination (Join-Path $folder 'Program.cs')
        $name = "Dnp3Scaling${count}E$capacity"
        $project = @"
<Project ToolsVersion="Current" DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <PropertyGroup><ProjectGuid>{310E1601-9B23-4A51-B344-100000000113}</ProjectGuid></PropertyGroup>
  <Import Project="$escapedRoot\Build\Library.props" />
  <Import Project="$escapedRoot\Build\Network.props" />
  <PropertyGroup><OutputType>Exe</OutputType></PropertyGroup>
  <ItemGroup>
    <Reference Include="mscorlib"><HintPath>`$(PackagesPath)nanoFramework.CoreLibrary.1.17.11\lib\mscorlib.dll</HintPath><Private>true</Private></Reference>
    <ProjectReference Include="$escapedRoot\Source\DNP3\VagabondK.nF.DNP3.Mapping\VagabondK.nF.DNP3.Mapping.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" SkipGetTargetFrameworkProperties="true" />
    <ProjectReference Include="$escapedRoot\Source\DNP3\VagabondK.nF.DNP3\VagabondK.nF.DNP3.nfproj" />
    <ProjectReference Include="$escapedRoot\Source\DNP3\VagabondK.nF.DNP3.Outstation\VagabondK.nF.DNP3.Outstation.nfproj" />
    <ProjectReference Include="$escapedRoot\Source\DNP3\VagabondK.nF.DNP3.TCP\VagabondK.nF.DNP3.TCP.nfproj" />
  </ItemGroup>
  <Import Project="$escapedRoot\Build\Library.targets" />
</Project>
"@
        $projectPath = Join-Path $folder "$name.nfproj"
        [IO.File]::WriteAllText($projectPath, $project)
        & $MSBuildPath $projectPath /t:Build /p:Configuration=Release /warnaserror /v:quiet /nologo
        if ($LASTEXITCODE -ne 0) { throw "Scaling MCU build failed: $count/$capacity" }
        $built = Join-Path $folder 'bin\Release'
        if (@(Get-ChildItem -LiteralPath $built | Where-Object { $_.Name -eq 'VagabondK.nF.DNP3.Mapping.dll' -or $_.Name -like '*Generator*' -or $_.Name -like '*CodeAnalysis*' }).Count) {
            throw 'Desktop dependency leaked into MCU output.'
        }
        $sizes += [PSCustomObject]@{
            Points = $count; EventCapacity = $capacity
            MappingPEBytes = (Get-Item -LiteralPath (Join-Path $built "$name.pe")).Length
            CorePEBytes = (Get-Item -LiteralPath (Join-Path $built 'VagabondK.nF.DNP3.pe')).Length
            OutstationPEBytes = (Get-Item -LiteralPath (Join-Path $built 'VagabondK.nF.DNP3.Outstation.pe')).Length
            DeploymentBytes = (Get-Item -LiteralPath (Join-Path $built "$name.bin")).Length
            ImageSHA256 = (Get-FileHash -LiteralPath (Join-Path $built "$name.bin")).Hash
        }
    }
}
$sizes | Export-Csv -LiteralPath (Join-Path $output 'sizes.csv') -NoTypeInformation
function Deploy([string]$Image, [string]$Label) {
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        & (Join-Path $env:USERPROFILE '.dotnet\tools\nanoff.exe') --nanodevice --deploy --serialport COM3 --image $Image --suppressnanoffversioncheck *>&1 |
            Tee-Object -FilePath (Join-Path $output "$Label-deploy-$attempt.log")
        if ($LASTEXITCODE -eq 0) { return }
        if ($attempt -eq 3) { throw "Deploy failed after three attempts: $Label" }
        Write-Warning "Deployment failed for $Label; retrying after 10 seconds."
        Start-Sleep -Seconds 10
    }
}
function Wait-Ready {
    $deadline = [Environment]::TickCount64 + 90000
    while ([Environment]::TickCount64 -lt $deadline) {
        $client = [Net.Sockets.TcpClient]::new()
        try {
            $task = $client.ConnectAsync($DeviceAddress, 20000)
            if ($task.Wait(1000)) { return }
        }
        catch [AggregateException] { Write-Verbose "MCU TCP not ready: $_" }
        finally { $client.Dispose() }
        Start-Sleep -Seconds 1
    }
    throw 'MCU TCP20000 readiness timeout.'
}
$peer = Join-Path $tests 'bin\Release\net10.0\VagabondK.nF.DNP3.Mapping.Tests.dll'
$changed = $false
try {
    $changed = $true
    Deploy (Join-Path $output 'sample.bin') 'sample'
    Wait-Ready
    & dotnet $peer --hardware $DeviceAddress $Iterations *>&1 | Tee-Object -FilePath (Join-Path $output 'sample-peer.log')
    if ($LASTEXITCODE -ne 0) { throw 'Sample independent MCU test failed.' }
    Start-Sleep -Seconds 5
    foreach ($size in $sizes) {
        $count = $size.Points; $capacity = $size.EventCapacity
        $folder = Join-Path $output "$count-$capacity"
        Deploy (Join-Path $folder "bin\Release\Dnp3Scaling${count}E$capacity.bin") "$count-$capacity"
        Wait-Ready
        & dotnet $peer --scaling $DeviceAddress $count $capacity $Rounds (Join-Path $folder 'measurements.json') *>&1 |
            Tee-Object -FilePath (Join-Path $folder 'peer.log')
        if ($LASTEXITCODE -ne 0) { throw "Scaling measurement failed: $count/$capacity" }
        Start-Sleep -Seconds 5
    }
}
finally {
    if ($changed) {
        Start-Sleep -Seconds 5
        try { & (Join-Path $PSScriptRoot 'Restore-ModbusHardware.ps1') -LogPath (Join-Path $output 'restored-modbus.log') }
        catch {
            Write-Warning "Restore failed; retrying once: $_"
            Start-Sleep -Seconds 5
            & (Join-Path $PSScriptRoot 'Restore-ModbusHardware.ps1') -LogPath (Join-Path $output 'restored-modbus-retry.log')
        }
    }
}
$sizes | Format-Table -AutoSize
Write-Output "DNP3 MAPPING HARDWARE PASS: original Modbus restored; $output"
