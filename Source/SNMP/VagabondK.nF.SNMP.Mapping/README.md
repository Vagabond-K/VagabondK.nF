# SNMP 정적 scalar 매핑

리플렉션 없이 모델 멤버를 직접 읽고 쓰는 `Model.SnmpStore : ISnmpValueStore`를
빌드할 때 생성합니다. 소스 생성기는 PC 컴파일러용이며 MCU에 DLL/PE로 배포하지 않습니다.
기존 `VagabondK.nF.SNMP`와 선택형 `.UDP` 이외의 런타임 DLL은 필요하지 않습니다.
생성된 매핑 Attribute 타입에는 `NANOFRAMEWORK` 또는 `NANOFRAMEWORK_1_0` 빌드에서
`ExcludeType`을 붙여 MCU PE에서 제외합니다. 해당 상수가 없는 일반 .NET 컴파일에는 선언 타입이 남지만
런타임에서 조회하지 않습니다.
이 기능은 **Agent 측** 매핑입니다. SNMP Manager, 테이블, 동적 OID, 자동 폴링,
Trap 변경 감지 또는 프로토콜 간 자동 동기화는 생성하지 않습니다.

```csharp
using VagabondK.nF.SNMP;
using VagabondK.nF.SNMP.Mapping;

[SnmpMap]
public partial class DeviceData
{
    [SnmpInteger("1.3.6.1.4.1.32473.10.1.0", Writable = true)]
    public int Setpoint { get; private set; } = -250;

    [SnmpOctetString("1.3.6.1.4.1.32473.10.2.0",
        Writable = true, MaximumLength = 32)]
    public byte[] Label = new byte[] { 112, 117, 109, 112 };
}
```

형식이 드러나는 `SnmpInteger`, `SnmpCounter32`, `SnmpGauge32`, `SnmpTimeTicks`,
`SnmpCounter64`, `SnmpOctetString`, `SnmpIpAddress`, `SnmpObjectIdentifier` 특성을 사용할 수 있습니다.
인수는 전체 OID이며 `Writable`과 `MaximumLength` 옵션은 기존과 같습니다.
기존 `SnmpScalar(oid, SnmpType.형식)`도 유지하고, 같은 선언에서 생성하는 저장소 실행 코드는 동일합니다.
한 멤버에 여러 매핑 특성을 지정하면 빌드 오류입니다.
이름만 구체화한 것이므로 아래 CLR 형식 제약은 그대로 적용됩니다.
특히 `SnmpOctetString`은 문자열 자동 변환이 아닌 `byte[]` 매핑입니다.

`new DeviceData.SnmpStore(model, sharedLock)`를 기존 `SnmpAgent`에 전달합니다.
앱도 **같은 잠금** 안에서 모델과 배열을 갱신해야 합니다. 속성을 직접 호출하거나 배열에 접근할 때는
잠금이 자동으로 적용되지 않습니다. getter와 setter는 실행 시간이 짧고 부작용이 적도록 구현하십시오.
private 멤버/private setter도 지원합니다.
`Read`는 같은 잠금 안에서 배열을 복사하여 `SnmpValue`가 모델과 저장 공간을 공유하지 않게 합니다.
`GetBytes()`/`GetObjectIdentifier()`의 반환 배열과 SET 후 모델이 소유하는 배열도 각각 복사본입니다.
이는 개별 값의 스냅샷 계약이며 여러 OID를 읽는 요청 전체의 동일 시점 스냅샷은 아닙니다.

| SMI 형식 | 모델 형식 |
|---|---|
| Integer32 | `int` |
| Counter32, Gauge32, TimeTicks | `uint` |
| Counter64 | `ulong` |
| OctetString, IpAddress | `byte[]` |
| ObjectIdentifier | `uint[]` |

최상위 비제네릭·비상속 `partial class`에 1~512개 인스턴스 필드/속성을 선언합니다.
읽기 전용이 기본입니다. `Writable = true`를 지정하려면 필드는 변경 가능해야 하고 속성에는 setter가 있어야 합니다.
`bool`, `string`, 부동소수점 형식, enum은 자동 변환하지 않습니다. 문자열은 앱에서 UTF-8
`byte[]`로 변환합니다. IpAddress는 4바이트, 값으로 쓰는 ObjectIdentifier는 유효한 OID여야 하며,
모델 값이 null이거나 유효하지 않으면 읽을 때 기존 `SnmpValue` API에서 예외가 발생합니다.

