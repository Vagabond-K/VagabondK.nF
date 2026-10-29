# 제한형 일반 MMS

ISO 9506 MMS의 일부 서비스를 제공하는 nanoFramework 라이브러리입니다.
IEC 61850 전용 라이브러리가 아니며 IEC 61850 DLL을 참조하지 않습니다.
ISO 9506 전체 구현이나 모든 MMS 장비와의 호환성, 정식 적합성을 뜻하지 않습니다.

| DLL | 역할 | 프로젝트 의존성 |
|---|---|---|
| `VagabondK.nF.MMS` | 값/형식·오류·채널 계약·명명 목록/보고 스냅샷·공통 MMS/OSI 코덱 | 없음 |
| `VagabondK.nF.MMS.Client` | 연결 협상·Read/Write·탐색·명명 목록·보고 수신 | MMS |
| `VagabondK.nF.MMS.Server` | 고정 변수·명명 변수 목록의 Server, 일반 InformationReport 송신 | MMS |
| `VagabondK.nF.MMS.TCP` | 호출자 소유 소켓을 사용하는 시간 제한 채널 | MMS |

MMS core에 TPKT/COTP Class 0·Session/Presentation/ACSE·BER 연결 코덱이 포함됩니다.
별도의 코덱 DLL·스레드·보고 큐를 만들지 않습니다. TCP DLL만 System.Net을 참조합니다.
Client/Server 연결 상태와 송수신 루프는 각 역할 DLL에 있고 코덱은 core 한 곳에만 컴파일합니다.
MMS core·Server·TCP는 MMS.Client를 참조하지 않습니다.
IEC 61850 계층은 MMS를 참조하며 반대 방향 프로젝트/어셈블리 참조는 없습니다.
내부 코덱과 프로파일 경계는 friend assembly로 공유하고 복사 컴파일하지 않습니다.

## 사용

```csharp
using VagabondK.nF.MMS;
using VagabondK.nF.MMS.Client;
using VagabondK.nF.MMS.TCP;

// socket의 연결·수명은 호출자가 관리합니다.
var client = new MmsClient(new MmsSocketChannel(socket));
client.Associate();
MmsValue value = client.Read("Plant", "Temperature");
client.Write("Plant", "Setpoint", 42);
MmsNamedVariableList list = client.GetNamedVariableListDirectory("Plant", "Measurements");
MmsReadResult[] snapshot = client.ReadNamedVariableList(list);
client.Release();
```

`GetNameList(namedVariableLists: true, domain: "Plant")`는 명명 변수 목록 이름을 조회합니다.
IEC 61850의 LN/FC/CDC 이름을 요구하지 않습니다. `$CO$` 등도 일반 식별자일 뿐입니다.
IEC 전용 DataSet·URCB·제어·LastApplError·Quality/UTC 의미 매핑은
[`VagabondK.nF.IEC61850.Client`](../../IEC61850/VagabondK.nF.IEC61850.Client/README.md)에 있습니다.

`MmsClient`·`MmsInformationReportHandler`는 `VagabondK.nF.MMS.Client` DLL/네임스페이스로 이동했습니다.
기존 Client 앱은 MMS core 참조를 유지하고 `.Client` 참조와 using을 추가하십시오.
값·형식·오류·참조·명명 목록·보고 스냅샷은 공통 MMS core에 남습니다.

일반 InformationReport는 `ReceiveInformationReport()`로 하나씩 받거나
`InformationReportReceived`를 설정하여 서비스 응답 대기 중 직렬 콜백으로 처리합니다.
명시적 변수 목록과 명명 변수 목록을 구분하고 VMD/domain/association 이름·항목별 접근 오류를 보존합니다.
결과는 수신 버퍼와 독립된 스냅샷입니다. 명명 목록 보고에서 각 이름에 대응하는 멤버를 자동으로 추정하지 않습니다.
보고의 최대 이름/결과는 각각 64개, 전체 값은 256노드, 깊이는 8입니다.
서비스 응답 전에 보고가 33개까지 연속 도착하면 burst 오류로 종료합니다.
콜백에서는 같은 클라이언트에 재진입하거나 대기하지 않아야 하며 콜백 실행은 원래 요청 기한을 연장하지 않습니다.
검증·모델 반영처럼 시간이 걸리거나 예외가 예상되는 처리는 독립 보고 스냅샷을
앱의 용량 제한 큐에 보관한 뒤 통신 작업 종료 후 수행할 수 있습니다.
큐 상한·넘침 정책·모델 접근 직렬화는 앱이 관리합니다. MCU의 예외 생성·디버그 출력 비용도
콜백 실행 시간에 포함될 수 있습니다.
콜백 예외·미처리 보고·잘못된 BER/이름/개수·연결/기한 오류가 발생하면 Faulted 상태로 전환하고 예외를 전파합니다.

## 지원 경계

