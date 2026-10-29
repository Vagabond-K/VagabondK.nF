# 정적 매핑 샘플 형식 대조표

각 프로토콜 생성기가 **현재 지원하는 값 형식·역할·제어 종류**를 해당 샘플에서 찾을 수 있도록 정리했습니다.
모든 예제에 같은 선언을 반복하는 대신, 통신 흐름이 단순한 기본 모델과 전체 형식을 보여 주는 추가 모델을
같은 샘플 프로젝트에 둡니다. 표에 있는 형식을 다른 프로토콜에서도 지원한다는 뜻은 아니며,
모든 옵션 조합을 실물에서 시험했다는 뜻도 아닙니다.

| 프로토콜 | 지원 형식 전체 | 샘플 소스·사용 |
|---|---|---|
| Modbus Client / Server | Boolean, Int16, UInt16, Int32, UInt32, Single, Int64, UInt64, Double | `Samples\Modbus\VagabondK.nF.Modbus.Mapping\AllFormats.cs`: `FormatSource`/`FormatTarget`, 시작 시 `AllFormats.Run()`에서 PDU 왕복 읽기와 명시적 쓰기 |
| Modbus 영역·순서 | Coil, DiscreteInput, HoldingRegister, InputRegister; High/LowByteFirst, High/LowWordFirst | `AllFormats.cs`와 `Models.cs`: InputRegister/ReadGroup은 기존 기본 모델, 나머지는 동반 모델. 워드·바이트 순서는 양쪽 모델에 동일하게 지정 |
| DNP3 Outstation / Master | Binary(bool), DoubleBitBinary(byte), Counter/FrozenCounter(uint), Analog/AnalogOutputStatus(float 또는 double), BinaryOutputStatus(bool) | `Samples\DNP3\VagabondK.nF.DNP3.Mapping\DeviceData.cs`와 `Samples\DNP3\VagabondK.nF.DNP3.MasterMapping\ReceivedData.cs`: 7종; 양쪽 `FloatFormats.cs`: float32 Analog/AOS 추가 예제 |
| DNP3 제어·메타데이터 | CROB(Group12), Analog Output(Group41); Flags, Timestamp, 이벤트 Class/Deadband/Variation, 수신 콜백 | 기존 `DeviceData.cs`/`ReceivedData.cs`. float 동반 모델은 기본 Flags/시각을 사용. Outstation은 시작 시 float 게시를 별도 실행하고, Master는 Observer에 float binding을 연결 |
| MMS Client / Server | Boolean(bool), Integer(int), FloatingPoint(float) | `Samples\MMS\VagabondK.nF.MMS.Mapping\Models.cs`: 읽기·검증기·명시적 int 쓰기; `WritableFormats.cs`: 세 형식 모두 Client/Server 쓰기 모델 |
| IEC61850 Client / Server | bool, int, float, IecDoublePoint, IecQuality, UTC long | `Samples\IEC61850\VagabondK.nF.IEC61850.Mapping\AllFormats.cs`: 여섯 형식과 쓰기 가능한 세 스칼라; `Models.cs`: 명시적 TimeQuality 연결, 추가 모델은 기본 시간 품질 사용 |
| IEC61850 제어 | DirectNormal, SboNormal, DirectEnhanced, SboEnhanced; Boolean(SPC), DoublePoint(DPC), AnalogFloat32(APC) | `AllFormats.cs`: `TypedLocal`/`TypedRemote`의 네 제어. PC 시험이 샘플 소스를 직접 사용해 Select/Operate/완료/Cancel 실행. 모든 제어는 기본 비활성 |
| SNMP Agent | Integer32(int), Counter32/Gauge32/TimeTicks(uint), Counter64(ulong), OctetString/IpAddress(byte[]), ObjectIdentifier(uint[]) | `Samples\SNMP\VagabondK.nF.SNMP.Mapping\DeviceData.cs`: 8종 모두 기존 UDP 샘플에서 게시·쓰기 |
| BACnet Device | AnalogValue(float), BinaryValue(bool) | `Samples\BACnet\VagabondK.nF.BACnet.Mapping\DeviceData.cs`: AV/BV, 읽기 전용/쓰기, 품질 멤버 |
| MQTT 양방향 공유 | JSON 계약의 sequence(int), temperatureDeciC(int), running(bool) → 다른 이름의 수신 모델 | `Samples\MQTT\VagabondK.nF.MQTT.Mapping\SharingContract.cs`: 독립 [MQTT.Mapping](../Source/MQTT/VagabondK.nF.MQTT.Mapping/README.md) 생성 API 사용. 업무상 값 범위·순번·값의 유효 시간은 앱 정책 |
| Sparkplug B 양방향 공유 | 장치 Boolean, Int32, Float, Double; 노드 bdSeq(Int64), Rebirth(Boolean) | `Samples\MQTT\VagabondK.nF.MQTT.Sparkplug.Mapping`: 독립 [Sparkplug.Mapping](../Source/MQTT/VagabondK.nF.MQTT.Sparkplug.Mapping/README.md)으로 모델 연결. 기존 런타임이 Birth/Death·별칭·수신 순서 검증. 전체 Sparkplug 명세 구현은 아님 |

