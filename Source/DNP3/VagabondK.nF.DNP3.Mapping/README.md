# DNP3 Outstation / Master 정적 객체 매핑

`VagabondK.nF.DNP3.Mapping`은 PC 빌드에서만 실행되는 선택형 Roslyn 4.12
소스 생성기입니다. 리플렉션, 동적 검색 또는 추가 MCU 런타임 DLL 없이
Outstation 모델을 기존 `Dnp3Database`에 게시하거나 Master의 수신을 모델에 반영합니다.
로컬 평가 버전은 `0.1.0-preview.1`입니다.

## Master 수신 매핑

```csharp
[Dnp3MasterMap]
internal partial class RemoteData
{
    [AnalogInput(0, Flags = nameof(Quality), Timestamp = nameof(Time))]
    public double Temperature { get; private set; }
    public byte Quality { get; private set; }
    public long Time { get; private set; }

    [Dnp3Received]
    private void Received(Dnp3Measurement[] measurements, Dnp3ResponseInfo response)
    {
        // 현재 모델과 별도로 배열의 모든 IsEvent 항목을 수신 순서대로 처리합니다.
    }
}
```

`new RemoteData.Dnp3MasterBinding(model, syncRoot)`가 `IDnp3MasterHandler`를 구현합니다.
이를 기존 `new Dnp3Master(2, 1, binding, ...)`에 전달합니다. MCU에는 코어와 **Master**만
필요하며 Outstation은 필요하지 않습니다. 전송 DLL은 TCP/Serial 중 별도로 선택합니다.
반대 역할 모델은 `[Dnp3Map]`을 사용하며 한 모델에 두 역할을 혼합하지 않습니다.

- 모델 조건과 Point 수/종류/인덱스는 아래 Outstation 계약과 같습니다.
  단, 값·Flags·Timestamp는 **쓰기 가능한** 인스턴스 필드/속성이어야 합니다.
  private setter와 setter-only 속성도 지원하지만 readonly/init-only/정적 멤버는 거부합니다.
  서로 다른 Point가 값·품질·시각 저장 멤버를 공유할 수 없습니다.
- Point 개수 제한 외에 nanoFramework 형식의 인스턴스/정적 필드 및 메서드가 각각 255개 이하여야 합니다.
  자동 속성의 내부 필드·접근자와 생성자도 포함하며, Master 모델이 이 한도를 초과하면 VKDN007 오류가 발생합니다.
  큰 모델은 배열 기반 속성 또는 별도 모델을 사용하십시오. Point 512개라는 구문 상한이
  512개 필드나 접근자를 MCU에서 허용한다는 뜻은 아닙니다.
  생성되는 바인딩 코드는 이진 검색 분기와 case가 최대 32개인 검증·대입 코드로 나누어 nanoCLR의 switch 항목 수 제한을 지킵니다.
- Master의 `[Dnp3Point]`에는 Flags/Timestamp만 지정할 수 있습니다.
  EventClass/Deadband/Variation 및 `Dnp3Control`은 Outstation 옵션이므로 진단 오류입니다.
  제어 명령은 기존 Master의 명시적 `Command` API를 사용합니다. 속성 대입이 원격 출력을 제어하지 않습니다.
- 같은 잠금 안에서 수신 Fragment의 모든 **매핑 대상 값**을 사전 검사한 뒤,
  배열 순서대로 값·품질·시각을 대입합니다. 같은 Point가 반복되면 모델에는 마지막 수신값이 남습니다.
  시각이 역행하거나 값·시각이 같아도 이벤트를 합치거나 생략하지 않습니다.
  시각 없는 Variation의 0과 품질 Flags도 디코딩된 그대로 반영합니다. RTC/수신 시각으로 대체하지 않습니다.
- Analog `float`은 유한한 float 범위만 수용하고 일반적인 Double→Single 반올림을 적용합니다.
  범위를 벗어나면 포화/Infinity로 바꾸지 않고 예외를 발생시킵니다. 정밀한 비트 보존에는 `double`을 사용합니다.
- 모델마다 `[Dnp3Received] void Method(Dnp3Measurement[], Dnp3ResponseInfo)`가 정확히 하나 필요합니다.
  모델 갱신 후 **원본 배열과 응답 정보**를 한 번 전달합니다. 빈 Fragment, 미등록 Point,
  같은 Point의 여러 이벤트도 빠짐없이 전달하므로 앱은 이를 명시적으로 처리해야 합니다.
  개별 이벤트 값은 갱신된 모델이 아니라 원본 배열에서 읽어야 합니다.
  이벤트 큐/콜백 지연 실행/새 배열/반영 여부 필터는 생성하지 않습니다.