- 동시 진행 요청 1개, 작업 전체에 적용되는 기한, PDU 1024~32768바이트(기본 4096), TPDU 128~1024.
- 고정 TSAP/S-selector=0001, P-selector=00000001, presentation context=1/3, 인증 없음.
- Read/Write/형식/명명 목록 접근은 domain-specific만 지원합니다. 일반 보고의 이름 범위와 구분하십시오.
- Read는 제한된 스칼라·구조체·동종 배열, 다중 읽기와 정적 명명 목록은 최대 16개 참조.
- Write는 Boolean·Int32·Float32 한 변수입니다. 일반 복합 쓰기·alternateAccess·파일·저널·동적 목록 생성/삭제는 없습니다.
- 이름은 공백 없이 출력 가능한 ASCII 1~64자입니다. 변수 목록과 모델은 고정이고 서버 원격 쓰기는 기본 금지입니다.

통신 오류 후 같은 클라이언트/소켓을 재사용하지 마십시오. 명령 응답이 유실되어도 적용된 변경이 롤백되지는 않으며 자동 재전송하지 않습니다.
인증·TLS·접속 주체별 권한·설비 출력 안전성을 제공하지 않습니다. 격리된 평가 환경에서만 사용하십시오.
고정 선택자 등 기존 IEC 상호운용 프로파일의 제약을 유지하므로 다른 MMS 구현과의 호환성을 별도로 확인해야 합니다.

```powershell
dotnet run -c Release --project .\Tests\MMS\VagabondK.nF.MMS.Tests `
  -- .\bin\protocol-tools\iec61850-build\Release\VagabondKMmsPeer.exe
