# MMS 기반 IEC 61850 Client

현재는 **스칼라 Write·제한된 Read/탐색 및 SPC/DPC/APC Direct/SBO normal/enhanced-security 제어를 제공하는 평가용 MMS 클라이언트**입니다.
완전한 IEC 61850 IED Client/Server나 적합성 검증 제품은 아닙니다.
일반 MMS 서비스는 별도 [`VagabondK.nF.MMS`](../../MMS/VagabondK.nF.MMS/README.md)로 분리했습니다.
이 Client는 IEC core와 MMS core/Client를 참조하고 Socket·UART·Reflection에는 의존하지 않습니다.
TCP 어댑터는 일반 MMS와 공유하는 `VagabondK.nF.MMS.TCP` 프로젝트입니다.
고정 모델과 선택적 scalar 쓰기의 Server는 별도
[`VagabondK.nF.IEC61850.Server`](../VagabondK.nF.IEC61850.Server/README.md) DLL입니다.
Client 앱이 Server DLL을 참조할 필요는 없습니다.
`Build\Pack.ps1 -LibrarySet IEC61850`은 MMS 4종과 IEC 61850 3종의 로컬 패키지를 생성합니다.
탐색·DataSet·보고 및 제어 명령을 재전송하지 않는 재접속 흐름은
[TCP Client 예제](../../../Samples/IEC61850/VagabondK.nF.IEC61850.TCPClient/README.md)를 참조하십시오.

## MMS 분리 후 API

일반 Client/Server는 `VagabondK.nF.MMS.Client.MmsClient`·`VagabondK.nF.MMS.Server.MmsServerSession`입니다.
IEC 보고·제어를 사용하는 앱은 `VagabondK.nF.IEC61850.Client.Iec61850Client`·`VagabondK.nF.IEC61850.Server.Iec61850ServerSession`을 사용합니다.
`IMmsChannel`·`MmsValue`·형식/오류/변수 참조는 MMS 네임스페이스로 이동했고
형식 오류 예외는 `MmsProtocolException`으로 통합했습니다.
`MmsControlClient`·`MmsReportClient`·`MmsDataSet`·`MmsReport`·`MmsControlError`와 콜백 형식은
`VagabondK.nF.IEC61850.Client` DLL/네임스페이스로 이동했습니다.
Client 앱은 MMS core/Client와 IEC core/Client를 직접 참조하고 `using VagabondK.nF.IEC61850.Client;`를 추가하십시오.
품질·UTC·Dbpos 변환 및 `MmsControlModel`·`MmsControlValueKind`는 IEC core에 남습니다.
Server와 SCL 생성 모델은 Client DLL을 참조하지 않습니다.
일반 Named Variable List와 IEC DataSet은 API에서 구분하며 URCB/LastApplError/완료는 IEC가 해석합니다.
이전 `VagabondK.nF.IEC61850.TCP` 참조를 `VagabondK.nF.MMS.TCP`로 교체하십시오.
분리 후 크기·할당·MCU 양방향 검증은 [MMS 검증 결과](../../MMS/VagabondK.nF.MMS/README.md)에 있습니다.
아래 기존 시험 수치와 이미지/로그는 분리 전 바이너리의 기록이며 새 이미지의 실측치와 구분합니다.

### normal 모델의 Operate 시간 초과를 예외 없이 처리하기

`MmsControlClient.TryOperate(value, utcMilliseconds, out errorCode, out timedOut, test)`는
Boolean/SPC/DPC 또는 Float32/APC의 **normal** Direct/SBO 제어용 추가 API입니다.
기존 `Operate`의 모델/선택/Test/LastApplError 의미와 무재전송 정책을 유지합니다.
`true`는 정상 Write 성공 응답이며 설비 동작 완료 보장은 아닙니다.
`false, timedOut=false`는 정상 접근 거절 코드 0~11,
`false, timedOut=true`는 실행 여부 불확실·errorCode=-1·Faulted·연결 폐기 필요입니다.
선택적 `IMmsTimeoutChannel`이 필요하며 `MmsSocketChannel`이 구현합니다.
EOF·소켓·서비스·프로토콜·인수/상태 오류는 여전히 예외입니다.

SBO normal은 같은 연결에서 기존 `Select()`를 먼저 호출해야 합니다.
enhanced 모델은 기존 `OperateAndWait`를 사용하며 이번 시간 초과 결과 반환 확장에 포함하지 않습니다.
기존 `Operate`·Select·Cancel·enhanced API는 시간 초과 시 예외를 발생시키는 동작을 유지합니다.
Read의 추가 `out timedOut` 오버로드는 기반
[MMS Client 안내](../../MMS/VagabondK.nF.MMS.Client/README.md)를 참조하십시오.
시간 초과 뒤 새 연결에서 명령을 다시 보내도 안전하다는 뜻은 아니며 실행 여부를 별도로 확인해야 합니다.

## 구현한 범위

| 구성 | 현재 범위 |
|---|---|
| `TpktDecoder` | RFC 1006 버전 3, 4-byte 헤더 포함 길이 7~65535, 사용자 지정 최대 용량 |
| BER 내부 코덱 | 단일 Byte 식별자, definite length 읽기/쓰기, Int64 INTEGER 읽기/쓰기, BOOLEAN 읽기 |
| COTP 내부 코덱 | Class 0 DT의 인코딩·검사와 EOT 표시 읽기 |
| `Iec61850Client` | COTP CR/CC·TPDU 크기 협상, 송신 분할·수신 TSDU 재조립, 제한된 Session/Presentation/ACSE·MMS Initiate |
| MMS Read | domain-specific 한 변수 읽기, BOOLEAN·Int64·UInt64·IEEE754 binary32/binary64 |
| 다중 Read·DataSet | 최대 16개 혼합 변수와 항목별 오류, 정적 디렉터리 조회·참조/개수를 검사하는 DataSet Read |
| 제한형 URCB | 연결당 최대 4개 등록, 고정 DataSet/OptFlds/ConfRev, GI·dchg·qchg·integrity 보고 |
| 품질·시각 | `IecQuality` 13비트 의미 매핑, `Iec61850Data` UTC epoch ms/시간 품질 명시 변환 |
| MMS Write | domain-specific 한 변수 쓰기, Boolean·Int32·Float32, 정상 거절과 통신 오류 구분·재전송 없음 |
| `MmsControlClient` | Boolean DirectNormal/SboNormal/DirectEnhanced/SboEnhanced, 선택·실행·취소·완료, 현재 요청의 LastApplError 진단 |
| 제어용 leaf | BitString 최대 32비트·OctetString 최대 64 Byte·VisibleString 최대 129 ASCII Byte·UtcTime 8 Byte 읽기/형식 조회 |
| 복합 Read | 위 scalar를 포함하는 구조체·동종 배열·중첩 복합 값, 깊이/전체 노드 상한 |
| GetNameList | VMD의 domain 또는 한 domain의 변수 이름, continueAfter를 이용한 한 페이지 조회 |
| GetVariableAccessAttributes | 지원 scalar·구조체 성분/이름·배열 길이/원소 형식, mmsDeletable |
| 오류·상태 | invoke ID·PDU/TPDU/버퍼 상한·전체 동작 기한 검사, DataAccessError, Faulted 이후 재사용 거절 |
| `MmsSocketChannel` | 호출자 소유 연결된 Socket의 Poll 준비 대기와 Send/Receive에 Timeout을 적용하는 Byte 채널 |

## 다중 읽기·정적 DataSet·제한형 URCB

```csharp
var refs = new MmsVariableReference[] {
    new MmsVariableReference("NFTESTLD0", "GGIO1$ST$Status$stVal"),
    new MmsVariableReference("NFTESTLD0", "GGIO1$MX$Analog$mag")
};
MmsReadResult[] values = client.ReadMultiple(refs);
// ErrorCode=-1이면 Value가 유효합니다. 오류 0~11도 요청 순서 그대로 보존합니다.
MmsNameListPage sets = client.GetNameList("NFTESTLD0", dataSets: true);
MmsDataSet set = client.GetDataSetDirectory("NFTESTLD0", "GGIO1$Measurements");
MmsReadResult[] snapshot = client.ReadDataSet(set);
MmsReport lastReport = null;
var report = new MmsReportClient(client, "NFTESTLD0", "GGIO1$RP$Events", set,
    r => { lastReport = r; Debug.WriteLine(r.ReportId + " sequence=" + r.SequenceNumber); });
report.Enable();
report.GeneralInterrogation();
if (lastReport == null) client.ReceiveReport();
report.Disable();
```

디렉터리를 조회할 때는 `SupportsDataSetDirectory`로 서버의 서비스 지원 여부를 확인합니다. 자체 서버의 DataSet은 읽기 전용이며
동적 생성/삭제·alternate access는 없습니다. 다중 읽기는 이종 값을 MMS Array로 강제하지 않고
각 `MmsReadResult`에 독립된 스냅샷을 보존합니다. 한 응답에 포함된 전체 값 노드의 상한은 256개입니다.
DataSet 읽기는 `specificationWithResult=true`로 원래 DataSet 참조와 결과 수를 검사합니다.

