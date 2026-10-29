# VagabondK.nF

본 리포지토리는 제가 개인적으로 .NET nanoFramework를 사용할 때 편리하게 활용하기 위해 필요한 기능을 만들어 놓은 라이브러리 세트입니다.

현재는 Modbus, DNP3, BACnet/IP, SNMP, MMS, IEC 61850, MQTT·Sparkplug 통신 기능을 제공합니다.
역할과 전송 계층을 나누어 필요한 구성만 선택할 수 있습니다. 객체 매핑 생성기를 선택하면 PC에서
빌드할 때 정적 C# 코드를 생성하므로 MCU에서 리플렉션을 사용하지 않습니다.

**현재는 평가용 사전 릴리스입니다.** 산업 현장 적용 검증이나 안전·적합성 인증을 마친 제품이 아닙니다.
지원 범위와 제약은 각 라이브러리 그룹의 상세 문서를 확인하십시오.
이 프로젝트는 [MIT 라이선스](LICENSE)로 제공하며 외부 의존성에는 각각의 라이선스가 적용됩니다.

## 라이브러리 그룹 안내

현재 제공하는 기능은 아래와 같습니다. 앞으로 통신 외 기능이 추가되어도 기능별로 상세 문서를 나누고,
이곳에서 각 기능의 요약과 문서 링크를 안내합니다.

| 그룹 | 주요 용도·지원 범위 | 상세 문서 |
|---|---|---|
| Modbus | RTU/ASCII/TCP Client·Server, 레지스터 데이터 변환, 정적 객체 매핑 | [Modbus 안내](Source/Modbus/README.md) |
| DNP3 | TCP/시리얼 Master·Outstation, 이벤트·품질·시각·제어, 정적 객체 매핑 | [DNP3 안내](Source/DNP3/README.md) |
| BACnet | 제한형 BACnet/IP Device, AV/BV 조회·설정, Device 객체 매핑 | [BACnet 안내](Source/BACnet/README.md) |
| SNMP | SNMPv1/v2c Agent, 조회·순회·설정 요청 처리, v2c Trap 송신, 스칼라 객체 매핑 | [SNMP 안내](Source/SNMP/README.md) |
| MMS | 제한형 일반 MMS Client·Server, Read/Write·탐색·명명 목록·보고, 객체 매핑 | [MMS 안내](Source/MMS/README.md) |
| IEC 61850 | MMS 기반 제한형 Client·Server, DataSet·URCB·SPC/DPC/APC 제어, 매핑·SCL 생성 | [IEC 61850 안내](Source/IEC61850/README.md) |
| MQTT·Sparkplug | MQTT 3.1/3.1.1/5 클라이언트, QoS0/1/2·TLS·RAM 세션, 제한형 Sparkplug B, JSON·메트릭 정적 매핑 | [MQTT 안내](Source/MQTT/README.md) |

프로토콜 간 자동 동기화 엔진은 제공하지 않습니다.
MMS는 IEC 61850에 의존하지 않으며 IEC 61850이 MMS를 참조합니다. 나머지 프로토콜도 서로 독립입니다.

객체 매핑은 구체적인 특성으로 선언을 간소화할 수 있습니다.
Modbus의 `[HoldingRegister(주소)]`, DNP3의 `[AnalogInput(인덱스)]`,
BACnet의 `[BacnetAnalogValue(instance)]`, SNMP의 `[SnmpInteger("OID")]`를 지원합니다.
MMS/IEC 61850은 클래스의 공통 `Domain`, MQTT JSON은 멤버 이름 생략,
Sparkplug는 멤버 형식으로부터 메트릭 형식을 확정하는 선언을 제공합니다.
기존 선언과 생성 API도 유지하며, 이 편의 기능은 PC 생성 단계에만 적용됩니다.
매핑 선언용 특성은 nanoFramework의 `ExcludeType`으로 MCU PE 이미지에서 제외합니다.
런타임 리플렉션·동적 바인딩 엔진·속성 변경에 따른 자동 전송이나 자동 폴링은 추가하지 않습니다.

