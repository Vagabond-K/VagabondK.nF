# MMS

[전체 프로젝트](../../README.md) · [빌드·패키징·공통 검증](../../Build/README.md)

ISO 9506 MMS의 일부 서비스를 제공하는 범용 클라이언트·서버 라이브러리입니다.
IEC 61850에 의존하지 않으며 LN/FC/CDC 이름을 요구하지 않습니다.
ISO 9506 전체 구현이나 모든 MMS 장비와의 호환성, 적합성 인증을 뜻하지 않습니다.
이 문서의 명령과 코드 경로는 저장소 루트 기준입니다.

## 구성 요소 선택

| 프로젝트 문서 | 역할 | 프로젝트 의존성 |
|---|---|---|
| [MMS core](VagabondK.nF.MMS/README.md) | 값·형식·오류·채널·명명 목록·보고 스냅샷·공통 MMS/OSI 코덱 | 없음 |
| [MMS.Client](VagabondK.nF.MMS.Client/README.md) | 연결 협상·Read/Write·탐색·명명 목록·보고 수신 | MMS |
| [MMS.Server](VagabondK.nF.MMS.Server/README.md) | 고정 변수·명명 목록 Server와 일반 InformationReport 송신 | MMS |
| [MMS.TCP](VagabondK.nF.MMS.TCP/README.md) | 호출자 소유 소켓을 사용하는 시간 제한 채널 | MMS |
| [MMS.Mapping](VagabondK.nF.MMS.Mapping/README.md) | 사용자 partial 모델의 Client/Server 바인딩 생성 | PC 빌드 전용 |

프로젝트·DLL 접두사는 `VagabondK.nF.`입니다.

| 구성 | 필요한 런타임 |
|---|---|
| TCP Client | `MMS` + `MMS.Client` + `MMS.TCP` |
| TCP Server | `MMS` + `MMS.Server` + `MMS.TCP` |
| 사용자 `IMmsChannel` 기반 Client | `MMS` + `MMS.Client` |
| 사용자 `IMmsChannel` 기반 Server | `MMS` + `MMS.Server` |

Client/Server는 서로 참조하지 않고 TCP DLL만 System.Net을 참조합니다.
사용자 `IMmsChannel`을 제공하면 TCP DLL 없이 사용할 수 있습니다.
이는 채널 어댑터 교체 지점이며 다른 전송 프로토콜의 상호운용을 보장한다는 의미는 아닙니다.
IEC의 DataSet·URCB·품질/UTC 의미·SPC/DPC/APC 제어는 [IEC 61850 계층](../IEC61850/README.md)에 있습니다.

## 사용과 지원 경계

```csharp
using VagabondK.nF.MMS;
using VagabondK.nF.MMS.Client;
using VagabondK.nF.MMS.TCP;

// 연결된 socket의 생성·수명은 호출자가 관리합니다.
var client = new MmsClient(new MmsSocketChannel(socket));
client.Associate();
MmsValue value = client.Read("Plant", "Temperature");
client.Write("Plant", "Setpoint", 42);
client.Release();
```

클라이언트는 제한된 스칼라·구조체·동종 배열 Read, 이름 페이지 탐색·형식 조회,
최대 16개 참조의 다중 Read·정적 명명 변수 목록과 일반 InformationReport 수신을 제공합니다.
Write는 Boolean·Int32·Float32 한 변수이며 Server 원격 쓰기는 기본 금지입니다.
복합 쓰기·alternateAccess·파일·저널·동적 목록 생성/삭제는 없습니다.
동시에 진행할 수 있는 요청은 하나입니다. 고정 선택자·이름·PDU·깊이 상한과 상세 계약은 각 프로젝트 문서를 따릅니다.

명명 목록이 없는 Server는 `new MmsServerSession(channel, variables)` 또는
`new MmsServerSession(channel, variables, 4096, 10000, 16)`처럼 2~5개 인수 오버로드를 사용할 수 있습니다.
nanoFramework 메타데이터 최소화 시 목록 형식 참조 오류를 피하며 빈 목록 배열이 필요하지 않습니다.
기존 6개 인수·`namedVariableLists:` 호출도 지원합니다.
중간 인수를 생략하는 `timeoutMilliseconds:` 호출은 기존 선택적 인수 생성자를 선택하므로
새 오버로드가 필요하면 앞선 제한 인수도 지정하십시오.

오류 후 같은 클라이언트·소켓을 재사용하지 않으며 자동 재전송이나 롤백은 하지 않습니다.
인증·TLS·주체별 권한·물리 출력 안전성을 제공하지 않으므로 격리된 평가 환경에서 사용하십시오.

## 정적 객체 매핑

[MMS.Mapping](VagabondK.nF.MMS.Mapping/README.md)은 모델의 bool/int/float 읽기·명시적 쓰기,
일반 보고 수신과 Server 모델 바인딩을 생성합니다.
모델당 포인트 1~16개를 지원하며 별도 MCU 런타임 DLL이나 리플렉션은 필요하지 않습니다.
역할 특성의 공통 `Domain`과 item만 받는 `[MmsPoint("item")]`을 지원하며,
두 인수로 지정한 domain이 공통값보다 우선합니다. 이름은 빌드 시 확정됩니다.
속성 설정자(setter)는 통신하지 않고 모델 동기화·연결 수명·오류 처리는 앱이 관리합니다.
SCL 모델 생성이나 IEC 제어 의미와는 별도 기능입니다.

## 샘플과 검증

[MMS 매핑 샘플](../../Samples/MMS/VagabondK.nF.MMS.Mapping)은 정적 모델 사용 예입니다.
일반 MMS와 IEC가 함께 사용하는 형식·역할 대조는 [매핑 대조표](../../Samples/MappingCoverage.md)를 참조하십시오.

```powershell
dotnet run -c Release --project .\Tests\MMS\VagabondK.nF.MMS.Tests
dotnet run -c Release --project .\Tests\MMS\VagabondK.nF.MMS.Mapping.Tests
.\Build\Test-MmsMapping.ps1
.\Build\Pack-MmsMapping.ps1 -LibrarySet MMS
.\Build\Pack.ps1 -LibrarySet MMS
.\Build\Test-Packages.ps1 -LibrarySet MMS
```

PC 가상 통신·nanoFramework 생성 코드 빌드·로컬 패키지 소비는 실제 IED/보드 시험과 구분합니다.
일반 MMS 독립 PC 시험은 IEC 소스를 링크하지 않습니다.
패키지 시험은 역할·전송별 NuGet 소비와 불필요한 IEC/Client/Server/Socket DLL 제외를 검사합니다.
과거 역할 분리 PE 크기·할당·양방향 MCU 상호운용은 [MMS core 기록](VagabondK.nF.MMS/README.md)에 있습니다.
2026-10-29에는 생성 MMS/IEC 매핑 네 역할을 독립 libiec61850 Client/Server와
보드에서 재검증했습니다. 결과·MMS 직접 PDU 인코딩/보고 해석의 제약·콜백 기한 대책은
[매핑 상호운용 기록](VagabondK.nF.MMS.Mapping/README.md#독립-libiec61850-상호운용-시험)을 참조하십시오.
원본 libiec61850 피어는 제품 의존성이 아니며 사용 시 해당 문서의 라이선스·배포 경계를 확인하십시오.