URCB는 **비활성 RCB를 읽어 고정 구성을 확인한 다음 등록**합니다.
RptID·DataSet 참조·ConfRev·항목 수로 상관관계를 검사합니다.
OptFlds는 seqNum/reason/DataSet/confRev만, BufTm=0이며 timestamp/dataRef/entryID/분할은 받지 않습니다.
`Reserve`·`Enable`·`Disable`·`GeneralInterrogation`·`SetIntegrityPeriod`를 제공합니다.
한 Client 수명에 등록 슬롯은 최대 4개이며 동일 RptID를 중복 등록할 수 없습니다.
`ReceiveReport`는 보고 하나를 기다립니다. 유휴 상태의 시간 초과를 피하려면 호출자가 소켓의 수신 준비 여부를 먼저 검사하십시오.
일반 서비스의 응답 대기 중 도착한 등록 보고도 콜백으로 즉시 전달하므로, 이미 수신했다면
위 예제의 명시적 `ReceiveReport`를 추가 호출할 필요는 없습니다.
콜백에서 같은 클라이언트에 재진입하거나 장시간 대기하지 마십시오.
콜백은 원래 작업 기한을 공유하며 예외·기한 초과는 Faulted로 이어집니다.
독립 보고 스냅샷을 앱의 용량 제한 큐에 보관하고 작업 종료 후 매핑 적용·검증을 수행할 수 있습니다.
라이브러리에는 이 큐나 작업 스레드가 없으므로 큐 상한·넘침 정책·모델 잠금은 앱 책임입니다.
일반 서비스 대기 중 최대 33개 보고를 처리한 뒤에도 응답이 없으면 burst 오류로 종료합니다.
보고 수신은 기존 전체 기한을 연장하지 않습니다.

`MmsReport.GetValue(index)`는 포함된 값의 독립된 스냅샷을 반환하며, 포함되지 않은 항목이면 null을 반환합니다.
`GetReason(index)`는 dchg=0x40/qchg=0x20/integrity=0x08/GI=0x04이며 미포함은 0입니다.
SqNum은 8비트 순환 번호일 뿐 전송 보장·중복 방지 키가 아닙니다.
무제한 큐·단절 버퍼·자동 재연결/재시도는 없고, 미등록/잘못된 보고는 명시적으로 Faulted 처리합니다.
시간·품질은 필요한 leaf를 명시적으로 읽고
`Iec61850Data.ReadQuality`·`ReadTimestampMilliseconds`·`ReadTimeQuality`로 해석합니다.
UTC 밀리초 변환은 24비트 소수부의 밀리초 미만을 버리므로 원래 정밀도가 필요하면 `MmsValue.GetBytes()`의 결과를 보존하십시오.
동기화·시각 정확도·값/q/t의 원자적 취득을 보장하지 않습니다.

PC 전용 [SCL 생성기](../VagabondK.nF.IEC61850.SCL/README.md)는 제한된 SCL을
MCU용 정적 변수/DataSet/URCB 코드로 변환합니다. XML 파서나 생성기는 MCU DLL에 포함하지 않습니다.

### 확장 후 ESP32-S3 Client 확인

`bin\iec61850-data-client-hardware-final`에서 기존 읽기·쓰기·normal/enhanced/SBO enhanced 경로에 더해
원본 PC 서버를 상대로 다중 읽기·DataSet·GI 보고·품질/UTC를 3개 독립 연결로 확인했습니다.
MMH8 진단의 DataCycles=3, 마지막 가용 Heap 760,956 Byte이며 데이터 반복 중 유지됐습니다.
기존 스칼라 반복 구간에서 측정한 Client+채널은 6,504 Byte로 보고 등록·값 스냅샷·소켓 전체 비용은 포함하지 않습니다.
core/TCP/시험앱 PE는 33,268/1,184/11,916 Byte, 전체 BIN은 122,984 Byte입니다.
이전 SboEnhanced 시점 대비 core PE 증가는 7,140 Byte이며 Client 앱에 Server/XML DLL을 추가하지 않았습니다.
completed·Restored=true·Error=null과 원래 Modbus 앱 복원·COM6 읽기를 확인했습니다.

## Client 사용

앱이 먼저 Socket을 연결하고 끝나면 닫습니다. 일반 MMS 포트는 102이며,
아래 예제의 로컬 시험 피어는 관리자 권한·포트 충돌을 피하려고 18102를 사용합니다.
현재 지원하는 scalar 또는 제한된 구조체/배열의 정확한 MMS 이름을 지정하십시오. URCB는 위 제한형 프로파일만 지원합니다.

```csharp
using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
{
    socket.Connect(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 18102));
    var client = new Iec61850Client(new MmsSocketChannel(socket),
        maximumPduSize: 4096, timeoutMilliseconds: 10000);
    client.Associate();
    bool status = client.Read("NFTESTLD0", "GGIO1$ST$Status$stVal").Boolean;
}
```

위 코드는 `System.Net`, `System.Net.Sockets`, `VagabondK.nF.IEC61850.Client`,
`VagabondK.nF.MMS.TCP` 네임스페이스를 사용합니다.
MMS core/Client/TCP와 IEC core/Client 프로젝트를 함께 참조하십시오. `RealBits`는 기존 공유 소스를 MMS 코어 안에 컴파일하며
DNP3나 Modbus DLL은 추가하지 않습니다.

`Associate`와 각 서비스 호출은 전체 송수신을 포함한 단조 시계 기반 기한을 각각 적용합니다.
천천히 계속 도착하는 분할 데이터도 이 기한을 연장하지 않습니다.
TCP 어댑터는 Poll 준비 대기도 같은 호출의 남은 기한으로 제한합니다.
다만 OS Socket 동작·MCU 스케줄링·예외 생성과 전달 비용까지 포함한 엄격한 반환 시각을 보장하지 않습니다.
실물에서 3초 대기 만료 후 호출자의 catch까지 5.25초가 관측됐으므로 하드 실시간 기한으로 사용하지 마십시오.
TSAP=0001, S-selector=0001, P-selector=00000001, ACSE/MMS presentation context=1/3의
제한된 프로파일이며 인증 없이 GetNameList·Read·GetVariableAccessAttributes를 제안합니다.
AP-title 등 다른 Association 구성은 아직 지원하지 않습니다.
TPDU 제안 크기는 128/256/512/1024 중 하나이고 기본값은 1024입니다.
MMS PDU 상한은 1024~32768 Byte, 기본 4096 Byte입니다.
수신 배열은 TPKT용 1028 Byte와 TSDU용 `maximumPduSize + 1024` Byte를 생성 시 확보합니다.
요청 인코딩·BER 필드·반환 값에는 추가 할당이 있으므로 전체 MMS 통신 무할당을 주장하지 않습니다.

호출을 단일 흐름으로 직렬화하고, 한 번에 한 요청만 사용하십시오.
`MmsAccessException.ErrorCode`는 정상 Read 응답의 DataAccessError(0~11)이며 연결은 유지됩니다.
탐색·형식 조회의 정상 ConfirmedError도 `MmsServiceException`으로 전파하며 연결을 유지합니다.
그 외 통신 예외·Timeout·잘못된 응답·미지원 데이터 형식은 `Faulted=true`로 설정하고 전파합니다.
이때 Socket을 폐기하고 새 연결·새 Client로 다시 협상해야 하며 자동 재전송하지 않습니다.
반환된 스칼라의 형식과 맞지 않는 속성을 읽으면 InvalidOperationException이 발생합니다.
원격 품질·시각을 자동 수신하거나 값과 원자적으로 조합하지 않습니다.

### DataAccessError를 예외 없이 받기

반복 조회에서 정상적인 DataAccessError를 처리할 때는 `TryRead`를 사용할 수 있습니다.
기존 `Read`의 반환 값·예외 계약은 유지하며, 두 API는 같은 요청·응답 검증 경로를 사용합니다.

```csharp
MmsValue value;
int errorCode;
if (client.TryRead("NFTESTLD0", "GGIO1$ST$Status$stVal", out value, out errorCode))
{
    // 성공: errorCode == -1, value는 연결 버퍼와 독립된 값입니다.
    bool status = value.Boolean;
}
else
{
    // 서버가 반환한 DataAccessError: value == null, errorCode는 0~11입니다.
    Debug.WriteLine("MMS DataAccessError: " + errorCode);
}
```

예제의 `Debug`는 `System.Diagnostics`입니다.
**코드 0도 DataAccessError**이므로 성공 여부는 반드시 반환 bool로 구분하십시오.
false는 정상 오류 응답에만 사용하며 연결은 계속 사용할 수 있습니다.
통신 단절·Timeout·잘못된 응답·미지원 값은 false로 숨기지 않고 기존과 동일하게 예외를 전파하며 Faulted로 전환합니다.
인수·호출 상태 오류는 기존과 같이 네트워크 작업 전에 거절합니다.
예외 시 out 값은 `null`·`-1`이며, 이 값으로 예외 처리를 대신해서는 안 됩니다.
성공한 Read의 인코딩·버퍼·반환 값 할당은 남아 있으므로 무할당 API가 아닙니다.
Timeout/Association 경로의 예외 비용도 이번 개선 대상이 아닙니다.

