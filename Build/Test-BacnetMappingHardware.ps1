#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][switch]$AllowDeviceChanges,
    [string]$DeviceAddress = '192.168.0.187',
    [ValidateRange(14,10000)][int]$Iterations = 128,
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
if (-not $OutputPath) { $OutputPath = Join-Path $root ('bin\bacnet-mapping-hardware-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$output = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $output) { throw "Choose a fresh output folder: $output" }
$null = New-Item -ItemType Directory -Path $output
$tests = Join-Path $root 'Tests\BACnet\VagabondK.nF.BACnet.Mapping.Tests'
$sample = Join-Path $root 'Samples\BACnet\VagabondK.nF.BACnet.Mapping'
& dotnet run -c Release --project (Join-Path $tests 'VagabondK.nF.BACnet.Mapping.Tests.csproj') *>&1 |
    Tee-Object -FilePath (Join-Path $output 'pc-tests.log')
if ($LASTEXITCODE -ne 0) { throw 'BACnet mapping tests failed.' }
& $MSBuildPath (Join-Path $sample 'VagabondK.nF.BACnet.Mapping.nfproj') /t:Rebuild /p:Configuration=Release /warnaserror /v:quiet /nologo
if ($LASTEXITCODE -ne 0) { throw 'Sample MCU build failed.' }
Copy-Item -LiteralPath (Join-Path $sample 'bin\Release\VagabondK.nF.BACnet.Mapping.bin') -Destination (Join-Path $output 'sample.bin')
$escapedRoot = [Security.SecurityElement]::Escape($root)
$sizes = @()
foreach ($count in @(8,32,64)) {
    $folder = Join-Path $output "$count"
    $null = New-Item -ItemType Directory -Path $folder
    $source = [Text.StringBuilder]::new()
    $null = $source.AppendLine('using VagabondK.nF.BACnet.Mapping; namespace BacnetScaling { [BacnetMap] internal partial class Model {')
    $null = $source.AppendLine("public const int Count = $count;")
    for ($i = 0; $i -lt $count / 2; $i++) {
        $null = $source.AppendLine("[BacnetPoint($i, Writable=true)] public float A$i;")
        $null = $source.AppendLine("[BacnetPoint($i, Writable=true)] public bool B$i;")
    }
    $null = $source.AppendLine('public void ChangeLocal(int slot) { switch(slot) {')
    for ($i = 0; $i -lt 3; $i++) {
        $index = @(0, [int]($count / 4), [int]($count / 2 - 1))[$i]
        $binary = if ($index % 2) { 'true' } else { 'false' }
        $null = $source.AppendLine("case $i`: A$index = -$([int](700 + $index))`f; B$index = $binary; break;")
    }
    $null = $source.AppendLine('default: throw new System.ArgumentOutOfRangeException("slot"); } } }}')
    [IO.File]::WriteAllText((Join-Path $folder 'Model.cs'), $source.ToString())
    Copy-Item -LiteralPath (Join-Path $root 'Tests\BACnet\VagabondK.nF.BACnet.Mapping.Hardware\Program.cs') -Destination (Join-Path $folder 'Program.cs')
    $name = "BacnetScaling$count"
    $project = @"
<Project ToolsVersion="Current" DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <PropertyGroup><ProjectGuid>{310E1601-9B23-4A51-B344-100000000108}</ProjectGuid></PropertyGroup>
  <Import Project="$escapedRoot\Build\Library.props" />
  <Import Project="$escapedRoot\Build\Network.props" />
  <PropertyGroup><OutputType>Exe</OutputType></PropertyGroup>
  <ItemGroup>
    <Reference Include="mscorlib"><HintPath>`$(PackagesPath)nanoFramework.CoreLibrary.1.17.11\lib\mscorlib.dll</HintPath><Private>true</Private></Reference>
    <ProjectReference Include="$escapedRoot\Source\BACnet\VagabondK.nF.BACnet.Mapping\VagabondK.nF.BACnet.Mapping.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" SkipGetTargetFrameworkProperties="true" />
    <ProjectReference Include="$escapedRoot\Source\BACnet\VagabondK.nF.BACnet\VagabondK.nF.BACnet.nfproj" />
    <ProjectReference Include="$escapedRoot\Source\BACnet\VagabondK.nF.BACnet.IP\VagabondK.nF.BACnet.IP.nfproj" />
  </ItemGroup>
  <Import Project="$escapedRoot\Build\Library.targets" />
</Project>
"@
    $projectPath = Join-Path $folder "$name.nfproj"
    [IO.File]::WriteAllText($projectPath, $project)
    & $MSBuildPath $projectPath /t:Build /p:Configuration=Release /warnaserror /v:quiet /nologo
    if ($LASTEXITCODE -ne 0) { throw "Scaling MCU build failed: $count" }
    $built = Join-Path $folder 'bin\Release'
    if (@(Get-ChildItem -LiteralPath $built | Where-Object { $_.Name -eq 'VagabondK.nF.BACnet.Mapping.dll' -or $_.Name -like '*Generator*' -or $_.Name -like '*CodeAnalysis*' }).Count) {
        throw 'Desktop dependency leaked into MCU output.'
    }
    $sizes += [PSCustomObject]@{
        Points = $count
        MappingPEBytes = (Get-Item -LiteralPath (Join-Path $built "$name.pe")).Length
        CorePEBytes = (Get-Item -LiteralPath (Join-Path $built 'VagabondK.nF.BACnet.pe')).Length
        DeploymentBytes = (Get-Item -LiteralPath (Join-Path $built "$name.bin")).Length
        ImageSHA256 = (Get-FileHash -LiteralPath (Join-Path $built "$name.bin")).Hash
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
$peer = Join-Path $tests 'bin\Release\net10.0\VagabondK.nF.BACnet.Mapping.Tests.dll'
$changed = $false
try {
    $changed = $true
    Deploy (Join-Path $output 'sample.bin') 'sample'
    & dotnet $peer --hardware $DeviceAddress $Iterations *>&1 | Tee-Object -FilePath (Join-Path $output 'sample-peer.log')
    if ($LASTEXITCODE -ne 0) { throw 'Sample independent MCU test failed.' }
    Start-Sleep -Seconds 5
    foreach ($count in @(8,32,64)) {
        $folder = Join-Path $output "$count"
        Deploy (Join-Path $folder "bin\Release\BacnetScaling$count.bin") "$count"
        & dotnet $peer --scaling $DeviceAddress $count $Rounds (Join-Path $folder 'measurements.json') *>&1 |
            Tee-Object -FilePath (Join-Path $folder 'peer.log')
        if ($LASTEXITCODE -ne 0) { throw "Scaling measurement failed: $count" }
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
Write-Output "BACNET MAPPING HARDWARE PASS: original Modbus restored; $output"
