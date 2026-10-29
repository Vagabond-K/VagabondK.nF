# Sparkplug B 라이브러리

`VagabondK.nF.MQTT.Sparkplug.dll`은 샘플과 독립된 Sparkplug B 계층으로, 실시간 상태 전달에 맞춰 처리 범위를 제한합니다.
**MQTT DLL, 고정 장치 이름, Sensor 모델, 네트워크 주소를 참조하지 않습니다.**
다른 MQTT 전송 구현에서도 같은 페이로드·토픽·수신 상태 API를 사용할 수 있습니다.
Sparkplug B 통신에는 MQTT 클라이언트와 브로커가 필수이며, DLL의 직접 참조만 분리한 구조입니다.
프로젝트·어셈블리·패키지·네임스페이스는 `VagabondK.nF.MQTT.Sparkplug`로 통일했습니다.

사용자 모델과 메트릭의 정적 연결은 선택형 PC 생성기
[Sparkplug.Mapping](../VagabondK.nF.MQTT.Sparkplug.Mapping/README.md)에서 제공합니다.
생성기는 이름·형식·별칭 선언과 모델 변환을 담당하며 Birth/Death·수신 순서·가용성 검사는 이 런타임을 재사용합니다.
MQTT 전송 DLL이나 생성기를 사용하지 않고도 아래 공개 API를 직접 호출할 수 있습니다.

| 공개 API | 역할 |
|---|---|
| `SparkplugMetric` | Boolean(11), Int32(3), Int64(4), Float(9), Double(10)의 이름·별칭 기반 불변 스칼라 값 |
| `SparkplugDataType` | metric의 `DataType`과 수신 스키마에 사용하는 표준 datatype enum |
| `SparkplugPayload` | `Encode` / 잘못된 입력에 예외를 던지지 않는 `TryDecode`, NCMD Rebirth 페이로드 |
| `SparkplugSession` | 호출자가 지정한 그룹·노드·bdSeq, 토픽 검증, NBIRTH·NDEATH·공유 seq 생성 |
| `SparkplugReceiver` | 지정 그룹·노드·장치와 1~8개 메트릭의 이름·형식 스키마, 별칭 학습, Birth/Death·순서·수신 경과 시간 검증 |
| `SparkplugSnapshot` | 스키마 순서로 정리한 마지막 메트릭 값의 불변 스냅샷 |

## 송신과 수신

```csharp
using VagabondK.nF.MQTT.Sparkplug;

var session = new SparkplugSession("Factory", "Boiler", 7);
byte[] will = session.NodeDeath(); // 바이너리 MQTT Will에 사용하며 seq·타임스탬프는 없습니다.
byte[] nodeBirth = session.NodeBirth(utcMilliseconds); // seq=0
byte[] deviceBirth = session.Next(utcMilliseconds, new[] {
    SparkplugMetric.Int64("Cycles", 100, 0),
    SparkplugMetric.Float("Pressure", 1.25f, 10)
}, true); // seq=1, 정의와 초기 값
byte[] data = session.Next(utcMilliseconds, new[] {
    SparkplugMetric.Int64(null, 101, 0, false)
}); // seq=2, 별칭만 사용한 부분 갱신

var remote = new SparkplugReceiver("Factory", "Boiler", "Unit",
    new[] { "Cycles", "Pressure" },
    new[] { SparkplugDataType.Int64, SparkplugDataType.Float }, 5000);
remote.SetConnected(true);
remote.Apply(remote.Topic("NBIRTH"), nodeBirth, false, localNow);
remote.Apply(remote.Topic("DBIRTH"), deviceBirth, false, localNow);
remote.Apply(remote.Topic("DDATA"), data, false, localNow);
if (remote.IsAvailable(localNow))
{
    SparkplugSnapshot snapshot = remote.Read();
    long cycles = unchecked((long)snapshot[0].Integer);
    float pressure = (float)snapshot[1].Real;
}
```

위 바이트 배열을 각각 `session.Topic("NBIRTH")`, `session.Topic("DBIRTH", "Unit")`,
`session.Topic("DDATA", "Unit")`에 게시하는 것은 앱과 전송 구현의 책임입니다.
NCMD는 `remote.CommandTopic`과 `SparkplugPayload.RebirthRequest()`로 생성합니다.
소켓 연결, 구독, 재접속, UTC 동기화, CONNECT마다 `bdSeq` 증가와 재부팅 후에도 유지되는 영속 저장,
`NeedsRebirth`에 따른 요청 간격은 앱에서 관리합니다. API가 타임스탬프를 임의로 만들지는 않습니다.