### 이름 페이지 탐색과 형식 조회

`Associate` 후 `SupportsNameList`·`SupportsVariableAttributes`로 서버의 서비스 광고를 확인합니다.
광고하지 않은 서비스를 호출하면 송신 전 `InvalidOperationException`이며 연결은 유지됩니다.
한 요청은 항상 한 페이지만 반환하므로 전체 목록을 자동 누적하지 않습니다.

```csharp
MmsNameListPage domains = client.GetNameList();
string after = null;
do
{
    MmsNameListPage page = client.GetNameList("NFTESTLD0", after, maximumNames: 128);
    foreach (string name in page.Names) Debug.WriteLine(name);
    after = page.ContinueAfter;
} while (after != null);

MmsVariableAttributes type =
    client.GetVariableAccessAttributes("NFTESTLD0", "GGIO1$MX$Analog$mag");
// Kind=FloatingPoint, BitWidth=32, ExponentWidth=8, Deletable=false.
```

`GetNameList()`의 domain=null은 VMD domain 이름, domain을 지정하면 해당 domain의 변수 이름입니다.
`maximumNames`는 **로컬 응답 항목 상한**(1~128, 기본 128)이며 서버에게 페이지 크기를 지시하지 않습니다.
전송 상한은 협상된 PDU이며 응답 이름·중복/역순·continueAfter 이후 진행 여부도 검사합니다.
이름은 기존 Read와 같은 공백 없는 printable ASCII 1~64자입니다.
`MoreFollows`가 생략되면 ASN.1 기본값 true를 적용하며, 후속 페이지가 있다고 표시하면서 이름이 없는 페이지는 거절합니다.
마지막 페이지의 `ContinueAfter`는 null입니다. 토큰은 반환 Names 배열을 수정해도 바뀌지 않습니다.
전체 페이지 수/누적 이름 수는 호출자가 자신의 메모리·운영 정책에 맞게 제한하십시오.
로컬 항목 상한 초과·잘못된 응답은 프로토콜 예외와 Faulted이며 조용히 목록을 자르지 않습니다.

스칼라 형식 조회는 Boolean·부호 있는/없는 정수·Float32/64를 해석합니다.
정수 BitWidth는 1~64이고 실수의 (BitWidth, ExponentWidth)는 (32,8)/(64,11)입니다.
`Deletable`은 MMS mmsDeletable이며 원격 쓰기 가능 여부나 접근 권한을 뜻하지 않습니다.
탐색 결과에는 원본 IED의 부모·구조체 이름도 포함될 수 있습니다.
그 모든 이름을 scalar로 해석하지 마십시오. 지원하는 구조체·배열은 아래의 트리 API로 접근합니다.
제어 확장으로 BitString·OctetString·VisibleString·UtcTime의 아래 제한도 지원합니다.
그 외 미지원 leaf를 포함하는 복합 형식/값은 여전히 예외와 Faulted입니다.

없는 변수 등 정상 ConfirmedError는 `MmsServiceException.ErrorClass`·`ErrorCode`에
원본 MMS 분류 번호와 코드를 보존합니다. 예를 들어 시험에서 존재하지 않는 이름을 조회한 결과는 access(7)/object-non-existent(2)입니다.
같은 연결에서 다음 Read/탐색이 가능하며 Read의 DataAccessError 10과는 다른 응답입니다.
modifierPosition·additionalCode·additionalDescription의 제한된 정상 형식도 검사합니다.
invoke ID가 일치하는 confirmed-requestPDU Reject(이유 0~9)는 `MmsRejectException.Reason`으로 전달하고 연결을 유지합니다.
`MmsRejectException`은 `MmsServiceException`의 파생형이며 ErrorClass/ErrorCode는 -1입니다.
단일 Read/TryRead도 정상 ConfirmedError·Reject 뒤 같은 연결을 유지합니다.
invoke 불일치·다른 Reject 분류·잘못된 형식·미지원 서비스별 추가 정보는 프로토콜 예외와 Faulted입니다.
정상 서비스 오류도 예외 경로이므로 MCU에서 반복되는 없는 이름 조회의 성능 최적화 API는 아닙니다.

### 구조체·배열의 형식과 값

```csharp
MmsVariableAttributes type =
    client.GetVariableAccessAttributes("NFTESTLD0", "GGIO1$MX$Samples");
// Structure: 첫 성분 이름은 "values", 형식은 길이 3인 Integer 배열입니다.
string component = type.GetComponentName(0);
int declaredLength = type.GetComponent(0).ArrayLength;
MmsValue value = client.Read("NFTESTLD0", "GGIO1$MX$Samples");
long firstSample = value.GetElement(0).GetElement(0).Integer;
```

이 예제의 확장 시험 모델은 `VagabondKMmsPeer.exe complexserver 18102`로 실행합니다.
기존 `server` 모드는 원래 세 scalar 모델을 유지합니다.

`MmsValue.Kind`는 Array(1)/Structure(2)를 추가했으며 기존 enum 숫자는 그대로입니다.
`Count`·`GetElement(index)`는 복합 값에서만 사용할 수 있습니다.
scalar 속성을 복합 값에서 읽거나 그 반대이면 InvalidOperationException입니다.
반환 트리는 수신 버퍼를 참조하지 않으며 다음 요청·소켓 종료 이후에도 보관할 수 있습니다.
원소 배열은 공개하지 않고 조회만 허용합니다.
구조체 값 자체에는 성분 이름이 없으므로 필요할 때 별도 형식 조회 결과의 선언 순서와 대응하십시오.

구조체 형식은 `ComponentCount`·`GetComponent(index)`·`GetComponentName(index)`로,
배열 형식은 `ArrayLength`·`ElementType`으로 접근합니다.
이름이 생략된 성분의 이름은 null이며, 이름이 있으면 기존 identifier 제한을 적용합니다.
복합 형식의 BitWidth/ExponentWidth는 0이고, 중첩 형식의 Deletable은 별도 광고가 없으므로 false입니다.
루트의 Deletable만 전체 GetVariableAccessAttributes 응답에서 받은 값입니다.
Read 때 형식 조회를 자동 수행하거나 두 응답을 자동 대조하지 않습니다.

**고정 한계:** root 깊이 0에서 최대 8단계의 자식 접근, root를 포함해 값 트리 256개 노드,
형식 선언 트리 256개 노드, 배열 선언 길이 0~255입니다.
빈 구조체/배열도 Client에서 해석합니다.
배열 형식은 원소 형식을 한 번만 저장하며 선언 길이만큼 값을 미리 할당하지 않습니다.
따라서 형식 조회가 가능해도 실제 확장된 Read 트리가 256노드를 넘으면 거절할 수 있습니다.
배열 값은 원소의 종류·복합 형태·Float32/64 폭이 같은지 검사하며 정수 BER 길이 차이는 허용합니다.
packed=true는 미지원이고, packed 생략/default false 또는 명시적 false만 허용합니다.
미지원 leaf·잘림·상한 초과·이종 배열은 명시적 프로토콜 예외·Faulted이며 부분 결과를 반환하지 않습니다.
TryRead의 out 값도 이 경우 null/-1입니다. 지원 범위를 넓힌 것이며 무제한 재귀/할당 API는 아닙니다.

### 직접 PC에서 읽기

첫 터미널에서 시험 서버를 실행하고 `READY`를 기다립니다.
종료할 때 `quit`를 입력합니다. 시험 서버의 기본 바인딩은 loopback입니다.
실물 시험은 마지막 인수에 PC의 LAN IPv4를 명시합니다. 와일드카드 `0.0.0.0`은 허용하지 않습니다.

```powershell
.\Build\Build-Iec61850ValidationPeer.ps1
.\bin\protocol-tools\iec61850-build\Release\VagabondKMmsPeer.exe server 18102
```

다른 터미널에서 아래 명령을 실행하면 BOOLEAN `True`를 반환합니다.
`$`가 포함된 MMS item 이름은 PowerShell의 작은따옴표로 감싸십시오.
`--read`는 임의의 IPv4·포트·domain·item을 받아 현재 지원하는 프로파일의 장치도 시험할 수 있지만,
그 장치의 모델·접속 요구가 이 제한과 맞는지 먼저 확인해야 합니다.

```powershell
dotnet run -c Release --project `
  .\Tests\IEC61850\VagabondK.nF.IEC61850.Tests\VagabondK.nF.IEC61850.Tests.csproj `
  -- --read 127.0.0.1 18102 NFTESTLD0 'GGIO1$ST$Status$stVal'
```

피어의 다른 속성은 `GGIO1$ST$Count$stVal`(INTEGER -123456),
`GGIO1$MX$Analog$mag`(FLOAT32 12.5)입니다.
예를 들어 `VagabondKMmsPeer.exe server 18102 192.168.0.210`은 지정한 PC LAN 주소에서만 대기합니다.
`silent` 모드는 TCP 연결과 COTP CR만 수신하고 응답 없이 `quit`까지 유지하는 장애 주입 전용 모드이며,
MMS Server 구현이 아닙니다.

