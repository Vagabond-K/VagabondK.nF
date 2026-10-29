# IEC 61850

[전체 프로젝트](../../README.md) · [빌드·패키징·공통 검증](../../Build/README.md) · [일반 MMS](../MMS/README.md)

MMS 기반의 제한형 IEC 61850 개발·평가 라이브러리입니다.
ESP32-S3와 PC 독립 피어에서 Client/Server 상호운용을 확인했지만,
완전한 IEC 61850 IED 서비스나 전체 의미 모델, 적합성 인증을 제공하지 않습니다.
이 문서의 명령과 코드 경로는 저장소 루트 기준입니다.

## 구성 요소 선택

| 프로젝트 문서 | 역할 | 프로젝트 의존성 |
|---|---|---|
| [IEC61850 core](VagabondK.nF.IEC61850/README.md) | 품질·UTC·Dbpos 변환과 공통 제어 모델 | MMS |
| [IEC61850.Client](VagabondK.nF.IEC61850.Client/README.md) | IEC DataSet·URCB 수신·SPC/DPC/APC Client 제어 | IEC61850, MMS, MMS.Client |
| [IEC61850.Server](VagabondK.nF.IEC61850.Server/README.md) | IEC 제어·DataSet·URCB Server 모델 | IEC61850, MMS, MMS.Server |
| [IEC61850.Mapping](VagabondK.nF.IEC61850.Mapping/README.md) | 모델과 IEC Client/Server의 정적 바인딩 생성 | PC 빌드 전용 |
| [IEC61850.SCL](VagabondK.nF.IEC61850.SCL/README.md) | 제한된 SCL에서 정적 C# Server 모델 생성 | PC 전용 .NET 10 도구 |

프로젝트·DLL 접두사는 `VagabondK.nF.`입니다.

| 구성 | 필요한 런타임 |
|---|---|
| TCP Client | `MMS` + `MMS.Client` + `MMS.TCP` + `IEC61850` + `IEC61850.Client` |
| TCP Server | `MMS` + `MMS.Server` + `MMS.TCP` + `IEC61850` + `IEC61850.Server` |
| 공통 값·품질·UTC 변환만 사용 | `MMS` + `IEC61850` |

사용자 `IMmsChannel`을 제공하면 Client/Server 조합에서 `MMS.TCP`를 제외할 수 있습니다.
이는 MMS 채널 어댑터 교체 지점이지 GOOSE·SV 지원을 의미하지 않습니다.
Server는 Client DLL이 필요하지 않으며 일반 MMS만 쓰는 앱은 IEC DLL이 필요하지 않습니다.
Client/Server 진입점은 각 역할 네임스페이스의 `Iec61850Client`·`Iec61850ServerSession`입니다.
SCL 도구는 MCU DLL에 포함하지 않으며 원본 libiec61850 피어도 제품 의존성이 아닙니다.

## 지원 범위와 제약

연결 협상·스칼라 Read·오류 후 새 연결, domain/변수 이름 페이지 탐색,
제한된 구조체·배열 형식 조회/Read, Boolean·Int32·Float32 단일 Write를 제공합니다.
Server는 기본 쓰기 금지이며 명시적으로 허용한 메모리 변수만 변경합니다.
최대 16개 혼합 변수 Read·정적 DataSet, 품질 13비트·UTC 의미 매핑,
고정 DataSet의 제한형 URCB(GI·변경·integrity)를 지원합니다.
MMS Conclude·ACSE Release 정상 종료와 서비스 오류·Reject 뒤 연결 유지도 제공합니다.

SPC(Boolean), DPC(Boolean 명령/Dbpos 상태), APC(AnalogueValue.f Float32)의
DirectNormal·SboNormal·DirectEnhanced·SboEnhanced 네 제어 모델을 제공합니다.
선택·실행·취소·만료, SBOw의 선택 값·출처·제어 번호·Test 일치 검사를 지원합니다.
선택적으로 앱의 허용 검사와 실행 처리기를 연결할 수 있으며, LastApplError와 enhanced 완료/실패/기한 초과 통지를 제공합니다.
진행 중 명령은 연결당 하나이며 실제 출력 드라이버·설비 안전 보장·시간 예약은 포함하지 않습니다.
제어는 기본 비활성이고 명시적 실행·완료 반영 정책이 필요합니다.

