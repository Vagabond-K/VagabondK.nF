# IEC 61850 정적 객체 매핑

PC Roslyn 생성기로 사용자 모델을 기존 IEC 61850 Client/Server에 연결합니다.
MCU에서 리플렉션·제네릭·LINQ를 사용하지 않으며, 새 런타임 DLL이나 속성 설정자(setter)에 의한 자동 제어 전송은 없습니다.
일반 스칼라·동기화·오류 처리 계약은 [MMS 매핑](../../MMS/VagabondK.nF.MMS.Mapping/README.md)과 같습니다.
로컬 평가용 `0.1.0-preview.1`이며 실제 IED 적합성 인증을 의미하지 않습니다.

매핑 NuGet 설치 시 같은 버전의 `VagabondK.nF.IEC61850` 공통 코어와 전이 의존성인
`VagabondK.nF.MMS`를 자동 복원합니다. Client/Server 역할과 TCP는 별도로 선택합니다.

Server에는 `MMS + MMS.Server + IEC61850 + IEC61850.Server`,
Client에는 `MMS + MMS.Client + IEC61850 + IEC61850.Client`를 참조합니다.
패키지 `VagabondK.nF.IEC61850.Mapping`의 `build\*.targets`를 Import하거나
생성기 csproj를 `OutputItemType="Analyzer" ReferenceOutputAssembly="false"
SkipGetTargetFrameworkProperties="true"`로 참조합니다. TCP DLL은 선택 사항입니다.

## 상태·품질·시각

```csharp
using VagabondK.nF.IEC61850;
using VagabondK.nF.IEC61850.Mapping;

[Iec61850ServerMap(Domain = "IEDLD0")]
public partial class Measurements
{
    [Iec61850Point("GGIO1$ST$Status$stVal")]
    public bool Status { get; set; }
    [Iec61850Point("GGIO1$ST$Status$q")]
    public IecQuality Quality { get; set; }
    [Iec61850Point("GGIO1$ST$Status$t", TimeQuality = nameof(TimeQuality))]
    public long Time { get; set; }
    public byte TimeQuality { get; set; } = 0x3f;
}
```

`Iec61850ClientMap`과 `Iec61850ServerMap`의 `Domain`은 item만 지정한 Point의 공통 domain입니다.
기존 `[Iec61850Point("domain", "item")]`도 지원하며 명시한 domain이 우선합니다.
공통 Domain과 item은 빌드 시 결합되므로 실행 중 문자열 처리나 설정 조회가 추가되지 않습니다.
같은 이름·옵션을 지정한 기존 선언과 바인딩 실행 코드는 동일합니다.
이 공통 설정은 Point에만 적용됩니다. `Iec61850Control`과 `Iec61850RemoteControl`은
오동작을 피하기 위해 기존처럼 domain/item을 모두 명시합니다.

`Iec61850ServerBinding(model, sync)`은 `Publish/GetVariable/CreateSession`을 생성합니다.
기본 bool/int/float 외에 `IecDoublePoint`(0~3), `IecQuality`(13비트),
Unix 기준 시각부터의 UTC 밀리초를 나타내는 long(0~uint.MaxValue초 범위)을 지원합니다.
TimeQuality 미지정 시 Server는 0x3f를 사용하며, 지정 시 같은 모델의 byte 멤버를 읽습니다.
수신 클라이언트에서는 타임스탬프와 함께 byte 형식의 TimeQuality 멤버에 setter로 반영합니다.
품질/시각/Dbpos는 원격 Writable 대상이 아니며 잘못된 enum/범위를 거절합니다.

`Iec61850ClientMap`은 `Iec61850ClientBinding(model, sync, client)`를 생성합니다.
`ReadAll`, `Apply`, 명시적 `Write` 외에 다음 수신 경로를 지원합니다.

- `ApplyDataSet(dataSet, results)`: 참조 이름으로 매핑합니다. 결과 수·대상 접근 오류·타입을
  모두 검사한 후 대입합니다. 미등록 항목은 무시합니다.
- `ApplyReport(dataSet, report)`: 기존 `MmsReportClient`가 검사한 스냅샷을 전달합니다.
  DataSet 이름/수량을 확인하고 보고에 포함된 값만 반영하며 미포함 값은 유지합니다.
  RCB confRev/옵션/sequence의 0~255 범위/포함 비트맵 검사는 기존 보고 수신기가 담당합니다.
  sequence의 연속성 검사·중복 제거·누락 보고 복원은 제공하지 않습니다.