BER·COTP 코덱은 향후 MMS API를 확정하기 전까지 내부 구현으로 유지합니다.
BER의 indefinite length·high-tag 식별자는 명시적인 프로토콜 예외로 거절합니다.
이는 유효한 BER 전체를 지원한다는 뜻이 아닙니다.
definite length의 비최소 길이 표기는 BER 규칙에 따라 허용하고, INTEGER의 불필요한 부호 Byte는 거절합니다.
중첩 요소는 상위 요소의 끝 위치를 전달하여 경계를 제한해야 하며, 자동 재귀 파싱은 하지 않습니다.

TCP 분할 수신과 여러 TPKT의 합쳐진 수신을 처리합니다.
디코더 생성 시 최대 패킷 크기의 배열 하나를 만들며, 정상 `Feed`는 추가 배열을 생성하지 않습니다.
콜백은 헤더를 포함한 재사용 배열과 유효 길이를 받습니다. 배열을 변경하거나 보관하지 마십시오.
내용을 보관해야 하면 콜백에서 앱이 복사해야 합니다.

```csharp
var decoder = new TpktDecoder(4096, OnPacket);
// TCP Receive가 반환한 실제 Byte 수를 전달합니다.
decoder.Feed(receiveBuffer, offset, receivedCount);
// TCP EOF일 때 남은 부분 프레임을 오류로 감지합니다.
decoder.CompleteInput();
```

`OnPacket(byte[] packet, int length)`는 앱의 동기 처리 메서드입니다.
호출은 직렬화해야 하며 콜백 안에서 디코더에 재진입하면 안 됩니다.
잘못된 헤더·용량 초과·콜백 예외는 `Faulted`를 설정하고 그대로 전파합니다.
헤더를 추측해 잘못된 스트림 뒤에서 재동기화하지 않습니다.
연결을 폐기한 뒤 `Reset`하여 다음 연결에서 재사용합니다.
버퍼 인수 오류는 네트워크 오류가 아니므로 `Faulted`를 설정하지 않습니다.
부분 프레임의 시간 제한·소켓 수명·접속 상태는 이 디코더를 사용하는 상위 계층이 관리해야 합니다.
`Iec61850Client`는 별도의 동기 수신 경로에서 위의 전체 기한을 적용합니다.

## 빌드와 PC 회귀

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe' `
  .\Source\MMS\VagabondK.nF.MMS.TCP\VagabondK.nF.MMS.TCP.nfproj `
  /t:Rebuild /p:Configuration=Release /warnaserror
dotnet run -c Release --project `
  .\Tests\IEC61850\VagabondK.nF.IEC61850.Tests\VagabondK.nF.IEC61850.Tests.csproj
```

독립 피어 상호운용과 PC/MCU Debug·Release 검사를 함께 재실행하려면 다음을 사용합니다.

```powershell
.\Build\Build-Iec61850ValidationPeer.ps1
.\Build\Test-Iec61850.ps1 -OutputPath .\bin\iec61850-tests-new
```

새 출력 폴더만 허용하며 빌드·시험 로그와 피어 바이너리 SHA256을 보존합니다.
독립 피어는 `libiec61850 v1.6.2`, revision
`f0f797bf955da763353f7081c892a1ff6b55e5a1`로 고정하고 원본 소스 수정 여부를 검사합니다.
시험 드라이버만 작은 모델을 만들고 라이브러리 자체는 수정하지 않습니다.
원본 Client와 Server의 패킷을 관찰하는 진단 모드는 PC 시험의 `--capture <피어 exe>`입니다.

PC 시험은 동일 코드를 C# 7.3으로 링크하며, BER INTEGER·길이 출력을
독립적인 .NET `System.Formats.Asn1` BER 구현과 대조합니다.
고정 TPKT/COTP 바이트 벡터, 모든 수신 분할 위치·Byte 단위·합쳐진 수신,
전체 UInt16 패킷 길이 범위, 잘린 EOF, 콜백 예외·재진입·Reset을 검사합니다.
TPKT 수신 10,000회 추가 managed 할당도 별도로 확인합니다.
MMS 회귀는 잘린 Association 전체 위치·잘못된 CC·invoke 불일치·scalar 경계·EOF·기한·TSDU 초과와
정상 DataAccessError 뒤 같은 연결의 다음 Read를 검사합니다.
실제 독립 서버와는 두 새 연결에서 Boolean/정수/실수 60회와 없는 변수 오류 20회를 확인합니다.
테스트 채널의 Read/Write를 최대 3/5 Byte로 제한하고, 두 번째 연결은 TPDU128을 협상하여
Association의 실제 COTP 분할·재조립도 확인합니다.
이는 MCU 실물 통신이나 다른 IED 제품·Edition 전체에 대한 호환성/적합성 인증을 의미하지 않습니다.
Socket 회귀는 실제 loopback에서 인수 오류·partial Read/Write·EOF와
무응답 Read/Association의 대기 기한·Faulted 상태도 확인합니다.

### 첫 독립 피어 검증 결과

`bin\iec61850-client-interop-first`에서 MCU용 core·TCP의 Debug/Release warnaserror 빌드와
PC Debug/Release 시험을 모두 통과했습니다.
각 구성에서 기반 코덱 289,613개 assertion, Client 상태·분할·기한 193개 assertion,
원본 Client의 baseline 및 `--read` CLI 실행을 확인했습니다.
자체 Client는 독립 서버와 두 Association에서 scalar 60회·DataAccessError 20회를 검증했습니다.
이는 assertion 누계이지 그만큼의 서로 다른 IED·시험 항목을 검증했다는 뜻은 아닙니다.
시험 피어 프로세스는 종료했고 MCU 보드·기존 앱·통신 설정은 변경하지 않았습니다.
근거는 출력 폴더의 `build-*.log`, `tests-*.log`, `peer-image.json`, `completed.txt`입니다.

## ESP32-S3 실물 시험

Geekble Nano ESP32-S3(COM3, 저장된 Wi-Fi, `192.168.0.187`)의 실제 Client와
PC(`192.168.0.210:18102`)의 원본 libiec61850 서버를 연결합니다.
시험앱은 `Tests\IEC61850\VagabondK.nF.IEC61850.Hardware`이며, 제품용 앱이 아닌 격리된 시험 fixture입니다.
제어·결과 전달용 UDP 20003을 사용하고 인증은 제공하지 않습니다.
Wi-Fi 자격 증명·펌웨어·방화벽 규칙은 변경하지 않습니다.

```powershell
.\Build\Build-Iec61850ValidationPeer.ps1
.\Build\Test-Iec61850Hardware.ps1 -AllowDeviceChanges `
  -HostAddress 192.168.0.210 -DeviceAddress 192.168.0.187 `
  -OutputPath .\bin\iec61850-hardware-new