BRCB·단절 이벤트 버퍼·전체 SCL·GOOSE·SV 등 전체 IEC 서비스는 제공하지 않습니다.
상세 상한·보안 부재·오류 계약은 Client/Server 문서를 확인하십시오.

## 객체 매핑과 SCL 생성

[IEC 매핑](VagabondK.nF.IEC61850.Mapping/README.md)은 partial 모델에 직접 접근하는 코드를 생성합니다.
bool/int/float, Dbpos·품질·UTC·DataSet/URCB 수신과 명시적 SPC/DPC/APC 제어를 연결합니다.
모델당 포인트 1~16개·제어 최대 8개를 지원하며 속성 설정자(setter)만으로 통신하지 않습니다.
Point에는 역할 특성의 공통 `Domain`과 item만 지정할 수 있으며 명시적 두 인수 domain이 우선합니다.
제어/원격 제어는 공통 Domain과 무관하게 domain/item을 모두 지정합니다.
추가 MCU 런타임 DLL이나 리플렉션은 필요하지 않습니다.

[SCL 도구](VagabondK.nF.IEC61850.SCL/README.md)는 제한된 XML을 PC에서 정적 C# 7.3 모델로 변환합니다.
MCU에서 XML을 파싱하지 않으며 전체 SCL을 가져오는 도구나 의미 검증기는 아닙니다.
SCL 모델 생성과 객체 매핑은 별도 기능이며 자동 연결하지 않습니다.

## 샘플과 검증

| 문서·프로젝트 | 내용 |
|---|---|
| [TCPHost](../../Samples/IEC61850/VagabondK.nF.IEC61850.TCPHost/README.md) | TCP Server 실행 |
| [TCPClient](../../Samples/IEC61850/VagabondK.nF.IEC61850.TCPClient/README.md) | 안전 재접속 Client·독립 피어 시험 |
| [Mapping](../../Samples/IEC61850/VagabondK.nF.IEC61850.Mapping) | IEC 정적 객체 매핑 |
| [Tests](../../Tests/IEC61850) | IEC·SCL PC 회귀와 역할·제어·보고·매핑 실물 Fixture |

```powershell
dotnet run -c Release --project .\Tests\IEC61850\VagabondK.nF.IEC61850.Tests
.\Build\Test-Iec61850.ps1
.\Build\Test-MmsMapping.ps1
.\Build\Pack-MmsMapping.ps1 -LibrarySet IEC61850
.\Build\Pack.ps1 -LibrarySet IEC61850
.\Build\Test-Packages.ps1 -LibrarySet IEC61850
.\Build\Pack-Iec61850SclTool.ps1
```

IEC 패키징은 필요한 MMS 패키지도 함께 생성합니다.
실물 매핑 양방향·기동별 재접속·Heap 시험은
`Build\Test-Iec61850MappingHardware.ps1 -AllowDeviceChanges`와 `-Client`로 수행합니다.
COM3 보드 앱을 변경하고 finally에서 Modbus 앱 복원·COM6 확인을 수행하므로 절차를 먼저 읽어야 합니다.
별도 `Build\Test-MappingInteropHardware.ps1 -AllowDeviceChanges -Iterations 3`는
독립 libiec61850 Client/Server와 실제 생성 MMS/IEC 바인딩 네 역할을 검사합니다.
2026-10-29 최종 `bin\mapping-interop-hardware-20261029-133546`에서
네 역할 완료·보존 힙 증가 0 B·원래 앱 복원을 확인했습니다.
IEC는 외부 보고 API로 교환했으며 MMS 일부 보고의 직접 PDU 인코딩과 외부 Client 제약은
[매핑 상호운용 기록](VagabondK.nF.IEC61850.Mapping/README.md#독립-libiec61850-상호운용-시험)을 따릅니다.

원본 libiec61850과 양방향 PC·실물 상호운용을 확인했습니다.
과거 Boolean 제어 시험은 512회 반복을 포함한 1,678개 요청,
SBO Enhanced 시험은 256회 반복을 포함한 총 2,476개 요청 뒤 가용 Heap 유지를 관측했습니다.
이는 특정 실행 기록이지 모든 제어·보드·펌웨어 조합이나 누수 부재의 보장은 아닙니다.
자세한 결과·안전 경계·복원 절차는 [Client](VagabondK.nF.IEC61850.Client/README.md),
[Server](VagabondK.nF.IEC61850.Server/README.md), [매핑](VagabondK.nF.IEC61850.Mapping/README.md) 문서를 따릅니다.