.\Build\Pack.ps1 -LibrarySet MMS
.\Build\Test-Packages.ps1 -LibrarySet MMS
```

독립 PC 시험은 IEC 소스를 링크하지 않습니다. 패키지 시험은 Core/Client/Server/TCP/Server+TCP/전체 소비자를
NuGet만으로 빌드하고 IEC DLL·선택하지 않은 Client/Server/Socket DLL의 혼입을 검사합니다.
독립 피어의 라이선스와 배포 경계는 기존 IEC 61850 안내를 따르며 런타임에 피어 코드를 포함하지 않습니다.

## 코어·Client·Server 역할 분리 결과

2026-10-26 역할 분리 당시의 최종 Release PE 크기입니다. 후속 기능·매핑 최적화 이전 기록이며,
현재 DLL 크기·패키지 ZIP 크기·상주 RAM 크기와는 구분합니다.

| DLL | PE Byte |
|---|---:|
| MMS | 15,580 |
| MMS.Client | 13,148 |
| MMS.Server | 16,844 |
| MMS.TCP | 1,172 |
| IEC61850 | 1,796 |
| IEC61850.Client | 12,032 |
| IEC61850.Server | 15,940 |

1차 MMS 추출 직후와 비교하면 TCP 제외 IEC Server 합계는 72,776 → 50,160 Byte로
22,616 Byte 감소했습니다. 일반 MMS Server도 core/Server 합계가 43,632 → 32,424 Byte로 줄었습니다.
반면 IEC Client 합계는 39,992 → 42,556 Byte로 2,564 Byte,
일반 MMS Client는 core/Client 합계가 26,788 → 28,728 Byte로 1,940 Byte 증가했습니다.
Client와 Server를 모두 사용하는 앱은 전체 7개 PE 합계 76,512 Byte이며
이전 5개 합계 73,948 Byte보다 2,564 Byte 증가했습니다.
역할별 코드 제외 효과와 추가 DLL 메타데이터 비용을 구분하십시오.

`Build\Test-Iec61850.ps1`은 Debug/Release 각각 실제 DLL의 AssemblyReference를 읽어
7개 DLL의 정확한 단방향 의존 관계를 검사합니다. core/Server/TCP에 Client 역참조가 없고 MMS에 IEC 역참조가 없습니다.
일반 MMS 111개 검사와 IEC 전체 회귀·원본 libiec61850 양방향·가상 장애 복구·할당 회귀도 다시 통과했습니다.
Read는 912 Byte/회, 로컬 setter 반복은 추가 할당 0 Byte를 유지했습니다.
결과와 실제 참조 목록은 `bin\role-split-regression`에 있습니다.

`bin\nuget\role-split-final-mms`의 MMS 4개와 `bin\nuget\role-split-final-iec`의 MMS/IEC 7개는
Core/Client/Server/TCP/Server+TCP/전체의 6가지 역할별 새 출력 디렉터리에서
패키지만으로 컴파일·배포 BIN 생성 및 금지 PE 제외를 확인했습니다.
두 피드는 동일한 최종 빌드 산출물이며 MMS 피드는 7개 세트에서 MMS 4개와 체크섬을 추출한 것입니다.
다시 Rebuild하면 DLL 메타데이터/해시가 달라질 수 있으므로 이후 빌드 출력과 과거 패키지를 섞지 마십시오.
최종 패키지의 PE가 두 MCU 시험에 배포한 PE와 SHA256까지 같음을 확인했습니다.
SCL 도구는 `bin\nuget\role-split-scl`에 로컬 패키징·설치했고,
설치 도구의 생성 모델은 Client 참조 없이 Debug/Release nanoFramework 빌드를 통과했습니다.
Client/Server 예제와 모든 IEC 보드 시험 앱도 두 구성으로 빌드했습니다.

ESP32-S3와 가상 IED 간 단기 양방향 실행은 아래와 같습니다.
실제 외부 IED 시험이나 새 이미지의 장시간 내구 시험을 뜻하지 않습니다.

| MCU 역할 | 연결 | 요청 | 보고 | Fault | 종료/기준/최소 free Heap Byte | 앱 포함 BIN Byte |
|---|---:|---:|---:|---:|---:|---:|
| IEC Server | 9 | 756 | 108 | 0 | 706,152 / 706,152 / 706,152 | 132,240 |
| IEC Client | 8 | 768 | 96 | 0 | 758,724 / 758,724 / 758,724 | 124,844 |

Server 출력에는 두 Client PE가, Client 출력에는 두 Server PE가 없음을 확인했습니다.
증거는 `bin\role-split-typed-server`·`bin\role-split-typed-client`에 있습니다.
양쪽 모두 원래 Modbus 앱으로 복원한 뒤 COM6 holding 0..2=123/456/65535를 확인했습니다.

## 1차 MMS 분리 기록 (Client 역할 분리 전)

2026-10-26의 1차 MMS 추출 당시 Release PE 크기는 다음과 같습니다.
아래는 Client 역할 분리 전의 기록이며 현재 DLL 크기와 구분합니다. 패키지 ZIP 크기나 상주 RAM 크기가 아닙니다.

| DLL | PE Byte |
|---|---:|
| MMS | 26,788 |
| MMS.Server | 16,844 |
| MMS.TCP | 1,172 |
| IEC61850 | 13,204 |
| IEC61850.Server | 15,940 |

일반 MMS Client는 MMS/TCP만, IEC Client는 MMS/IEC/TCP를 사용합니다.
TCP를 제외한 IEC Client 합계는 39,992 Byte로 분리 전 35,136 Byte보다 4,856 Byte 증가했습니다.
IEC Server의 두 core와 두 Server 합계는 72,776 Byte로 분리 전 69,532 Byte보다 3,244 Byte 증가했습니다.
일반 보고 스냅샷 기능과 DLL 경계의 메타데이터 비용이 추가되므로 이 분리가 전체 IEC 앱의 용량을 줄이는 것은 아닙니다.
대신 일반 MMS 앱은 IEC 보고·제어 코드와 Server를 포함하지 않고 사용할 수 있습니다.

일반 MMS 전용 PC 시험은 Debug/Release 각각 111개 검사를 통과했습니다.
실제 Socket의 Read/Write·탐색·정적 목록·보고 끼어들기·종료, callback 실패/재진입,
보고의 결과 64개/값 노드 256개 경계와 상한 초과·잘린 패킷·이름 범위·스냅샷을 확인했습니다.
IEC 전체 Debug/Release 회귀와 원본 libiec61850 양방향 상호운용도 통과했고,
서버 Read 할당은 912 Byte/회, 로컬 setter 3종 반복은 추가 할당 0 Byte를 유지했습니다.
패키지는 `bin\nuget\mms-final-20261026`의 MMS 3개와
`bin\nuget\mms-iec-final-20261026`의 MMS/IEC 5개이며 역할별 오프라인 소비자 빌드도 통과했습니다.
예제·보드 시험 앱·로컬 설치 SCL 도구의 생성 모델은 Debug/Release nanoFramework 빌드로 확인했습니다.

ESP32-S3와 가상 IED의 분리 후 단기 양방향 시험은 다음과 같습니다.
실제 외부 IED와의 시험이나 분리 전 2시간 내구 시험을 대체하지 않습니다.

| MCU 역할 | 연결 | 요청 | 보고 | Fault | 종료/기준/최소 free Heap Byte | 앱 포함 BIN Byte |
|---|---:|---:|---:|---:|---:|---:|
| IEC Server | 9 | 756 | 108 | 0 | 704,688 / 704,688 / 704,688 | 154,856 |
| IEC Client | 8 | 768 | 96 | 0 | 759,096 / 759,096 / 759,096 | 122,204 |

증거는 `bin\mms-final-typed-server`·`bin\mms-final-typed-client`에 보존했습니다.
양쪽 모두 원래 Modbus 앱으로 복원한 뒤 COM6에서 holding 0..2=123/456/65535를 확인했습니다.
