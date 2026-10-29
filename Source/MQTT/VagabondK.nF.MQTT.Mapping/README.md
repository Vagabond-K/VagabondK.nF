# VagabondK.nF.MQTT.Mapping

MQTT는 전송할 페이로드의 형식을 정하지 않습니다. 이 **평가용 사전 릴리스** 생성기는 PC에서 실행되며,
앱이 선언한 **정확한 토픽과 고정된 평면 JSON 스칼라 계약**만 처리합니다. MQTT 프로토콜이나 연결을 다시 구현하지 않습니다.
기존 BACnet/SNMP/DNP3 매핑과 같은 `netstandard2.0` 기반 Roslyn 4.12 증분 소스 생성기이며,
MCU에 생성기 DLL을 배포하지 않습니다.
선언용 특성은 nanoFramework 빌드의 `ExcludeType`으로 MCU PE 이미지에서 제외하며,
생성한 바인딩·델리게이트는 앱 코드로 남습니다.

```csharp
using VagabondK.nF.MQTT.Mapping;

[MqttJsonMap("plant/device/sensor", MaximumBytes = 160)]
public sealed partial class Sensor
{
    [MqttJsonMember(0, "sequence", Minimum = 1)]
    public int Sequence { get; private set; }
    [MqttJsonMember(1, "temperatureDeciC", Minimum = -500, Maximum = 1500)]
    public int Temperature { get; private set; }
    [MqttJsonMember(2, "running")]
    public bool Running { get; private set; }
}
```

## 생성 API

JSON 이름과 C# 멤버 이름이 같으면 `[MqttJsonMember(0)] public int Sequence { get; set; }`처럼
이름을 생략할 수 있습니다. 이 선언의 JSON 이름은 대소문자를 그대로 유지한 `Sequence`이며,
`[MqttJsonMember(0, "Sequence")]`와 동일한 코덱을 생성합니다.
`@event` 같은 이스케이프 식별자는 `event`로 처리하며 기존 ASCII 이름 검사를 적용합니다.
**이름을 생략한 멤버를 개명하면 전송 계약도 바뀝니다.** 위 예제처럼 C# 이름과 JSON 이름이
다르거나 전송 계약을 고정해야 할 때는 기존처럼 JSON 이름을 명시하십시오.
순서·범위·필수 필드 검사와 지원 형식은 바뀌지 않습니다.

- `Sensor.MqttBinding.Topic`, `MaximumBytes`: 고정 계약 상수.
- `Encode(model)`: 각 멤버의 값을 한 번씩 읽고 범위를 확인한 뒤, `Order` 오름차순으로 JSON `byte[]`를 반환합니다.
- `Encode(model, buffer, offset = 0)`: 제공된 버퍼에 JSON을 기록하고 기록한 바이트 수를 반환합니다. 값과 남은 공간을 모두 검사한 뒤 기록하며, 공간 부족이나 범위 오류에서는 버퍼를 변경하지 않습니다.
- `TryDecode(payload, out Snapshot)`: 전체 JSON과 모든 값의 범위를 검증해 불변 스냅샷을 반환합니다. 실패하면 `false`를 반환하고 결과는 `null`입니다.
- `Apply(model, snapshot)`: 검증한 스냅샷을 모델의 속성이나 필드에 명시적으로 반영합니다.
- `TryApply(model, receivedTopic, payload)`: 토픽이 정확히 일치하고 전체 페이로드가 유효할 때만 `Apply`를 호출합니다.
- `Publish(model, MqttJsonPublisher)`: `publisher(Topic, Encode(model))`를 한 번 호출합니다. 발행 함수는 앱이 소유하는
  `VagabondK.nF.MQTT.MqttConnection` 등 앱이 선택한 연결을 호출합니다. 현재 샘플은 자체 MQTT를 사용합니다.

```csharp
Sensor.MqttBinding.Publish(sensor, (topic, bytes) =>
    connection.Publish(topic, bytes, VagabondK.nF.MQTT.MqttQos.AtLeastOnce, false));
Sensor.MqttBinding.Snapshot snapshot;
if (Sensor.MqttBinding.TryDecode(receivedBytes, out snapshot))
{
    // 보관 메시지 여부, 연결 상태, 순번, 값의 유효 시간 등 앱 정책을 먼저 확인합니다.
    lock (gate) Sensor.MqttBinding.Apply(sensor, snapshot);
}
```

`Publish`는 콜백을 동기적으로 호출하는 API이며, 그 자체로 MQTT ACK 완료를 확인하지 않습니다.
QoS·보관 메시지·재연결·재시도·ACK 대기는 런타임과 앱이 관리합니다. 속성에 값을 대입해도 네트워크 통신은 자동으로 발생하지 않습니다.
`Encode(model)`은 중간 문자열 없이 정확한 길이의 결과 배열 하나를 할당합니다.
버퍼 오버로드는 생성된 인코더 내부에서 성공 시 힙을 할당하지 않습니다.
앱의 속성 접근자나 전송 계층에서 발생하는 할당까지 없애는 것은 아닙니다.
`TryDecode`는 성공 시 스냅샷을 할당합니다.
리플렉션, 범용 JSON 라이브러리, 런타임 매핑 테이블은 사용하지 않습니다.

```csharp
var buffer = new byte[Sensor.MqttBinding.MaximumBytes];
int length;
lock (gate) length = Sensor.MqttBinding.Encode(sensor, buffer);
// buffer 전체가 아니라 [0, length) 구간만 전송합니다.
```

