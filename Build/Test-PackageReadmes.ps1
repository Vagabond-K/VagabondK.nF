#Requires -Version 7.0
param([Parameter(Mandatory = $true)][string]$OutputPath)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$catalog = Get-Content -LiteralPath (Join-Path $root 'README.md') -Raw -Encoding UTF8
$overview = [regex]::Match($catalog, '\A# VagabondK\.nF\r?\n[ \t]*\r?\n(?<text>[^\r\n]+(?:\r?\n[^\r\n]+)*)')
if (-not $overview.Success) { throw 'Missing introductory paragraph in the root README.' }
$projectIntroduction = $overview.Groups['text'].Value.Trim()
[xml]$projects = Get-Content -LiteralPath (Join-Path $root 'VagabondK.nF.proj')
$names = @($projects.Project.ItemGroup.ChildNodes |
    Where-Object { $_.LocalName -like '*Library' -or $_.LocalName -eq 'BuildTool' } |
    ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_.Include) } | Sort-Object -Unique)
if ($names.Count -ne 34) { throw 'Update the package documentation inventory for the changed package set.' }
foreach ($name in $names) {
    $file = Join-Path $OutputPath "$name\README.md"
    & (Join-Path $PSScriptRoot 'New-PackageReadme.ps1') -PackageId $name -OutputPath $file
    $text = Get-Content -LiteralPath $file -Raw -Encoding UTF8
    if (-not $text.Contains($projectIntroduction)) { throw "Stale project introduction: $name" }
    $opening = (($text -split '## 설치', 2)[0]).Replace("`r`n", "`n")
    if (-not $opening.Contains($projectIntroduction.Replace("`r`n", "`n") + "`n`n이 패키지는 ") -or
        $opening.Contains('애플리케이션에 필요한 역할과 전송 방식에 맞춰 다른 패키지와 조합해 사용하십시오.')) {
        throw "Missing separate, package-specific introduction: $name"
    }
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
