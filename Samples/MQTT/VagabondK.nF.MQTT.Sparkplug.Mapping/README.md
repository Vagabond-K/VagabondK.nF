# MQTT Sparkplug B 매핑 샘플

**PC Edge A ↔ 브로커 ↔ MCU Edge B**에서 메모리 모델의 값을 서로 다른 이름의 수신 모델에
반영합니다. MCU는 단순 소비자가 아니라 NBIRTH/DBIRTH/DDATA/DDEATH/NDEATH를 게시하는 Edge Node입니다.
별도 센서/GPIO 출력은 구동하지 않습니다. 실물 MCU 1대+PC 1대이며 MCU 두 대 실측이 아닙니다.
별도 [MQTT 라이브러리](../../../Source/MQTT/VagabondK.nF.MQTT/README.md)와
[Sparkplug 라이브러리](../../../Source/MQTT/VagabondK.nF.MQTT.Sparkplug/README.md)의 공개 API와
[정적 매핑 생성기](../../../Source/MQTT/VagabondK.nF.MQTT.Sparkplug.Mapping/README.md)를 사용하는 평가용 샘플입니다.
전송·코덱·수신 수명주기는 DLL, 반복적인 모델 변환은 생성 코드에 있고
샘플에는 선언형 모델·장치 설정·앱의 송수신 정책만 있습니다. Sparkplug 전체 명세 인증 구현을 뜻하지 않습니다.
폴더·프로젝트·어셈블리·네임스페이스는 `VagabondK.nF.MQTT.Sparkplug.Mapping`으로 통일했습니다.
이전 `.DataSharing` 샘플과 토픽·메트릭 계약은 같으며 `Source`의 동명 생성기와 경로를 구분합니다.

## 선언형 모델과 생성 API

`SparkplugMapping.cs`의 `RemoteProcess`는 `[SparkplugMap] partial class`이며 각 멤버가
와이어 이름·형식·별칭을 선언합니다. 예를 들어:

```csharp
[SparkplugMetric("Sensor/Count", Alias = 11)]
public int SourceCount { get; private set; }
```

Generator가 `RemoteProcess.Metrics(bool birth)`, `ApplySnapshot(snapshot)`,
`SparkplugSequence`, 중첩 `SparkplugBinding`을 만듭니다.
private setter에 직접 접근하므로 리플렉션이 필요하지 않고 setter가 네트워크를 호출하지 않습니다.
기존 `SparkplugEncoder.Metrics/Values` API는 이 생성 API로 변환한 결과를 명시적으로 인코딩하며
이름·형식·별칭 10..13과 기존 배열 순서는 유지합니다.

기존 `SparkplugMapping` 래퍼는 `RemoteProcess.SparkplugBinding`의 수신기를 구성합니다.
`Read()`는 `binding.ReadInto`로 새 모델에 값을 복사하므로 이전에 읽은 모델은 이후 수신으로 바뀌지 않습니다.
이름 없는 부분 DDATA와 다음 DBIRTH의 별칭 교체는 기존 `SparkplugReceiver`가 검증·병합하고
바인더는 그 검증된 스냅샷을 사용합니다. 스키마/값을 모두 검증한 뒤 대입하며,
사용자 정의 setter의 예외/부작용까지 롤백하는 트랜잭션은 제공하지 않습니다.

이름/형식/선언 순서와 일치하는 정적 스냅샷을 직접 적용하려면 `ApplySnapshot`도 쓸 수 있지만,
동적 별칭 수신에는 `SparkplugBinding.ReadInto`를 사용합니다.
`Read()`는 진단용 마지막 값도 반환하므로 **`IsAvailable(now)` 확인은 여전히 앱 책임**입니다.
토픽·원격 장치·3초 신선도 설정, MQTT 연결·구독·명시적인 게시,
seq/bdSeq·시계·재탄생 요청/응답·정상 종료 정책은 계속 샘플 앱이 관리합니다.
생성기는 이런 앱 정책이나 프로토콜 수명주기를 새로 구현하지 않습니다.