같은 세션의 호출은 차례로 실행해야 합니다. `NodeBirth`는 Rebirth=false와 bdSeq만 담는 노드 프로파일로
seq=0에서 다시 시작하며, `Next`는 노드와 장치가 공유하는 seq를 0~255 범위에서 순환시킵니다.
인코딩이 실패하면 seq를 소비하지 않습니다. `birth=true`는 이름·형식과 이름·별칭의 중복 여부를 검증합니다.
세션은 실제 전송 성공 여부를 알 수 없으므로 게시 결과가 불확실하면 앱이 연결을 종료하고 다시 동기화해야 합니다.
장치의 Birth/Death 게시 순서나 장치별 송신 상태를 자동으로 관리하는 클라이언트는 아닙니다.

수신기는 원격 노드 하나의 장치 하나를 처리하는 프로파일입니다.
노드 메트릭은 bdSeq/Rebirth만, NDATA는 빈 하트비트만 허용하며 다른 장치가 섞어 보내는 순서 번호는 처리하지 않습니다.
수신기를 여러 개 만들어 구독하는 것만으로 여러 장치가 공유하는 seq를 관리할 수는 없습니다.
새 NBIRTH 뒤에는 DBIRTH가 필요하며, DBIRTH는 구성된 모든 값을 포함하고 별칭 정의를 교체합니다.
DDATA는 이름·별칭 또는 별칭만으로 일부 값을 갱신할 수 있습니다.
고정된 Sensor 이름·네 개 값·3초 기준은 라이브러리에 없고 기존 공유 샘플의 설정입니다.

`Read()`는 오류·단절 뒤에도 마지막 진단 값을 유지하므로 `IsAvailable`을 함께 확인하십시오.
현재 bdSeq와 일치하는 NDEATH, 연결 단절, 잘못된 페이로드, seq 누락이 발생하면 값을 사용 불가 상태로 표시합니다.
DDEATH 뒤에는 DBIRTH가 필요하고, 오래된 bdSeq의 DEATH는 무시합니다.
seq 중복·역순·누락은 순서가 맞춰지기를 기다리지 않고 Rebirth 요청으로 처리하며 정확히 한 번 전달을 보장하지 않습니다.
값의 유효 시간은 **생산 시각이 아닌 로컬 장치 값 수신 시각부터** 계산합니다.
NDATA 하트비트는 값을 갱신하지 않습니다.
입력 스키마는 복사하고 스냅샷은 한 번에 교체하므로, 실패 시 일부 값만 반영하지 않습니다.
수신 스키마에는 `SparkplugDataType[]`를 사용합니다. `Unspecified`는 데이터의 생략된
형식을 나타내며 스키마에는 허용하지 않습니다. 기존 사전 릴리스의 `int[]` 스키마는 열거형 배열로 변경해야 합니다.
Eclipse Tahu JavaScript 클라이언트와의 상호운용을 위해, 이름이 `bdSeq`이고 별칭이 없는
0~255의 세션 번호는 Int64(4) 외에 UInt64(8)로도 수신합니다.
송신 형식은 기존 Int64를 유지하고, 일반 장치 메트릭·매핑 스키마의 UInt64 지원으로 확대하지 않습니다.

## 지원 경계

페이로드는 최대 512바이트, 메트릭은 최대 8개, 메트릭 이름은 ASCII 64자로 제한합니다.
그룹·노드·장치 ID는 각각 ASCII 64자, 완성된 토픽은 128자까지 허용합니다.
별칭은 0과 `UInt64.MaxValue`를 포함한 uint64 전체 범위를 지원합니다.
Int32/Int64는 ZigZag가 아닌 2의 보수로 전송하며, 부동소수점 값은 유한값만 허용합니다.
페이로드와 메트릭의 타임스탬프는 인코딩·디코딩 과정에서 보존합니다.
형식이 생략된 데이터는 값 필드와 BIRTH에서 학습한 형식으로 검증합니다.
지원하지 않는 필드·형식, 중복 필드, 잘린 입력, 이력(historical)·일시(transient)·null 값은 명시적으로 거부합니다.
`Encode`는 유효하지 않은 로컬 인수에 예외를 던지고, `TryDecode`는 잘못된 전송 입력에 `false`를 반환합니다.

