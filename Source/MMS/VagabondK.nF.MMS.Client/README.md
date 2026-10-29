# 제한형 일반 MMS Client

`VagabondK.nF.MMS`의 공통 값·채널 계약·코덱만 참조하는 동기 Client입니다.
MMS Server·IEC·Socket DLL은 참조하지 않습니다.
ISO 9506 전체 구현·모든 MMS 장비 호환성·정식 적합성을 뜻하지 않습니다.

```csharp
using VagabondK.nF.MMS;
using VagabondK.nF.MMS.Client;

// 연결된 IMmsChannel의 수명은 호출자가 관리합니다.
var client = new MmsClient(channel);
client.Associate();
MmsValue value = client.Read("Plant", "Temperature");
MmsNamedVariableList list = client.GetNamedVariableListDirectory("Plant", "Measurements");
MmsReadResult[] snapshot = client.ReadNamedVariableList(list);
client.Release();
```

Read/Write·탐색·명명 변수 목록·일반 InformationReport 수신을 제공합니다.
`MmsClient`와 `MmsInformationReportHandler`는 이 DLL/네임스페이스에 있습니다.
반환 값·형식·오류·명명 목록·보고 스냅샷은 공통 `VagabondK.nF.MMS`에 남습니다.
이전 MMS 앱은 core 참조를 유지하고 `.Client` 참조와 `using VagabondK.nF.MMS.Client;`를 추가하십시오.
TCP 사용 시 `VagabondK.nF.MMS.TCP`도 직접 참조합니다.

호출은 직렬화하며 한 번에 한 요청만 처리합니다. 요청 전체에 하나의 기한을 적용하며 콜백에서 같은 클라이언트에 재진입하면 안 됩니다.
Faulted 뒤에는 Client/연결을 교체해야 하며 응답 유실 뒤 요청을 자동 재전송하지 않습니다.

`InformationReportReceived`와 `ReceiveInformationReport()`가 전달하는 보고는 수신 버퍼와
독립된 스냅샷입니다. 콜백은 원래 작업 기한 안에서 동기 실행되며 콜백 예외·기한 초과는
Client를 Faulted로 만듭니다. 검증·모델 반영을 미루려면 앱의 용량 제한 큐에 스냅샷을
보관하고 작업 종료 후 처리하십시오. 라이브러리는 보고 큐·백그라운드 처리·자동 재시도를 제공하지 않습니다.

## 시간 초과를 예외 없이 반환하는 API

기존 `TryRead(domain, item, out value, out errorCode)`는 정상 DataAccessError만
false와 오류 코드로 반환하고 시간 초과는 예외로 처리합니다. 추가 `out bool timedOut` 오버로드는
소켓 준비 대기 및 요청 전체 기한 만료도 예외 생성 없이 반환합니다.

```csharp
MmsValue value;
int errorCode;
bool timedOut;
bool success = client.TryRead("Plant", "Temperature", out value, out errorCode, out timedOut);
// success=true: 값 유효, errorCode=-1, timedOut=false
// false + timedOut=false: 정상 DataAccessError 0~11, 연결 유지
// false + timedOut=true: value=null/errorCode=-1, Faulted, 연결 폐기 필요
```

연결 채널은 선택적 `IMmsTimeoutChannel`을 구현해야 합니다. `MmsSocketChannel`은
이를 구현하며 사용자 채널이 지원하지 않으면 송신 전에 `NotSupportedException`입니다.
이 인터페이스의 `TryRead`/`TryWrite`는 준비 대기 시간 초과만 false로 반환합니다.
읽기 EOF는 true/transferred=0이므로 시간 초과와 구분됩니다.
소켓 예외·EOF·손상 프레임·서비스 오류·인수/상태 오류는 기존처럼 예외를 전파합니다.
기존 `IMmsChannel`, Read 및 4인수 TryRead의 계약은 변경하지 않습니다.
Association·탐색·다중 Read·보고 수신·Release는 여전히 기존 예외 기반 API입니다.

시간 초과를 결과로 반환할 때도 Faulted 상태 전환·Associated 해제·후속 호출 거절을 유지하며 자동 재전송하지 않습니다.
내부에서 TimeoutException을 잡아 false로 바꾸는 방식이 아니라, 채널 대기부터 요청 반환까지
시간 초과를 명시적으로 전달합니다. 콜백은 직렬로 실행되므로 장시간 실행되는 콜백이나
Socket 자체의 Send/Receive·OS 스케줄링에 대한 하드 실시간 반환 보장은 아닙니다.
실물 Read/Operate 비교는 [Client 시험](../../../Samples/IEC61850/VagabondK.nF.IEC61850.TCPClient/README.md)에 있습니다.

일반 MMS는 IEC 제어·보고·품질/UTC 의미를 해석하지 않습니다.
그 기능은 별도 `VagabondK.nF.IEC61850.Client`에서 이 Client를 확장합니다.
인증·TLS·설비 출력 안전성을 제공하지 않으며 격리된 평가 환경에서만 사용하십시오.

전체 서비스 상한·검증·크기·라이선스 경계는 [MMS 공통 안내](../VagabondK.nF.MMS/README.md)를 참조하십시오.