MCU `.nfproj`는 생성기 프로젝트를 분석기로 참조합니다. 생성기는 PC 빌드에서만 실행되며
MCU에는 생성 코드와 런타임 DLL만 포함됩니다.

## 자체 MQTT를 사용하는 이유

M2Mqtt의 일반 Publish는 `byte[]`를 지원하지만, 현재 nanoFramework.M2Mqtt 5.1.226의
CONNECT Last Will 인수는 `string`입니다. Sparkplug NDEATH protobuf를 문자열로 변환하면
바이너리 보존이 보장되지 않습니다. 따라서 이 샘플은 `VagabondK.nF.MQTT.dll`의 **자체 MQTT 3.1.1**
전송 계층으로 Will을 길이+원본 바이트 그대로 보냅니다. JSON 샘플도 같은 자체 MQTT 라이브러리를 사용합니다.
두 라이브러리는 샘플 소스 없이 별도 DLL/로컬 preview 패키지로 사용할 수 있습니다.
현재 자체 MQTT가 M2Mqtt의 전체 기능을 대체하거나 성능이 우월하다는 뜻은 아닙니다.

| 역할 | group / node / device | 게시·수신 |
|---|---|---|
| PC의 장치 A 역할 | VKNF / edge-a / sensor-a | MQTTnet 브로커·Edge A, MCU 값을 독립 생성 protobuf로 해석 |
| MCU의 장치 B 역할 | VKNF / edge-b / sensor-b | 자체 MQTT·protobuf 인코더/디코더, A의 메트릭을 `RemoteProcess`로 매핑 |

토픽은 `spBv1.0/VKNF/{NBIRTH,NDATA,NDEATH,NCMD}/{node}` 및
`spBv1.0/VKNF/{DBIRTH,DDATA,DDEATH}/{node}/{device}`입니다.
각 Edge는 자신의 NCMD를 QoS1로 구독한 뒤 BIRTH를 게시합니다.
BIRTH/DATA와 정상 종료 DEATH는 QoS0/retain=false, 브로커 Last Will은 QoS1/retain=false입니다.
구독 QoS에 따라 Will이 소비자에게 QoS0으로 전달될 수 있습니다.

## 공유 형식과 수명주기

| metric / DBIRTH alias | Sparkplug 형식 | B의 수신 멤버 |
|---|---|---|
| Sensor/Running / 10 | Boolean (11) | SourceRunning |
| Sensor/Count / 11 | Int32 (3) | SourceCount |
| Sensor/Temperature / 12 | Float (9) | ExternalTemperature |
| Sensor/Energy / 13 | Double (10) | ImportedEnergy |

NBIRTH의 `bdSeq`는 Int64 (4), `Node Control/Rebirth`는 Boolean false입니다.
외부 Tahu가 보내는 UInt64 (8)의 bdSeq도 별칭 없는 0..255의 노드 세션 번호에 한해 수신합니다.
일반 장치 메트릭의 UInt64 지원은 아닙니다.
DBIRTH에 이름·형식·별칭과 전체 초기 값을 싣고 DDATA는 별칭만 사용한 부분 갱신도 받습니다.
다음 DBIRTH의 별칭은 이전 것을 교체합니다. 음수 Int32는 uint32 2의 보수이며 ZigZag가 아닙니다.
NBIRTH seq=0부터 node/device가 하나의 0..255 순서를 공유합니다. NCMD와 NDEATH에는 seq가 없습니다.