- 콜백은 `Process` 안에서 동기적으로 실행되며, 모델과 같은 잠금 안에서 Confirmation을 반환하기 전에 호출됩니다.
  `First/Final/Unsolicited/Sequence/IIN`은 기존 `Dnp3ResponseInfo`를 그대로 전달합니다.
  여러 Fragment 전체의 원자적 갱신이나 자동 조립은 제공하지 않습니다.
- 사전 값 검사가 실패하면 대입 전에 예외가 발생합니다. 반면 사용자 setter나 콜백에서 예외가 발생하면 이미 반영한 값은 되돌리지 않습니다.
  예외는 숨기지 않으며 기존 Master는 해당 호출에서 Confirmation을 반환하지 않습니다.
  앱은 실패한 TCP 세션을 교체하고 Endpoint를 초기화해야 합니다.
  재시도나 재연결 이후에도 각 이벤트를 정확히 한 번만 처리하는 것은 보장하지 않습니다.
- 앱 읽기에도 같은 `syncRoot`를 사용해야 값·품질·시각을 일관되게 관찰할 수 있습니다.
  setter와 콜백은 짧게 유지하고 네트워크 작업·일관되지 않은 잠금 획득 순서·동일 Master로의 재진입을 피하십시오.

진단 VKDN006은 Master Fragment 수신 메서드 계약 오류입니다.
[실제 MCU Master 샘플·실물 비용](../../../Samples/DNP3/VagabondK.nF.DNP3.MasterMapping/README.md)을 참조하십시오.

## Outstation 게시 매핑

```csharp
using VagabondK.nF.DNP3;
using VagabondK.nF.DNP3.Mapping;

[Dnp3Map]
internal partial class Measurements
{
    public byte Quality = 1;
    public long UtcMilliseconds;

    [AnalogInput(0, EventClass = 1, Deadband = 0.5,
        Flags = nameof(Quality), Timestamp = nameof(UtcMilliseconds))]
    public double Temperature { get; private set; }
}
```

Master와 Outstation 모두 다음 축약 특성을 사용할 수 있습니다.
기존 `Dnp3Point` 선언도 계속 지원하며, 대응하는 선언은 동일한 실행 코드를 생성합니다.

| 특성 | `Dnp3PointType` |
|---|---|
| `BinaryInput(index)` | `Binary` |
| `DoubleBitInput(index)` | `DoubleBitBinary` |
| `Counter(index)` | `Counter` |
| `FrozenCounter(index)` | `FrozenCounter` |
| `AnalogInput(index)` | `Analog` |
| `BinaryOutput(index)` | `BinaryOutputStatus` |
| `AnalogOutput(index)` | `AnalogOutputStatus` |

`BinaryOutput`과 `AnalogOutput`은 **출력 상태 값**의 매핑입니다. 속성 대입으로 제어 명령을
송신하지 않으며, 제어는 기존 `Dnp3Control` 또는 Master 명령 API를 명시적으로 사용합니다.
한 멤버에 Point 특성을 여러 개 붙이면 빌드 오류입니다.
Master에서는 축약형에도 `Flags`와 `Timestamp`만 허용하며 이벤트 정책은 지정할 수 없습니다.

`new Measurements.Dnp3Binding(model, syncRoot, eventCapacity: 32)`로 생성하고,
`binding.Database`와 `binding`을 `Dnp3Station`의 Database/제어 처리기로 전달합니다.
앱이 같은 `syncRoot`로 모델을 변경한 다음 `binding.Publish()`를 명시적으로 호출합니다.
생성자는 초기값을 읽어 Database를 만들며 초기 이벤트는 생성하지 않습니다.

변경된 Point를 아는 앱은 `binding.Publish(Dnp3PointType.Analog, 0)`으로 해당 Point만
게시할 수 있습니다. 종류와 인덱스로 생성된 분기에서 직접 멤버를 읽으며 모델 전체를 순회하거나
이전 값 캐시를 추가하지 않습니다. 선택하지 않은 Point의 값·품질·시각은 읽거나 갱신하지 않습니다.
미등록 종류/인덱스는 `ArgumentException`, 대상 getter/값 검증 예외는 그대로 전파됩니다.
전체를 일관된 모델 스냅샷으로 읽어야 할 때는 기존 `Publish()`를 사용하십시오.