보고 콜백은 원래 MMS 작업의 기한을 공유합니다. 같은 Client 재진입이나 장시간 검증 대신
독립 보고 스냅샷을 앱의 용량 제한 큐에 보관하고 작업 종료 후 `ApplyReport`를 호출할 수 있습니다.
큐 상한·넘침 정책·모델 접근 직렬화는 앱 책임이며 생성기가 보고 큐나 작업 스레드를 만들지는 않습니다.

DataSet/URCB 정의와 구독 정책을 자동 생성하지 않습니다.
Server는 `GetVariable()`로 실제 변수를 모아 `MmsServerDataSet` /
`MmsServerReportControl`을 구성하고 `CreateSession(channel, dataSets, reports)`에 전달합니다.
SCL→정적 모델 생성은 별도 도구가 담당하며 그 생성 모델과 이 객체 바인딩을 자동 연결하지 않습니다.
기존 세션의 최대 128 확장 이름·RCB 최대 4개 등 제한은 그대로 적용되며 초과 시 세션 생성이 실패합니다.

## 명시적 제어

Server는 `int Execute(MmsControlRequest)` 메서드에
`[Iec61850Control("IEDLD0", "GGIO1$CO$Switch", MmsControlModel.DirectEnhanced,
Validate = nameof(Check))]`를 지정합니다.
`int Check(MmsControlRequest)`는 부작용 없는 필수 검증기이며 -1 수락/0~11 거절입니다.
검증만 통과한 Test 요청에는 실행 메서드를 호출하지 않습니다.

생성된 바인딩은 `ControlCount/GetControl(index)`를 제공합니다.
제어는 **기본 비활성**이며 앱의 명시적 `Enabled = true` 뒤에만 실행합니다.
DirectNormal/SboNormal/DirectEnhanced/SboEnhanced와
`ValueKind = MmsControlValueKind.DoublePoint` 또는 `AnalogFloat32`를 지원합니다.
SPC/DPC 명령은 request.Value(bool), APC는 request.AnalogValue를 사용하십시오. DPC 상태만 Dbpos입니다.
실행 메서드의 -1은 **수락**이지 실제 설비 동작 완료가 아닙니다.
enhanced 요청 객체를 앱에 보관하고, 실제 결과를 확인한 후 세션과 같은 실행 흐름에서
`GetControl(index).Complete(request, success, additionalCause)`와
`session.ProcessControlCompletion()`을 호출해야 합니다. 생성기는 실제 완료를 자동 성공 처리하지 않습니다.
Test의 준비된 완료 통지/기한 초과 통지도 기존 `ProcessControlCompletion()`으로 처리합니다.
블로킹 `ProcessRequest()`로 완료 처리가 막히지 않도록 앱 이벤트 루프에서 수신 가능 여부를 확인하십시오.

Client 모델에는 `[Iec61850RemoteControl("IEDLD0", "GGIO1$CO$Switch")]`를 선언합니다.
`CreateControl(index, origin, originCategory = 3)`는 ctlModel을 조회한 기존 `MmsControlClient`를 반환하며
Select/Operate/Cancel은 자동 호출하지 않습니다. 이후 명시적으로 기존 제어 API를 사용하십시오.
응답 유실 후 자동 재전송하지 않으며, enhanced 수락/완료의 구별도 기존 API 그대로입니다.
`$CO$/$RP$`는 일반 Point가 아닌 이 제어/보고 API를 사용해야 합니다.

## 제한과 예제

모델당 **포인트 1~16개와 제어 최대 8개**를 지원합니다. 제어만 있는 모델은 초기 지원 범위에 포함하지 않습니다.
제어 이름은 `LN$CO$Object`, 최대 53자이며 상태/ctlModel/SBO 등 모든 확장 이름의 충돌을 빌드에서 거절합니다.
포인트 인덱스는 domain/item의 서수(ordinal) 정렬 순서이며, 제어 인덱스는 선언 순서입니다.
Client의 두 수신 필드가 같은 TimeQuality 저장 멤버를 공유하면 빌드 오류입니다.
진단은 `VKIE001~007`이며 일반 모델 제약은 MMS와 같습니다.