순서가 누락되면 재정렬을 기다리지 않고 사용 불가 상태로 전환한 뒤 재탄생을 요청합니다.
NDEATH는 현재 `bdSeq`와 일치할 때만 적용하고, DDEATH 뒤에는 새 DBIRTH가 필요합니다.
접속 단절·새 NBIRTH·잘못된 메트릭을 받았을 때는 이전 값을 사용 가능한 상태로 표시하지 않습니다.
수신 오류 시 부분적으로 값을 대입하지 않고 마지막 불변 스냅샷을 진단용으로 유지합니다.
`Read()`와 함께 `IsAvailable(now)`를 확인해야 하며 마지막 device 값 수신부터 **3초 미만**만 유효합니다.
NDATA 생존 신호는 장치 값의 신선도를 갱신하지 않습니다.
여기서 신선도의 기준은 생산 시각이 아닌 로컬 수신 후 경과 시간입니다.

이 샘플의 장치 값 공유에는 네 스칼라 형식을 사용하고 노드 bdSeq는 Int64입니다.
라이브러리의 Boolean/Int32/Int64/Float/Double 다섯 스칼라를 모두 사용하며 나머지 형식, 문자열·배열·DataSet·Template,
historical/transient/null, Primary Host STATE/HA, 다중 장치 발견, DCMD·실제 제어,
재부팅 간 영속 세션/중복 제거 및 Sparkplug TCK 적합성은 구현·주장하지 않습니다.
`bdSeq`는 RAM 카운터이고 200에서 시작합니다. 이는 ASCII 범위를 넘는 varint Will 실측을 위한 값이며
재부팅 간 영속성이 필요한 제품 정책을 대신하지 않습니다.

## MQTT 구현 범위

이 샘플은 Clean Session, 단일 연결·스레드·진행 중 작업, QoS0/1, SUBACK/PUBACK, 부분 송수신,
PINGREQ/PINGRESP 및 정상 DISCONNECT를 사용합니다. 낮은 QoS의 유효 SUBACK도 허용합니다.
라이브러리의 토픽·클라이언트 ID 한도는 유효 UTF-8 128바이트이며, 샘플의 이름은 계속 ASCII입니다.
샘플 페이로드·Will은 512바이트, 패킷 본문은 1024바이트 기본값을 사용합니다.
protobuf는 최대 메트릭 8개, 메트릭 이름은 ASCII 64자입니다.
`Poll()`을 자주 호출해야 하며 콜백 안에서 Publish/Subscribe를 재진입 호출하면 안 됩니다.
콜백에서 요청 플래그를 설정하고 Service에서 실행합니다.
연결된 소켓의 생성·종료는 호출자 책임이며 오류가 발생한 뒤에도 소켓을 닫아야 합니다.

전송·응답과 부분 프레임의 시간 제한은 3초입니다. TCP `Socket.Connect` 자체는 OS 소켓 연결 정책을 따릅니다.
DISCONNECT 뒤에는 같은 3초 안에 브로커 EOF를 확인하고 소켓을 닫습니다.
남은 수신 바이트는 콜백·ACK 없이 비워, 미수신 데이터가 있는 TCP 연결을 닫을 때 발생하는 연결 초기화로
마지막 DEATH/DISCONNECT가 유실되는 것을 방지합니다. EOF가 없으면 정상 종료 성공으로 숨기지 않습니다.
EOF·형식 오류·ACK ID 불일치·시간 초과·콜백 실패 시 연결은 오류 상태가 되며 새 소켓·클라이언트가 필요합니다.
유효하지 않은 로컬 인수는 전송 전에 예외로 거절합니다.
QoS1 응답 시간 초과는 전달 여부가 불확실할 수 있어 앱이 자동 재게시하지 않습니다.
수신 QoS1 DUP는 ACK하고 앱에 다시 전달하므로 exactly-once가 아닙니다.
매핑은 중복 sequence도 순서 불일치로 판단해 재탄생으로 재동기화합니다.
독립 [MQTT 라이브러리](../../../Source/MQTT/VagabondK.nF.MQTT/README.md)는 QoS2, 사용자/암호,
Unicode, 다중 구독/해제, 선택형 재전송·RAM 세션·worker와 인증 완료 stream 어댑터도 제공합니다.
이는 이 Sparkplug 샘플의 기능 변경이 아니며, 샘플은 해당 옵션이나 TLS를 활성화하지 않습니다.
독립 라이브러리는 MQTT 5 속성·AUTH·복수 in-flight·native TLS도 지원하며
ESP32-S3 TLS 1.2 인증서/호스트명 검증까지 확인했습니다. 이 샘플의 MQTT 3.1.1 평가 설정은 유지합니다.
재부팅 간 영속성은 제공하지 않습니다. 이 매핑 샘플은 TLS를 사용하지 않으며,
MQTT 런타임에서 별도로 수행한 TLS 1.2의 2시간 시험과 구분합니다.
**인터넷·실설비망에 노출하지 말고 격리된 평가 LAN에서만 사용하십시오.**

