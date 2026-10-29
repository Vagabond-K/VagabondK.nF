# 빌드·패키징·검증

[전체 프로젝트](../README.md)

VagabondK.nF는 nanoFramework를 편리하게 사용하기 위한 라이브러리 세트입니다.
이 문서는 각 라이브러리에 공통으로 적용되는 개발 환경·문서 관리·빌드·패키징·검증 절차를 설명합니다.
개별 기능과 사용법은 루트 README에서 안내하는 그룹별 문서를 참조하십시오.
아래 명령과 코드 경로는 모두 **저장소 루트 기준**입니다.

## 개발 환경과 솔루션

| 항목 | 기준 |
|---|---|
| 운영 환경 | Windows, 스크립트 실행은 PowerShell 7 |
| IDE·빌드 | Visual Studio 2026과 .NET nanoFramework 확장, Visual Studio MSBuild |
| PC SDK | .NET 10 SDK |
| 패키지 | NuGet CLI와 각 프로젝트에 필요한 nanoFramework 패키지 |
| 대상 장치 | 시스템 패키지와 호환되는 nanoFramework 펌웨어 |

nanoFramework 자체는 VS 2022도 지원하지만 이 솔루션의 PC 프로젝트는 `net10.0`이므로
전체 솔루션 개발은 VS 2026을 기준으로 합니다. MCU에서 사용할 수 있는 언어 기능과 PC 빌드에 필요한 SDK 버전은 별개의 조건입니다.
기존 `.sln`은 그대로 사용합니다. 헤더의 Format Version 12.00을 IDE 버전에 맞춰 바꾸거나
`.slnx`로 전환할 필요는 없습니다.

```powershell
nuget restore .\packages.config -PackagesDirectory .\packages -NonInteractive
$msbuild = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe'
$env:MSBUILDDISABLENODEREUSE = '1'
# 라이브러리와 PC 생성기·도구만 빌드합니다. 생성기의 의존성 복원도 포함합니다.
& $msbuild .\VagabondK.nF.proj /p:Configuration=Release /nr:false /v:minimal
# 복원된 환경에서 샘플·테스트를 포함한 전체 솔루션을 빌드합니다.
& $msbuild .\VagabondK.nF.sln /t:Build /p:Configuration=Release '/p:Platform=Any CPU' /m /nr:false /v:minimal
```

Visual Studio에서는 `VagabondK.nF.sln`을 열고 NuGet 복원을 수행합니다.
SDK 형식 PC 프로젝트의 의존성도 복원해야 하며 `dotnet run --project ...`는 해당 프로젝트를 자동 복원합니다.
위 MSBuild 경로는 설치 환경에 맞게 지정하십시오.
출력은 각 프로젝트의 `bin\Release`에 생성되며 MCU 프로젝트는 DLL/EXE와 PE·배포 이미지를 만듭니다.
빌드는 장치 배포·실행 시험이 아닙니다.

nanoFramework 메타데이터 처리기가 읽은 DLL을 재사용 중인 MSBuild 프로세스가 잠글 수 있으므로
반복 검증에는 노드 재사용을 끄는 것을 권장합니다. `/nr:false`와 위 환경 변수는 이후 실행되는 빌드에 적용됩니다.
이미 실행 중인 IDE·빌드가 파일을 잠근 경우 그 작업이 끝났는지 확인해야 하며
다른 사용자의 MSBuild 프로세스를 일괄 종료하지 마십시오.

## 이름·공유 소스·API 문서

접두사는 nanoFramework의 소문자 n·대문자 F를 반영한 **`VagabondK.nF`**입니다.
네임스페이스·프로젝트·어셈블리·패키지에 동일하게 적용합니다.
프로토콜 약어의 대소문자(`DNP3`, `BACnet`, `SNMP`)는 유지하며 타입 이름은
`Dnp3Master`, `BacnetDevice`, `SnmpAgent`, `IBacnetValueStore`처럼 PascalCase를 사용합니다.