```

기본값은 기존 `Read` 경로입니다. `-UseTryRead`를 추가하면 **동일 시험앱 이미지**에서
정상 순회의 scalar Read와 DataAccessError를 모두 `TryRead`로 계측합니다.
두 모드 모두 피어 종료 시 `TryRead`가 통신 오류를 false로 숨기지 않는지도 확인합니다.

새 출력 폴더가 필요합니다. COM3 평가 보드와 원래 Modbus 복원 이미지의 SHA256을 사전 검사하고,
PC 회귀·MCU Release warnaserror 빌드 후에만 배포합니다.
복원 이미지는 `bin\hardware\restore-Modbus.SerialServer.bin`,
기대 SHA256은 `66C51145509667EA4019649750B42CAD47344CE76D9736994B9658E0F45226A7`입니다.
피어의 소스 revision·원본 수정 여부·실행파일 identity도 검사합니다.
호스트 주소나 방화벽 때문에 접속할 수 없으면 실패로 기록하며 자동으로 방화벽을 열지 않습니다.

무응답 피어에서 COTP CR 수신 증거와 3초 준비 대기 만료·Faulted를 확인한 뒤 새 Client로 복구합니다.
정상 TPDU128/PDU4096 연결 5회에서 매번 Boolean·정수·실수 60회와 없는 변수 오류 20회를 확인합니다.
중간에는 살아 있는 연결에서 피어를 종료해 EOF/소켓 실패·Faulted를 확인하고,
피어를 재시작해 새 연결로 다시 읽습니다. 정상 DataAccessError는 기존 연결을 유지해야 합니다.
어느 단계든 실패하면 실패 상태를 남기고, finally에서 소유 피어를 종료하고 원래 Modbus 앱을 복원합니다.
복원 실패는 명시적으로 알리고 1회 재시도하며 COM6 RTU holding 0..2의 `123,456,65535`까지 확인합니다.

### 첫 완료 실측: 2026-10-26

최종 근거는 `bin\iec61850-hardware-deadline`입니다.
`status.json`은 `completed`·`Restored=true`·`Error=null`이며,
`measurements.jsonl`·`sizes.json`·`images.json`·`peer-*.log`·`restored-modbus.log`와 배포 이미지 사본을 보존합니다.

| 항목 | 실측 |
|---|---|
| 검증 누계 | 423회, scalar 301회(정상 순회 300 + 장애 전 1), DataAccessError 100회 |
| 연결과 복구 | 정상 순회 5회, 무응답 만료·피어 종료 각 1회 및 이후 정상 읽기 |
| Association | 정상 순회의 마지막 실측 90ms, 다섯 순회 범위 90~100ms |
| 정상 scalar Read | 계측 300회 합계 4,260ms, 평균 14.2ms, 최대 30ms |
| DataAccessError | 계측 100회 합계 176,610ms, 평균 1,766.1ms, 최대 1,780ms |
| 무응답 기한 | 준비 대기 3,000ms, 호출자의 예외 처리까지 5,250ms |
| GC 후 가용 Heap | 기준·최소·최대 모두 761,868 Byte, 기준 이후 잔류 증가 0 Byte |
| Client + Socket 채널 생성 비용 | GC 후 가용 Heap 차이 6,396 Byte |
| 실제 배포 출력의 PE | core 12,908 Byte, TCP 1,184 Byte, 시험앱 4,876 Byte |
| 배포 BIN | 95,584 Byte, nanoFramework 참조 DLL·시험앱 포함 |
| 원래 앱 복원 | 첫 읽기에서 COM6 `123,456,65535` 확인 |

정상 Read와 DataAccessError를 분리해야 합니다. 오류 예외 경로가 훨씬 비싸며,
3초의 네트워크 대기 기한을 호출자가 3초 안에 예외를 받는 보장으로 해석하면 안 됩니다.
별도 무응답 계측에서 기한 만료 시점 3,000ms와 예외 처리 완료 5,250ms를 구분했습니다.
생성 비용 6,396 Byte는 이미 연결된 Socket에서 Client와 채널을 생성할 때의 managed Heap 차이이며,
Socket·Wi-Fi native 메모리나 Association/Read 중 최대 사용량이 아닙니다.
GC 후 안정값도 짧은 반복 시험의 결과이지 장시간 누수 부재·최대 RAM·실시간 성능 보장이 아닙니다.
PE 수치는 실제 배포앱 출력 폴더 기준이며 BIN 전체를 라이브러리 단독 크기로 보지 마십시오.

중간 조사에서는 PowerShell 자체 TCP listener로의 접속이 `10053`으로 실패했습니다.
이를 timeout 성공으로 취급하지 않고, 기존 서버와 같은 native 실행파일의 `silent` 모드로 경로를 통일했습니다.
무응답 대기와 예외 전달을 한 숫자로 비교한 초기 시도들은 실패로 보존했고,
최종 시험에서 별도 계측으로 차이를 확인했습니다. 모든 배포 시도 후 원래 앱 복원을 확인했습니다.
방화벽이 원인인지는 확정하지 않았습니다.

최종 코드의 PC/MCU Debug·Release 회귀와 시험앱 Debug 빌드 근거는
`bin\iec61850-hardware-regression`에 있습니다. 정식 IED·다른 펌웨어·다른 Edition에 대한 검증은 아닙니다.

### TryRead 성능 개선 실측: 2026-10-26

같은 보드·독립 서버·시험앱 BIN(SHA256
`4F4DCAB51587FE05A61AC5C01249230C6085746C999CF2F555E0026D43AE3D7C`)에서
UDP 명령으로 API만 선택해 비교했습니다.
`bin\iec61850-tryread-hardware`는 `-UseTryRead`,
`bin\iec61850-tryread-legacy-compare`는 기본 `Read` 경로입니다.
각 실행은 scalar 301회·DataAccessError 100회·검증 423회·두 장애와 복구를 완료했고,
이 중 정상 순회 scalar 300회와 DataAccessError 100회를 지연 비교에 사용했습니다.

| 항목 | 기존 Read API | TryRead API |
|---|---:|---:|
| DataAccessError 평균 | 1,767.1ms | 16.4ms |
| DataAccessError 최대 | 1,770ms | 30ms |
| 정상 scalar 평균 | 15.57ms | 14.97ms |
| 정상 scalar 최대 | 30ms | 30ms |
| GC 후 가용 Heap 기준·최소·최대 | 762,264 Byte | 762,264 Byte |
| Client·채널 생성 비용 | 6,396 Byte | 6,396 Byte |

이 시험에서 **DataAccessError 경로는 약 108배 빨라졌습니다.**
정상적인 오류 응답을 예외로 전달하는 비용을 제거한 효과이며 모든 Read가 108배 빨라진다는 뜻은 아닙니다.
정상 읽기의 작은 차이는 네트워크·타이머 변동도 포함하므로 유의한 성능 개선으로 주장하지 않습니다.
무응답 시험의 준비 대기 3,000ms·예외 처리 포함 5,250ms는 두 모드에서 동일하며,
통신 실패를 예외로 전달하는 계약과 비용은 유지했습니다.

이번 배포 출력은 core PE 12,956 Byte, TCP PE 1,184 Byte,
시험앱 PE 5,100 Byte, 전체 BIN 95,856 Byte입니다.
첫 완료 실측 대비 core PE는 48 Byte 증가했으며, BIN에는 비교용 시험앱 변경도 포함됩니다.
두 모드 모두 기준 이후 가용 Heap 감소 없이 완료했고 원래 Modbus 앱과 COM6 읽기를 복원했습니다.
장시간·다른 펌웨어·다른 IED 성능을 보장하는 결과는 아닙니다.

PC 회귀는 DataAccessError 0~11 전체, 오류 이후 같은 연결의 성공,
out 값 초기화, 기존 Read 예외, 잘못된 코드·형식·invoke ID, EOF·Timeout·Faulted 이후 재사용 거절을 확인합니다.
독립 피어의 두 Association은 각각 Read와 TryRead를 사용합니다.
최종 PC/MCU Debug·Release 회귀와 시험앱 Debug 빌드는
`bin\iec61850-tryread-regression2`에 기록했으며 Client assertion은 구성당 264개입니다.

## 탐색·형식 조회 검증: 2026-10-26

`bin\iec61850-discovery-regression-final`에서 PC/MCU Debug·Release, 양방향 원본 피어를 통과했습니다.
시험앱 두 개의 Debug 빌드는 `bin\iec61850-discovery-regression2`에,
최종 Server 시험앱 Debug 빌드는 최종 회귀 폴더에도 기록했습니다.
구성당 Client assertion 352개·Server 7,028개·기반 289,613개이며,
분할 I/O·잘못된 페이지·기본 moreFollows·중복/역순/진행·로컬 상한·미지원 형식·invoke·오류 후 상태,
다중 domain·64자 이름·실제 PDU 상한에 따른 페이지 분할·정상/오류 후속 요청을 포함합니다.
PC Server Read 할당은 요청당 912 Byte, setter 3종 반복은 추가 할당 0 Byte로 기존 결과를 유지합니다.
이는 탐색/형식 조회도 무할당이라는 뜻은 아닙니다.

실물 Client 근거는 `bin\iec61850-discovery-client-hardware`입니다.
`-UseTryRead` 모드로 5회 연결에서 domain/변수/continueAfter 응답 총 15회,
세 scalar 형식 조회 15회, 없는 변수 ConfirmedError 5회와 이후 정상 Read를 확인했습니다.
기존 scalar 301회·DataAccessError 100회·피어 종료/무응답 복구도 유지했고 총 473개 검증을 통과했습니다.
당시 진단 형식은 MMH2·27개 UInt32·124 Byte 고정 헤더였습니다. 이후 MMH3~MMH8을 거쳐 확장됐으며
현재 형식은 [TCP Client 예제의 시험 안내](../../../Samples/IEC61850/VagabondK.nF.IEC61850.TCPClient/README.md)를 참조하십시오.
가용 Heap은 기준/최종 762,048 Byte로 같았고 Client+채널 생성 차이는 6,420 Byte였습니다.
실제 배포 PE는 core 17,212/TCP 1,184/시험앱 6,096 Byte, 전체 BIN은 101,108 Byte입니다.
기존 최적화 후 core PE 12,964 Byte보다 4,248 Byte 증가했습니다.
3초 준비 대기/예외 검사까지 5,250ms는 그대로이며 서비스 확장에 따른 지연 개선을 주장하지 않습니다.
Server 측 실물·크기는 [Server 결과](../VagabondK.nF.IEC61850.Server/README.md)를 참조하십시오.
양쪽 시험의 `status.json`은 completed·Restored=true·Error=null이며,
각각 첫 복원 배포 및 COM6 첫 읽기에서 원래 Modbus 앱의 `123,456,65535`를 확인했습니다.

## 복합 형식/값 확장 검증: 2026-10-26

PC·MCU Debug/Release 및 양쪽 시험앱 Debug 빌드는 `bin\iec61850-composite-regression-final`에 기록했습니다.
구성당 Client assertion 356개·복합 경계/모델 45개·Server 7,032개·기반 289,613개를 통과했습니다.
원본 피어가 생성한 구조체·배열·배열을 포함하는 구조체의 형식/값을 자체 Client에서 읽고,
원본 Client도 자체 Server의 명명된 구조체 안 Integer 배열을 반복해서 읽습니다.
TPDU128에서 80개 배열 값의 여러 프레임 재조립, 다음 요청 전후 자식 값 변경과 이전 결과 보존,
깊이 8/9·노드 256/257 경계·빈 값·packed·이종 배열·잘린 중첩·작은 PDU의 명시적 실패도 검사합니다.
Read 전용 Server의 PC 할당은 요청당 912 Byte, scalar setter 반복 추가 할당은 0 Byte를 유지했습니다.
복합 Read/형식 조회는 결과 트리와 TLV 배열을 할당하며 별도 최적화하지 않았습니다.

실물 Client 근거는 `bin\iec61850-composite-client-hardware-final`입니다.
5회 연결에서 구조체/배열/중첩 구조체 각각의 형식 조회와 Read **총 30회**,
기존 이름 페이지 15회·scalar 형식 조회 15회·서비스 오류 5회,
scalar 301회·DataAccessError 100회와 피어 종료/무응답 복구를 포함해 518개 검증을 통과했습니다.
확장 진단은 **MMH3·28개 UInt32·128 Byte 헤더**이며 구버전 호스트/이미지와 혼용하지 마십시오.
GC 후 가용 Heap은 기준/최종 761,508 Byte로 일정했고 Client+채널 생성 차이는 6,420 Byte였습니다.
Client 배포 PE는 core 18,996/TCP 1,184/시험앱 6,848 Byte, 전체 BIN 103,644 Byte입니다.
탐색 버전 대비 core PE는 1,784 Byte 증가했습니다. 코드/모델/시험앱이 바뀌므로 상주 RAM 차이를
특정 라이브러리 객체의 비용으로 단정하지 않습니다.
준비 대기는 2,990ms, 예외 검사까지 5,250ms로 런타임 예외 경로의 비용은 남아 있습니다.
두 실물 시험 모두 첫 복원 배포·COM6 첫 읽기에서 원래 Modbus 값 정상 복원을 확인했습니다.
Server 측 요청/크기/상태는 [Server 결과](../VagabondK.nF.IEC61850.Server/README.md)를 참조하십시오.

## 단일 scalar MMS Write

`SupportsWrite`는 협상된 서비스 지원이며 해당 변수의 쓰기 권한이 아닙니다.
Boolean·Int32·Float32 한 변수의 `TryWrite(domain, item, value, out errorCode)`와
`Write(domain, item, value)`를 지원합니다. 복합 값·다중 변수·alternateAccess 쓰기는 없습니다.

```csharp
int code;
if (!client.TryWrite("NFTESTLD0", "GGIO1$SP$Writable$count", 42, out code))
    Debug.WriteLine("Write denied: " + code);