필드, 속성, `private` 쓰기 접근자, 읽기 전용 속성, 검증·수신 메서드는 기본 모델과 추가 모델에서 보여 줍니다.
지원하는 모든 멤버 접근자 조합이나 프로토콜별 모든 Variation을 나열한 표는 아닙니다.
예를 들어 DNP3 setter-only·배열 기반 모델의 큰 규모 사용은
`Tests\DNP3\VagabondK.nF.DNP3.Mapping.Hardware`의 규모 샘플과 Master README에 있습니다.
SNMP Manager/BACnet Client는 생성기가 지원하지 않으므로 지원 샘플이 있는 것으로 표시하지 않습니다.
MMS/IEC 코덱의 구조체·배열 지원도 이 정적 스칼라 매핑의 지원 형식과는 별개입니다.

## 실행과 누락 방지

기본 모델과 `AllFormats.cs`, `FloatFormats.cs`, `WritableFormats.cs`의 추가 모델은
영역·값 종류별 특성, 공통 `Domain`, 메트릭 형식 추론 등 간소화한 선언을 사용합니다.
기존 형식의 선언도 계속 지원하며, 기존 선언과 간소화한 선언 53쌍이 같은 실행 코드를 생성하는지 비교합니다.
지원 형식·주소·명시적 전송 시점은 바꾸지 않았습니다. MQTT JSON 샘플은 C# 멤버 이름과
전송 필드 이름이 다르므로 이름을 생략하지 않습니다.

MQTT JSON 샘플은 시작 시 `SnapshotJson.DemonstrateBufferEncoding()`으로 같은 버퍼를 재사용하며
서로 다른 길이의 JSON이 기존 인코딩 결과와 일치하는지 검사합니다.
`SnapshotJson.Encode(snapshot, buffer, offset)`은 기록 길이를 반환하므로 그 구간만 사용해야 합니다.
실제 자체 `MqttConnection` 발행은 배열 전체를 받으므로 정확한 길이의 배열을 반환하는 `Encode(snapshot)`을 유지합니다.
버퍼 예제 때문에 전송 JSON에 공백을 덧붙이거나 메시지 계약을 바꾸지는 않습니다.

PC 테스트는 샘플 파일 자체를 링크해 컴파일하고 생성한 바인딩의 값을 검사합니다.
테스트 안에 비슷한 모델을 따로 작성하는 것만으로 샘플 검증을 대신하지 않습니다.
MQTT/Sparkplug 코어는 샘플 소스 링크가 아니라 별도 PC DLL ProjectReference로 검사하고,
매핑 선언과 앱 정책 어댑터는 소스로 링크하며 PC 생성기 Analyzer가 모델 연결 코드를 생성합니다.
추가로 다른 이름을 사용하는 메트릭 1~8개의 스키마, Int64, 별칭 전체 범위, 설정 가능한 값의 유효 시간을 공개 API로 검증합니다.
Modbus 동반 예제는 보드에서 외부 통신 전에 메모리 PDU 왕복을 실행합니다.
MMS/IEC 동반 예제는 MCU에서 생성·Publish하고 PC 시험에서 TCP 양방향과 제어를 실행합니다.
DNP3 float 동반 모델의 게시·수신은 PC에서도 값까지 검사합니다.
이 추가 형식의 PC 실행/MCU 빌드를 기존 7종 DNP3 실물 측정과 혼동하지 마십시오.