`Source\Shared\VagabondK.nF.Shared.shproj`는 사용하는 프로젝트에서 `.projitems`를 가져와 빌드하는 공유 프로젝트입니다.
현재 Modbus·DNP3와 관련 PC 테스트에 소스를 직접 컴파일하며 별도 DLL·NuGet을 만들지 않습니다.

공개 API 설명은 한국어 XML 주석으로 관리합니다. API 식별자와 표준 명칭은 원문을 유지하고,
일반 설명은 자연스러운 한국어로 작성합니다. 같은 개념에는 같은 용어를 사용합니다.
라이브러리·PC 생성기·SCL 도구는 동명의 `.xml` 문서를 출력하며 런타임 NuGet에도 포함합니다.
API를 변경할 때는 `<summary>`·매개변수·반환값 설명뿐 아니라 단위·제약·소유권·실패 조건도 코드와 함께 갱신하십시오.
샘플·테스트 주석은 주요 흐름과 검증 의도를 설명하며 PC·가상 런타임·실물을 구분합니다.

매핑 실행 샘플도 `VagabondK.nF.<그룹>.Mapping` 이름을 사용합니다.
MQTT 두 샘플의 이전 `.DataSharing` 폴더·프로젝트·네임스페이스는 `.Mapping`으로 바뀌었습니다.
`Source`의 동명 생성기와 `Samples`의 실행 앱을 경로로 구분하며,
통합 계약 테스트 `VagabondK.nF.MQTT.DataSharing.Tests`와 하드웨어 스크립트 이름은 유지합니다.

## 로컬 사전 릴리스 패키지

외부에 게시하지 않고 전체 배포 후보를 만들려면 다음 명령을 사용합니다.

```powershell
.\Build\Prepare-Packages.ps1
```

새 `bin\release-candidate-<시각>` 폴더에 패키지를 생성하고, 소스 참조 없이 복원한
런타임·매핑 프로젝트의 MCU 빌드와 SCL 도구 설치·실행을 확인합니다.
검증을 통과한 34개 패키지는 `packages`에 모으며, 메타데이터·README·아이콘을 검사한 뒤
루트 `sha256.csv`에 해시와 설명을 기록합니다. 세부 로그는 `logs`에 남습니다.
각 패키지에 실제로 포함한 README가 검토용 `readmes\<패키지>\README.md`와 같은지도 확인합니다.
실패하면 즉시 중단하며 성공한 배포 후보로 표시하지 않습니다.
이 명령은 NuGet.org 업로드·GitHub 푸시·보드 배포를 수행하지 않습니다.
온라인 저장소의 공개 여부와 문서 링크 접근성은 별도로 확인해야 합니다.

평가 버전은 **`0.1.0-preview.1`**, 어셈블리 버전은 `0.1.0.0`입니다.
API 호환성·공개 패키지 피드 지원 정책은 확정되지 않았습니다. 패키지의 외부 게시·업로드·서명이나 산업 인증은 수행하지 않았습니다.
프로젝트 라이선스는 [MIT](../LICENSE)이며 타사 의존성의 라이선스는 원본 패키지를 따릅니다.

| `Pack.ps1 -LibrarySet` | 런타임 패키지 구성 |
|---|---|
| `Modbus` | 공통·Client·Server·RTU·ASCII·TCP·Serial 7개 |
| `DNP3` | 공통·Master·Outstation·TCP·Serial 5개 |
| `BACnet` | 공통·IP 2개 |
| `SNMP` | 공통·UDP 2개 |
| `MMS` | 공통·Client·Server·TCP 4개 |
| `IEC61850` | IEC 공통·Client·Server 3개와 필요한 MMS 4개 |
| `MQTT` | MQTT 1개 |
| `Sparkplug` | MQTT.Sparkplug 1개 |

중복을 제외한 MCU 런타임은 총 25개입니다. 선택형 매핑 생성기 8개와 PC SCL 도구는 별도로 패키징합니다.
Sparkplug DLL은 MQTT DLL을 직접 참조하지 않으므로 필요한 전송 클라이언트는 별도로 선택합니다.