## 실행

루트 `packages.config` 복원과 .NET 10 SDK/nanoFramework Visual Studio 도구가 필요합니다.
이 MCU 샘플은 M2Mqtt나 별도 MQTT packages.config 없이 기존 System.Net 참조를 사용합니다.
보드 Wi-Fi는 저장된 DHCP/AutoConnect 설정을 사용하고 PC 주소는 Program.cs의 `192.168.0.210:18883`입니다.
보드 UTC가 동기화되어 있어야 하며 2020년 이전 시각이면 가짜 타임스탬프 대신 오류를 냅니다.

```powershell
dotnet run -c Release --project .\Tests\MQTT\VagabondK.nF.MQTT.DataSharing.Tests -- --sparkplug-peer 192.168.0.210
```

PC READY 후 별도 터미널에서 일반 MCU 앱을 빌드하고 Device Explorer/nanoff로 배포합니다.

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe' `
  .\Samples\MQTT\VagabondK.nF.MQTT.Sparkplug.Mapping\VagabondK.nF.MQTT.Sparkplug.Mapping.nfproj `
  /t:Rebuild /p:Configuration=Release /p:MqttHardwareTest=false /warnaserror
```

PC는 60초 실행하며 NCMD rebirth에 응답하고 A→B 값을 게시합니다.
MCU는 B→A 값을 약 1초 간격으로 30회 게시한 뒤 정상 death/DISCONNECT로 종료합니다.
PC의 `MCU edge-b -> PC`와 보드 Debug Output의 `A -> B`에서 각 모델을 확인합니다.
PC는 일반 MCU의 30회 수신과 마지막 값·정상 death를 검사합니다.
수동 실행은 원래 앱을 자동 복원하지 않습니다.

## 자동 시험과 안전 복원

보드 없는 계약 시험은 위 PC 프로젝트를 인수 없이 실행합니다. 독립 Google.Protobuf 생성
디코더·인코더로 네 값의 비트 경계, BIRTH/DEATH, 별칭·순서·신선도·잘림을 검사합니다.
자체 MQTT는 미리 응답을 지정한 채널로 바이트별 수신, 바이너리 Will, ACK ID·길이·거절, 잘못된 프레임,
EOF·시간 초과, QoS1 중복, 콜백 실패, 연결 유지를 검사합니다.

```powershell
.\Build\Test-MqttDataSharingHardware.ps1 -AllowDeviceChanges -Sparkplug `
  -OutputPath .\bin\sparkplug-diagnostic-new
.\Build\Test-MqttDataSharingHardware.ps1 -AllowDeviceChanges -Sparkplug -NormalSample `
  -OutputPath .\bin\sparkplug-normal-new
npm ci --prefix .\Tests\MQTT\VagabondK.nF.MQTT.DataSharing.Tests\Tahu
.\Build\Test-MqttDataSharingHardware.ps1 -AllowDeviceChanges -Sparkplug -Tahu `
  -OutputPath .\bin\sparkplug-tahu-new