```

성공은 true/code=-1, 정상 DataAccessError는 false/code=0~11이고 연결을 유지합니다.
Write wrapper는 정상 거절을 MmsAccessException으로 전파합니다.
정상 ConfirmedError는 MmsServiceException이며 연결을 유지합니다.
서비스 미협상·인수 오류는 송신 전에 거절합니다.
통신·기한·형식 오류가 발생하면 예외를 전파하고 Faulted 상태로 전환합니다.
**요청 후 응답 유실/Timeout/EOF가 나면 서버에 이미 적용되었을 수 있습니다. 자동 재전송하지 않습니다.**
새 연결에서 값을 다시 읽는 Readback도 원자적 제어나 동작 완료를 보장하지 않습니다.
이 API는 MMS 값 쓰기이지 IEC 61850 Direct Operate/SBO가 아닙니다.
인증·TLS가 없으므로 격리된 평가 환경의 메모리 시험 변수만 사용하십시오.
자체 Server는 기본 쓰기 금지이며 [EnableWrite·검증기](../VagabondK.nF.IEC61850.Server/README.md)로 허용합니다.
로컬 스칼라 스냅샷은 MmsValue.FromBoolean/FromInteger/FromFloatingPoint로 만들 수 있습니다.

### Write 추가 후 검증: 2026-10-26

PC/MCU Debug·Release 및 원본 피어 양방향 최종 근거는 `bin\iec61850-write-regression-final`입니다.
Client 408개·Server 7,055개·복합 45개·기반 289,613개 assertion 외에
기본 거절·형식/범위/값 불변·잘못된 요청·검증기 오류·응답 유실 후 재전송 없음도 검사합니다.
원본 Server의 ST/MX 측정 속성 대신 별도 SP Writable 메모리 변수를 사용합니다.
이것을 임의 IED의 보호 정책이나 쓰기 가능성 보장으로 해석하지 마십시오.

실물 근거 `bin\iec61850-write-client-hardware`는 completed·Restored=true·Error=null입니다.
5회 연결에서 Write 성공 30회·정상 거절 20회·별도 Readback 20회와
기존 scalar 301회/접근 오류 100회·복합 30회·이름 페이지 15회·형식 15회·서비스 오류 5회,
장애 후 복구까지 563개 검증을 통과했습니다.
진단은 **MMH4·31개 UInt32·140 Byte 헤더**이며 이전 MMH3 기록/이미지와 혼용하지 마십시오.
GC 후 가용 Heap은 761,424 Byte로 일정했고 Client+채널 생성 차이는 6,432 Byte였습니다.
core/TCP/시험앱 PE는 20,000/1,184/7,692 Byte, 전체 BIN은 105,492 Byte입니다.
무응답 준비 대기 2,990ms/예외 검사까지 5,250ms이며 하드 실시간을 보장하지 않습니다.
원래 Modbus 앱 첫 복원·COM6 첫 읽기를 통과했습니다.
기존 scalar Read PC 할당 912 Byte/요청, setter 추가 할당 0 Byte는 유지했습니다.
Write 및 복합 경로는 할당이 있으며 과거 Read 지연과 직접 개선율을 비교하지 않습니다.

## Boolean normal-security 제어

`MmsControlClient`는 원본 MMS domain과 `LN$CO$object`(최대 55자)를 받습니다.
생성 시 대응하는 `LN$CF$object$ctlModel`을 읽고
**1=DirectNormal, 2=SboNormal, 3=DirectEnhanced, 4=SboEnhanced**를 허용합니다.
Boolean `ctlVal`·origin·Unsigned8 ctlNum·UtcTime T·Test·2비트 Check 구성만 전송합니다.
아래 normal 모델은 요청 수락만 확인합니다. enhanced 모델의 완료 알림과 SBOw는 다음 절을 참조하십시오.
DPC는 같은 Boolean 명령과 별도의 Dbpos 상태, APC는 아래의 명시적 Float32 프로파일을 사용합니다. 시간예약 필드는 없습니다.

```csharp
var control = new MmsControlClient(client, "NFTESTLD0", "GGIO1$CO$Selected",
    new byte[] { 78, 70 }, originCategory: 3);
long utcMilliseconds = (DateTime.UtcNow.Ticks - new DateTime(1970, 1, 1).Ticks) / 10000;
int code;
if (control.Select())
{
    bool accepted = control.Operate(true, utcMilliseconds, out code);
    // accepted는 정상 Write 응답이며 실제 설비 완료 보장이 아닙니다.
}
```

DirectNormal은 Select 없이 Operate합니다. SboNormal은 같은 객체/연결에서 Select 후
Operate 또는 Cancel하며, 한 번 시도한 뒤에는 다시 Select해야 합니다.
빈 SBO 참조는 false, 다른 참조/형식은 프로토콜 예외·Faulted입니다.
클라이언트의 로컬 선택 표시는 서버 기한이나 원격 허용을 보장하지 않습니다.
Cancel은 선택만 취소하며 이미 끝난 동작을 되돌리지 않습니다.
정상 실행/취소 거절은 false/code=0~11이고 연결을 유지합니다.
송신 후 Timeout/EOF/응답 오류는 실행 여부 불명이며 자동 재전송하지 않습니다.
모든 제어·다른 MMS 호출을 직렬화하십시오. 새 연결에는 새 제어 객체를 생성하십시오.

origin 0~64 Byte는 복사하며 category는 0~8입니다. origin/ctlNum은 인증·재전송 방지 기능이 아닙니다.
UTC 시각은 호출자가 제공하고 UInt32 초 범위로 제한합니다. 품질 바이트는 **0x3f(시계 미동기화/정밀도 미지정)**이며
시각 동기화·시각 기반 승인·시간예약을 제공하지 않습니다. Check는 항상 false입니다.
`test=true`는 Test 플래그를 보내며 자체 Server는 값 변경을 하지 않지만 임의 IED의 정책까지 보장하지 않습니다.
normal-security라는 IEC 제어 모델 이름은 TLS/인증을 의미하지 않습니다.

원본 서버 또는 `ReportControlErrors=true`인 자체 Server의 거절 앞에 오는 `LastApplError`는
**현재 제어 요청의 참조·origin·ctlNum이 모두 일치하는 1개**만 받습니다.
진단 후 같은 전체 기한 안에서 정상 응답을 기다리며 `LastError`의 Error/AdditionalCause/ControlNumber에 보존합니다.
진단이 없으면 null이며 자체 normal Server의 ReportControlErrors 기본값은 false입니다.
등록한 제한형 URCB 이외의 Report·다른/중복 진단·진단 후 성공 응답은 지원하지 않고 명시적으로 Faulted 처리합니다.
이 요청 거절 진단은 아래 DirectEnhanced의 CommandTermination과는 별개입니다.

제어용 값/형식 조회는 BitString 최대 32비트(패딩 검사), OctetString 64 Byte,
VisibleString 129 printable ASCII Byte, UtcTime 정확히 8 Byte로 제한합니다.
`VisibleString`, `BitCount`, `GetBytes()`로 원시 값을 읽으며 Byte 배열은 복사합니다.
형식의 `MaximumLength`는 비트 또는 Byte 상한입니다. 품질/시각을 IEC 의미로 해석하거나 값에 원자적으로 결합하지 않습니다.
임의 문자열·OctetString 쓰기를 공개한 것은 아닙니다.

원본 확장 제어 모델을 대상으로 전체 GetNameList를 조회할 때 엄격한 ASCII 정렬 검사에서 응답이 거절되는 경우를 확인했습니다.
이 경계를 완화하지 않았으며 제어 시험은 명시적인 raw MMS 이름과 형식 조회를 사용합니다.
전체 IED 탐색·모델 적합성을 보장하지 않습니다.

## Boolean DirectEnhanced 완료 확인

`ctlModel=3`은 **선택 없이 요청 수락 후 별도 완료 통지를 받는 제어 모델**이며 TLS/접근 인증이 아닙니다.
`Operate` 대신 아래 동기 API를 사용하십시오. 일반 Operate 호출은 명시적으로 거절하여 읽지 않은 완료 통지가 남지 않게 합니다.

```csharp
var enhanced = new MmsControlClient(client, "NFTESTLD0", "GGIO1$CO$Enhanced",
    new byte[] { 78, 70 }, originCategory: 3);
