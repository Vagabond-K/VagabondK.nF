#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][switch]$AllowDeviceChanges,
    [string]$DeviceAddress = '192.168.0.187',
    [ValidateRange(10,10000)][int]$Rounds = 100,
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
if (-not $OutputPath) { $OutputPath = Join-Path $root ('bin\snmp-scaling-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$output = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $output) { throw "Choose a fresh output folder: $output" }
$null = New-Item -ItemType Directory -Path $output
$tests = Join-Path $root 'Tests\SNMP\VagabondK.nF.SNMP.Mapping.Tests'
& dotnet build (Join-Path $tests 'VagabondK.nF.SNMP.Mapping.Tests.csproj') -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'PC peer build failed.' }
$escapedRoot = [Security.SecurityElement]::Escape($root)
$sizes = @()
foreach ($count in @(8,32,128)) {
    $folder = Join-Path $output "$count"
    $null = New-Item -ItemType Directory -Path $folder
    $source = [Text.StringBuilder]::new()
    $null = $source.AppendLine('using VagabondK.nF.SNMP; using VagabondK.nF.SNMP.Mapping; namespace SnmpScaling { [SnmpMap] internal partial class Model {')
    $null = $source.AppendLine("public const int Count = $count;")
    for ($i = 1; $i -lt $count; $i++) {
        $null = $source.AppendLine("[SnmpScalar(`"1.3.6.1.4.1.32473.30.$i.0`", SnmpType.Integer32, Writable=true)] public int R$i;")
    }
    $null = $source.AppendLine("[SnmpScalar(`"1.3.6.1.4.1.32473.30.$count.0`", SnmpType.OctetString, Writable=true, MaximumLength=512)] public byte[] Text = new byte[512]; }}")
    [IO.File]::WriteAllText((Join-Path $folder 'Model.cs'), $source.ToString())
    Copy-Item -LiteralPath (Join-Path $root 'Tests\SNMP\VagabondK.nF.SNMP.Mapping.Hardware\Program.cs') -Destination (Join-Path $folder 'Program.cs')
    $name = "SnmpScaling$count"
    $project = @"
<Project ToolsVersion="Current" DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <PropertyGroup><ProjectGuid>{310E1601-9B23-4A51-B344-100000000103}</ProjectGuid></PropertyGroup>
  <Import Project="$escapedRoot\Build\Library.props" />
  <Import Project="$escapedRoot\Build\Network.props" />
  <PropertyGroup><OutputType>Exe</OutputType></PropertyGroup>
  <ItemGroup>
    <Reference Include="mscorlib"><HintPath>`$(PackagesPath)nanoFramework.CoreLibrary.1.17.11\lib\mscorlib.dll</HintPath><Private>true</Private></Reference>
    <ProjectReference Include="$escapedRoot\Source\SNMP\VagabondK.nF.SNMP.Mapping\VagabondK.nF.SNMP.Mapping.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" SkipGetTargetFrameworkProperties="true" />
    <ProjectReference Include="$escapedRoot\Source\SNMP\VagabondK.nF.SNMP\VagabondK.nF.SNMP.nfproj" />
  </ItemGroup>
  <Import Project="$escapedRoot\Build\Library.targets" />
</Project>
"@
    $projectPath = Join-Path $folder "$name.nfproj"
    [IO.File]::WriteAllText($projectPath, $project)
    & $MSBuildPath $projectPath /t:Build /p:Configuration=Release /warnaserror /v:quiet /nologo
    if ($LASTEXITCODE -ne 0) { throw "MCU build failed: $count" }
    $built = Join-Path $folder 'bin\Release'
    if (@(Get-ChildItem -LiteralPath $built | Where-Object { $_.Name -eq 'VagabondK.nF.SNMP.Mapping.dll' -or $_.Name -like '*Generator*' -or $_.Name -like '*CodeAnalysis*' }).Count) {
        throw 'Desktop dependency leaked into MCU output.'
    }
    $sizes += [PSCustomObject]@{
        Points = $count
        MappingPEBytes = (Get-Item -LiteralPath (Join-Path $built "$name.pe")).Length
        CorePEBytes = (Get-Item -LiteralPath (Join-Path $built 'VagabondK.nF.SNMP.pe')).Length
        DeploymentBytes = (Get-Item -LiteralPath (Join-Path $built "$name.bin")).Length
        ImageSHA256 = (Get-FileHash -LiteralPath (Join-Path $built "$name.bin")).Hash
    }
}
$sizes | Export-Csv -LiteralPath (Join-Path $output 'sizes.csv') -NoTypeInformation
$changed = $false
try {
    foreach ($count in @(8,32,128)) {
        $folder = Join-Path $output "$count"
        $changed = $true
        for ($attempt = 1; $attempt -le 3; $attempt++) {
            & (Join-Path $env:USERPROFILE '.dotnet\tools\nanoff.exe') --nanodevice --deploy --serialport COM3 --image (Join-Path $folder "bin\Release\SnmpScaling$count.bin") --suppressnanoffversioncheck *>&1 |
                Tee-Object -FilePath (Join-Path $folder "deploy-$attempt.log")
            if ($LASTEXITCODE -eq 0) { break }
            if ($attempt -eq 3) { throw "Deploy failed after three attempts: $count" }
            Write-Warning "Deployment failed for $count points; waiting before attempt $($attempt + 1)."
            Start-Sleep -Seconds 10
        }
        & dotnet (Join-Path $tests 'bin\Release\net10.0\VagabondK.nF.SNMP.Mapping.Tests.dll') --scaling $DeviceAddress $count $Rounds (Join-Path $folder 'measurements.json') *>&1 |
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
Write-Output "SNMP SCALING PASS: original Modbus restored; $output"
