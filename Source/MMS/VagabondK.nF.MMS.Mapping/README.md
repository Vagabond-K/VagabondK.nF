# MMS 정적 객체 매핑

PC 빌드 시점에 실행되는 Roslyn 소스 생성기입니다. MCU에서는 생성된 코드를 통해 필드/속성에 직접 접근하며
리플렉션·LINQ·제네릭·동적 모델 탐색을 하지 않습니다. 런타임 DLL/NuGet 23개와 별개인 개발 도구이며,
생성기·Roslyn DLL을 MCU에 배포하지 않습니다. 로컬 평가용 `0.1.0-preview.1`입니다.

## 설치와 모델

매핑 NuGet 설치 시 같은 버전의 `VagabondK.nF.MMS` 공통 코어를 자동 복원합니다.
Client/Server 역할과 TCP는 용도에 따라 별도로 설치합니다.

SDK 프로젝트는 `VagabondK.nF.MMS.Mapping` 패키지를 analyzer로 사용합니다.
기존 nanoFramework `packages.config` 프로젝트는 패키지의
`build\VagabondK.nF.MMS.Mapping.targets`를 Import하십시오.
소스 참조는 다음과 같습니다.

```xml
<ProjectReference Include="VagabondK.nF.MMS.Mapping.csproj"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false"
                  SkipGetTargetFrameworkProperties="true" />
```

Server 앱에는 `MMS + MMS.Server`, Client 앱에는 `MMS + MMS.Client`만 필요합니다.
TCP는 선택 사항입니다. 하나의 앱에서 두 역할을 사용해도 **모델 클래스는 역할별로 분리**합니다.

```csharp
using VagabondK.nF.MMS;
using VagabondK.nF.MMS.Mapping;

[MmsServerMap(Domain = "Plant")]
public partial class Measurements
{
    [MmsPoint("Temperature")]
    public float Temperature { get; set; }

    [MmsPoint("Target", Writable = true, Validate = nameof(Check))]
    public int Target { get; private set; }

    private int Check(MmsValue value) { return value.Integer < 0 ? 11 : -1; }
}

// 모델 접근, Publish, 세션 처리 전체를 같은 sync 잠금으로 직렬화합니다.
var sync = new object();
var model = new Measurements();
var binding = new Measurements.MmsServerBinding(model, sync);
lock (sync)
{
    model.Temperature = 20.5f;
    binding.Publish();
}
// var session = binding.CreateSession(channel);
```

`MmsClientMap`과 `MmsServerMap`에 `Domain = "Plant"`를 지정하면
각 멤버는 `[MmsPoint("item")]`으로 선언할 수 있습니다.
기존 두 인수 `[MmsPoint("OtherDomain", "item")]`은 공통 설정보다 우선하므로
서로 다른 domain의 멤버를 같은 모델에 둘 수 있습니다.
공통 Domain 없이 item만 지정하거나 null/빈 Domain을 지정하면 빌드 오류입니다.
domain/item의 유효성·정렬·중복 검사는 기존과 같으며, 이름은 빌드 시 확정됩니다.
실행 중 경로를 합치거나 설정을 검색하지 않고 기존 두 인수 선언과 동일한 바인딩 코드를 생성합니다.

`MmsClientMap`은 `MmsClientBinding(model, sync, client)`를 생성합니다.
`ReadAll()`은 요청 결과 전체를 검사한 뒤 모델을 갱신합니다.
`Apply(index, value)`는 개별 수신 값을 반영하고,
`ApplyInformationReport(report, list = null)`는 domain/item 이름이 일치하는 결과만 반영합니다.
명명 변수 목록 보고에는 **조회한 디렉터리 스냅샷**을 전달해야 하며 바인딩은 목록 이름과 결과 수를 검사합니다.
VMD/association-specific 보고·등록하지 않은 항목은 매핑 대상이 아닙니다.
보고 콜백은 원래 MMS 작업의 기한 안에서 동기 실행됩니다. 콜백에서 같은 Client를 호출하거나
장시간 검증을 수행하지 마십시오. 수신 버퍼와 독립된 보고 스냅샷을 앱의 용량 제한 큐에 보관하고,
통신 작업이 끝난 뒤 `ApplyInformationReport`로 반영할 수 있습니다. 큐의 상한·넘침 정책과
모델 잠금은 앱 책임이며 생성기가 백그라운드 처리나 보고 큐를 제공하지는 않습니다.

