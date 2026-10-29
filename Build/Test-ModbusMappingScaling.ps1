param(
    [string]$MSBuildPath = '',
    [string]$OutputPath = ''
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputPath) { $OutputPath = Join-Path $root ('bin\mapping-scaling-' + [Guid]::NewGuid().ToString('N')) }
$output = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $output) { throw "Choose a new scaling output folder: $output" }
if (-not $MSBuildPath) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $MSBuildPath = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' |
        Select-Object -First 1
    if (-not $MSBuildPath) { throw 'Specify -MSBuildPath for Visual Studio MSBuild.' }
}
$null = New-Item -ItemType Directory -Path $output
$escapedRoot = [Security.SecurityElement]::Escape($root)
$results = @()
foreach ($count in @(8, 32, 128)) {
    $folder = Join-Path $output "$count"
    $null = New-Item -ItemType Directory -Path $folder
    $name = "MappingScaling$count"
    $source = [Text.StringBuilder]::new()
    $null = $source.AppendLine('using System;')
    $null = $source.AppendLine('using VagabondK.nF.Modbus.Mapping;')
    $null = $source.AppendLine('[ModbusMap(ModbusMapRole.Server, BufferBudgetBytes = 2)]')
    $null = $source.AppendLine('internal partial class Model {')
    for ($i = 0; $i -lt $count; $i++) {
        $null = $source.AppendLine("[ModbusPoint(ModbusArea.HoldingRegister, $i, Writable = true)] public ushort R$i;")
    }
    $null = $source.AppendLine('}')
    $used = [Math]::Min($count, 123)
    $null = $source.AppendLine(@"
internal static class Program {
    public static void Main() {
        var store = new Model.ModbusStore(new Model(), new object());
        var values = new ushort[$used];
        for (int i = 0; i < values.Length; i++) values[i] = (ushort)(i + 1);
        store.WriteHoldingRegisters(0, $used, values);
        for (int i = 0; i < values.Length; i++) values[i] = 0;
        store.ReadRegisters(false, 0, $used, values);
        for (int i = 0; i < values.Length; i++)
            if (values[i] != i + 1) throw new InvalidOperationException("Mapping scaling failed.");
    }
}
"@)
    [IO.File]::WriteAllText((Join-Path $folder 'Program.cs'), $source.ToString())
    $project = @"
<Project ToolsVersion="Current" DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <PropertyGroup><ProjectGuid>{310E1601-9B23-4A51-B344-100000000097}</ProjectGuid></PropertyGroup>
  <Import Project="$escapedRoot\Build\Library.props" />
  <PropertyGroup><OutputType>Exe</OutputType></PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$escapedRoot\Source\Modbus\VagabondK.nF.Modbus.Mapping\VagabondK.nF.Modbus.Mapping.csproj"
                      OutputItemType="Analyzer" ReferenceOutputAssembly="false" SkipGetTargetFrameworkProperties="true" />
    <ProjectReference Include="$escapedRoot\Source\Modbus\VagabondK.nF.Modbus\VagabondK.nF.Modbus.nfproj" />
    <ProjectReference Include="$escapedRoot\Source\Modbus\VagabondK.nF.Modbus.Server\VagabondK.nF.Modbus.Server.nfproj" />
  </ItemGroup>
  <Import Project="$escapedRoot\Build\Library.targets" />
</Project>
"@
    $projectPath = Join-Path $folder "$name.nfproj"
    [IO.File]::WriteAllText($projectPath, $project)
    & $MSBuildPath $projectPath /t:Build /p:Configuration=Release /warnaserror /v:quiet /nologo
    if ($LASTEXITCODE -ne 0) { throw "Scaling build failed: $count" }
    $built = Join-Path $folder 'bin\Release'
    $pe = Get-Item -LiteralPath (Join-Path $built "$name.pe")
    $image = Get-Item -LiteralPath (Join-Path $built "$name.bin")
    if (@(Get-ChildItem -LiteralPath $built | Where-Object { $_.Name -eq 'VagabondK.nF.Modbus.Mapping.dll' -or $_.Name -like '*Generator*' -or $_.Name -like '*CodeAnalysis*' }).Count -ne 0) {
        throw 'Generator must not appear in MCU output.'
    }
    $results += [PSCustomObject]@{ Points = $count; MappingPEBytes = $pe.Length; DeploymentBytes = $image.Length }
}
$results | Export-Csv -LiteralPath (Join-Path $output 'sizes.csv') -NoTypeInformation
$results | Format-Table -AutoSize
Write-Output "SCALING BUILD PASS: MCU bytecode sizes only, not MCU runtime measurements. Output: $output"