예제 `Samples\IEC61850\VagabondK.nF.IEC61850.Mapping`에는 상태/품질/시각/Dbpos,
DataSet/URCB 구성, 비활성 기본 제어와 명시적 완료 반영을 포함합니다.
`AllFormats.cs`의 `TypedLocal`/`TypedRemote`는 여섯 값 형식, bool/int/float 명시적 쓰기와
SPC/DPC/APC를 사용하는 네 제어 모델을 보여 줍니다. 이 모델의 UTC는 기본 시간 품질 0x3f를 쓰고,
`Models.cs`는 byte 멤버를 지정하는 `TimeQuality` 연결을 보여 줍니다.
`Program.Main`은 연결 없이 바인딩을 생성·Publish합니다. `Create`·`ReportOutcome`·`Receive`는
호출자가 세션·통신·완료 정책을 구성할 때 사용하는 별도 예제 메서드입니다.
예제의 상태 변경은 가상 동작이며 인증/실제 출력/백그라운드 처리를 제공하지 않습니다.

```powershell
.\Build\Pack-MmsMapping.ps1 -LibrarySet IEC61850 -OutputPath .\bin\nuget\iec-mapping
.\Build\Test-MmsMapping.ps1 -FeedPath .\bin\nuget\iec-mapping
```

위 명령의 검증 범위는 PC 가상 IED/루프백과 nanoFramework 생성 코드·패키지 이미지 빌드입니다.
아래 보드 시험은 별도 명령이며 실제 외부 IED 적합성 시험·장시간 내구 시험과 구분합니다.

## 성능 개선 기록

IEC 해석은 `MmsValue.GetByte(index)`로 스냅샷 내부 값을 **읽기만** 하며 배열을 노출하지 않습니다.
기존 `GetBytes()`는 그대로 독립 복사본을 반환합니다. 품질·Dbpos·시각·시간 품질의 반복 해석에
임시 배열이 필요하지 않습니다. 수신 스냅샷을 생성하는 네트워크 디코딩 자체는 여전히 메모리를 할당합니다.
`MmsDataSet.Reference`는 생성 시 domain/item 문자열을 한 번 보관하며,
매 보고 반영 때 같은 문자열을 새로 조합하지 않습니다.
생성 Publish는 실제 전송 값이 다른 항목만 갱신합니다. 기존 명시적 Set/원격 쓰기는 바꾸지 않았습니다.

최적화 전후 비교 당시 PC Release, 1,000회 준비 후 10,000회 반복의 스레드 할당량입니다.
예제의 Dbpos·품질·Boolean·시각 4개 Point를 사용하며,
수신 스냅샷·DataSet·바인딩 구성은 반복 구간 밖에서 수행했습니다.

| 반복 경로 | 개선 전 Byte/회 | 개선 후 Byte/회 |
|---|---:|---:|
| 동일값 Publish + URCB 변경 검사 | 136 | 0 |
| Boolean 변경 Publish + 보고 생성 | 약 2,548 | 약 2,444 |
| 기존 스냅샷의 ApplyDataSet | 224 | 0 |
| 기존 스냅샷의 ApplyReport | 280 | 0 |
| IEC 4종 의미 값 해석 | 128 | 0 |

시간은 같은 PC 측정에서 각각 약 0.46→0.28µs, 2.84→1.45µs, 0.65→0.56µs,
0.62→0.52µs, 0.12→0.08µs였습니다. JIT·호스트 부하에 민감한 참고 수치이며
MCU 속도 향상률이나 실시간 응답 상한을 뜻하지 않습니다.
회귀 시험은 유휴 Publish/보고 검사와 기존 스냅샷 반영·IEC 해석의 **추가 할당 0**을 직접 검사합니다.
실제 변경 보고·통신 인코딩·디코딩의 전체 무할당을 보장하지 않습니다.

Release PE 비용은 MMS +68, MMS.Server +244, IEC core +4, IEC.Client +112,
IEC.Server +480 Byte입니다. MMS.Client/TCP는 그대로이며 런타임 7개 합계는 +908 Byte,
예제 자체는 7,176→7,168 Byte로 -8 Byte입니다. 이는 임시 할당 감소와 교환한 코드 크기 비용입니다.
Publish용 이전값 배열/모델 캐시는 추가하지 않았습니다.

