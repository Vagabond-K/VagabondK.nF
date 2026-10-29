# BACnet AV/BV 정적 객체 매핑

`partial` 모델의 `float`와 `bool` 멤버를 기존 `IBacnetValueStore`에 직접 연결하는
**Device 측** 저장소를 빌드 시 생성합니다. 런타임 리플렉션·멤버명 검색·델리게이트 바인딩을 사용하지 않으며,
추가 MCU 런타임 DLL도 필요하지 않습니다. 기존 BACnet/BACnet.IP 패키지만 사용합니다.
소스 생성기와 Roslyn은 PC 빌드 전용이며 보드에 배포하지 않습니다.
생성된 매핑 Attribute 타입도 `NANOFRAMEWORK` 또는 `NANOFRAMEWORK_1_0` 빌드에서는
`ExcludeType`으로 MCU PE에서 제외합니다. 해당 상수가 없는 일반 .NET 컴파일에는 선언 타입이 남지만
런타임에서 조회하지 않습니다.

```csharp
using VagabondK.nF.BACnet.Mapping;

[BacnetMap]
public partial class DeviceData
{
    [BacnetAnalogValue(0, Quality = nameof(Good))]
    public float Temperature { get; private set; } = 250f;

    [BacnetAnalogValue(100, Writable = true)]
    public float Setpoint { get; private set; } = 800f;

    [BacnetBinaryValue(0, Writable = true)]
    public bool Enabled { get; private set; } = true;

    public bool Good = true;
}
```

`BacnetAnalogValue(instance)`는 `float` AV, `BacnetBinaryValue(instance)`는 `bool` BV를
명시하는 특성입니다. 기존 `BacnetPoint(instance)`의 형식 추론도 유지합니다.
동일한 선언은 같은 저장소·float 변환 코드를 생성하며, `Writable`과 `Quality` 옵션도 같습니다.
AV 특성을 bool에 붙이거나 BV 특성을 float에 붙이면 빌드 오류입니다.
한 멤버에는 기존형 또는 구체형 중 하나만 지정합니다.
이름이 구체화되어도 지원 객체는 AV/BV의 Present_Value로 제한되며 AI/BI나 Client 기능은 추가되지 않습니다.

`new DeviceData.BacnetStore(model, sharedLock)`를 기존 `BacnetDevice`에 전달합니다.
모델과 잠금 객체의 수명은 앱이 관리하고 **앱도 같은 잠금 안에서 값·품질을 갱신**해야 합니다.
`Read`는 같은 잠금 안에서 값과 품질을 읽어 하나의 `BacnetSample` 구조체를 반환합니다.
`Quality`를 지정하지 않으면 `Good=true`입니다. 쓰기는 setter나 필드를 통해 값을 즉시 반영하되 품질은 변경하지 않습니다.
private setter·필드·품질 getter를 지원하며, 앱의 getter나 setter에서 발생한 예기치 않은 예외는 그대로 전달합니다.
ACK는 setter 호출이 반환되었음을 뜻하며 실제 장치의 동작 완료를 보장하지 않습니다. 여러 요청에 걸친 트랜잭션이나 변경 되돌리기도 제공하지 않습니다.
모델에 값을 대입해도 자동으로 패킷을 보내지 않습니다. 앱이 Device/UDP 처리 루프를 구동하고,
원격 ReadProperty 요청을 받았을 때 현재 모델 값을 응답합니다.

## 선언 계약

- 최상위 비제네릭·비상속 `partial class`, 1~64개 인스턴스 필드/속성입니다.
- `float`는 Analog Value, `bool`은 Binary Value입니다. 다른 숫자·enum·배열 자동 변환은 없습니다.
- `BacnetPoint(uint instance)`는 0~4194302 범위입니다. 기본값은 읽기 전용입니다.
- 같은 **종류+instance** 중복만 거절합니다. AV0/BV0는 서로 다른 객체이므로 허용합니다.
- 종류(AV=2, BV=5)와 인스턴스 번호로 구성한 객체 식별자 순서에 따라 저장소 인덱스를 정합니다.
  기존 Device의 Object_List는 Device가 첫 항목이고 그 뒤 생성 객체가 이어집니다.
