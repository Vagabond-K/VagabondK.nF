param(
    [Parameter(Mandatory)][ValidateSet(8,32,128)][int]$Count,
    [Parameter(Mandatory)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$source = [Text.StringBuilder]::new()
$null = $source.AppendLine('using VagabondK.nF.Modbus.Mapping;')
$null = $source.AppendLine('namespace VagabondK.nF.Modbus.Mapping.Hardware {')
$null = $source.AppendLine('[ModbusMap(ModbusMapRole.Server, BufferBudgetBytes = 2)] internal partial class ScaleModel {')
$null = $source.AppendLine("internal const int Count = $Count;")
for ($i = 0; $i -lt $Count; $i++) {
    $null = $source.AppendLine("[ModbusPoint(ModbusArea.HoldingRegister, $i, Writable = true)] public ushort R$i;")
}
$null = $source.AppendLine('} }')
$path = [IO.Path]::GetFullPath($OutputPath)
$null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($path)) -Force
[IO.File]::WriteAllText($path, $source.ToString())