전체 Sparkplug 3.0/TCK 적합성, 그 밖의 메트릭 형식·문자열·배열·DataSet·Template,
Primary Host STATE/HA, 여러 장치의 수신 순서 번호 공유 관리·DCMD·물리 제어는 제공하지 않습니다.
이 제한형 프로파일의 재사용 DLL이지 전체 Sparkplug 적합성 인증 라이브러리는 아닙니다.

## 참조와 검증

`SparkplugSession`/`Receiver`는 동일 protobuf 코덱을 사용하고, MCU DLL에 코덱은 한 번만 포함됩니다.
PC `.csproj`는 같은 소스를 별도 호스트 DLL로 빌드하며 시험은 공개 API를 참조합니다.
별도로 Google.Protobuf에서 생성한 스키마를 사용해 전송 형식의 호환성과 부호·실수 비트·타임스탬프를 확인합니다.
샘플과 다른 Factory/Boiler/Unit7 스키마, 메트릭 1~8개, Int64, 별칭 경계값도 검사합니다.
외부 세션 구현과의 별도 실물 시험에는 **Eclipse Tahu `sparkplug-client` 3.2.4**를 사용합니다.
테스트 의존성은 MCU 라이브러리에 포함되지 않으며 `package-lock.json`에 버전과 무결성 값이 고정됩니다.

```powershell
dotnet run -c Release --project .\Tests\MQTT\VagabondK.nF.MQTT.DataSharing.Tests
npm ci --prefix .\Tests\MQTT\VagabondK.nF.MQTT.DataSharing.Tests\Tahu
# 전용 COM3 평가 보드의 앱을 바꾸고, 종료 시 원래 Modbus 앱을 복원합니다.
.\Build\Test-MqttDataSharingHardware.ps1 -AllowDeviceChanges -Sparkplug -Tahu
.\Build\Pack.ps1 -LibrarySet Sparkplug -OutputPath .\bin\nuget\sparkplug-new
.\Build\Test-Packages.ps1 -LibrarySet Sparkplug -FeedPath .\bin\nuget\sparkplug-new
```

Tahu가 생성한 NBIRTH/DBIRTH/DDATA/DDEATH/NDEATH를 실제 보드가 수신해 네 값·부분 갱신·별칭·
가용성·재탄생을 확인하고, 반대 방향은 Tahu가 보드의 Birth 네 값·공유 seq·별칭 프레임·
DEATH와 브로커 Will을 디코딩합니다. 최종 재검증은 `bin\mqtt-sharing-20261029-125212`에서
통과하고 원래 앱을 복원했습니다.
Tahu의 `sparkplug-payload` 1.0.3은 DDATA에서 생략된 형식을 BIRTH 별칭으로 복원하지 않아
그 값은 `null`로 반환합니다. 따라서 **보드 송신 alias-only DDATA의 값 비트까지 Tahu로 검증했다고
간주하지 않습니다**. 이 부분은 기존 독립 Google.Protobuf 시험이 보완합니다.
Tahu 외부 구현을 수정하거나 메시지 형식을 바꾸어 통과시키지는 않습니다. 전체 TCK 인증 시험은 아닙니다.

별도 생성 매핑 진단 `bin\mqtt-sharing-20261029-125046`은 네 장치 형식의 부분 갱신 264회,
다른 값 보존·seq 순환·별칭 교체·잘못된 후반 메트릭 거절을 보드에서 검사했습니다.
2,374검사와 측정 구간 보존 힙 증가 0 B를 확인했으며 장기 누수 부재를 뜻하지 않습니다.

`.nfproj`는 MCU DLL/PE/XML을 만듭니다. 패키지 전용 검증 프로젝트는 라이브러리·샘플 소스나 MQTT DLL을 참조하지 않고 빌드합니다.
`Pack`으로 만드는 사전 릴리스 패키지는 로컬 평가용이며 외부에 배포하지 않습니다.
[MCU 공유 샘플](../../../Samples/MQTT/VagabondK.nF.MQTT.Sparkplug.Mapping/README.md)은
이 DLL과 별도 [MQTT DLL](../VagabondK.nF.MQTT/README.md)을 참조하고 장치별 모델 이름만 연결합니다.

참조: [Sparkplug 3.0](https://sparkplug.eclipse.org/specification/version/3.0/documents/sparkplug-specification-3.0.0.pdf),
[Eclipse Tahu protobuf](https://github.com/eclipse-tahu/tahu/blob/5736e404889d4b95910613040a99ba79589ffb13/sparkplug_b/sparkplug_b.proto).