주소는 `.0`을 포함한 전체 숫자 scalar OID입니다. 빌드 시 각 구성 요소(arc)의 정숫값을 기준으로 정렬하고,
중복·서로 접두 관계인 말단 OID·예약된 MIB-II system 그룹과의 충돌을 거부합니다. 구성 요소 수가 2~128개인지,
각 값이 `uint` 범위인지, 첫 두 값이 ASN.1 규칙을 따르는지 검사합니다. `SnmpStore`는 생성 전용 이름입니다.
예시의 enterprise 32473은 문서용이며 실제 제품에는 자신의 할당 번호를 사용하십시오.

`MaximumLength`는 OctetString의 SET 길이 제한(기본 255, 0~1024)이며 다른 형식에는
지정하지 않습니다. 읽기 길이 제한이나 앱의 온도/범위 정책을 대신하지 않습니다.
Agent는 SET 바인딩을 모두 검증한 뒤 순서대로 `QueueWrite`를 호출합니다.
검증에서 거부하면 적용하지 않습니다. 반면 setter에서 예외가 발생하면 이전 setter가 적용한 값까지 되돌리지는 않으며,
요청 전체의 원자성이나 같은 시점의 스냅샷을 보장하지 않습니다. 예기치 않은 앱 예외는 그대로 전달합니다.
허용 온도 범위 같은 업무별 검증 정책은 이 첫 버전에서 자동 생성하지 않습니다.

## MCU 비용

생성 저장소는 런타임 리플렉션, 델리게이트 바인딩, 멤버명 검색, OID 문자열 파싱을 수행하지 않습니다.
숫자 OID 배열과 `SnmpObject`는 저장소 생성 시 할당하며 멤버 접근은 `switch`입니다.
기존 Agent의 system OID 병합/정렬은 그대로 남아 있습니다.
OctetString SET 길이는 `SnmpValue.ByteLength`로 복사 없이 검사합니다.
**무할당 구현은 아닙니다.** `Read` 호출마다 수행하는 `SnmpValue` 생성, SET 적용 시 모델이 소유할 배열의 복사,
BER 메시지·UDP 응답 할당은 기존 API 비용으로 남습니다.
매핑 수에 따라 생성 코드/Flash와 정의 배열/RAM도 증가합니다.
실물 8/32/128개 비용과 길이 검사 복사 제거 전후 수치는
`Tests\SNMP\VagabondK.nF.SNMP.Mapping.Hardware\README.md`에 기록했습니다.
이 개선은 임시 배열을 제거하는 것이며 영구 RAM 감소나 네트워크 RTT 향상을 뜻하지 않습니다.

## 빌드 및 패키지

매핑 NuGet 설치 시 같은 버전의 `VagabondK.nF.SNMP` 공통 코어를 자동 복원합니다.
UDP 전송이 필요하면 `VagabondK.nF.SNMP.UDP`를 별도로 설치합니다.

소스 프로젝트 참조:

```xml
<ProjectReference Include="..\..\VagabondK.nF.SNMP.Mapping\VagabondK.nF.SNMP.Mapping.csproj"
  OutputItemType="Analyzer" ReferenceOutputAssembly="false" SkipGetTargetFrameworkProperties="true" />
```

NuGet은 `VagabondK.nF.SNMP.Mapping`를 빌드 전용으로 설치합니다.
기존 nanoFramework `packages.config` 프로젝트에서는 패키지의
`build\VagabondK.nF.SNMP.Mapping.targets`를 가져와야 합니다.
Analyzer 참조와 런타임 참조를 혼동하지 마십시오. Roslyn 4.12 이상과 C# 7.3 생성 코드를 사용합니다.
OctetString 길이 검사를 생성하려면 `ByteLength`를 제공하는 최신 로컬 SNMP 런타임과
Generator를 함께 다시 패키징해야 합니다. 같은 preview 버전의 예전 패키지와 혼용하지 마십시오.

```powershell
.\Build\Pack-SnmpMapping.ps1 -OutputPath .\bin\nuget\snmp-mapping-candidate
.\Build\Test-SnmpMapping.ps1 -FeedPath .\bin\nuget\snmp-mapping-candidate
dotnet run -c Release --project .\Tests\SNMP\VagabondK.nF.SNMP.Mapping.Tests
```

VKSN001은 클래스 형태, VKSN002는 멤버/자료형/개수, VKSN003은 OID/옵션,
VKSN004는 OID 충돌, VKSN005는 생성 이름 충돌 진단입니다.
실제 MCU UDP 샘플·안전한 배포/복원 절차는 `Samples\SNMP\VagabondK.nF.SNMP.Mapping\README.md`에 있습니다.