bool accepted;
int code;
bool completed = enhanced.OperateAndWait(true, utcMilliseconds, out accepted, out code);
// accepted: 요청 수락 여부, completed: 긍정 완료 여부, LastError: 요청 거절/부정 완료의 상세진단
```

| 결과 | accepted | 반환값 | code / LastError |
|---|---|---|---|
| Write 거절 | false | false | DataAccessError 0~11 / 선택적 진단 |
| 긍정 CommandTermination | true | true | -1 / null |
| 부정 CommandTermination | true | false | -1 / 필수 실패 진단 |
| 수락 이후 통신 실패·기한 초과 | true | 예외/Faulted | -1 / 수신된 진단만 보존 |

송신 중 실패는 accepted=false여도 서버에 전달되었을 수 있습니다.
**재전송·물리 동작 취소·성공 추정은 하지 마십시오.** 서버의 timeout 부정 완료는 정상 통지여서
연결을 유지하지만, Client 자신의 기한 초과는 완료 여부 불명인 통신 예외이며 연결을 폐기해야 합니다.
호출은 전체 동기 대기이며 다른 Read/Write/제어와 병행하지 않습니다.
요청 송신·수락·완료 수신까지 기존 Client timeout **하나의 기한**을 공유합니다.
서버 완료 기한보다 Client 기한이 충분히 길어야 하며 자동 연장하지 않습니다.

지원 완료 형식은 원본 피어에서 관찰한 **Oper 한 항목의 긍정 InformationReport**,
또는 **LastApplError와 같은 Oper 두 항목의 부정 InformationReport**입니다.
domain/item·origin·ctlNum뿐 아니라 반환된 Oper 전체 값의 BER payload가 송신 명령과 같은지 확인합니다.
정상 완료에 상세진단은 없으며 부정 완료는 실패 진단이 필수입니다.
다른 명령·잘못된 순서/구조·상관관계 불일치·수락 전 완료는 Faulted입니다.
중복 통지가 다음 서비스에 남으면 그 서비스도 프로토콜 오류로 거절합니다.
등록한 제한형 URCB 이외의 Report·임의 통지 순서·BER 표현을 바꾸는 다른 IED까지 지원한다고 보장하지 않습니다.

`test=true`도 수락과 완료를 확인합니다. 자체 서버는 허용 검사만 호출하고 자동으로 시험 완료를 통지하며
실행 처리기를 호출하거나 Value를 변경하지 않습니다. 실제 IED의 Test 정책은 별도로 확인하십시오.
SboEnhanced(ctlModel=4)는 다음 SBOw 선택 절차가 추가되며, 예약 제어는 지원하지 않습니다.

## Boolean SboEnhanced·SBOw

`SelectWithValue`로 값을 지정하여 선택한 뒤, **같은 제어 객체·연결에서**
`OperateAndWait`로 실행과 완료를 확인합니다. normal의 `Select()`/`Operate()`는 사용할 수 없습니다.

```csharp
var selected = new MmsControlClient(client, "NFTESTLD0", "GGIO1$CO$SboEnhanced",
    new byte[] { 78, 70 }, originCategory: 3);
int code;
if (selected.SelectWithValue(true, utcMilliseconds, out code))
{
    bool accepted;
    bool completed = selected.OperateAndWait(true, utcMilliseconds, out accepted, out code);
    // 실행하지 않을 때에는 OperateAndWait 대신 selected.Cancel(utcMilliseconds, out code)를 호출합니다.
}
```

선택 성공은 실행 수락/완료가 아닙니다. 선택과 실행의 Boolean 값·origin category/identifier·ctlNum·Test는
동일해야 합니다. UTC T는 각 요청 시각으로 달라질 수 있지만 예약 시각이나 인증에 사용하지 않습니다.
Check는 양쪽 모두 false만 지원합니다. ctlNum은 SBOw에서 증가하고 후속 Oper/Cancel에서 재사용합니다.
`test=true`를 선택했다면 실행도 `test=true`여야 하며 Cancel에도 선택한 Test를 사용합니다.
잘못된 값/Test의 로컬 실행 호출은 송신 없이 예외이며 기존 선택을 유지하므로 일치하는 실행 또는 취소를 할 수 있습니다.

로컬 선택 중 다시 SelectWithValue를 호출하면 예외입니다.
실행/취소를 송신하는 시도에서는 로컬 선택을 해제하고 **자동 재선택·재실행하지 않습니다**.
서버 선택이 만료되면 실행은 정상 거절(false/accepted=false/code=3, 자체 Server addCause=18)입니다.
연결 유실/기한 초과는 원격 선택이나 물리 동작의 종료를 보장하지 않습니다.
이 Client는 매 실행마다 새로 선택하는 operate-once 흐름을 제공합니다.
수락 후 완료 형식·전체 기한·실패 처리와 물리 출력 한계는 DirectEnhanced와 같습니다.

Server는 SBOw/Oper/Cancel의 정상 거절에 LastApplError를 송신하므로,
일반 scalar Write가 아닌 위 제어 API를 사용하십시오.

### SboEnhanced 최신 실측: 2026-10-26

최종 Debug/Release·nanoFramework 빌드·원본 양방향 상호운용·할당 회귀 근거는
`bin\iec61850-sbo-enhanced-regression-final`입니다.
선택 소유권/기한·동일 소유자 재선택 거절·값/출처/번호/Test 불일치·취소·Oper 재검사,
응답 송신 실패/EOF/Close 후 선택 해제·선택 요청 객체로 완료 처리 금지와 로컬 API 오용을 검사했습니다.
잘못된/조기/중복 완료·전체 기한·EOF·부정 완료의 실패 진단 검사도 두 enhanced 모델에 적용합니다.
실제 출력·범용 IED 적합성은 확인한 것이 아닙니다.

Client 실물 근거는 `bin\iec61850-sbo-enhanced-client-hardware`입니다.
기존 Read/Write·normal/DirectEnhanced·장애 복구에 이어 별도 **3개 연결×22개 요청**으로
SBOw→완료 성공/실패/timeout·Test·값 읽기·선택 취소·만료 진단을 확인했습니다.
서버는 원본 libiec61850 v1.6.2 공개 API로 구성했습니다.
준비 구간 이후 가용 Heap은 761,520 Byte로 새 제어 경로 종료까지 일정했고 Client+채널은 6,480 Byte입니다.
PE는 core 26,128 / TCP 1,184 / 시험앱 10,168 Byte, BIN 114,096 Byte입니다.
이전 DirectEnhanced 단계 대비 **core PE +364 Byte**이며 시험앱 증가는 공통 검사 코드도 포함합니다.
새 진단은 **MMH7·34개 UInt32·152 Byte 헤더**로 MMH6와 혼용하지 마십시오.
`SboEnhancedCycles=3`은 전체 공통 검사 통과 횟수이며 내부 assertion을 Checks에 따로 누적하지 않습니다.

PC x64에서 SBOw 선택·실행·완료 생명주기 10,000회 기준 긍정 10,248 / 부정 11,264 Byte를
동작당 누적 할당했습니다. 이는 상주 버퍼 크기나 MCU 순간 최대 할당량이 아닙니다.
DirectEnhanced의 7,520/8,536 Byte 및 기존 normal/Read 경로 할당량은 유지했습니다.
Server의 실제 256회 반복과 자원 측정은 [Server 안내](../VagabondK.nF.IEC61850.Server/README.md)를 참조하십시오.
두 실물 시험은 completed·Restored=true·Error=null과 이미지 해시/배포 PE 일치를 확인했고,
원래 Modbus 앱 복원 후 COM6에서 기존 값을 읽었습니다.

### DirectEnhanced 이전 실측: 2026-10-26

PC Debug/Release·nanoFramework 빌드와 할당 회귀 근거는 `bin\iec61850-enhanced-regression-final`입니다.
원본 libiec61850 v1.6.2와 양방향으로 수락→성공/실패/기한 초과·Test·Readback을 확인했습니다.
잘못된 완료·조기/중복 통지·출처/번호/값 불일치·전체 기한·EOF 후 재전송 없음도 PC에서 검사했습니다.

Client 실물 근거는 `bin\iec61850-enhanced-client-hardware`입니다.
기존 Write/Read/normal 제어/장애 복구에 이어 **별도 연결 3회×12개 요청**으로
긍정 완료·부정 완료(addCause=9/16)·Test와 읽기 확인을 수행했습니다.
가용 Heap은 761,112 Byte로 기존 warmup 이후부터 새 완료 경로 종료까지 일정했습니다.
core/TCP/시험앱 PE는 25,764/1,184/9,348 Byte, BIN은 112,912 Byte입니다.
당시 진단은 **MMH6·33개 UInt32·148 Byte 헤더**였으며 이후 SBO Enhanced 단계에서 MMH7로 확장했습니다.
아래 normal 제어 절의 실측은 이전 이미지 결과입니다.
원래 Modbus 앱 복원·COM6 읽기를 확인했습니다. Server 실측은 Server 안내의 최신 절을 참조하십시오.

### 제어 추가 후 PC·실물 검증: 2026-10-26

최종 PC/MCU Debug·Release 근거는 `bin\iec61850-control-regression-final`입니다.
Client 432개·Server 7,055개·복합 45개·기반 289,613개 assertion에 더해
제어 소유권·선택 만료·취소·한 번 실행·기본 거절·Test 무변경·Check 거절·잘못된 명령,
연결 종료 후 선택 해제·CF 설정 조회·잘못된 상세진단/재전송 없음·새 leaf 경계를 검사했습니다.
원본 libiec61850과 **자체 Client→원본 Server 및 원본 Client→자체 Server** 양방향으로
scalar Write/Readback·Direct/SBO 제어를 검사합니다. 실제 출력은 연결하지 않습니다.
스칼라 Read의 PC 할당 912 Byte/요청, setter 추가 할당 0 Byte는 유지했습니다.

Client 실물 근거는 `bin\iec61850-control-client-hardware`입니다.
기존 5회 연결의 Write 성공 30회/거절 20회/Readback 20회 및 Read/탐색/장애 복구를 유지하고,
별도 제어 연결 3회에서 매회 **21개 요청**으로 Direct 실행·Test·읽기 확인,
SBO 선택/실행/취소·만료 후 거절과 LastApplError(addCause=18)·값 불변을 확인했습니다.
`ControlCycles=3`은 각 공통 제어 검사 전체 통과 횟수이며, `Checks=566`에는 그 내부 검사를 따로 누적하지 않습니다.
당시 진단은 **MMH5·32개 UInt32·144 Byte 헤더**였으며 이후 SBO Enhanced 단계에서 MMH7로 확장했습니다.
GC 후 가용 Heap은 761,568 Byte로 일정했고 Client+채널 생성 차이는 6,480 Byte였습니다.
core/TCP/시험앱 PE는 24,708/1,184/8,836 Byte, 전체 BIN은 111,344 Byte입니다.
준비 대기 2,990ms/예외 검사까지 5,250ms로 하드 실시간 기한 보장은 없습니다.
completed·Restored=true·Error=null과 배포 이미지 해시, 원래 Modbus 앱 복원·COM6 정상 읽기를 확인했습니다.
Server 결과는 [Server 안내](../VagabondK.nF.IEC61850.Server/README.md)를 참조하십시오.

## DPC/APC 제어 프로파일

`MmsControlValueKind.Boolean`(기본 SPC), `DoublePoint`(DPC), `AnalogFloat32`(APC)를
Client·Server 양쪽에 명시합니다. CDC나 설비 종류를 이름으로 추측하지 않습니다.
네 모델 DirectNormal/SboNormal/DirectEnhanced/SboEnhanced의 선택·Test·취소·완료 계약은 같습니다.

```csharp
var analog = new MmsControlClient(client, "NFTESTLD0", "GGIO1$CO$A1", new byte[] { 78, 70 },
    valueKind: MmsControlValueKind.AnalogFloat32);