Debug/Release 각각 16,988개 검사를 통과했으며 품질 8,192개 전체, Int32 경계,
Float32 부호 있는 0·NaN/Infinity, UTC 경계·시간 품질,
불변 스냅샷, 명시적 동일값 쓰기, 변경 없음/GI/실제 변경 보고를 포함합니다.
기존 원본 libiec61850 양방향 가상 IED 회귀 기록은 `bin\mapping-perf-runtime-regression`,
측정 전후 기록은 `bin\mapping-perf-before.log` / `bin\mapping-perf-after.log`,
MCU 생성 코드 빌드 기록은 `bin\mapping-perf-mcu.log`입니다.
최적화된 로컬 패키지는 `bin\nuget\mapping-perf-final`에 별도로 생성합니다.
이 성능 비교 시점에는 실제 보드 배포·실제 IED 시험을 수행하지 않았습니다.

## nF 명명 이후 보드 매핑 시험

전용 Geekble Nano ESP32-S3(COM3, 저장된 Wi-Fi 설정)를 사용합니다.
명시적 장치 변경 허용이 필요하며 펌웨어·네트워크 설정·전체 Flash 삭제는 수행하지 않습니다.
각 실행은 finally에서 원래 Modbus 앱의 고정 SHA-256 이미지로 복원하고,
COM6 RTU 9600 8E1 holding 0..2의 123/456/65535를 읽어 복원을 확인합니다.

```powershell
.\Build\Test-Iec61850MappingHardware.ps1 -AllowDeviceChanges -OutputPath .\bin\mapping-nf-server-final
.\Build\Test-Iec61850MappingHardware.ps1 -AllowDeviceChanges -Client -OutputPath .\bin\mapping-nf-client-final
```

Server/Client는 각각 독립 이미지이며 반대 역할 DLL·PC analyzer를 포함하지 않습니다.
역할별 동일 이미지의 실제 재배포로 **2회 앱 기동 × 16회 정상 접속**을 수행합니다.
6개 매핑 Point의 읽기·품질/Dbpos/UTC/시간 품질·DataSet 반영·URCB GI/변경 보고,
접속당 int/float 각각 32회 명시적 쓰기·잘못된 scalar 쓰기 거절,
Test 제어의 무실행과 실제 DirectEnhanced 수락/명시적 완료·상태 반영을 확인합니다.
시험 Server의 완료 성공은 가상 모델 실행 결과이며 실제 설비 완료를 자동 성공 처리하는 정책이 아닙니다.
MCU Server에는 기동마다 잘린 TPKT 연결 1회도 주입하며,
이후 16회 정상 접속을 요구하므로 전체 연결 17회/예상 전송 장애 1회를 검사합니다.

반복 안정성 기준은 매 접속 종료 후 강제 GC로 측정한 가용 Heap입니다.
6번째 접속을 기준값으로 잡고 이후 최저값 하락이 512 Byte를 넘으면 실패합니다.
각 기동의 시간 상한은 10분입니다. 상태/복원 결과는 `status.json`,
기동별 Heap·요청/보고·장애 수는 `measurements.jsonl`, 배포 이미지 크기/해시는 `images.json`에 남깁니다.
진단의 `Requests`는 Server에서는 처리한 MMS 요청 수, Client에서는 매핑 결과 검사 횟수입니다.
`Reports`는 Server에서는 전송한 보고/제어 완료 통지 수, Client에서는 수신한 URCB 보고 수입니다.
보드 시간 측정은 100회 준비 후 동일값 Publish·Boolean 변경 Publish 또는
기존 스냅샷의 ApplyDataSet을 각각 1,000회 실행합니다.
측정 전후 GC 후 Heap 하락은 최대 128 Byte만 허용합니다.
이는 **GC 후 가용 힙과 단기 반복 안정성** 검사이며, 전체 통신 경로의 무할당이나 장시간 누수 부재를 증명하지 않습니다.