클라이언트 모델의 일반 속성 설정자(setter)는 네트워크 통신을 하지 않습니다. `Writable = true`를 지정한 스칼라만
`Write(index)`로 명시 전송하며, 응답 유실/연결 단절 후 자동 재시도하지 않습니다.
바인딩 인덱스는 선언 순서가 아닌 **domain → item의 서수(ordinal) 정렬** 순서입니다.
`GetReference(index)` / `GetVariable(index)`로 이름을 확인하십시오.

## 계약과 제한

- 모델은 최상위 비제네릭·비상속 `partial class`이며 모델당 **포인트 1~16개**를 지원합니다.
  타입은 `bool/int/float`, domain/item은 공백 없는 ASCII 1~64자입니다.
  구조체/배열 전체, 문자열, 자동 탐색/폴링/재연결은 이번 매핑 범위가 아닙니다.
- 서버 모델에는 속성 접근자(getter), 클라이언트 모델에는 속성 설정자(setter)가 필요합니다. Writable에는 둘 다 필요합니다.
  private 접근자도 허용합니다. 서버의 읽기 전용 필드에는 readonly도 허용합니다.
- Writable 기본값은 false입니다. Server는 `int Validate(MmsValue)`로 -1 수락/0~11 거절을 반환합니다.
  검증기는 부작용이 없어야 합니다. 검증 성공 뒤 모델 setter를 한 번 호출한 다음 MMS 메모리 값을 변경합니다.
  모델 setter는 짧고 예외 없이 반환하도록 작성하십시오.
- float는 매핑 경계에서 NaN/Infinity를 거절합니다. Client binary64 수신은 Float32로 반올림하며
  변환 결과가 무한대인 경우도 거절합니다. 서버가 전송받는 쓰기 값은 기존과 같이 Float32만 지원합니다.
- `Publish()`는 모든 getter의 값을 스냅샷으로 읽고 값 검사를 마친 후 기존 변수 버퍼에 반영합니다.
  실제 변수의 전송 값과 다른 항목만 `Update*`로 반영하여 변경 없는 호출이 URCB 재인코딩을 유발하지 않습니다.
  정상 반복 Publish는 임시 배열/객체를 할당하지 않습니다. 별도 모델 캐시를 두지 않으므로
  앱의 직접 변수 변경도 다음 Publish에서 모델 값으로 복원합니다.
  Float32는 +0/-0의 전송 비트 차이까지 반영합니다.
  기존 명시적 `Set*`와 원격 Write는 같은 값을 써도 종전의 버전 갱신/검증·반영기 호출 의미를 유지합니다.
- 수신 일괄 반영은 모든 **매핑 대상**의 접근 오류·형식·범위를 검사한 후 시작합니다.
  앱의 setter가 실행 중 예외를 던지는 경우의 원자성이나 롤백까지 보장하지는 않습니다.
  읽기/보고 스냅샷은 기존 MMS API와 같은 할당을 수반합니다.
- 모든 모델 접근, Publish, 변수/세션/명명 목록 처리를 **호출자가 동일한 잠금으로 직렬화**하십시오.
  생성된 콜백의 잠금만으로 세션 루프 전체가 보호되지는 않습니다.
  클라이언트의 네트워크 I/O는 생성된 바인딩의 모델 잠금 밖에서 실행되지만,
  기존 Client의 동시 요청·재진입 금지 및 전체 기한 정책을 그대로 따릅니다.