```powershell
.\Build\Test-MappingSamples.ps1
```

위 명령은 PC 테스트 8개(Modbus/DNP3/MMS·IEC/SNMP/BACnet/MQTT·Sparkplug 생성기·계약)를 실행하고 MCU 샘플 9개를 빌드합니다.
빌드한 CLR 어셈블리의 형식 토큰과 nanoFramework `.pdbx`를 대조하여
선언용 매핑 특성이 MCU PE 이미지에서 제외되었는지도 검사합니다.
보드 앱을 변경하지 않습니다. .NET 10 SDK, nanoFramework Visual Studio 도구,
기존 루트 `packages.config`와 MQTT 샘플 `packages.config`의 NuGet 복원이 필요합니다.
새 지원 형식을 추가할 때는 생성기, 이 표, 실제 샘플, 샘플을 링크하는 테스트를 함께 갱신하십시오.

MQTT 실물 교환·장애/복원 실행은 별도의
[`Test-MqttDataSharingHardware.ps1`](../Build/Test-MqttDataSharingHardware.ps1)로 수행합니다.
`-Sparkplug`는 자체 MQTT/Sparkplug 양방향 통신과 바이너리 Will을,
`-Sparkplug -NormalSample`은 일반 MCU 샘플의 데이터 공유 30회와 정상 DEATH를 검사합니다.

## 외부 구현 상호운용과 보조 시험의 구분

정상 통신의 호환성은 가능한 한 외부 구현으로 확인하고, 직접 작성한 시험기는
잘린 패킷·비정상 입력·경계값·할당·반복 상태 시험을 보완하는 용도로 유지합니다.
외부 구현도 지원 범위와 제약이 있으므로 통과 결과를 전체 명세 인증으로 해석하지 않습니다.

| 범위 | 외부 구현 | 남겨 둔 보조 시험·제약 |
|---|---|---|
| Modbus TCP | pymodbus 3.11.3 | 생성기 동등성·메모리 PDU·할당·오류 주입 |
| DNP3 TCP | OpenDNP3 독립 Master/Outstation | 생성기·저장소·이벤트/수신 반복·메모리 시험 |
| BACnet/IP | BACnet 4.0.0 | 생성기·메모리 저장소·오류 주입 |
| SNMP 매핑 | SharpSnmpLib 12.5.7 | 직접 BER 경계·비정상 입력 시험; 외부 구현이 지원하지 않는 두 번째 OID arc ≥40 별도 검사 |
| MMS/IEC61850 기본 서비스·생성 매핑 Client/Server | libiec61850 1.6.2 독립 Client/Server (`f0f797bf955da763353f7081c892a1ff6b55e5a1`) | 네 역할을 보드에서 검증. IEC 리포트는 외부 라이브러리로 교환. MMS의 일부 리포트 송신은 아래에 설명한 시험 래퍼의 직접 PDU 인코딩으로 보완하며, 기존 자체 피어 반복 시험도 유지 |
| MQTT | MQTTnet 4.3.7.1207 | 자체 JSON 계약·순번·유효 시간·장애 주입 |
| Sparkplug | Eclipse Tahu sparkplug-client 3.2.4 + MQTTnet 브로커 | 기존 Google.Protobuf 독립 스키마·오류 시험 유지; Tahu가 해석하지 못하는 alias-only DDATA 값은 기존 시험으로 보완 |

SNMP는 기본 PC 시험에서도 외부 피어 루프백을 실행하며 실물 시험 명령은 그대로입니다.
Tahu 실물 시험은 Node.js와 아래 테스트 전용 의존성을 설치한 후 명시적으로 선택합니다.
기존 `-Sparkplug`와 `-Sparkplug -NormalSample` 시험을 대체하거나 없애지 않습니다.

```powershell
npm ci --prefix .\Tests\MQTT\VagabondK.nF.MQTT.DataSharing.Tests\Tahu
.\Build\Test-MqttDataSharingHardware.ps1 -AllowDeviceChanges -Sparkplug -Tahu
```

