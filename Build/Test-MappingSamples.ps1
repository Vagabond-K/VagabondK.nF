#Requires -Version 7.0
[CmdletBinding()]
param([string]$MSBuildPath='C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe')
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
foreach ($name in @('Modbus.Mapping','DNP3.Mapping','MMS.Mapping','SNMP.Mapping','BACnet.Mapping','MQTT.Mapping','MQTT.Sparkplug.Mapping','MQTT.DataSharing')) {
    $protocol = $name.Split('.')[0]
    & dotnet run -c Release --project (Join-Path $root "Tests\$protocol\VagabondK.nF.$name.Tests\VagabondK.nF.$name.Tests.csproj")
    if ($LASTEXITCODE -ne 0) { throw "Sample regression failed: $name" }
}
foreach ($name in @('Modbus.Mapping','DNP3.Mapping','DNP3.MasterMapping','MMS.Mapping','IEC61850.Mapping','SNMP.Mapping','BACnet.Mapping','MQTT.Mapping','MQTT.Sparkplug.Mapping')) {
    $protocol = $name.Split('.')[0]
    & $MSBuildPath (Join-Path $root "Samples\$protocol\VagabondK.nF.$name\VagabondK.nF.$name.nfproj") /t:Build /p:Configuration=Release /warnaserror /nr:false /v:quiet /nologo
    if ($LASTEXITCODE -ne 0) { throw "MCU sample build failed: $name" }
    $assemblyPath = Join-Path $root "Samples\$protocol\VagabondK.nF.$name\bin\Release\VagabondK.nF.$name.exe"
    [xml]$pdbx = Get-Content -Raw ([IO.Path]::ChangeExtension($assemblyPath, '.pdbx'))
    $retainedTokens = @{}
    foreach ($class in $pdbx.PdbxFile.Assembly.Classes.Class) {
        $retainedTokens[[Convert]::ToInt32($class.Token.CLR, 16)] = $true
    }
    $stream = [IO.File]::OpenRead($assemblyPath)
    $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
    try {
        $reader = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        $row = 0
        $attributeCount = 0
        foreach ($handle in $reader.TypeDefinitions) {
            $row++
            $type = $reader.GetTypeDefinition($handle)
            $typeNamespace = $reader.GetString($type.Namespace)
            $typeName = $reader.GetString($type.Name)
            if ($typeNamespace -like 'VagabondK.nF.*.Mapping' -and $typeName.EndsWith('Attribute')) {
                $attributeCount++
                if ($retainedTokens.ContainsKey(0x02000000 -bor $row)) {
                    throw "Build-only mapping attribute retained in MCU image: $name, $typeNamespace.$typeName"
                }
            }
        }
        if ($attributeCount -eq 0) { throw "No mapping attribute declarations found: $name" }
    }
    finally {
        $pe.Dispose()
        $stream.Dispose()
    }
}
Write-Output 'MAPPING SAMPLES PASS: eight PC suites (including IEC, MQTT/Sparkplug generators and contracts), nine MCU builds; mapping attributes excluded from MCU images; no device changes.'