```powershell
.\Build\Pack.ps1 -LibrarySet Modbus
.\Build\Test-Packages.ps1 -LibrarySet Modbus
.\Build\Pack.ps1 -LibrarySet DNP3
.\Build\Test-Packages.ps1 -LibrarySet DNP3
.\Build\Pack.ps1 -LibrarySet BACnet
.\Build\Test-Packages.ps1 -LibrarySet BACnet
.\Build\Pack.ps1 -LibrarySet SNMP
.\Build\Test-Packages.ps1 -LibrarySet SNMP
.\Build\Pack.ps1 -LibrarySet MMS
.\Build\Test-Packages.ps1 -LibrarySet MMS
.\Build\Pack.ps1 -LibrarySet IEC61850
.\Build\Test-Packages.ps1 -LibrarySet IEC61850
.\Build\Pack.ps1 -LibrarySet MQTT
.\Build\Test-Packages.ps1 -LibrarySet MQTT
.\Build\Pack.ps1 -LibrarySet Sparkplug
.\Build\Test-Packages.ps1 -LibrarySet Sparkplug
.\Build\Pack-Iec61850SclTool.ps1
```

`Pack.ps1`은 Release 라이브러리를 다시 빌드하고 선택한 세트를 `bin\nuget\<세트>`에 출력합니다.
`.nupkg`, `sha256.csv`, `specs\*.nuspec`을 남기며 같은 버전의 기존 패키지를 덮어쓰지 않습니다.
다시 만들 때는 `-OutputPath .\bin\nuget-candidate2`, 검증에는 `-FeedPath .\bin\nuget-candidate2`처럼
새 경로를 전달하십시오. MSBuild 자동 탐색이 실패하면 두 스크립트 모두 `-MSBuildPath`를 지정합니다.

런타임 패키지는 자신의 DLL·PE·PDBX·XML, 라이선스와 패키지 전용 README를 포함하고 타사 바이너리를 재포장하지 않습니다.
모든 패키지는 `Build\nugetIcon.png`를 공통 아이콘으로 포함합니다. 이 파일은
`VagabondK.Indicators` 프로젝트의 `nugetIcon.png`를 복사한 것이며, 패키징할 때 외부 프로젝트 경로를 참조하지 않습니다.
런타임·매핑 생성기·SCL 도구의 전용 README는 `New-PackageReadme.ps1`이 패키징할 때 생성합니다.
루트 프로젝트 목록의 소개와 공통 템플릿을 바탕으로 패키지별 설치 방법·역할·직접 의존성·사용 순서·제한을 안내합니다.
`Get-PackageDocumentation.ps1`은 해당 프로젝트의 상세 문서에서 기능·API 사용 예·지원 범위와 제약을 가져옵니다.
개별 문서가 없는 패키지는 같은 프로토콜의 그룹 문서를 사용하되, 런타임에는 매핑 전용 절을 넣지 않습니다.
개발·시험·과거 성능 기록은 제외하고, 상대경로 링크는 저장소의 절대 URL로 바꿉니다.
DNP3·BACnet·SNMP와 일부 공통·전송 패키지에는 실제 API를 사용하는 시작 예제도 추가합니다.
다른 문서를 열지 않아도 기본 사용법을 이해할 수 있도록 패키지 안에 본문과 코드 예제를 함께 담습니다.
`Test-PackageReadmes.ps1`은 34개 문서의 상세 설명·코드 예제·API 이름·링크 형식을 검사합니다.
README만 먼저 생성·검토하려면 다음처럼 새 출력 경로를 지정합니다.

```powershell
.\Build\Test-PackageReadmes.ps1 -OutputPath .\bin\package-readmes-review
```