int error;
bool ok = analog.Operate(12.5f, 1700000000000L, out error); // DirectNormal
```

APC는 단일 `f` 성분의 AnalogueValue 구조체만 지원합니다. 명령은 float 오버로드를 사용합니다.
정수형 AnalogueValue·setMag·시간예약은 없고 NaN/Infinity는 송신 전에 거절합니다.
다른 프로파일의 오버로드는 예외입니다. DPC의 ctlVal은 Boolean이고 `stVal`은
`Iec61850Data.ReadDoublePoint(value)`로 해석하는 2비트 Intermediate/Off/On/Bad입니다.
원본 libiec61850 공개 DPC/APC 생성·제어 API와 네 모델의 양방향 선택/실행/Test/취소/읽기 확인을 수행합니다.
메모리 상태이지 실제 접점·아날로그 출력/피드백 보장은 아닙니다.

## 정상 종료와 로컬 패키지

`client.Release()`는 같은 전체 기한 안에서 MMS Conclude 응답과
ACSE RLRQ/RLRE(Session FINISH/DISCONNECT)를 처리합니다.
종료 응답 앞의 등록 URCB 보고는 정상적으로 전달합니다.
성공하면 Associated=false/Faulted=false이며 재협상은 금지합니다. 새 연결에 새 Client를 생성하십시오.
실패는 예외/Faulted이며 자동 재전송하지 않습니다. 전달받은 Socket/채널은 닫지 않으므로 호출자의 finally에서 폐기하십시오.
원본 공개 API와 양방향 종료, 잘림·context·invoke·Reject 경계를 확인합니다.
제한형 프로파일이며 모든 Session 옵션·Release 이유·피어 주도 종료를 구현한 것은 아닙니다.

MMS core/Client/Server/TCP와 IEC core/Client/Server는 `Build\Pack.ps1 -LibrarySet IEC61850`로 로컬 패키지 7개를 생성합니다.
`Build\Test-Packages.ps1 -LibrarySet IEC61850`은 저장소 소스 참조 없이 전체·Core·Server·TCP 소비자를
오프라인 복원/빌드합니다. PC SCL 도구와 native 검증 피어는 MCU 패키지에 포함하지 않습니다.
예제는 [TCPHost](../../../Samples/IEC61850/VagabondK.nF.IEC61850.TCPHost/README.md)를 참조하십시오.

## 다음 구현 경계

고정 모델의 제한된 Server는 별도 프로젝트로 구현했습니다.
이름 탐색·구조체/배열 형식과 값·제어용 leaf·Boolean normal-security 제어는 위 제한된 범위로 구현했습니다.
DirectEnhanced와 SboEnhanced/SBOw의 제한된 선택·완료 통지도 구현했습니다.
품질/시각 의미 매핑·정적 DataSet·제한형 URCB·PC SCL 정적 코드 생성은 위 범위로 추가했습니다.
일반 복합/다중 변수 쓰기·시간예약·BRCB·단절 이벤트 보존·전체 SCL/Logical Node 의미 모델은 없습니다.
ACSE 인증·전체 OSI 옵션·추가 Reject 분류는 없으며,
지원하지 않는 응답은 명시적 프로토콜 예외와 Faulted로 처리합니다.
GOOSE·SV·IEC 62351 보안도 구현하지 않았습니다. 격리된 평가 환경에서만 사용하십시오.

실제 IED별 프로파일·장시간 운전·전원/네트워크 장애 반복은 별도 검증이 필요합니다.
서비스 확장과 Server는 대상 Edition과 정식 규격을 확인하면서 추가해야 합니다.
공개 자료만으로 확인되지 않은 동작을 적합성 보장으로 문서화하지 않습니다.

## 참조와 라이선스 경계

- [RFC 1006](https://www.rfc-editor.org/rfc/rfc1006.txt): TPKT와 TCP 위 ISO Transport.
- [ITU-T X.690](https://www.itu.int/rec/T-REC-X.690/en): ASN.1 BER 규칙.
- [libiec61850](https://github.com/mz-automation/libiec61850): 별도 PC Client/Server 상호운용 피어.

MCU 라이브러리에 libiec61850의 소스·바이너리를 포함하거나 링크하지 않습니다.
별도 PC 시험 도구는 `bin\protocol-tools`에 원본을 다운로드하고 정적으로 링크합니다.
이 시험 바이너리는 GPLv3/상용 라이선스 검토 대상이며 독자 라이브러리와 같은 배포물로 취급하지 마십시오.
표준 필드·관찰한 시험 패킷에 기반한 관리 코드 구현이며 외부 라이브러리 구현 코드를 복사하지 않습니다.
IEC 61850-8-1·7-2/7-3/7-4, ISO 9506 및 관련 OSI 계층의 정식 규격은 아직 확보하지 않았습니다.
SCL 단계에서는 IEC 61850-6도 필요합니다.