## 추가 견고성 실물 시험

2026-10-29, 격리된 ESP32-S3(COM3)에서 아래 시험을 순차 실행했습니다.
정상 통신은 위 외부 피어를 사용하고, 고장 주입·동시성·메모리 시험은 별도 보조 시험으로 구분합니다.
각 실행은 원래 Modbus SerialServer를 복원한 뒤 COM6에서 HoldingRegister 0..2가
`123, 456, 65535`인지 확인합니다. Wi-Fi 설정과 네이티브 펌웨어는 변경하지 않습니다.

| 범위 | 추가 실물 검증 | 결과·증거 폴더(`bin` 아래) |
|---|---|---|
| Modbus | 9개 모델 멤버를 하나씩 로컬 변경 후 외부 읽기, 나머지 주소 보존, 반대 방향 쓰기로 복원. 정수 극값·음의 0·비정규수·무한대·quiet NaN 등 37종의 정확한 비트 왕복과 이웃 주소 보존. 워드/바이트 순서, 잘못된 Transaction/Unit ID 거절과 모델 보존, 예외·지연·단절 후 새 연결 | `mapping-hardware-20261029-125737`: 경계 37종 및 Master 고장 5종 PASS. 8/32/128포인트별 60초 이상 재접속·비정상 프레임·GC 혼합 시험은 `mapping-hardware-20261029-110932`: 각 보존 힙 증가 0 B |
| DNP3 Outstation | 8/32/128포인트 × 이벤트 용량 16/128. 첫/중간/마지막 선택 게시와 다른 포인트 보존, flags만 변경, timestamp만 변경, deadband 정확한 경계/초과, 외부 이벤트 수신·confirm 후 제거 | `dnp3-mapping-hardware-20261029-112507`: 각 100라운드 PASS, 보존 힙 증가 0 B. 작은 큐 overflow 103회는 의도된 검사이며 큰 큐는 0회 |
| DNP3 Master/TCP | 모델을 유지한 채 피어 재시작 3회·TCP 강제 단절 3회와 최종 정상 수신. 양 역할의 header/data/mixed CRC 및 부분 프레임 만료 | `dnp3-master-soak-20261029-115537`: 8사이클/73,854검사 PASS. `dnp3-master-soak-20261029-120122`: 고장 8종, CRC 오류 28회·부분 만료 2회 PASS |
| DNP3 Serial | 양 역할 header/data/mixed CRC, ACK 재시도 소진, 응답 만료, Reset, 부분 프레임 만료 후 실제 정상 교환 | `dnp3-master-soak-20261029-120508`: CRC 6종 PASS. `dnp3-master-soak-20261029-121035`: 44,465검사, ACK 재시도 2회·응답/부분 만료 후 회복 PASS. 정상 회복 시 보존 힙 증가 0 B |
| BACnet | AV/BV 8/32/64개, 첫/중간/마지막 로컬 변경 후 모든 객체 비교, 잘못된 형식/무한대 쓰기 후 기존 값 보존, 새 외부 peer/invoke 공간에서 재조회 | `bacnet-mapping-hardware-20261029-111849`: 샘플 256회 및 규모별 100라운드 PASS, 보존 힙 증가 0 B |
| SNMP | SharpSnmpLib 256회 8종 SMI 왕복. 첫/중간/마지막 로컬 변경, 다중 SET 마지막 항목의 wrongType에 앞선 모든 값 보존, 비정상 데이터그램 이후 복구 64회 | `snmp-mapping-hardware-20261029-122940`: PASS, 혼합 고장 구간 보존 힙 증가 144 B(허용 256 B) |
| SNMP 규모 보조 | 8/32/128 OID, 512바이트 OctetString, GET/GETBULK/SET 각 100라운드. 이 규모 시험의 피어는 외부 라이브러리가 아닌 기존 BER 시험기 | `snmp-scaling-20261029-123740`: PASS, 각 보존 힙 증가 0 B |
| IEC61850 Server/Client | 실제 생성 매핑과 libiec61850의 양방향 쓰기·리포트 모델 적용, 잘못된 값 거절, 서버 단절·응답 유실 후 새 세션 | `mapping-interop-hardware-20261029-133546`: Server 6세션/고장 3회/쓰기 6회/리포트 6회/요청 252회, Client 3세션/쓰기 6회/검사 66회/적용 9회/거절 3회 PASS. 두 역할 모두 보존 힙 증가 0 B |
| MMS Server/Client | 실제 생성 매핑의 양방향 쓰기, 부분 리포트 적용·잘못된 값 거절 후 모델 보존, 쓰기 응답 대기 중 리포트 수신, 서버 단절·응답 유실 회복 | `mapping-interop-hardware-20261029-133546`: Server 6세션/고장 3회/쓰기 6회/리포트 3회/요청 171회, Client 3세션/쓰기 6회/검사 72회/적용 6회/거절 12회 PASS. 두 역할 모두 보존 힙 증가 0 B. MMS 리포트의 외부 구현 범위는 아래 제약 참조 |
| 동시 스냅샷 보조 | 같은 앱 잠금을 쓰는 writer와 reader 각 최소 800회: Modbus 다중 워드 스칼라, BACnet 값/품질, DNP3 값/flags/시각. SNMP는 별도로 배열 동시 변경/읽기와 byte[]/OID 배열 소유권 | SNMP 견고성 이미지의 시작 전 검사로 실행. `snmp-mapping-hardware-20261029-122940`: 모두 완료 후 외부 SNMP 시험 PASS |
| MQTT JSON | 온도/운전 상태를 독립 변경 64회, 필드 순서 변경, 중복 키·누락·범위/정수 넘침·최대 길이 초과·오래된 순번·모든 JSON 절단 위치 거절, 전체 모델 보존 및 정상 입력 회복 | `mqtt-sharing-20261029-124920`: 적용 68회/거절 65회 PASS, 보존 힙 증가 12 B(허용 512 B) |
| Sparkplug | 4개 형식별 단일 메트릭 부분 갱신 264회, 나머지 값 보존, seq 255→0, 중복/갭, 옛 bdSeq death, 별칭 교체, 잘못된 후반 메트릭의 원자성, 재접속/Birth/Will | `mqtt-sharing-20261029-125046`: 2,374검사 PASS, 부분 갱신 구간 보존 힙 증가 0 B(허용 512 B) |
| Eclipse Tahu | 공식 세션 클라이언트와 양방향 Birth/부분 데이터/재탄생/Device·Node Death/Will 재검증 | `mqtt-sharing-20261029-125212`: PASS. alias-only 값 해석 제약은 앞 표와 같음 |

