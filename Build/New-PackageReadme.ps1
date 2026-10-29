param(
    [Parameter(Mandatory = $true)][string]$PackageId,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [string]$Version = '0.1.0-preview.1'
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
[xml]$metadata = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'PackageMetadata.props')
$repositoryUrl = [string]$metadata.Project.PropertyGroup.RepositoryUrl
$catalog = Get-Content -LiteralPath (Join-Path $root 'README.md') -Raw -Encoding UTF8
$pattern = '(?m)^\| \[' + [regex]::Escape($PackageId) + '\]\([^)]+\) \| ([^|]+) \| ([^|]+) \|'
$entry = [regex]::Match($catalog, $pattern)
if (-not $entry.Success) { throw "Package is not documented in the root catalog: $PackageId" }
$summary = $entry.Groups[2].Value.Trim()
$group = $PackageId.Split('.')[2]
$mapping = $PackageId.EndsWith('.Mapping')
$tool = $PackageId.EndsWith('.SCL')
$introduction = if ($tool) { 'PC에서 실행하는 명령줄 도구입니다.' } elseif ($mapping) {
    'PC에서 빌드할 때 모델 연결 코드를 만드는 소스 생성기입니다.'
} else { 'MCU에서 실행하는 nanoFramework 라이브러리입니다.' }
$projectFolder = Join-Path $root "Source\$group\$PackageId"
$extension = if ($mapping -or $tool) { 'csproj' } else { 'nfproj' }
[xml]$project = Get-Content -LiteralPath (Join-Path $projectFolder "$PackageId.$extension") -Encoding UTF8
$dependencies = @($project.SelectNodes("//*[local-name()='ProjectReference']") |
    Where-Object { $_.OutputItemType -ne 'Analyzer' } |
    ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_.Include) })
if ($mapping) { $dependencies = @($PackageId.Substring(0, $PackageId.Length - '.Mapping'.Length)) }
$dependencyText = if ($dependencies.Count) {
    ($dependencies | ForEach-Object { '- `' + $_ + '`' }) -join "`n"
} else { '다른 VagabondK.nF 런타임 패키지에 직접 의존하지 않습니다.' }

