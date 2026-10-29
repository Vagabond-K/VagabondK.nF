# VagabondK.nF.MQTT.Sparkplug.Mapping

Sparkplug 스칼라 모델의 연결 코드를 생성하는 **PC 빌드 전용 증분 소스 생성기**입니다.
Roslyn 4.12 / netstandard2.0을 기반으로 하며 MCU에는 생성된 C# 7.3 코드와
`VagabondK.nF.MQTT.Sparkplug` 런타임만 배포합니다. 리플렉션이나 동적 모델 검색은 사용하지 않습니다.
제한된 런타임 프로파일의 정적 매핑이며 Sparkplug 전체 사양/TCK 적합성은 뜻하지 않습니다.
선언용 특성은 nanoFramework의 `ExcludeType`으로 MCU PE 이미지에서 제외하며 생성 바인딩은 앱에 포함합니다.

## 선언과 명시적 송신

```csharp
using VagabondK.nF.MQTT.Sparkplug;
using VagabondK.nF.MQTT.Sparkplug.Mapping;

[SparkplugMap]
public sealed partial class Motor
{
    [SparkplugMetric("Motor/Running", Alias = 0)]
    public bool Running { get; private set; }

    [SparkplugMetric("Motor/Count", Alias = ulong.MaxValue)]
    public long Count { get; private set; }

    public void Update(bool running, long count)
    { Running = running; Count = count; }
}
```

`Boolean=bool`, `Int32=int`, `Int64=long`, `Float=float`, `Double=double`만 지원합니다.
`[SparkplugMetric("이름")]`은 이 대응표로 멤버 형식을 빌드 시 확정합니다.
형식을 명시한 기존 두 인수 선언과 동일한 송수신 코드를 생성하며 런타임 형식 탐색은 하지 않습니다.
별칭은 `Alias = 값`으로 지정할 수 있습니다. `Alias` 미지정은 별칭 없음이고 `Alias = 0`은 유효한 별칭입니다.
기존 두 인수·세 인수 생성자도 계속 지원하지만 생성자의 alias와 `Alias`를 함께 지정하면 빌드 오류입니다.
명시 형식과 CLR 형식의 불일치는 예전처럼 거절하며, 지원 형식을 넓히거나 숫자 형식을 임의로 변환하지 않습니다.

읽고 쓸 수 있는 인스턴스 필드 또는 속성을 1~8개 선언합니다. `private` 필드와 접근자도 지원합니다.
별칭 없이 선언하면 DATA에서도 이름과 형식을 유지합니다.
별칭 0과 `ulong.MaxValue`는 모두 유효합니다.

```csharp
var model = new Motor();
var session = new SparkplugSession("Factory", "edge-1", 0);
model.Update(true, 12);
byte[] nodeBirth = session.NodeBirth(1000);
byte[] deviceBirth = session.Next(1001, model.Metrics(true), true);
model.Update(false, 13);
byte[] data = session.Next(1002, model.Metrics(false));
```

`Metrics(true)`는 이름·형식·선언된 별칭을 담고,
`Metrics(false)`는 별칭이 있으면 이름·형식을 생략합니다.
배열은 호출할 때마다 새로 만들며 **멤버 선언 순서**로 채웁니다. 여러 `partial` 파일로 나눈 모델도
생성된 송신·수신 스키마에서 같은 순서를 사용합니다. 별도로 만든 스냅샷을 직접 반영할 때도
이 순서가 일치해야 하므로 컴파일 단위나 멤버 순서를 바꾸면 스키마가 변경된 것으로 취급하십시오.
메트릭 이름은 출력 가능한 ASCII 1..64자이며 이름·별칭은 중복될 수 없습니다.
8개 이하더라도 긴 이름을 모두 포함한 BIRTH가 런타임 512바이트 제한을 넘으면
인코딩 시 예외가 발생합니다. 생성기를 사용해도 페이로드 크기 제한은 그대로 적용됩니다.
NaN과 무한대는 런타임의 팩터리 메서드가 예외로 거부합니다.

속성에 값을 대입하거나 `Update`를 호출해도 네트워크 통신은 발생하지 않습니다.
연결, MQTT 구독·게시, 공유 순서 번호(seq), 타임스탬프·단조 시계, bdSeq 보존,
BIRTH/DATA/DEATH 게시와 NCMD 응답은 앱이 명시적으로 관리합니다.

## 수신: 기존 SparkplugReceiver에 위임

```csharp
var binding = new Motor.SparkplugBinding("Factory", "edge-1", "motor-1", 3000);
var receiver = binding.Receiver;
receiver.SetConnected(true);
// MQTT 수신마다:
SparkplugResult result = receiver.Apply(topic, bytes, retained, monotonicMilliseconds);
// 앱과 모델 사용자가 동일한 잠금을 사용해야 합니다.
if (receiver.IsAvailable(monotonicMilliseconds))
    binding.ReadInto(model);
```