```

격리 COM3 ESP32-S3/MCU 192.168.0.187/PC 192.168.0.210/COM6 RS-485 전용입니다.
진단 모드는 네 형식의 PC↔MCU 값, 별칭 부분 갱신, 3초 경과 후 사용 불가 전환, 순서 누락·재탄생,
DDEATH·이전 별칭 거절, 재접속·NCMD 및 **MCU TCP 강제 종료 후 실제 브로커의 바이너리 NDEATH Will**
전달을 확인합니다. UDP 20005는 진단 빌드에만 포함됩니다.
단일 메트릭 갱신 264회로 이웃 값 보존과 seq 255→0을 확인하고,
중복·갭·옛 bdSeq·별칭 교체·후반 invalid 메트릭 거절 후 마지막 모델 보존도 검사합니다.
`MQS1` 진단 요청은 1..255 순번을 순환하며 `GCM1`은 순번을 소비하지 않고 GC 후 가용 힙을 반환합니다.
일반 모드는 진단 UDP 없는 30회 게시·birth/death를 확인하므로 B의 수신 값 직접 조회는 진단 결과와 구분합니다.
모든 실행은 성공/실패 후 원래 Modbus 이미지를 체크섬 확인해 복원하고
COM6 holding 0..2=123/456/65535까지 확인해야 `Restored=true`가 됩니다.
방화벽·네트워크 설정을 자동 변경하지 않습니다.

2026-10-28 최종 진단 기록은 `bin\sparkplug-native-20261028-final-r5`입니다.
정상 종료 보강 후 일반 샘플은 `bin\sparkplug-native-normal-20261028-final-r5`/`r6`/`r7`에서
각각 30회 공유·정상 death·Will 미발행을 확인했습니다. 모두 원래 Modbus 복원·COM6 읽기를 마쳤습니다.
초기 실패 기록도 보존하며, 수정 내용과 PC/MCU 회귀 범위는
[`Tests\FaultRecovery.md`](../../../Tests/FaultRecovery.md)에 있습니다.

참조: [Sparkplug 3.0 명세](https://sparkplug.eclipse.org/specification/version/3.0/documents/sparkplug-specification-3.0.0.pdf),
[Tahu protobuf](https://github.com/eclipse-tahu/tahu/blob/5736e404889d4b95910613040a99ba79589ffb13/sparkplug_b/sparkplug_b.proto).
PC `SparkplugSubset.proto`는 해당 전송 필드 번호를 사용하는 독립 스칼라 시험 스키마이지 전체 Tahu 구현이 아닙니다.

라이브러리 분리·송신 버퍼 최적화 후 실물 기록은
`bin\sparkplug-library-hardware-20261028`과 `bin\sparkplug-library-normal-20261028`입니다.
전자는 기존 양방향·장애·Will 검증, 후자는 일반 30회 공유·정상 death 검증을
별도 DLL을 참조하는 MCU 앱에서 다시 실행했고 원래 Modbus 복원·COM6 읽기를 마쳤습니다.

MQTT 3.x 기능 확장·송수신 최적화 후에는 `bin\sparkplug-expanded-hardware-20261028`과
`bin\sparkplug-expanded-normal-20261028`에서 같은 진단·일반 30회 공유를 다시 통과했습니다.
두 실행 모두 원래 Modbus 복원·COM6 읽기를 완료했습니다.

2026-10-29 생성 매핑 견고성 진단은 `bin\mqtt-sharing-20261029-125046`에서
2,374검사와 부분 갱신 구간 보존 힙 증가 0 B(허용 512 B)를 확인했습니다.
공식 Eclipse Tahu `sparkplug-client` 3.2.4 세션 시험도
`bin\mqtt-sharing-20261029-125212`에서 통과하고 원래 앱을 복원했습니다.
Tahu가 alias-only DDATA의 생략 형식을 Birth 별칭으로 복원하지 못하므로,
보드 송신 alias-only 값 비트는 기존 Google.Protobuf 시험으로 보완합니다.
이 결과는 전체 TCK 인증·장기 누수 부재를 의미하지 않습니다.
이후 샘플 이름 변경은 PC 회귀·MCU 빌드로 확인했으며 보드 결과는 위 당시 실행 기록입니다.