$notes = @{
    Modbus = 'RTU·ASCII·TCP 모두 Client(Master)와 Server(Slave)를 지원합니다. 역할과 전송 패키지를 함께 선택하십시오. RTU/ASCII에서 실제 UART를 사용하려면 Serial도 필요합니다. 주소는 0 기반이며, 쓰기 시간 초과가 상대의 미실행을 뜻하지는 않습니다.'
    DNP3 = 'TCP·시리얼 모두 Master와 Outstation을 지원합니다. 역할 하나와 전송 하나를 선택하십시오. 포트·소켓의 생성과 수명, 출력 제어 정책은 앱이 관리합니다. 시리얼은 DNP3 프레임이며 Modbus RTU/ASCII가 아닙니다.'
    BACnet = '지원 범위를 제한한 BACnet/IP Device 구현으로, AnalogValue와 BinaryValue 객체를 제공합니다. UDP 통신에는 IP 패키지도 필요합니다. 원격 Client API와 분할 메시지 재조립은 제공하지 않습니다.'
    SNMP = 'SNMPv1/v2c Agent와 v2c Trap 송신을 제공합니다. UDP 통신에는 UDP 패키지도 필요합니다. Manager와 Trap 수신기는 제공하지 않으며 GetBulk는 v2c에서만 지원합니다.'
    MMS = '일반 MMS Client/Server 기능 중 명시된 범위만 제공합니다. 역할에 맞는 패키지를 선택하고, TCP 통신이 필요하면 TCP 패키지를 추가하십시오. IMmsChannel을 직접 구현해 사용할 수도 있습니다. IEC 61850 패키지에는 의존하지 않습니다.'
    IEC61850 = 'MMS 기반 제한형 IEC 61850 Client/Server입니다. 해당 역할의 MMS 패키지도 필요하며 TCP는 MMS.TCP를 사용합니다. DataSet·URCB와 SPC/DPC/APC 제어를 제공하지만 전체 IEC 61850 적합성 인증 구현은 아닙니다.'
    MQTT = 'MQTT 런타임은 3.1/3.1.1/5 클라이언트이며 브로커를 제공하지 않습니다. Sparkplug는 별도 제한형 스칼라 프로파일입니다. Sparkplug 런타임은 MQTT DLL에 직접 의존하지 않으므로 전송 클라이언트를 별도로 선택해야 합니다.'
}
$combinations = @{
    Modbus = @('RTU Master: Modbus.Client + Modbus.RTU + Modbus.Serial', 'RTU Slave: Modbus.Server + Modbus.RTU + Modbus.Serial', 'ASCII: RTU 대신 Modbus.ASCII 선택', 'TCP: Modbus.Client 또는 Modbus.Server + Modbus.TCP')
    DNP3 = @('TCP: DNP3.Master 또는 DNP3.Outstation + DNP3.TCP', '시리얼: DNP3.Master 또는 DNP3.Outstation + DNP3.Serial')
    BACnet = @('BACnet + BACnet.IP')
    SNMP = @('SNMP + SNMP.UDP')
    MMS = @('MMS.Client 또는 MMS.Server + MMS.TCP', '사용자 채널: 역할 패키지 + IMmsChannel 구현')
    IEC61850 = @('Client: IEC61850.Client + MMS.Client + MMS.TCP', 'Server: IEC61850.Server + MMS.Server + MMS.TCP')
    MQTT = @('MQTT: MQTT 클라이언트 단독 사용', 'JSON 매핑: MQTT.Mapping + 선택한 MQTT 전송 클라이언트', 'Sparkplug 매핑: MQTT.Sparkplug.Mapping + 선택한 MQTT 전송 클라이언트')
}
$compositionText = ($combinations[$group] | ForEach-Object { '- ' + $_ }) -join "`n"
$groupNotes = $notes[$group]
$compositionIntro = '아래 이름에는 `VagabondK.nF.` 접두사를 붙입니다. 공통 코어는 의존성으로 설치됩니다.'
$dependencyNote = 'NuGet은 이 패키지뿐 아니라 의존하는 패키지도 함께 설치합니다. 필요한 nanoFramework 시스템 패키지와 버전은 패키지 메타데이터에서 확인할 수 있습니다.'
$install = @"
Visual Studio의 nanoFramework 프로젝트에서 NuGet 패키지 관리자를 열고 **사전 릴리스 포함**을 선택하십시오.
이 평가 패키지를 보관한 로컬 피드와 공식 nanoFramework 의존성을 받을 수 있는 소스를 등록한 뒤 설치합니다.
패키지 관리자 콘솔에서는 대상 nanoFramework 프로젝트를 선택하고 다음 명령을 사용합니다.