- 검증기·모델 반영기의 잘못된 반환값/예외는 세션 Faulted로 전파합니다.
  외부 부작용의 롤백, 자동 재시도, 정확히 한 번 실행(exactly-once)을 보장하지 않습니다.
  일반 Writable setter를 실제 설비 제어 대신 사용하지 마십시오. IEC 제어는 별도 API로 명시 실행합니다.
- `GetVariable()`은 실제 공유 변수를 반환합니다. 앱에서 `EnableWrite/DisableWrite`를 재설정하면
  생성된 모델 반영/검증 정책을 교체하므로 직접 변경 시 그 책임은 앱에 있습니다.
- 빌드 진단 `VKMM001~007`: 모델/역할, 멤버/수량/타입, 옵션, 중복, 예약 이름, 제어, MCU 멤버 상한입니다.
  상속·중첩·static/indexer·init/ref 속성은 허용하지 않으며 모델의 필드/메서드 상한은 각각 255입니다.

전체 예제: `Samples\MMS\VagabondK.nF.MMS.Mapping`.
`Models.cs`는 공통 Domain과 bool/int/float 읽기·Count 검증/명시적 쓰기를,
`WritableFormats.cs`는 세 형식 모두의 Client/Server 쓰기 선언을 보여 줍니다.
`Program.Main`은 연결 없이 Server 바인딩을 생성·Publish하며, 네트워크 읽기/쓰기는
Association을 맺은 Client를 받는 별도 `Receive` 예제입니다.

```powershell
dotnet run -c Release --project .\Tests\MMS\VagabondK.nF.MMS.Mapping.Tests\VagabondK.nF.MMS.Mapping.Tests.csproj
.\Build\Pack-MmsMapping.ps1
.\Build\Test-MmsMapping.ps1 -FeedPath .\bin\nuget\mms-mapping
```

위 검증은 가상 TCP 피어와 MCU 이미지 빌드입니다. 실제 IED/보드 실행 또는 장시간 내구 시험을 의미하지 않습니다.

최초 구현은 Debug/Release 각각 매핑 전용 463개 검사를 통과했습니다. 일반/IEC 읽기·쓰기·보고,
전체 선검사 후 반영, 오류 후 무재전송, 네 제어 모델과 기본 비활성·Test·명시적 완료를 포함합니다.
각 역할을 따로 복원한 패키지 소비 이미지에는 반대 역할·TCP·Roslyn DLL이 포함되지 않습니다.
`bin\nuget\mapping-final`에는 런타임 7개와 빌드 전용 생성기 2개의 로컬 패키지가 있습니다.
기존 런타임 회귀 및 원본 libiec61850 상호운용 기록은 `bin\mapping-runtime-regression`이며,
새 객체 바인딩의 통신 검증인 매핑 전용 가상 TCP 양방향 시험과는 구분합니다.