`SparkplugBinding`은 선언된 이름·형식으로 `SparkplugReceiver`를 구성할 뿐
BIRTH/DEATH, bdSeq, seq, 별칭 학습·교체, 부분 DDATA 병합, 수신 값의 유효 시간 검사를 중복 구현하지 않습니다.
`ReadInto(model)`은 수신기가 검증한 마지막 불변 스냅샷을 모델에 대입합니다.
DBIRTH 전에는 `false`를 반환합니다. 그 뒤에는 값의 유효 시간이 지나거나 DEATH·수신 실패가 발생한 후에도 **진단용 마지막 값**을 복사합니다.
유효성은 항상 `Receiver.IsAvailable(now)`로 확인하고
`Receiver.NeedsRebirth`에 대한 NCMD 게시 정책은 앱에서 구현하십시오.
`ReadInto`는 자기 수신기의 검증된 스냅숏만 사용하므로 다음 DBIRTH에서 별칭이 바뀌어도 작동합니다.

`model.ApplySnapshot(snapshot)`은 별도로 만든 스냅샷을 직접 반영하는 API입니다.
전체 개수와 각 위치의 이름·형식·값 필드를 먼저 확인하며, 이름이 생략된 메트릭은 선언한 별칭과 일치해야 합니다.
이 API 자체는 수신 수명주기·값의 유효 시간이나 원격 별칭 협상을 처리하지 않습니다.
동적 별칭 수신에는 `SparkplugBinding.ReadInto`를 사용하십시오.
`SparkplugSequence`는 마지막으로 반영한 스냅샷의 seq이며 처음에는 -1입니다.

스키마나 값이 일치하지 않으면 모델을 변경하지 않고 `ArgumentException`을 던집니다.
인수가 `null`이면 `ArgumentNullException`을 던집니다.
모든 메트릭 검사를 마친 뒤 속성의 접근자를 순서대로 호출합니다.
**사용자 정의 접근자에서 발생한 예외는 그대로 전달하며, 이미 발생한 부작용은 되돌리지 않습니다.**
여러 멤버를 하나의 원자적 앱 상태로 읽고 쓸 때는 앱과 바인더 호출에 동일한 잠금을 사용하십시오.

## 진단과 패키지

| ID | 거절하는 선언 |
|---|---|
| VKSP001 | partial이 아닌 클래스, 중첩/제네릭/상속/static/abstract/file-local 및 지원하지 않는 클래스 형태 |
| VKSP002 | 지원하지 않는 CLR/메트릭 형식 조합, static/readonly/const, 읽기 또는 쓰기 불가능한 속성, indexer/ref/init-only |
| VKSP003 | 이름/옵션/메트릭 개수 제한 위반 |
| VKSP004 | 중복 이름 또는 별칭 |
| VKSP005 | 생성 API 이름과 사용자 멤버/모델 이름 충돌 |

NuGet 패키지는 Analyzer와 SDK 형식이 아닌 nanoFramework 프로젝트용 `build` 대상 파일을 포함합니다.
런타임 의존성 메타데이터는 공통 `Build\Mapping.Package.targets`에서 구성합니다.
생성기 소스 프로젝트를 직접 참조하는 MCU 프로젝트에는 다음 설정을 추가합니다.

```xml
<ProjectReference Include="..\경로\VagabondK.nF.MQTT.Sparkplug.Mapping.csproj"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false"
                  SkipGetTargetFrameworkProperties="true" />
```

## 회귀 시험과 샘플

독립 PC 회귀 시험:

```powershell
dotnet run -c Release --project .\Tests\MQTT\VagabondK.nF.MQTT.Sparkplug.Mapping.Tests
```

실제 MCU 예제는 [`Samples\MQTT\VagabondK.nF.MQTT.Sparkplug.Mapping`](../../../Samples/MQTT/VagabondK.nF.MQTT.Sparkplug.Mapping/README.md)입니다.
하드웨어나 브로커 없이 잘못된 선언에 대한 진단, C# 7.3 컴파일, 다섯 스칼라 형식의 경계값,
별칭 0·최댓값, 대입 전 스키마 검증, 부분 수신·수명주기 처리 위임 및 기존 샘플 계약을 검사합니다.

## 실물 검증

`bin\mqtt-sharing-20261029-125046`에서 네 장치 형식의 단일 메트릭 갱신 264회,
다른 값 보존·seq 255→0·별칭 교체·잘못된 후반 메트릭 거절을 보드에서 확인했습니다.
공식 Eclipse Tahu 세션 시험은 `bin\mqtt-sharing-20261029-125212`에 별도로 기록했습니다.
bdSeq의 UInt64 수신 호환성은 런타임의 노드 세션용이며 이 생성기의 장치 UInt64 매핑 지원이 아닙니다.
Tahu alias-only 값 해석 한계와 측정 범위는 [매핑 대조표](../../../Samples/MappingCoverage.md#추가-견고성-실물-시험)를 참조하십시오.