``````powershell
Install-Package $PackageId -Version $Version
``````
"@
$requirements = 'MCU용 .NET nanoFramework 패키지입니다. 일반 .NET 앱용 패키지가 아닙니다. 설치되는 nanoFramework 시스템 패키지와 장치 펌웨어의 네이티브 어셈블리 버전이 호환되어야 합니다. UART·소켓·TLS 사용 시 해당 기능이 포함된 펌웨어가 필요합니다.'
if ($mapping) {
    $requirements = 'netstandard2.0 기반이며 Roslyn 4.12를 사용합니다. 생성기 자체는 MCU에서 실행하지 않고, 생성 코드에서도 리플렉션을 사용하지 않습니다. 패키지에 포함된 빌드 설정이 생성기를 Analyzer로 등록합니다. 선언용 특성은 nanoFramework의 ExcludeType으로 MCU PE 이미지에서 제외하고, 생성된 C# 7.3 바인딩은 앱의 일부로 컴파일합니다. 역할·전송 패키지는 별도로 선택하십시오. 속성 대입만으로 통신하거나 자동 폴링하지 않으며, 명시적인 전송·수신 적용과 앱의 잠금·검증·연결 수명 정책이 필요합니다.'
}
if ($tool) {
    $groupNotes = '이 도구는 PC에서 SCL 파일을 읽어 MCU 앱에 포함할 C# 소스를 만듭니다. 도구를 설치하는 것만으로 MCU 프로젝트에 런타임 패키지가 추가되지는 않습니다.'
    $compositionIntro = '생성한 코드를 사용하는 MCU 프로젝트에는 다음 패키지가 필요합니다. 이름에는 `VagabondK.nF.` 접두사를 붙이십시오.'
    $compositionText = '- IEC61850.Server + MMS.Server' + "`n" + '- TCP 통신을 사용할 때: MMS.TCP 추가'
    $dependencyNote = '도구를 실행할 PC에는 .NET 10 SDK가 필요합니다. 생성 코드를 사용하는 MCU 프로젝트의 의존성은 별도로 설치하십시오.'
    $install = @"
.NET 10 SDK가 설치된 PC에서 실행합니다. 패키지를 보관한 로컬 피드를 사용하려면 --add-source에 해당 경로를 추가하십시오.

``````powershell
dotnet tool install $PackageId --version $Version --tool-path .\tools
.\tools\vk-nf-iec61850-scl input.scd GeneratedIec61850Model.cs
``````
"@
    $requirements = 'PC 전용 .NET 10 명령줄 도구입니다. MCU에 설치하거나 배포하지 않습니다. 제한된 SCL을 C# 7.3 정적 Server 모델로 변환하며 전체 SCL 가져오기·의미 검증·IEC 61850-6 적합성 검증 도구는 아닙니다.'
}
$projectDoc = if (Test-Path -LiteralPath (Join-Path $projectFolder 'README.md')) { "Source\$group\$PackageId\README.md" } else { "Source\$group\README.md" }
$documentation = & (Join-Path $PSScriptRoot 'Get-PackageDocumentation.ps1') -PackageId $PackageId
$sampleGuide = if ($mapping) {
    $sample = "Samples\$group\$PackageId"
    if (-not (Test-Path -LiteralPath (Join-Path $root $sample))) {
        throw "Missing mapping sample: $sample"
    }
    '생성 API를 사용하는 실행 샘플은 [매핑 샘플](' + $repositoryUrl + '/tree/HEAD/' + $sample.Replace('\', '/') +
        ')에서 확인할 수 있습니다. Source의 동명 프로젝트는 PC 생성기이고 Samples의 프로젝트는 MCU 앱입니다.'
} else { '' }
$text = @"
# $PackageId

**주요 기능:** $summary

VagabondK.nF는 .NET nanoFramework를 편리하게 사용하기 위한 라이브러리 세트입니다.
$introduction 애플리케이션에 필요한 역할과 전송 방식에 맞춰 다른 패키지와 조합해 사용하십시오.

## 설치

$install

## 용도와 구성

$groupNotes

$compositionIntro

$compositionText

### 이 패키지의 직접 의존성

$dependencyText

$dependencyNote

## 기능과 사용법

$documentation

## 실행 환경과 제한

$requirements

$sampleGuide

**$Version 평가용 사전 릴리스**입니다. API 호환성과 공개 피드 지원 정책은 확정되지 않았습니다.
산업 현장 적용 검증이나 안전·적합성 인증을 마친 제품이 아닙니다. 통신 성공이 실제 설비 동작의 완료를 보장하지는 않습니다.

## 상세 문서와 라이선스

이 문서에는 패키지의 기능·사용 예·주요 제약을 함께 담았습니다.
추가 샘플과 개발 기록은 [패키지 상세 문서]($repositoryUrl/blob/HEAD/$($projectDoc.Replace('\', '/')))와
[라이브러리 그룹 안내]($repositoryUrl/blob/HEAD/Source/$group/README.md)에 있습니다.
[소스 저장소]($repositoryUrl)에서 전체 코드를, [이슈 페이지]($repositoryUrl/issues)에서 문제 제보와 논의를 확인할 수 있습니다.
저장소 빌드에는 .NET 10 SDK와 Visual Studio 2026의 nanoFramework 도구를 사용합니다.
MCU에서는 일반 .NET 10이 아니라 nanoFramework가 실행됩니다.

MIT 라이선스이며, 외부 의존성에는 각각의 라이선스가 적용됩니다.
"@
$directory = Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath))
$null = New-Item -ItemType Directory -Path $directory -Force
[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath), $text, [Text.UTF8Encoding]::new($false))