원본 README나 생성 템플릿을 수정해도 기존 `.nupkg` 안의 문서는 바뀌지 않습니다.
현재 문서를 포함한 배포 후보가 필요하면 `Prepare-Packages.ps1`로 새 경로에 다시 생성하십시오.
이 스크립트는 생성한 34개 문서와 패키지 내부 README의 본문 일치도 검사합니다.
기존 피드·과거 검증 패키지를 덮어쓰거나 외부에 업로드하지 않습니다.
SDK 패키징에도 PowerShell 7의 `pwsh` 명령이 필요합니다.
런타임 문서는 출력 폴더의 `specs\readmes\<패키지>\README.md`, 생성기와 도구 문서는 프로젝트 `obj` 아래에 남습니다.
프로젝트·저장소 URL은 `PackageMetadata.props`에서 공통으로 관리합니다.
`Authors`와 `Company`는 모두 `Vagabond K`를 사용합니다. MCU 어셈블리의 회사명도 같은 값으로 설정합니다.
검색 태그와 첫 평가판의 릴리스 노트도 공통 설정으로 관리합니다.
런타임 패키지의 설명은 루트 프로젝트 목록에서 가져오며, 생성기·도구는 각 프로젝트의 설명을 사용합니다.
현재 예정 주소는 `https://github.com/Vagabond-K/VagabondK.nF`입니다.
패키지 README는 이 주소를 기준으로 상세 문서와 이슈 페이지의 절대 링크를 생성합니다.
문서 링크의 `HEAD`는 기본 브랜치를 가리키며, 공개 배포 전에는 저장소 공개 여부와 링크 접근성을 확인하십시오.
내부·시스템 의존성은 빌드한 정확한 버전으로 고정하고 전이 의존성은 원본 패키지로 복원합니다.
평탄한 `lib` 구조는 nanoFramework 패키지 관례이며 일반 .NET TFM 권장 경고 `NU5101`이 나올 수 있습니다.
패키지 분석이나 다른 경고는 숨기지 않습니다. 일반 .NET 앱용 패키지가 아니며 장치 펌웨어 호환성을 확인해야 합니다.

### NuGet 패키지만 사용하는 프로젝트 검증

`Test-Packages.ps1`은 SHA-256·바이너리 3개·XML과 현재 Release 산출물의 바이트 일치를 확인합니다.
로컬 피드와 저장소 `packages`만 사용해 매번 새 경로로 의존성을 복원하고
`Packaging\VagabondK.nF.<세트>.PackageSmoke`를 소스 참조 없이 빌드해 배포 이미지까지 확인합니다.
각 검증 프로젝트에서는 선택하지 않은 역할·전송 DLL이 출력에 섞이지 않았는지도 확인합니다.

`Packaging`은 솔루션에 포함하지 않습니다. 별도의 의존성 복원 경로와 `SmokePackagesPath` 설정이 필요한 프로젝트이므로
직접 빌드하지 말고 해당 검증 스크립트를 사용하십시오.
생성한 이미지는 패키지 사용 여부를 검증하기 위한 것으로, 보드에 배포하지 않습니다.
검증 결과와 별도로 복원한 의존성은 `bin` 아래에 남습니다. 피드 등록·외부 업로드·보드 접근은 하지 않습니다.

### 다른 앱에 매핑 패키지 설치

8개 `VagabondK.nF.<그룹>.Mapping`은 같은 버전의 대응 런타임을 자동 설치합니다.
IEC 매핑을 설치하면 의존성에 따라 MMS도 함께 복원합니다. 생성기는 Analyzer 전용이고 MCU에 포함하지 않습니다.
Client/Server·Master/Outstation·전송 DLL은 별도로 선택하며 Modbus Data 변환은 코어에 포함됩니다.
이 의존성은 NuGet에만 적용하고 소스 Analyzer `ProjectReference`에 자동 적용하지 않습니다.
매핑별 패키징·검증 스크립트와 사용 조건은 각 그룹 문서를 참조하십시오.

MQTT와 Sparkplug는 `Pack-MqttMapping.ps1`·`Test-MqttMapping.ps1`에서
`-LibrarySet MQTT` 또는 `-LibrarySet Sparkplug`로 선택합니다.
Sparkplug 매핑은 Sparkplug 런타임에만 의존하며 MQTT 전송 DLL을 자동으로 설치하지 않습니다.