동시성은 **생성 저장소가 제공하는 한 스칼라/샘플의 잠금 단위**만 검사합니다.
여러 독립 요청 전체에 트랜잭션을 보장하거나, 모든 중간 변경을 이벤트로 전달한다는 뜻은 아닙니다.
DNP3 timestamp만 변경하면 Static 시각은 갱신하지만 이벤트를 만들지 않으며,
deadband는 마지막 이벤트 기준으로 **초과**해야 합니다.
BACnet은 서버 매핑만 지원하므로 다른 invoke ID의 응답을 받아 모델을 갱신하는 자체 Client 매핑은 없습니다.
MQTT JSON의 상한 순번·온도 범위·중복 거절과 Sparkplug의 갭/중복 시 재탄생 요구는 각 샘플의 기존 정책입니다.
MMS/IEC Client의 리포트 모델 갱신은 추가 `ReadAll` 없이 수신한 리포트로 확인합니다.
libiec61850의 domain별 항목 리포트 송신 길이 계산 제약 때문에 MMS Client 방향의 일부
리포트는 시험 래퍼 `peer.c`가 직접 PDU를 인코딩합니다. 이 인코딩은 외부 라이브러리 검증으로
계산하지 않으며, 래퍼는 해당 고정 revision의 내부 구조에도 의존합니다.
외부 MMS Client가 domain named-list 리포트를 처리하지 못하므로 MMS Server 방향은
목록·값 읽기와 리포트 수신 내성까지만 확인합니다. 네이티브 다중 항목 쓰기는 이 시험에 포함하지 않습니다.
서버 응답 유실은 지연·타임아웃으로 주입하며, Client의 `Rejected`는 예상한 거절 검사 횟수입니다.
힙 수치는 정해진 유한 반복 구간의 최저 GC 여유 메모리를 초기값과 비교한 값이며,
며칠간의 누수 부재나 모든 소켓·큐·지연 한계를 증명하지 않습니다.