별도 PC 회귀는 매핑 모델의 scalar setter와 제어 실행 뒤 Write 응답을 의도적으로 유실시킵니다.
클라이언트의 Faulted 상태 전환·동일 세션 재시도 거절을 검사하고, 재접속하여 새 바인딩을 생성한 뒤에도 실행 횟수가 1회인지 확인합니다.
이 절의 응답 유실 검사는 PC 주입 시험입니다. 뒤의 독립 libiec61850 상호운용 시험에서
보드 응답 유실을 지연·타임아웃으로 주입한 결과와 구분하십시오.
PC Debug/Release 매핑 회귀는 각각 17,004개 검사입니다.
현재 PC 성능 검사는 TieredCompilation을 끄고 준비 구간도 10,000회로 늘려
일회성 런타임 준비 비용과 반복 할당을 구분합니다. 위 초기 시간 수치와 직접 비교하지 마십시오.
마지막 MMS/IEC 7개 런타임 + 2개 매핑의 로컬 전용 패키지 피드는
`bin\nuget\nf-mapping-board-verified-final`이며 역할별 오프라인 소비를 검증합니다.

### 실측 결과

PC 가상 상대와 실제 보드 사이에서 최종 Server/Client 각각 2회 기동·32회 정상 접속을 통과했습니다.
Server의 추가 부분 프레임 장애는 기동당 의도한 1회뿐이며 이후 접속이 정상 복구되었습니다.
모든 정상 접속에서 scalar 쓰기/보고/거절/명시적 제어 검사를 완료했습니다.

| MCU 역할·기동 | 전체 접속 | 예상 전송 장애 | 가용 Heap 기준/최저 Byte | 기준 이후 하락 |
|---|---:|---:|---:|---:|
| Server 1 | 17 | 1 | 745,056 / 745,056 | 0 |
| Server 2 | 17 | 1 | 745,056 / 745,056 | 0 |
| Client 1 | 16 | 0 | 757,716 / 757,716 | 0 |
| Client 2 | 16 | 0 | 758,148 / 758,148 | 0 |

같은 6개 포인트 모델에서 측정한 보드 소요 시간입니다. 통신·스냅샷 구성은 반복 구간 밖입니다.

| 경로 | 1,000회 소요 시간 | 측정 전후 GC 후 가용 Heap 변화 |
|---|---:|---:|
| 동일값 Publish | 590 / 600 ms | 0 Byte |
| Boolean 변경 Publish | 640 / 640 ms | 0 Byte |
| 기존 스냅샷의 ApplyDataSet | 2,920 / 2,920 ms | +24 Byte |

Server의 두 Publish 구간은 한 쌍으로 Heap을 측정했습니다.
개선 전 동일 조건의 보드 수치가 없으므로 MCU 성능 향상률은 계산하지 않습니다.
ApplyDataSet은 현재 6개 Point에서 약 2.92 ms/회입니다. 포인트 인덱스 캐시 같은 추가 상태/API는
도입하지 않았으며, 더 높은 보고 빈도·허용 CPU 점유율 요구가 생기면 이 경로를 우선 재측정합니다.
실제 변경 Publish도 일시적인 값/버퍼를 할당하며, GC 후 Heap 유지가 총 할당 0을 뜻하지 않습니다.

Server 배포 BIN은 137,316 Byte(시험 앱 PE 8,340 Byte),
Client BIN은 129,844 Byte(시험 앱 PE 9,316 Byte)입니다.
기존 Modbus 앱 복원과 COM6 읽기를 두 역할 종료 시 각각 확인했습니다.
증거 경로는 `bin\mapping-nf-server-final` / `bin\mapping-nf-client-final`입니다.
실제 외부 IED와의 상호운용, 인증·실시간 상한, 수 시간에 걸친 장기 내구성은 이번 결과에 포함하지 않습니다.

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
IEC Server는 6세션/예상 고장 3회/쓰기 6회/보고 6회/요청 252회,
IEC Client는 3세션/쓰기 6회/검사 66회/적용 9회/예상 거절 3회를 완료했습니다.
두 역할의 기준 이후 GC 후 보존 힙 증가량은 0 B입니다. Client 진단의 `Reports`는
수신 보고 누계가 아니므로 0을 보고 미수신으로 해석하지 마십시오.
이는 유한 반복 구간의 관측이며 장시간 누수 부재를 증명하지 않습니다.
직전 `133151` 실행은 외부 MMS Client 초기 연결 실패로 중단되어 성공으로 계산하지 않습니다.
네 역할의 전체 결과와 복원 경계는 [매핑 대조표](../../../Samples/MappingCoverage.md)를 참조하십시오.