## 라이브러리·도구 프로젝트

프로젝트 이름은 DLL·패키지 이름의 기준입니다. 아래 **런타임**은 MCU용 DLL,
**생성기**는 PC에서 실행하는 Roslyn 소스 생성기, **도구**는 PC 실행 프로그램입니다.
생성기는 별도 MCU 런타임 DLL을 추가하지 않으며, 역할·전송 DLL은 애플리케이션에 맞게 선택합니다.
각 그룹의 상세 문서에 구성 요소 선택, 지원 경계, 사용 예와 검증 방법을 정리했습니다.

### Modbus

| 프로젝트 | 종류 | 요약 |
|---|---|---|
| [VagabondK.nF.Modbus](Source/Modbus/VagabondK.nF.Modbus) | 런타임 | 공통 규칙·오류·채널·시간 제한 처리와 레지스터의 숫자·ASCII 변환 |
| [VagabondK.nF.Modbus.Client](Source/Modbus/VagabondK.nF.Modbus.Client) | 런타임 | Master 요청 송신과 응답 검증 |
| [VagabondK.nF.Modbus.Server](Source/Modbus/VagabondK.nF.Modbus.Server) | 런타임 | Slave 요청 처리와 데이터 저장소 |
| [VagabondK.nF.Modbus.RTU](Source/Modbus/VagabondK.nF.Modbus.RTU) | 런타임 | CRC16·RTU 프레임과 직렬 수신 처리 |
| [VagabondK.nF.Modbus.ASCII](Source/Modbus/VagabondK.nF.Modbus.ASCII) | 런타임 | LRC·ASCII 프레임과 직렬 수신 처리 |
| [VagabondK.nF.Modbus.TCP](Source/Modbus/VagabondK.nF.Modbus.TCP) | 런타임 | MBAP 프레임과 TCP 소켓 어댑터 |
| [VagabondK.nF.Modbus.Serial](Source/Modbus/VagabondK.nF.Modbus.Serial) | 런타임 | RTU/ASCII 공용 UART 어댑터 |
| [VagabondK.nF.Modbus.Mapping](Source/Modbus/README.md#빌드-시점-객체-매핑) | 생성기 | Client 읽기·명시적 쓰기와 Server 모델 저장소 생성 |

### DNP3

| 프로젝트 | 종류 | 요약 |
|---|---|---|
| [VagabondK.nF.DNP3](Source/DNP3/VagabondK.nF.DNP3) | 런타임 | CRC·링크·전송 계층·포인트·값·객체·제어 코덱과 통신 종단 인터페이스 |
| [VagabondK.nF.DNP3.Master](Source/DNP3/VagabondK.nF.DNP3.Master) | 런타임 | 읽기·제어·시간 동기화와 응답·비요청 수신 |
| [VagabondK.nF.DNP3.Outstation](Source/DNP3/VagabondK.nF.DNP3.Outstation) | 런타임 | 값·이벤트 DB, 읽기 응답·비요청 송신과 제어 처리 |
| [VagabondK.nF.DNP3.TCP](Source/DNP3/VagabondK.nF.DNP3.TCP) | 런타임 | 호출자가 소유한 소켓의 부분 송수신·시간 제한·폴링 |
| [VagabondK.nF.DNP3.Serial](Source/DNP3/VagabondK.nF.DNP3.Serial) | 런타임 | UART, 확인 응답을 사용하는 링크와 사용하지 않는 링크, ACK·FCB·재시도 |
| [VagabondK.nF.DNP3.Mapping](Source/DNP3/VagabondK.nF.DNP3.Mapping/README.md) | 생성기 | Outstation DB 게시와 Master 수신 모델·명시적 제어 코드 생성 |

### BACnet

| 프로젝트 | 종류 | 요약 |
|---|---|---|
| [VagabondK.nF.BACnet](Source/BACnet/VagabondK.nF.BACnet) | 런타임 | BACnet/IP 프레임·Device·AV/BV 객체·조회/설정과 저장소 계약 |
| [VagabondK.nF.BACnet.IP](Source/BACnet/VagabondK.nF.BACnet.IP) | 런타임 | 호출자 소유 UDP 소켓의 데이터그램 처리 |
| [VagabondK.nF.BACnet.Mapping](Source/BACnet/VagabondK.nF.BACnet.Mapping/README.md) | 생성기 | float/bool 모델의 Device 측 AV/BV 저장소 생성 |

### SNMP

| 프로젝트 | 종류 | 요약 |
|---|---|---|
| [VagabondK.nF.SNMP](Source/SNMP/VagabondK.nF.SNMP) | 런타임 | v1/v2c BER·Agent·system 그룹·Trap 생성과 저장소 계약 |
| [VagabondK.nF.SNMP.UDP](Source/SNMP/VagabondK.nF.SNMP.UDP) | 런타임 | 호출자 소유 UDP 소켓의 요청 처리와 v2c Trap 송신 |
| [VagabondK.nF.SNMP.Mapping](Source/SNMP/VagabondK.nF.SNMP.Mapping/README.md) | 생성기 | 스칼라 OID 검증·정렬과 Agent 측 모델 저장소 생성 |

### MMS

| 프로젝트 | 종류 | 요약 |
|---|---|---|
| [VagabondK.nF.MMS](Source/MMS/VagabondK.nF.MMS/README.md) | 런타임 | 값·형식·채널·명명 목록·보고 스냅샷과 공통 MMS/OSI 코덱 |
| [VagabondK.nF.MMS.Client](Source/MMS/VagabondK.nF.MMS.Client/README.md) | 런타임 | 연결 협상·Read/Write·탐색·명명 목록·보고 수신 |
| [VagabondK.nF.MMS.Server](Source/MMS/VagabondK.nF.MMS.Server/README.md) | 런타임 | 고정 변수·명명 목록 Server와 일반 InformationReport 송신 |
| [VagabondK.nF.MMS.TCP](Source/MMS/VagabondK.nF.MMS.TCP/README.md) | 런타임 | 호출자가 소유한 TCP 소켓에 시간 제한을 적용하는 채널 |
| [VagabondK.nF.MMS.Mapping](Source/MMS/VagabondK.nF.MMS.Mapping/README.md) | 생성기 | 일반 스칼라 읽기·명시적 쓰기·보고 수신과 Server 모델 바인딩 생성 |

### IEC 61850

| 프로젝트 | 종류 | 요약 |
|---|---|---|
| [VagabondK.nF.IEC61850](Source/IEC61850/VagabondK.nF.IEC61850/README.md) | 런타임 | MMS 기반 품질·UTC·Dbpos 변환과 공통 제어 모델 |
| [VagabondK.nF.IEC61850.Client](Source/IEC61850/VagabondK.nF.IEC61850.Client/README.md) | 런타임 | IEC DataSet·URCB 수신과 SPC/DPC/APC Client 제어 |
| [VagabondK.nF.IEC61850.Server](Source/IEC61850/VagabondK.nF.IEC61850.Server/README.md) | 런타임 | IEC 제어·DataSet·URCB Server 모델 |
| [VagabondK.nF.IEC61850.Mapping](Source/IEC61850/VagabondK.nF.IEC61850.Mapping/README.md) | 생성기 | IEC 값·품질·시각·DataSet·URCB·명시적 제어 바인딩 생성 |
| [VagabondK.nF.IEC61850.SCL](Source/IEC61850/VagabondK.nF.IEC61850.SCL/README.md) | 도구 | 제한된 범위의 SCL을 정적 C# 모델로 변환하는 .NET 10 명령줄 도구 |

### MQTT·Sparkplug

| 프로젝트 | 종류 | 요약 |
|---|---|---|
| [VagabondK.nF.MQTT](Source/MQTT/VagabondK.nF.MQTT/README.md) | 런타임 | 동기식 MQTT 클라이언트, QoS0/1/2·TLS·세션·배치·선택형 Worker |
| [VagabondK.nF.MQTT.Sparkplug](Source/MQTT/VagabondK.nF.MQTT.Sparkplug/README.md) | 런타임 | 제한형 Sparkplug B 스칼라 코덱과 Birth/Data/Death·별칭·Rebirth·세션 관리 |
| [VagabondK.nF.MQTT.Mapping](Source/MQTT/VagabondK.nF.MQTT.Mapping/README.md) | 생성기 | 명시적 Topic·평면 JSON 메시지 계약과 모델 사이의 정적 변환·게시·수신 코드 생성 |
| [VagabondK.nF.MQTT.Sparkplug.Mapping](Source/MQTT/VagabondK.nF.MQTT.Sparkplug.Mapping/README.md) | 생성기 | 모델 멤버와 스칼라 메트릭 사이의 송신·수신 연결 코드 생성 |

두 런타임 프로젝트의 `.nfproj`는 MCU용, 같은 폴더의 `.csproj`는 PC 검증용 빌드입니다.
매핑 생성기는 다른 그룹과 마찬가지로 PC 빌드 전용이며, 샘플은 장치별 모델·설정과 생성 API 사용법을 보여 줍니다.
MQTT 매핑은 제한된 JSON 계약을 제공하며 임의의 페이로드 형식을 자동으로 추정하지 않습니다.

### 공통 공유 소스

| 프로젝트 | 종류 | 요약 |
|---|---|---|
| [VagabondK.nF.Shared](Source/Shared/VagabondK.nF.Shared.shproj) | 공유 프로젝트 | Modbus·DNP3와 관련 PC 테스트에 직접 컴파일하는 `RealBits` 등 공통 소스 |

공유 프로젝트는 `.projitems`를 가져와 사용하며 별도 DLL이나 NuGet 패키지를 만들지 않습니다.

## 샘플·테스트

샘플은 실제 앱 구성 예제이고, 테스트는 PC 회귀·독립 피어 상호운용·가상 런타임·실물 검증으로 나뉩니다.
빌드 성공은 장치 실행 성공을 뜻하지 않으며, PC 결과는 MCU의 메모리·타이밍 검증을 대체하지 않습니다.

| 그룹 | 샘플 | 테스트·상호운용 |
|---|---|---|
| Modbus | [TCP/직렬 Client·Server, 메모리 자체 시험, 매핑](Samples/Modbus) | [프로토콜·레지스터 변환·매핑 회귀와 실물 검증용 앱](Tests/Modbus) |
| DNP3 | [TCP/시리얼 Host, Outstation·Master 매핑](Samples/DNP3) | [프로토콜·매핑, OpenDNP3·Python·Go 피어, 실물 검증용 앱](Tests/DNP3) |
| BACnet | [IP Host·AV/BV 매핑](Samples/BACnet) | [프로토콜·매핑, 독립 클라이언트와 실물 검증용 앱](Tests/BACnet) |
| SNMP | [UDP Agent·스칼라 매핑](Samples/SNMP) | [프로토콜·매핑, 독립 Manager와 실물 검증용 앱](Tests/SNMP) |
| MMS | [일반 MMS 객체 매핑](Samples/MMS) | [일반 MMS·정적 매핑 회귀](Tests/MMS) |
| IEC 61850 | [TCP Client·Server와 객체 매핑](Samples/IEC61850) | [IEC 서비스·SCL 생성·매핑·실물 검증](Tests/IEC61850) |
| MQTT·Sparkplug | [생성 매핑을 사용하는 JSON·Sparkplug B 장치 간 데이터 공유](Samples/MQTT) | [생성기 진단·값 변환·MQTT/Sparkplug 계약, 공유·TLS·브로커·실물 검증](Tests/MQTT) |
| 공통 | — | [장시간 반복 시험 계측 회귀·nanoCLR 가상 검증·공유 테스트 소스](Tests/Common) |

매핑 샘플의 폴더·프로젝트·어셈블리·네임스페이스는 `VagabondK.nF.<그룹>.Mapping`으로 맞춥니다.
MQTT 샘플도 `Samples\MQTT\VagabondK.nF.MQTT.Mapping`과
`Samples\MQTT\VagabondK.nF.MQTT.Sparkplug.Mapping`을 사용합니다.
`Source`의 동명 프로젝트는 PC 생성기이고 `Samples`의 프로젝트는 MCU 실행 앱이므로 경로를 구분하십시오.
MQTT 통합 테스트의 `DataSharing.Tests` 이름과 `Test-MqttDataSharingHardware.ps1` 실행 명령은 유지합니다.

지원 형식·역할은 [매핑 샘플 대조표](Samples/MappingCoverage.md),
손상 입력과 복구 범위는 [장애·복구 검증표](Tests/FaultRecovery.md)를 참조하십시오.
각 그룹의 상세 문서에서 개별 샘플 README와 재실행 명령을 찾을 수 있습니다.
2026-10-29에는 전 그룹의 추가 견고성 시험을 ESP32-S3에서 완료했습니다.
항목별 값 변경·부분 실패 후 보존·단절 회복·유한 반복 힙 측정을 구분해 기록하며,
MMS/IEC 생성 매핑도 libiec61850와 양 역할을 대조했습니다. 일부 MMS 리포트의 보조 인코딩 등
외부 구현의 제약은 대조표에 명시합니다. 모든 지원 조합·전체 명세 인증이나 장기 누수 부재를 뜻하지 않습니다.

## 개발 시작

전체 솔루션 개발 기준은 **Visual Studio 2026 + .NET nanoFramework 확장 + .NET 10 SDK**입니다.
패키지 복원·로컬 패키징에는 NuGet CLI도 필요합니다.
nanoFramework 자체의 VS 2022 지원과 달리, 이 솔루션은 PC 프로젝트의 `net10.0` 때문에 VS 2026을 기준으로 합니다.
기존 `VagabondK.nF.sln` 형식은 유지합니다.

Visual Studio에서 [VagabondK.nF.sln](VagabondK.nF.sln)을 엽니다.
설치·복원, 명령줄 빌드, 패키지를 사용하는 프로젝트의 검증, 장시간 시험과 실물 시험 주의사항은
[빌드·패키징·검증 안내](Build/README.md)에 정리했습니다.

## 폴더·문서 구조

| 경로 | 내용 |
|---|---|
| `Source\<그룹>` | 기능별 안내와 라이브러리·PC 생성기·도구 |
| `Source\Shared` | DLL을 만들지 않는 공유 프로젝트 |
| `Samples\<그룹>` | 실행 샘플과 개별 사용·설정 안내 |
| `Tests\<그룹>` | 동작·매핑·상호운용·실물 검증 프로젝트 |
| `Tests\Common` | 솔루션 밖의 공통 검증 프로젝트와 공유 테스트 소스 |
| `Packaging` | 솔루션 밖의 NuGet 전용 소비 검증 프로젝트 |
| `Build` | 빌드·패키징·검증 스크립트와 공통 개발 안내 |

현재 통신 라이브러리는 프로토콜 이름을 그룹으로 사용하며, 실제 프로젝트 경로는 Visual Studio의 솔루션 폴더와 일치합니다.
공개 API의 상세 설명은 한국어 XML 주석으로 관리하며 라이브러리·생성기·SCL 도구 빌드 시
동명의 `.xml` 문서를 출력합니다. 그룹별 안내는 구성 선택과 사용법을, 개별 README는 세부 사용 조건과 검증 기록을 설명합니다.
NuGet용 README도 그룹·개별 문서와 공통 템플릿에서 패키지별로 생성합니다.
설치·직접 의존성·사용 API·제약을 포함하고 개발용 성능·과거 시험 기록은 제외합니다.
문서 변경 후 34개 README의 생성과 실제 패키지 포함 여부는 [패키징 안내](Build/README.md)를 따릅니다.