후속 성능 개선과 최종 패키지는
[IEC 매핑 성능 기록](../../IEC61850/VagabondK.nF.IEC61850.Mapping/README.md#성능-개선-기록)에 정리했습니다.

## 독립 libiec61850 상호운용 시험

생성 매핑(IEC 61850 매핑과 MMS 전용 매핑을 각각 따로)을 원본 libiec61850 v1.6.2
(`f0f797bf955da763353f7081c892a1ff6b55e5a1`)의 독립 Client/Server와 양방향으로 시험합니다.
라이브러리 원본은 수정하지 않지만 MMS 리포트의 일부는 아래 제약에 따라 시험 래퍼가 직접 인코딩합니다.
피어 래퍼는 `Tests\IEC61850\VagabondK.nF.IEC61850.ValidationTools\peer.c`의
`mapserver`/`mapiecclient`/`mapmmsclient` 모드입니다.

```powershell
pwsh -NoProfile -File Build\Test-MappingInterop.ps1 -Iterations 3
# 기대: MAPPING INTEROP PC PASS iterations=3 peer=libiec61850-v1.6.2-f0f797bf; <dir>
pwsh -NoProfile -File Build\Test-MappingInteropHardware.ps1 -AllowDeviceChanges -Iterations 3
# 기대: MAPPING INTEROP MCU <role> 4줄 + MAPPING INTEROP HARDWARE PASS ...; status.json Restored=true
```

- 시험 범위: 생성 Client/Server의 양방향 읽기·쓰기와 모델 관측, Client가 추가 `ReadAll` 없이 리포트로 모델을 갱신하는지, 부분·역순·비매핑 항목 처리, 잘못된 매핑 값의 선검사 거절과 기존 값 보존, IEC 값·품질·시각 갱신, 응답 유실·새 연결 뒤 쓰기 무재전송입니다. 앱 setter 예외의 롤백이나 네이티브 MMS Client의 domain named-list 리포트 해석까지 확인한 것은 아닙니다.
- 진단 로그: 결과 폴더에는 `native-server-<mms|iec>.log`(PC 시험은 `native-server.log`, 네이티브 서버의 READY 기준 ms 시각 추적: 쓰기 수신, 리포트 전송, 명령 적용, 연결 변화)가 실패해도 항상 저장됩니다. MCU 실패 문구 뒤에는 `| run= step= reports= ops=` 형식으로 현재 시험 단계와 최근 채널 읽기·쓰기 8개(요청 바이트, 허용 ms, 단계 기준 시작 ms, 소요 ms, 결과)가 붙습니다.
- 수신 콜백: 마지막 MMS Client 타임아웃은 fixture 콜백의 NaN 검증 예외와 nanoCLR 디버그 출력 지연이 3초 작업 기한을 소모한 결과였습니다. fixture는 콜백에서 독립 보고 스냅샷을 고정 8슬롯 큐에 보관하고, `ApplyInformationReport`/`ApplyReport`의 적용·거절 검사를 작업 종료 후 기한 밖에서 실행하도록 바꿨습니다. 엔진·펌웨어·타임아웃 값은 변경하지 않았습니다.
- 리포트 처리: `MmsServerReportControl`은 최소 간격 안의 리포트를 미룹니다. 서버 루프가 요청을 받을 때만 리포트를 처리하면 미룬 리포트가 전송되지 않으므로, 대기 중에도 `Poll`로 주기적으로 리포트를 처리해야 합니다.
- 제한
  - libiec61850 v1.6.2의 listOfVariables InformationReport 인코더는 domain 항목의 길이를 잘못 계산합니다. 그래서 피어가 PDU를 직접 인코딩하며, 이때 고정 리비전의 비공개 내부 구조에 의존합니다. 이 경로는 외부 라이브러리의 리포트 인코더 검증으로 계산하지 않습니다. IEC URCB 리포트는 외부 라이브러리의 보고 API로 교환합니다.
  - 네이티브 MMS Client는 domain named-list 리포트를 처리하지 않으므로, 이 방향에서는 목록·값 읽기와 수신 스트림 내성만 확인합니다.
  - 응답 유실은 지연·타임아웃으로만 주입합니다.
  - 네이티브 다중 항목 쓰기는 다루지 않습니다.
  - 대규모 안전성 증명이나 인증을 대신하지 않습니다.

2026-10-29 최종 실물 증거는 `bin\mapping-interop-hardware-20261029-133546`이며
`status.json`의 completed·Restored=true·Error=null을 확인했습니다.
MMS Server는 6세션/예상 고장 3회/쓰기 6회/보고 송신 3회/요청 171회,
MMS Client는 3세션/쓰기 6회/검사 72회/적용 6회/예상 거절 12회를 완료했습니다.
두 역할의 기준 이후 GC 후 보존 힙 증가량은 0 B입니다. Client 진단의 `Reports`는
수신 보고 누계가 아니므로 0을 보고 미수신으로 해석하지 마십시오.
이는 유한 반복 구간의 관측이며 장시간 누수 부재를 증명하지 않습니다.
직전 `133151` 실행은 외부 MMS Client 초기 연결 실패로 중단되어 성공으로 계산하지 않습니다.
네 역할의 전체 결과와 복원 경계는 [매핑 대조표](../../../Samples/MappingCoverage.md)를 참조하십시오.