Visual Studio NuGet 관리자에서 `bin\nuget\<세트>`의 절대 경로를 로컬 소스로 등록하고
사전 릴리스 표시·공식 nanoFramework 의존성 다운로드 소스를 활성화합니다.
공개 소스는 원본 의존성 다운로드에 사용하며 로컬 패키지를 업로드하지 않습니다.
RTU Client는 `Modbus.Client + Modbus.RTU + Modbus.Serial`,
TCP Server는 `Modbus.Server + Modbus.TCP`를 설치하며 공통 코어는 자동 복원합니다.
DNP3는 코어+역할+전송, BACnet은 코어+IP, SNMP는 코어+UDP를 선택합니다.

NuGet CLI `install`만으로 `.nfproj` 참조는 추가되지 않습니다.
CLI로 복원할 때는 패키지를 사용하는 프로젝트의 `HintPath`·`Private`를 설정해야 하므로 패키지 전용 검증 프로젝트를 참고하십시오.
`packages.config`로 매핑을 사용하는 프로젝트는 생성기의 `.targets`를 가져오는지도 확인해야 합니다.
SDK 형식의 `PackageReference`는 Analyzer를 자동으로 추가합니다.

## PC 회귀와 매핑 샘플

PC 테스트는 .NET 10에서 실제 라이브러리 소스를 링크해 실행합니다.
MQTT·Sparkplug 런타임은 별도 PC DLL을 참조하고 샘플의 모델·앱 정책은 소스로 링크합니다.
기본 16개 회귀 프로젝트는 외부 장비·피어 없이 실행합니다.
MQTT 데이터 공유 계약과 두 매핑 생성기 테스트도 포함합니다. 독립 피어를 이용한 상호운용 시험에는 별도의 의존성과 준비가 필요합니다.

```powershell
.\Build\Test-All.ps1
.\Build\Test-All.ps1 -Filter '*DNP3*'
dotnet run -c Release --project .\Tests\MQTT\VagabondK.nF.MQTT.DataSharing.Tests
.\Build\Test-MappingSamples.ps1
```

매핑 샘플 스크립트는 PC 테스트 8개(MMS/IEC·MQTT/Sparkplug 생성기·계약 포함)를 실행하고 MCU 샘플 9개를 빌드합니다.
장치 배포는 하지 않습니다.
[매핑 대조표](../Samples/MappingCoverage.md)와 [장애·복구 검증표](../Tests/FaultRecovery.md)는
지원 형식·역할과 PC/nanoCLR/실물 경계를 정리합니다.
테스트 코드의 존재, 과거 실행 기록, 이번에 다시 실행한 결과를 구분하십시오.
정상 통신을 장시간 반복한 시험을 오류 주입·복구 시험의 결과로 간주해서는 안 됩니다.

## PC 장시간 반복·자원 안정성

`Test-Soak.ps1`은 기본 Modbus·DNP3·BACnet·SNMP·MMS·IEC61850을 별도 PC 프로세스로 순차 실행합니다.
기본 회귀·CI에는 장시간 시험을 포함하지 않습니다.
공통 계측기 집계·예외·한도 검사는 `Tests\Common\VagabondK.nF.Soak.Tests`에 있으며 기본 회귀에 포함합니다.
MQTT는 `-Protocol MQTT`로 별도 선택합니다.

```powershell
# 프로토콜당 기본 5분과 워밍업: 기본 6개는 약 30분 이상 소요됩니다.
.\Build\Test-Soak.ps1
.\Build\Test-Soak.ps1 -Seconds 10 -SampleSeconds 2
.\Build\Test-Soak.ps1 -Protocol Modbus,MMS -Seconds 3600 -SampleSeconds 30
.\Build\Test-Soak.ps1 -Protocol MQTT
```

기본 6개 프로토콜은 회차마다 정상 통신(`normal`), 비정상 입력(`malformed`),
전송 계층 재생성·복구(`transport-cycle`) 시나리오를 수행한 뒤 측정값을 기록합니다.
실행 시간·표본 간격은 회차 길이만큼 늘어날 수 있습니다.