| Point 종류 | 모델 형식 |
|---|---|
| Binary, BinaryOutputStatus | `bool` |
| DoubleBitBinary | `byte` (값 0~3) |
| Counter, FrozenCounter | `uint` |
| Analog, AnalogOutputStatus | `float` 또는 `double` (유한한 값) |

모델은 최상위 비제네릭·비상속 `partial class`, Point는 1~512개입니다.
private/readonly 필드와 getter-only/private getter 속성도 읽을 수 있습니다.
인덱스는 종류별 0~65535이며 다른 종류에서 같은 인덱스를 사용할 수 있습니다.
Flags와 Timestamp는 같은 모델의 읽기 가능한 인스턴스 `byte`/`long` 멤버 이름입니다.
미지정 시 각각 Online(1)/0이고, Timestamp는 UTC 밀리초 0~0xFFFFFFFFFFFF입니다.
생성 이름 `Dnp3Binding`은 예약됩니다.

## 이벤트·동기화 계약

- Class는 기본 1, 0이면 이벤트 없음, 그 밖에는 1~3입니다.
- 이벤트 용량 기본값은 기존 Database와 같은 128입니다. MCU RAM 예산에 맞게 명시하십시오.
  ESP32-S3 실측에서 용량 16→128은 13,440 B를 추가했습니다. 작은 버퍼에서는 동시 변경의
  이벤트 손실을 처리해야 하므로, 용량 축소가 항상 적절한 최적화는 아닙니다.
- Deadband와 Static/Event Variation은 기존 `Dnp3PointDefinition` 지원 범위를 그대로 사용합니다.
  Deadband는 Analog 계열에만 지정하며, 같은 Flags에서 마지막으로 저장된 이벤트 값
  (첫 이벤트 전에는 초기값) 대비 변화량이 **Deadband를 초과**할 때 이벤트가 발생합니다.
  정확히 같은 변화량이면 이벤트가 없고 Static 값은 갱신됩니다.
  Flags만 바뀌면 이벤트를 만들지만 Timestamp만 바뀌면 Static 시각만 갱신합니다.
  Class 0은 이러한 변화에도 이벤트를 만들지 않습니다.
- 전체 Publish는 같은 잠금 안에서 값·Flags·Timestamp를 모두 지역 구조체로 읽은 뒤
  기존 `Database.Update`를 순서대로 호출합니다. 정상 게시에 임시 배열을 만들지 않습니다.
  이벤트 버퍼의 용량이 부족해도 첫 실패에서 중단하지 않고 모든 Point의 갱신을 수행합니다.
- 반환값 `false`는 하나 이상 이벤트가 버퍼에 들어가지 못했다는 뜻입니다.
  **Static 값은 갱신되며 `Database.Overflow`가 유지됩니다.** 호출자는 로그·알림으로 처리해야 합니다.
  이벤트를 저장하지 못한 Point는 이벤트 생성의 비교 기준을 갱신하지 않으므로 다음 Publish에서 다시 이벤트 생성을 시도합니다.
- 선택 Publish도 같은 잠금 안에서 대상 값·Flags·Timestamp를 읽고 기존 `Database.Update`를
  호출합니다. 반환값/Deadband/Overflow 계약은 전체 게시와 같습니다.
  `false`인 대상을 값이 바뀌지 않았다는 이유로 완료 처리하지 말고 이벤트 공간 확보 후 다시 게시하십시오.
  공유 Flags/시각이 바뀌면 그 변경을 반영할 모든 Point를 앱이 게시해야 합니다.
- 이벤트는 Master의 Confirmation으로 제거됩니다. 모두 제거한 뒤에만
  `Database.ClearOverflow()`를 호출할 수 있습니다.
- getter·검증·실행 과정에서 발생한 예외는 그대로 전달합니다. 게시 전 값 읽기는 같은 잠금 안에서 이루어지지만,
  Database의 전체 갱신은 원자적 트랜잭션이 아니며, 이미 적용한 변경을 되돌리지 않습니다.
  통신 스레드는 갱신 중간 상태를 읽을 수 있습니다.
- 모델은 앱 소유입니다. 같은 잠금을 사용하지 않는 앱 접근, 여러 게시 주체의 호출, 직접 Database 갱신 사이의
  일관성은 앱에서 관리해야 합니다. 잠금 안에서 실행되는 getter·제어 메서드는 짧게 유지하고,
  외부 I/O나 일관되지 않은 잠금 획득 순서로 교착 상태를 만들지 않아야 합니다.