재실행 명령은 다음과 같습니다. 보드 작업은 병렬 실행하지 마십시오.
`Test-Dnp3MasterMappingSoak.ps1`의 CRC 모드는 PC 어셈블리를 로드하므로
모드마다 새 PowerShell 프로세스를 사용해야 다음 빌드 시 DLL 잠금을 남기지 않습니다.

```powershell
.\Build\Test-ModbusMappingHardware.ps1 -AllowDeviceChanges -NoDebugger -StabilitySeconds 60 -Iterations 128
.\Build\Test-Dnp3MappingHardware.ps1 -AllowDeviceChanges -Rounds 100 -Iterations 256
pwsh -NoProfile -File .\Build\Test-Dnp3MasterMappingSoak.ps1 -AllowDeviceChanges -DurationMinutes 3 -FaultEveryCycles 1 -CyclePauseSeconds 0
pwsh -NoProfile -File .\Build\Test-Dnp3MasterMappingSoak.ps1 -AllowDeviceChanges -TcpRecovery -DurationMinutes 1
pwsh -NoProfile -File .\Build\Test-Dnp3MasterMappingSoak.ps1 -AllowDeviceChanges -SerialValidation -SerialCrc -DurationMinutes 1
pwsh -NoProfile -File .\Build\Test-Dnp3MasterMappingSoak.ps1 -AllowDeviceChanges -SerialValidation -DurationMinutes 1
.\Build\Test-BacnetMappingHardware.ps1 -AllowDeviceChanges -Rounds 100 -Iterations 256
.\Build\Test-SnmpMappingHardware.ps1 -AllowDeviceChanges -Robustness -Iterations 256
.\Build\Test-SnmpMappingScaling.ps1 -AllowDeviceChanges -Rounds 100
.\Build\Test-MappingInteropHardware.ps1 -AllowDeviceChanges -Iterations 3
.\Build\Test-MqttDataSharingHardware.ps1 -AllowDeviceChanges
.\Build\Test-MqttDataSharingHardware.ps1 -AllowDeviceChanges -Sparkplug
.\Build\Test-MqttDataSharingHardware.ps1 -AllowDeviceChanges -Sparkplug -Tahu
```

시험기 자체에서 발견한 문제도 수정했습니다. MQTT 진단의 1바이트 요청 순번이 255 이후 진행하지
못하던 부분은 1..255 순환으로 수정했으며, 같은 순번 재전송은 여전히 명령을 중복 실행하지 않습니다.
최초 통합 동시성 fixture의 `Action<int>` 사용 시에는 네이티브 CLR `LoadProhibited`가 발생했습니다.
비제네릭 fixture로 교체한 뒤 같은 보드에서 위 스냅샷 시험을 완료했습니다.
이때 자동 wire 배포가 불가능해 부트 로그로 확인한 **관리 앱 전용 deploy 파티션**만 ROM 경로로 복원했고,
원래 Modbus 응답과 nanoFramework 장치 인식을 다시 확인했습니다.
이 결과만으로 매핑 엔진 버그 또는 네이티브 CLR의 모든 원인을 단정하지는 않습니다.

MMS Client 시험에서는 수신 콜백 안의 검증 예외가 nanoFramework 디버그 출력 비용으로
통신 작업 기한을 소모해 타임아웃을 일으켰습니다. 시험 fixture의 콜백은 독립 리포트 스냅샷을
고정 용량 큐에 보관하고, 매핑 적용·검증은 통신 작업 종료 후 수행하도록 수정했습니다.
엔진·펌웨어·타임아웃 값은 변경하지 않았습니다. 수정 후 위 네 역할을 모두 보드에서 완료했습니다.
직전 실행 `mapping-interop-hardware-20261029-133151`은 외부 MMS Client의 초기 연결 실패로
중단됐으며, 이를 성공으로 계산하지 않았습니다. 최종 결과는 전체 재실행한 `133546` 기준입니다.