| 프로토콜 | normal | malformed | transport-cycle |
|---|---|---|---|
| Modbus | TCP Write/Read 100쌍 | MBAP 중간 EOF·길이/ID·느린 수신 | 소켓 폐기 후 새 연결 교환 |
| DNP3 | TCP Class 0 Poll 50회 | 잡음/CRC·부분 프레임 만료 | 피어·통신 종단 오류 후 새 세션 |
| BACnet | UDP Who-Is/I-Am 100회 | BVLC/NPDU/APDU·거절 응답 | 소켓 재생성·손상 뒤 정상 요청 |
| SNMP | UDP SET/GET 100쌍 | BER 경계·변이·무작위 입력 | 소켓 재생성·오류 뒤 SET/Trap |
| MMS | 연결 협상 뒤 Read 100회 | 보고 경계·재현 가능한 퍼징 | 콜백 실패 뒤 새 연결·Release |
| IEC61850 | Association 뒤 Boolean Read 100회 | TPKT 경계·분할·잘림·Timeout/EOF | 종료·서비스 오류 복구와 새 연결 |

기본 6개 프로토콜의 정상 통신 회차는 종료할 때 연결을 닫습니다.
연결을 무기한 유지하거나 전체 기능·동시 클라이언트 부하·보고 전송만을 시험하는 구성은 아닙니다.
UDP의 transport-cycle은 재접속이 아니라 소켓 재생성·오류 후 계속 처리입니다.
MQTT의 지속 연결·TLS·세션 수명주기와 준비 조건은 [MQTT 문서](../Source/MQTT/README.md)를 참조하십시오.

워밍업 3회 후 기준을 잡고 표본마다 명시적 GC 뒤 관리 힙·누적 할당·Private Bytes·핸들·스레드를 기록합니다.
기본 실패 기준은 검증 예외나 기준 대비 관리 힙 **8 MiB**, 핸들 **32개**, 스레드 **8개** 초과입니다.
`-MaxManagedGrowthBytes`·`-MaxHandleGrowth`·`-MaxThreadGrowth`로 변경하며 제품 자원 보장은 아닙니다.
프로토콜당 지정 시간+120초를 넘으면 해당 자식 프로세스 트리를 종료하고 실패합니다.

기본 결과는 `bin\soak\run-...`의 JSONL·stdout/stderr·`summary.json`입니다.
`-OutputDirectory`로 바꿀 수 있으며 시나리오별 횟수·총/최대 시간·정상 교환 처리량·최대 지연을 기록합니다.
교환은 Read/Poll 1회 또는 Write/Read·SET/GET·Who-Is/I-Am 한 쌍이므로 프로토콜끼리 같은 작업으로 비교하면 안 됩니다.
`ExchangesPerSecond`는 교환 시간 기준으로 연결 생성·GC·다른 시나리오를 제외하고
`CyclesPerSecond`도 해당 시나리오에 걸린 시간을 기준으로 합니다. 전체 부하를 판단할 때는 실행 횟수와 `ElapsedSeconds`를 함께 보십시오.

Private Bytes에는 JIT·네이티브 소켓·캐시도 포함되며 GC 힙과는 별개입니다.
강제 GC와 계측 자체가 결과에 영향을 줍니다. 짧은 시간 동안 메모리 사용량이 일정하거나 설정한 한도를 넘지 않았다고 해서 누수가 없다고 보장할 수는 없습니다.
PC 관측은 nanoCLR·MCU 메모리·스케줄링·UART/RS-485 결과를 대체하지 않습니다.

## nanoCLR 가상 검증

```powershell
.\Build\Test-Virtual.ps1
```

nanoFramework 확장이 있는 Visual Studio MSBuild와 `dotnet tool install -g nanoclr`가 필요합니다.
CI에는 포함하지 않습니다. `Tests\Common\VagabondK.nF.Virtual.Tests`로 실제 nanoFramework 빌드 결과를
가상 런타임에서 실행하고 **`VIRTUAL PASS`** 완료 마커를 확인합니다.
Modbus RTU/ASCII·DNP3 양 역할·BACnet·SNMP·MMS Client/Server의 경계·왕복·오류 복구를 검사합니다.