## 출력 제어

`[Dnp3Control(12, index, Validate = nameof(Check))]`는 CROB,
Group 41은 Analog Output입니다. 다음 형태의 앱 메서드를 직접 호출합니다.

```csharp
private Dnp3CommandStatus Check(Dnp3Command command)
{
    // Select에서도 호출되므로 실제 출력을 변경하지 않습니다.
    return Dnp3CommandStatus.Success;
}

[Dnp3Control(41, 0, Validate = nameof(Check))]
private Dnp3CommandStatus Apply(Dnp3Command command, bool selected)
{
    // 앱이 범위·권한·장치 상태를 검사하고 실제 결과를 반환해야 합니다.
    return Dnp3CommandStatus.Success;
}
```

두 메서드 모두 모델과 같은 잠금 안에서 실행됩니다. `Operate`는 앱 검증을 다시 수행합니다.
Select/Operate 시퀀스·선택 만료·재전송 중복 처리는 기존 Station의 책임입니다.
직접 `binding.Operate(..., true)`를 호출하는 것만으로 Select가 수행되는 것은 아닙니다.
미등록 Group/인덱스는 `NotSupported`이며 그룹별 같은 인덱스 중복은 빌드 오류입니다.
CROB의 ControlCode·Count·On/Off 시간과 `selected`를 변경하지 않습니다.
자동 bool 대입, 펄스 타이머, GPIO, 출력 성공 추정, 자동 Status 변경/게시를 제공하지 않습니다.

## 설치 및 검증

매핑 NuGet 설치 시 같은 버전의 `VagabondK.nF.DNP3` 공통 코어를 자동 복원합니다.
Master/Outstation 역할과 TCP/Serial은 선택 의존성이므로 별도로 설치합니다.

MCU에는 `VagabondK.nF.DNP3`와 역할에 따라 `VagabondK.nF.DNP3.Outstation` 또는
`VagabondK.nF.DNP3.Master`, 필요한 TCP 또는 Serial 패키지만 참조합니다.
생성기는 Analyzer 전용이며, 한 역할만 사용하는 앱에 반대 역할의 MCU DLL을 추가하지 않습니다.

```xml
<ProjectReference Include="VagabondK.nF.DNP3.Mapping.csproj"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false"
                  SkipGetTargetFrameworkProperties="true" />
```

`packages.config`를 사용하는 프로젝트에서는 패키지의 `build\VagabondK.nF.DNP3.Mapping.targets`를
가져와야 합니다. SDK 프로젝트는 NuGet의 Analyzer 자동 검색을 사용합니다.
생성된 매핑 Attribute 타입에는 `NANOFRAMEWORK` 또는 `NANOFRAMEWORK_1_0` 빌드에서
`ExcludeType`을 붙여 MCU PE에서 제외합니다. 해당 상수가 없는 일반 .NET 컴파일에는 선언 타입이 남지만
런타임에서 조회하지 않습니다. Generator·Roslyn DLL도 PC 빌드 전용입니다.
진단은 VKDN001(모델/이름), 002(멤버/개수), 003(옵션/메타데이터),
004(중복), 005(제어 메서드), 006(Master 수신 메서드), 007(Master 모델 레이아웃)입니다.

```powershell
dotnet run -c Release --project .\Tests\DNP3\VagabondK.nF.DNP3.Mapping.Tests
.\Build\Pack-Dnp3Mapping.ps1 -OutputPath .\bin\nuget\dnp3-mapping-new
.\Build\Test-Dnp3Mapping.ps1 -FeedPath .\bin\nuget\dnp3-mapping-new
.\Build\Test-Dnp3MappingHardware.ps1 -AllowDeviceChanges
.\Build\Test-Dnp3MasterMappingHardware.ps1 -AllowDeviceChanges
.\Build\Test-Dnp3MasterMappingSoak.ps1 -AllowDeviceChanges -SerialValidation
```

PC 시험도 독립 원본 OpenDNP3 Master/Outstation을 사용하므로
`Build\Build-Dnp3ValidationMaster.ps1`로 먼저 준비합니다.
Freeze 처리나 품질·시각의 자동 생성, 실제 장치 드라이버, 인증은 지원 범위에 포함하지 않습니다.
