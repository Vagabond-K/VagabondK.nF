#Requires -Version 7.0
param([Parameter(Mandatory = $true)][string]$OutputPath)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
[xml]$projects = Get-Content -LiteralPath (Join-Path $root 'VagabondK.nF.proj')
$names = @($projects.Project.ItemGroup.ChildNodes |
    Where-Object { $_.LocalName -like '*Library' -or $_.LocalName -eq 'BuildTool' } |
    ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_.Include) } | Sort-Object -Unique)
if ($names.Count -ne 34) { throw 'Update the package documentation inventory for the changed package set.' }
foreach ($name in $names) {
    $file = Join-Path $OutputPath "$name\README.md"
    & (Join-Path $PSScriptRoot 'New-PackageReadme.ps1') -PackageId $name -OutputPath $file
    $text = Get-Content -LiteralPath $file -Raw -Encoding UTF8
    $details = ($text -split '## 기능과 사용법', 2)[1] -split '## 실행 환경과 제한', 2 | Select-Object -First 1
    if (-not $text.StartsWith("# $name") -or $details.Length -lt 600 -or
        $details -notmatch '```(?:csharp|powershell)' -or $details -notmatch '지원|제한|않|없|호출') {
        throw "Missing substantive package guidance: $name"
    }
    $withoutCode = [regex]::Replace($text, '(?ms)^```[^\r\n]*\r?\n.*?^```', '')
    if ($withoutCode -match '(?m)^\s*```' -or $withoutCode -match '\]\((?!https?://)[^)]+\)') {
        throw "Unbalanced code fence or relative link: $name"
    }
    $group = $name.Split('.')[2]
    $expected = switch ($group) {
        'Modbus' { 'ReadModbus|ReadHoldingRegisters|ReadSingle' }
        'DNP3' { 'Dnp3MasterBinding|Dnp3TcpSession' }
        'BACnet' { 'BacnetStore|BacnetIpSession' }
        'SNMP' { 'SnmpStore|SnmpUdpSession' }
        'MMS' { 'MmsClient|MmsServer|MmsBinding' }
        'IEC61850' { 'Iec61850|GeneratedIec61850Model' }
        'MQTT' { 'MqttBinding|MqttConnection|Sparkplug' }
    }
    if ($details -notmatch $expected) { throw "Missing protocol-specific API guidance: $name" }
    if ($text -match 'Samples[/\\]MQTT[/\\]VagabondK\.nF\.MQTT(?:\.Sparkplug)?\.DataSharing') {
        throw "Stale MQTT sample path: $name"
    }
    if ($name.EndsWith('.Mapping') -and
        ($text -notmatch 'ExcludeType' -or $text -notmatch '/tree/HEAD/Samples/')) {
        throw "Missing build-only attribute or mapping sample guidance: $name"
    }
}
Write-Output "PACKAGE README PASS: $($names.Count) package-specific documents with detailed guidance, examples and absolute links."