RTU/ASCII 손상 쓰기 거절·값 보존, DNP3 CRC·부분 프레임 만료 뒤 교환,
BACnet/SNMP 손상 뒤 정상 읽기/쓰기, MMS TPKT의 Faulted/Reset·새 Association을 포함합니다.
메모리 전송 시험이며 실제 UART·RS-485·Wi-Fi·소켓 드라이버 검증을 대신하지 않습니다.
MQTT 가상 검증 조건과 명령은 [MQTT 상세 문서](../Source/MQTT/VagabondK.nF.MQTT/README.md)를 참조하십시오.

## 실물 시험과 복원

실물 스크립트는 연결된 보드 앱·설정을 바꿀 수 있으므로 문서·배선·포트·대상을 먼저 확인하고
명시적 `-AllowDeviceChanges` 절차를 따르십시오. 이 안내를 읽는 것만으로 시험 실행을 승인한 것으로 간주하지 않습니다.
MCU 실행 프로젝트는 `.bin`이 없으면 Release Rebuild하고 완료 마커·실제 응답까지 확인해야 합니다.

COM3/COM6 평가 구성의 `Test-Dnp3GoSerial.ps1`·`Test-BacnetHardware.ps1`·`Test-SnmpHardware.ps1` 등은
성공·실패 모두 `Build\Restore-ModbusHardware.ps1`로 원래 Modbus 앱을 복원합니다.
복원 이미지는 `bin\hardware\restore-Modbus.SerialServer.bin`이며 SHA-256을 확인한 뒤 배포합니다.
개별 실행·복원 결과와 한계는 각 샘플·실물 Fixture 문서를 따릅니다.
장치·피어·실행 기록을 구분하고 시험하지 않은 구성을 성공으로 일반화하지 마십시오.

2026-10-29 추가 견고성 보드 시험의 실행 명령·결과 폴더·외부 피어·제약은
[매핑 대조표의 실물 시험 절](../Samples/MappingCoverage.md#추가-견고성-실물-시험)에 모았습니다.
MMS/IEC의 실제 생성 Client/Server 상호운용은 `Test-MappingInteropHardware.ps1`로 네 역할을 순차 실행합니다.
PC 전용 `Test-MappingInterop.ps1`과 구분하며 libiec61850 빌드 도구·고정 실행 파일의 방화벽 허용이 필요합니다.
수신 콜백의 처리 시간도 통신 기한을 소모할 수 있으므로 느린 검증·예외 경로는 독립 스냅샷을 보관한 뒤
통신 작업 밖에서 처리합니다. 사용자 콜백을 강제로 중단하거나 작업 기한을 늘려 성공으로 간주하지 않습니다.

## 기존 사전 릴리스 참조를 갱신할 때

이전 `NF` 참조·using은 `nF`로 바꾸고 앱·의존 DLL을 함께 재빌드합니다.
NuGet ID는 대소문자를 구분하지 않아 이름 대소문자만으로 새 패키지가 생기지 않습니다.
이전 동일 버전 캐시 대신 격리 피드·복원 경로로 검증하고 이미 외부 배포한 패키지의 변경에는 새 버전을 사용하십시오.

매핑 생성기 8개의 이름은 `VagabondK.nF.<그룹>.Mapping`으로 통일했습니다.
옛 패키지의 `.targets` Import·Analyzer DLL 경로도 갱신합니다.
구현 클래스의 `*MappingGenerator`·구현 네임스페이스 `.Generator`와 별도 SCL의 `Scl.Generator` 이름은 유지합니다.
Modbus의 옛 Data DLL은 코어로 통합했으므로 별도 참조를 제거합니다.

이전에 함께 개발한 PLC형 로직 실행·원격 업데이트(`nanoEdge.Logic.*`)는 현재 구성에서 폐기했습니다.
당시 전체 상태는 저장소 밖 `nanoEdge-full-backup-20261025.zip`에 보관했으며 현재 솔루션의 기능에 포함하지 않습니다.