버퍼의 유효 구간을 받는 전송 API에만 이 결과를 그대로 넘기십시오.
현재 샘플의 `MqttConnection.Publish`처럼 배열 전체를 받는 API에는
기존 `Encode(model)` 결과를 사용해야 합니다. 큰 버퍼의 나머지 바이트를 함께 보내면 잘못된 JSON이 됩니다.
비동기 전송에 버퍼를 넘겼다면 전송 측에서 사용을 마칠 때까지 재사용하지 마십시오.

## 명시적인 범위와 거부 정책

- 다른 형식 안에 중첩되지 않고, 상속·제네릭·`static`·`abstract`를 사용하지 않는 `partial class`만 지원합니다.
- 읽고 쓸 수 있는 인스턴스 필드 또는 인덱서가 아닌 속성만 지원합니다. `private` 접근자도 사용할 수 있습니다.
- 멤버 **1~32개**, 형식 **bool/int**. `Order`는 중복 없는 **0~31**, JSON 이름도 중복 불가.
- 토픽은 **출력 가능한 ASCII 1~256자**이며 와일드카드 `+/#`는 사용할 수 없습니다.
- JSON 이름은 **출력 가능한 ASCII 1~64자**이며 큰따옴표와 역슬래시는 사용할 수 없습니다.
  **비ASCII 유니코드 필드 이름과 `\u`를 포함한 이름의 모든 이스케이프 표기는 거부합니다.**
  대소문자를 구분하며 수신 순서는 자유입니다.
- 모든 필드는 정확히 한 번 있어야 합니다. 정의하지 않은 필드·필수 필드 누락·중복 필드,
  마지막 항목 뒤의 쉼표·JSON 뒤의 추가 데이터·BOM·배열·중첩 객체·null·문자열·실수·지수 표기·선행 `+`·선행 0을 거부합니다.
  JSON 공백(`SP/CR/LF/TAB`)은 토큰 사이에만 허용합니다. `-0`은 JSON 정수 0으로 허용합니다.
- int는 **-2147483648~2147483647**(양끝 포함), 추가 `Minimum`/`Maximum` 범위도 양끝 포함입니다.
  bool에는 범위를 지정할 수 없습니다. 숫자 오버플로는 해석 중 거부하며 인코딩은 지역별 숫자 표기 설정에 영향을 받지 않습니다.
- `MaximumBytes` **2~4096**, 기본 1024. 최장 인코딩 길이보다 작으면 컴파일 진단을 냅니다.
  null/빈 입력/상한 초과는 파싱 전에 거부합니다.
- **전체 구문과 모든 값의 범위를 검증한 뒤에만 모델에 대입합니다.** 잘못된 입력으로 모델의 일부만 바뀌지는 않습니다.
  단, 앱의 접근자나 콜백에서 발생한 예외는 그대로 전달하며, 접근자의 부작용은 되돌리지 않습니다.
  모델을 동시에 읽고 쓰는 경우 앱에서 같은 잠금 객체를 사용해 보호해야 합니다.

진단: `VKMQ001` 클래스 형태, `002` 멤버/형식/수량, `003` 토픽/옵션/길이,
`004` 이름/순서 중복, `005` 생성 이름(`MqttBinding`, 스냅샷의 `Snapshot`) 충돌.
유효하지 않은 모델의 바인딩은 생성하지 않습니다.

## 연결 및 패키지

MCU 프로젝트에는 런타임 참조가 아닌 Analyzer 참조로 생성기를 추가합니다.

```xml
<ProjectReference Include="..\path\VagabondK.nF.MQTT.Mapping.csproj"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false"
                  SkipGetTargetFrameworkProperties="true" />
```

NuGet 패키지는 `analyzers\dotnet\cs`의 DLL과 기존 nanoFramework 프로젝트용 `build` 대상 파일을 포함합니다.
공통 `Build\Mapping.Package.targets` 패키징을 따릅니다. 생성 코드는 C# 7.3 및 nanoFramework 코어 API만 사용합니다.

[`Samples\MQTT\VagabondK.nF.MQTT.Mapping`](../../../Samples/MQTT/VagabondK.nF.MQTT.Mapping/README.md)의
`SharingContract.cs`는 동일한
`sequence/temperatureDeciC/running` 계약과 `SnapshotJson` API를 유지하며 생성 코덱을 호출합니다.
센서 값의 범위와 수신 측의 보관 메시지·순번·연결·유효 시간 정책은 유지합니다.
샘플은 자체 `MqttConnection`으로 발행하고 PUBACK를 동기적으로 기다리며, 대기 중에는 Poll로 수신을 처리합니다.
시작 시 `SnapshotJson.DemonstrateBufferEncoding()`을 실행해 재사용 버퍼의 offset·기록 길이와
기존 배열 인코딩 결과의 일치를 확인합니다. 실제 MQTT 송신은 정확한 길이의 배열을 계속 사용합니다.

## 생성기 회귀 시험

`dotnet run --project Tests\MQTT\VagabondK.nF.MQTT.Mapping.Tests`.
진단, 생성 코드의 C# 7.3 컴파일, nanoFramework 참조 컴파일, 경계값·잘못된 JSON·대입 전 전체 검증·예외 전파와 샘플 계약을 검사합니다.

## 실물 검증

자체 MQTT를 사용하는 현재 매핑 샘플의 보드 시험은
`bin\mqtt-sharing-20261029-124920`에서 적용 68회·거절 65회와 원래 앱 복원을 확인했습니다.
이는 샘플의 bool/int 계약 검증이며 임의 JSON·모든 모델 접근자의 원자성을 뜻하지 않습니다.
상세 범위는 [매핑 대조표](../../../Samples/MappingCoverage.md#추가-견고성-실물-시험)를 참조하십시오.