- `Writable=true` 멤버에는 변경 가능한 필드 또는 setter가 필요합니다.
- `Quality=nameof(Member)`는 같은 모델의 읽기 가능한 인스턴스 bool 멤버 이름입니다.
  상수/정적/상속 멤버·null/빈 이름은 지원하지 않습니다.
- `BacnetStore`는 생성 전용 이름입니다. `VagabondK.nF.BACnet.Mapping.Generated`는 생성되는 보조 코드 전용 네임스페이스입니다.

AV의 유한한 float32 값은 음의 0·비정규수(subnormal)를 포함하여 원시 비트를 보존합니다.
생성 저장소의 직접 쓰기에도 NaN/Infinity 또는 BV의 0/1 이외 값은 예외로 거절합니다.
앱이 AV에 비유한 값을 직접 넣었을 때의 읽기 동작은 기존 Device 계약을 따릅니다.
품질 정책과 도메인 범위(온도 상한 등)는 앱에서 관리합니다.
Client 매핑, MS/TP, COV, priority array, commandable 객체, 새 객체 종류/속성 또는
BTL 인증은 이 기능에 포함하지 않습니다.

VKBN001은 클래스 형태, VKBN002는 멤버·자료형·개수, VKBN003은 instance/품질,
VKBN004는 객체 중복, VKBN005는 생성 이름 충돌 진단입니다.

## MCU 비용 및 설치

매핑 NuGet 설치 시 같은 버전의 `VagabondK.nF.BACnet` 공통 코어를 자동 복원합니다.
IP 전송이 필요하면 `VagabondK.nF.BACnet.IP`를 별도로 설치합니다.

저장소 생성 시 정의 배열과 `BacnetObject`를 한 번 생성하고, `Read`/`QueueWrite`를 직접 호출하는
정상 경로에서는 새 객체나 배열을 만들지 않습니다. float 변환에는 기존 `RealBits.cs` 공유 소스의
double 비트 변환 API를 활용한 무할당 코드를 생성하며, AV가 하나라도 있을 때 컴파일 단위에 한 번만 포함합니다.
BV 전용 모델에는 이 보조 코드를 생성하지 않습니다. `BacnetDevice`의 정의 복사·중복 검사,
프로토콜/UDP 요청·응답 할당은 그대로이므로 전체 통신은 무할당이 아닙니다.

소스 프로젝트 참조:

```xml
<ProjectReference Include="..\..\VagabondK.nF.BACnet.Mapping\VagabondK.nF.BACnet.Mapping.csproj"
  OutputItemType="Analyzer" ReferenceOutputAssembly="false" SkipGetTargetFrameworkProperties="true" />
```

`VagabondK.nF.BACnet.Mapping` NuGet 패키지는 빌드 전용으로 설치합니다.
기존 nanoFramework packages.config 프로젝트는 패키지의
`build\VagabondK.nF.BACnet.Mapping.targets`를 가져와야 합니다.
Roslyn 4.12 이상과 C#7.3 생성 코드를 사용합니다.

```powershell
dotnet run -c Release --project .\Tests\BACnet\VagabondK.nF.BACnet.Mapping.Tests
.\Build\Pack-BacnetMapping.ps1 -OutputPath .\bin\nuget\bacnet-mapping-new
.\Build\Test-BacnetMapping.ps1 -FeedPath .\bin\nuget\bacnet-mapping-new
```

같은 버전 패키지를 덮어쓰지 않고 새 로컬 출력 폴더를 사용합니다.
패키지 검증은 소스 프로젝트 참조 없이 AV/BV 모델과 BV 전용 모델을 사용하는 MCU 프로젝트를 각각 빌드합니다.
실물 샘플은 `Samples\BACnet\VagabondK.nF.BACnet.Mapping`, 자원 측정·안전 복원 절차는
`Tests\BACnet\VagabondK.nF.BACnet.Mapping.Hardware\README.md`를 참조하십시오.
