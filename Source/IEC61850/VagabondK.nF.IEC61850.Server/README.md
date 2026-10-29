# 제한된 MMS Server

고정 domain/item 모델의 **Boolean·Int32·IEEE754 Float32 단일 변수 Read**를 제공하는 평가용 서버입니다.
같은 고정 모델의 domain/변수 이름 페이지 탐색과 세 가지 스칼라 형식 조회도 지원합니다.
Boolean·Int32·Float32 자식으로 구성한 고정 구조체·동종 배열의 형식 조회와 단일 변수 Read도 지원합니다.
Boolean·Int32·Float32 단일 변수 Write는 명시적으로 허용한 변수만 지원합니다.
선택적인 Boolean normal-security Direct Operate/SBO와 애플리케이션 허용 검사·실행 요청 핸들러도 제공합니다.
Boolean DirectEnhanced/SboEnhanced는 연결당 명령 1개를 진행할 수 있으며 명시적인 완료·실패·기한 초과 통지를 지원합니다.
DPC(Boolean 명령/Dbpos 상태)와 Float32 APC도 같은 네 제어 모델을 지원합니다.
완전한 IEC 61850 IED Server·데이터 모델·적합성 인증 제품이 아닙니다.
Client는 `VagabondK.nF.IEC61850.Client`, 이 Server는 별도 `VagabondK.nF.IEC61850.Server` DLL입니다.
IEC Server는 IEC core와 일반 MMS core/Server를 참조하지만 Client 전용 앱은 Server를 참조하지 않습니다.
BER/COTP/MMS 코덱은 MMS core 한 곳에만 컴파일하고 내부 friend assembly 경계로 공유합니다.
TCP 어댑터는 `VagabondK.nF.MMS.TCP`입니다. 패키지 세트는 MMS 4종과 IEC 61850 3종입니다.
IEC core에는 공유 데이터·제어 모델 정의만 남겼고 이 Server는 MMS/IEC Client DLL을 참조하지 않습니다.
일반 변수는 `VagabondK.nF.MMS.Server.MmsServerVariable`, IEC 세션은 `Iec61850ServerSession`입니다.
Dbpos/Quality/Timestamp 팩터리는 `Iec61850ServerData`로 이동했고 setter는 같은 IEC Server 네임스페이스의 확장 메서드입니다.
분리 후 크기·할당·MCU 양방향 검증은 [MMS 검증 결과](../../MMS/VagabondK.nF.MMS/README.md)에 있습니다.
아래 과거 시험 이미지·크기·Heap 수치는 분리 전 바이너리의 기록이며 새 이미지와 구분합니다.

## 지원 범위와 수명

| 항목 | 범위 |
|---|---|
| 전송 | 호출자 소유 `IMmsChannel`, TPKT·COTP Class 0, TPDU128~1024 협상·분할·재조립 |
| Association | 고정 Session/Presentation/ACSE 프로파일, MMS version 1, 동시 진행 요청 1개 |
| 선택자 | TSAP/S-selector=0001, P-selector=00000001, ACSE/MMS context=1/3 |
| 모델 | 고정 이름 1~128개, domain/item은 공백 없는 printable ASCII 1~64자 |
| 값 | Boolean·Int32·Float32·Dbpos·Quality·UTC, 제한된 구조체·배열, leaf setter로 타입을 유지하며 변경 |
| Read | 단일/다중 domain-specific 이름 또는 정적 DataSet, specificationWithResult=false/true, invoke ID는 UInt32 |
| 다중 Read·DataSet | 최대 16개 혼합 변수, 정적 DataSet 최대 8개·각 최대 16개 참조, 디렉터리·이름·값 조회, specificationWithResult 지원 |
| 품질/UTC | 13비트 `IecQuality`, UTC 8 Byte·시간 품질, 추가 할당 없는 로컬 setter |
| 제한형 URCB | 최대 4개 고정 DataSet, GI·dchg·품질 leaf qchg·integrity, 단일 소유권·기본 비활성 |
| Write | 한 domain-specific scalar, 기본 거절·명시적 EnableWrite·선택적 값 검증 |
| 제어 | Boolean/DPC/Float32 APC의 DirectNormal/SboNormal/DirectEnhanced/SboEnhanced, 기본 비활성·SBO/SBOw 선택 소유권/기한·실행·취소·Test 무변경·핸들러/LastApplError/완료 통지 |
| 정상 종료·서비스 거절 | MMS Conclude/ACSE Release·직접 FINISH에서 소유권 정리, 정상 미지원 confirmed service는 Reject 후 연결 유지 |
| 없는 이름 | DataAccessError 10, 같은 연결의 다음 Read 허용 |
| GetNameList | VMD domain/한 domain의 변수 이름, ASCII 순서·continueAfter·항목/PDU 상한에 따른 페이지 |
| GetVariableAccessAttributes | 고정 scalar/구조체/배열 형식, 성분 이름·배열 길이, mmsDeletable=false |
| 탐색 서비스 오류 | 없는 domain/변수/continuation은 ConfirmedError access(7)/object-non-existent(2), 연결 유지 |
| 경계 | PDU 기본 4096 Byte(설정 1024~32768), 수신 버퍼와 협상 상한·전체 작업 기한 검사 |

PDU 상한은 피어 제안과 로컬 상한 중 작은 값이고, TPDU는 최대 1024를 선택합니다.
중첩 깊이는 최대 8이며 피어가 더 작은 값을 제안하면 그 값으로 응답합니다.
AP-title·AE-qualifier 식별 필드는 제한된 형태만 읽으며 **인증에 사용하지 않습니다.**
인증 옵션·지원하지 않는 OSI 옵션은 거절합니다.

`Iec61850ServerSession`은 연결 하나만 처리하며 수신 대기 소켓(listener)의 관리나 소켓 수락·해제를 담당하지 않습니다.
수신 대기 루프에서 이전 소켓을 닫고 다음 소켓을 수락하면 한 번에 연결 하나를 처리할 수 있습니다.
수신 대기 큐(backlog)에 있는 연결까지 거절하는 접속 정책은 별도로 구현해야 합니다.
모델 업데이트·세션 처리·채널 접근을 호출자가 직렬화하십시오.
여러 세션이 같은 모델에 동시에 접근하는 사용법은 이번 검증 범위가 아닙니다.

## 사용

MMS core/Server/TCP와 IEC core/Server를 참조하고 다음 네임스페이스를 사용합니다.
`System`, `System.Net`, `System.Net.Sockets`, `VagabondK.nF.MMS`, `VagabondK.nF.MMS.Server`,
`VagabondK.nF.IEC61850`, `VagabondK.nF.IEC61850.Server`, `VagabondK.nF.MMS.TCP`.

```csharp
var model = new MmsServerVariable[] {
    new MmsServerVariable("NFTESTLD0", "GGIO1$ST$Status$stVal", true),
    new MmsServerVariable("NFTESTLD0", "GGIO1$ST$Count$stVal", -123456),
    new MmsServerVariable("NFTESTLD0", "GGIO1$MX$Analog$mag", 12.5f)
};
using (var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
{
    listener.Bind(new IPEndPoint(IPAddress.Any, 18103));
    listener.Listen(1);
    using (Socket accepted = listener.Accept())
    {
        var session = new Iec61850ServerSession(new MmsSocketChannel(accepted), model);
        session.Associate();
        while (session.ProcessRequest())
        {
            // 같은 흐름에서 모델의 다음 값을 업데이트할 수 있습니다.
            model[1].SetInteger(-123455);
        }
    }
}
```

모델 배열은 생성 시 복사하지만 변수 객체는 공유합니다.
이름·타입·모델 구성은 고정이고 로컬 setter 또는 명시적으로 허용한 scalar Write로 값을 변경합니다.
잘못된 타입의 setter는 명시적으로 예외를 발생시킵니다.
값 업데이트는 생성 시 확보한 인코딩 배열을 재사용합니다.
Read는 이름 문자열을 만들지 않고 수신 Byte와 모델 이름을 직접 비교하며, scalar 응답은 수신을 마친 패킷 버퍼에 직접 인코딩합니다.
요청 해석의 BER 객체와 Association 중간 배열 할당은 남아 있으므로 무할당 Server가 아닙니다.
정수 리터럴은 Int32, 실수는 `f`를 붙여 Float32 생성자를 선택하십시오.

`ProcessRequest`는 한 요청의 정상 응답/지원하는 서비스의 ConfirmedError를 송신하면 true,
다음 프레임을 시작하기 전의 정상 TCP EOF이면 false입니다.
기존 `ProcessRead`도 호환용 래퍼로 유지하며 동일한 요청 서비스를 처리합니다.
부분 프레임 EOF·잘못된 형식·기한 만료·채널 오류는 예외를 전파하고
`Faulted=true`·`Associated=false`로 전환합니다.
그 Socket을 닫고 새 Socket·새 세션을 생성해야 합니다. 정상 EOF 이후에도 기존 세션을 재사용하지 마십시오.
위 예제는 오류를 숨기지 않도록 예외를 호출자에게 전파합니다.
실제 수신 대기 앱에는 예외를 기록하고 소켓을 정리한 뒤 다음 연결을 받을 정책이 필요합니다.

Association과 각 `ProcessRequest`에는 전체 수신·응답 송신 기한이 있습니다.
첫 요청 대기도 포함하므로 유휴 Client도 설정한 기한을 넘으면 실패합니다.
채널·운영체제·예외 전달 비용까지 포함한 하드 실시간 반환 기한은 보장하지 않습니다.
실물에서는 3,000ms 대기 만료 후 예외 처리까지 약 5,260ms를 관측했습니다.

### 페이지와 형식 조회의 경계

`Iec61850ServerSession(..., maximumNamesPerPage: 16)`의 페이지 상한은 1~128, 기본 16입니다.
이름 한 개를 포함한 응답 전체가 협상 PDU에 들어가는지 검사하므로 실제 응답은 더 작아질 수 있습니다.
이름 하나도 들어가지 않으면 이름 없이 후속 페이지가 있다고 표시하는 대신 resource(3)/other(0) ConfirmedError를 송신합니다.
모델 배열의 복사본을 생성 시 domain/item의 ASCII 사전순으로 정렬하고,
domain 이름은 중복 제거합니다. 호출자의 원본 배열 순서는 바꾸지 않습니다.
모델 구성은 세션 중 바뀌지 않으므로 continuation 기준도 안정적입니다.
continueAfter는 앞 페이지의 마지막 이름이며,
존재하는 마지막 이름 이후에는 빈 최종 페이지를 반환합니다. 없는 토큰은 정상 서비스 오류입니다.
domain 목록은 실제 변수가 있는 domain만 포함하며 모델 자체를 비울 수는 없습니다.

VMD-domain/objectClass=9, domain-variable/objectClass=0과 domain-DataSet/objectClass=2 조합을 제공합니다.
다른 scope/class 조합은 service(4)/other(0) ConfirmedError이며,
잘린 BER·잘못된 identifier 등은 여전히 프로토콜 예외·Faulted입니다.
형식 조회는 domain-specific 이름만 받으며 지원 scalar와 구조체·배열 선언을 광고합니다.
미지원 데이터 모델이나 복합 쓰기/삭제 서비스를 제공한다는 뜻은 아닙니다.
`mmsDeletable=false`는 삭제 불가이며 쓰기 권한을 나타내지 않습니다.
탐색·형식 조회는 문자열·TLV·결과 배열을 할당하며 Read의 최적화된 직접 인코딩 경로와 구분됩니다.

### 고정 복합 모델

```csharp
var first = new MmsServerVariable("NFTESTLD0", "e0", 100);
var samples = new MmsServerVariable("NFTESTLD0", "samples", new MmsServerVariable[] {
    first,
    new MmsServerVariable("NFTESTLD0", "e1", 101),
    new MmsServerVariable("NFTESTLD0", "e2", 102)
}, isArray: true);
var bundle = new MmsServerVariable("NFTESTLD0", "bundle", new MmsServerVariable[] {
    new MmsServerVariable("NFTESTLD0", "flag", true), samples
});
var model = new MmsServerVariable[] { bundle };
// ProcessRequest와 같은 흐름에서 호출하면 다음 전체 bundle Read에 반영됩니다.
first.SetInteger(200);
```

`MmsServerVariable(domain, item, elements, isArray: false)`는 구조체이고 true는 동종 배열입니다.
자식 배열은 복사하지만 자식 변수 객체/값은 공유합니다. 구성·이름·형식은 생성 후 고정이며
값은 leaf setter로 변경합니다. 별도 composite setter나 복합 원격 쓰기는 없습니다.
자식을 최상위 모델에도 등록하고 EnableWrite하면 그 scalar 이름으로만 쓸 수 있습니다.
구조체 성분 이름은 각 자식의 Item을 사용하고 중복 이름은 거절합니다.
배열의 모든 원소는 동일한 전체 형식(중첩 성분 이름/종류·배열 길이 포함)이어야 합니다.
배열 원소의 Item은 형식에 쓰지 않으므로 서로 달라도 됩니다.
자식 Domain/Item은 기존 생성자 규칙을 따르지만 자동으로 경로를 구성하지 않습니다.

최상위 모델 배열에 등록한 이름만 GetNameList/Read/형식 조회의 대상입니다.
예제에서는 `NFTESTLD0/bundle`만 등록되고,
`bundle$flag` 또는 자식의 원래 Item이 자동으로 별도 등록되지는 않습니다.
필요하면 호출자가 별도 최상위 이름을 명시적으로 등록해야 합니다.
이는 SCL/IEC 61850 Logical Node 모델 자동 생성이나 MMS alternateAccess 지원이 아닙니다.

구성당 직접 자식 1~255개, root 깊이 0에서 최대 8단계, 실제 확장 모델은 root 포함 256노드로 제한합니다.
공유 자식을 여러 번 넣어도 각 위치를 노드로 계산합니다.
Client의 빈 복합 값 해석과 달리 Server 생성자는 원소 형식을 확정할 수 있도록 빈 배열/구조체를 제공하지 않습니다.
미지원 leaf·null·이종 배열·깊이/노드 초과는 생성 시 명시적 인수 예외입니다.
세션과 값 변경은 항상 직렬화해야 하며 복합 읽기의 일관된 시점도 호출자 직렬화에 의존합니다.

복합 응답은 지원하는 분할 송신 경로를 사용하고 TLV 중간 배열을 할당합니다.
기존 작은 scalar 응답의 패킷 버퍼 직접 인코딩 경로는 그대로 유지합니다.
형식 또는 값 응답 전체가 협상 PDU에 들어가지 않으면 예외·Faulted로 종료하며
부분 응답이나 자동 분할 Read를 제공하지 않으며, 잘린 값을 정상 결과처럼 반환하지 않습니다.
COTP 프레임 분할은 가능하지만 MMS PDU 자체를 여러 요청/응답으로 쪼개지는 않습니다.

## 명시적 scalar 쓰기

```csharp
var target = new MmsServerVariable("NFTESTLD0", "GGIO1$SP$Writable$count", 0);
target.EnableWrite(delegate(MmsValue candidate) {
    return candidate.Integer < -1000000 || candidate.Integer > 1000000 ? 11 : -1;
});
// 이 변수를 최상위 model에 등록합니다. 나머지 변수는 계속 쓰기 금지입니다.
// target.DisableWrite()는 원격 쓰기만 차단하며 로컬 SetInteger에는 영향이 없습니다.
```

기본 거절은 DataAccessError 3, 형식 불일치는 7, 없는 이름은 10,
Int32 범위 초과는 11입니다. Float64 쓰기는 지원하지 않습니다.
정상 거절은 값을 바꾸지 않고 연결을 유지합니다. 복합 변수의 EnableWrite는 예외입니다.
형식·Int32 범위를 먼저 검사하고 검증기는 **-1 허용/0~11 거절**을 반환합니다.
Float32 NaN/Infinity를 포함한 업무 범위는 검증기로 제한하십시오. null 검증기는 형식 검사만 합니다.
검증기는 동기로 호출되며 **대기와 외부 부작용이 없는 검증 전용**입니다. 설비를 구동하는 콜백이 아닙니다.
잘못된 반환 값/예외는 숨기지 않고 전파하며 세션을 Faulted로 만듭니다.
검증은 로컬 setter에 적용되지 않습니다. 모델·허용 정책·세션 접근을 모두 직렬화하십시오.

Write 성공은 메모리 적용 응답이며 **IEC 61850 Direct Operate/SBO나 설비 동작 완료가 아닙니다.**
적용 직전 기한도 확인하지만 검증기 실행/응답 송신 중 실패하면 이미 적용되었을 수 있습니다.
실행 여부 불명 상태에서 자동 재전송하지 마십시오. 후속 Read도 원자적 제어나 실행 완료 보장은 아닙니다.
서비스 광고는 쓰기 권한 광고가 아닙니다. 인증·TLS·접속 주체별 권한 검사는 없습니다.
격리된 평가 환경의 메모리 시험 변수에만 허용하십시오.

## Boolean Direct/SBO 제어

```csharp
var selected = new MmsServerControl("NFTESTLD0", "GGIO1$CO$Selected",
    MmsControlModel.SboNormal, selectionTimeoutMilliseconds: 1000);
selected.Enabled = true; // 격리된 시험 환경에서 명시적으로 허용
var session = new Iec61850ServerSession(channel, new MmsServerVariable[0],
    controls: new MmsServerControl[] { selected });
try
{
    session.Associate();
    while (session.ProcessRequest()) { }
}
finally
{
    session.Close(); // 채널/Socket 해제는 여전히 호출자 책임
}
```

DirectNormal(ctlModel=1)은 선택 없이 즉시 메모리 값을 변경합니다.
SboNormal(2)은 **같은 세션**이 `$SBO`를 읽어 선택한 뒤 `$Oper`를 써야 합니다.
기한은 1~60000ms(기본 1000)이며 작업 시 만료를 검사합니다. 별도 타이머/백그라운드 실행은 없습니다.
다른 소유자가 있으면 빈 선택 참조, 미선택/만료/다른 세션의 실행·취소는 DataAccessError 3입니다.
소유권·기한·Enabled·Check 검사를 통과한 Operate는 Test나 애플리케이션 거절/실패여도
선택을 소비하는 operate-once이며 `$Cancel`도 선택을 해제합니다.
같은 세션의 재선택은 기한을 갱신합니다. Enabled=false도 선택을 해제합니다.
정상 EOF·프로토콜/통신 실패·명시적 Close에서 그 세션의 선택만 해제합니다.
호출자가 소켓을 먼저 닫고 처리를 중단하면 finally에서 Close를 호출하십시오.
공유 모델의 모든 세션 처리는 **호출자가 직렬화**해야 합니다. 동시 스레드 안전성을 제공하지 않습니다.

`Value`와 생성된 `LN$ST$object$stVal`은 메모리 상태이며 실제 설비 피드백이 아닙니다.
Test=true는 허용 검사만 수행하고 실행 처리기를 호출하거나 메모리 값을 변경하지 않습니다.
핸들러 없는 메모리 모드에서는 정상 수락합니다. interlock/synchroCheck=true는 거절합니다.
명령의 필드 순서/개수·origin·ctlNum·UtcTime·Check 패딩을 검사하고 잘못된 구조는 Faulted입니다.
형식 불일치는 7, 제한된 필드 범위 위반은 11로 거절합니다.
UTC는 원시 8바이트로 검사하며 시각 동기화·재생 방지·시간 예약 승인에 사용하지 않습니다.
실행 후 송신 실패 시 이미 값이 변경되었을 수 있으므로 자동 재시도하지 마십시오.

control item은 `LN$CO$object` 최대 53자, 제어 객체는 세션당 최대 16개입니다.
일반 변수와 생성된 모든 이름의 합은 1~128개이고 중복 이름을 거절합니다.
Direct는 5개, SBO는 8개 이름을 등록합니다. CO 구조/Oper/Cancel/SBOw는 **형식 조회 전용**이며
Read에는 접근 거절을 반환합니다. 과거 명령 값처럼 보이는 초기 템플릿을 반환하지 않습니다.
normal SBO Read는 선택을 수행하고 CF ctlModel·sboTimeout·sboClass(0=operate-once), ST stVal은 읽기 전용입니다.
CF sboTimeout/sboClass는 SBO에만 있습니다. 이름 탐색/형식 조회에서도 생성된 이름을 찾을 수 있습니다.
제어를 허용해도 일반 scalar 변수의 EnableWrite나 복합 쓰기 권한을 넓히지 않습니다.

이 기능은 **평가용 제어 모델**입니다. 실제 출력 드라이버·물리적 interlock·장치별 정책의 기본 구현,
시간예약·영속 감사 로그·인증/TLS는 없습니다. enhanced 모델은 다음 절의 제한된 선택/완료 통지만 지원합니다.
Server는 정상 거절을 DataAccessError로 반환합니다. 선택적으로 아래 LastApplError 진단을 먼저 송신할 수 있습니다.
origin/ctlNum과 normal-security라는 이름은 접근 인증이나 전송 보안을 제공하지 않습니다.

### 애플리케이션 허용 검사·실행 요청

`SetHandlers(MmsControlValidator, MmsControlHandler)`는 null이 아닌 두 핸들러를 함께 등록합니다.
등록/교체 시 **비활성화·선택 해제**하므로 반드시 명시적으로 다시 Enabled=true로 설정해야 합니다.
normal 모델의 미등록 객체는 기존 메모리 전용 모드를 유지합니다. enhanced 모델은 두 핸들러 등록이 필수입니다.
모든 객체는 생성 시 Enabled=false입니다.

아래 예는 물리 출력 없이 한 개의 대기 요청만 보관합니다. 실제 설비 드라이버가 아닙니다.
샘플의 허용 플래그는 기본 false이며, 애플리케이션이 자체 정책에 따라 명시적으로 변경해야 합니다.

```csharp
bool operationAllowed = false;
bool pending = false;
bool pendingValue = false;
selected.SetHandlers(
    delegate(MmsControlRequest request)
    {
        return operationAllowed && !pending ? -1 : 3;
    },
    delegate(MmsControlRequest request)
    {
        pendingValue = request.Value;
        pending = true;
        return -1;
    });
selected.Enabled = true;
// pending/pendingValue 소비와 플래그 변경도 세션 처리 사이에 같은 실행 흐름에서 직렬화합니다.
```

허용 검사는 부작용 없이 **각 Operate 실행 직전**에 호출하며 Test에도 적용합니다.
SBO Select는 요청 값이 없으므로 이 검사를 호출하지 않습니다. 선택 성공은 실행 허가가 아닙니다.
허용 검사를 통과한 요청 중 Test가 아닌 요청에만 실행 처리기를 **요청당 한 번** 호출합니다.
Cancel·만료·다른 소유자·비활성·Check 거절·형식/범위 오류에서는 두 핸들러 모두 호출하지 않습니다.
Test는 허용 검사에 따라 거절될 수 있지만 실행 핸들러를 호출하지 않습니다.

두 핸들러의 반환은 -1=허용/실행 요청 수락, 0~11=DataAccessError 거절/실패입니다.
정상 거절/실패는 연결을 유지하고 Value/ST stVal을 변경하지 않습니다.
normal 모델은 실행 핸들러 성공 이후에만 Value/ST stVal을 요청 값으로 변경하며,
**실제 장치 상태 피드백이나 동작 완료가 아닙니다.** enhanced 모델은 아래 명시적 긍정 완료까지 변경하지 않습니다.
예외나 범위 밖 반환값은 전파하고 세션을 Faulted로 전환하며 선택을 해제합니다.
외부 부작용은 되돌릴 수 없으므로 실패·예외라도 실제 동작이 없었다고 가정하지 마십시오.

`MmsControlRequest`는 domain/item·Boolean Value·Test·OriginCategory·ControlNumber를 제공합니다.
`GetOrigin()`과 `GetUtcTime()`은 독립 복사본이며, 요청도 수신 버퍼와 독립적이어서 보관할 수 있습니다.
T는 원시 UTC 8 Byte이며 예약 실행·신뢰할 수 있는 시각·인증을 뜻하지 않습니다.
origin과 순환하는 ctlNum 역시 접근 권한·유일한 요청 ID가 아닙니다.

핸들러는 동기로 실행됩니다. **대기/장시간 실행·같은 모델/세션 재진입·정책 변경을 하지 마십시오.**
같은 모델의 제어 재진입과 Enabled/SetHandlers 변경은 InvalidOperationException입니다.
이 검사는 스레드 안전성을 제공하지 않으므로 모든 모델 접근/세션 처리는 여전히 호출자가 직렬화해야 합니다.
기한은 기존 요청 전체 기한이며 실행 중인 핸들러를 강제 중단하지 않습니다.
응답 유실이나 시간 초과 시 이미 실행 요청이 전달되었을 수 있으며, 자동 재전송이나 자동 롤백을 하지 않습니다.
요청당 1회 호출은 재연결이나 새 Direct 요청까지 포함한 중복 방지 또는 정확히 한 번 실행(exactly-once)을 보장하지 않습니다.

### 선택적 LastApplError 송신

```csharp
selected.ReportControlErrors = true;
```

기본 false이므로 기존 단일 Write 응답만 처리하는 평가 피어와의 동작은 바뀌지 않습니다.
**제어 InformationReport를 처리하는 Client에만** 활성화하십시오.
자체 `MmsControlClient`와 원본 libiec61850 high-level ControlObjectClient로 상호운용을 확인했습니다.
일반 scalar `TryWrite`는 제어 진단 수신 경로가 아니므로 CO 명령 쓰기에 사용하지 마십시오.

유효한 Oper/Cancel이 정상 거절되면 **요청 참조·origin·ctlNum을 복사한 LastApplError 1개**,
그다음 정상 Write DataAccessError를 같은 연결/요청 기한으로 송신합니다.
실패 원인별 기본 진단은 다음과 같습니다.

| 거절 이유 | Error | AdditionalCause |
|---|---|---|
| Enabled=false | 0(no-error) | 8(blocked-by-mode) |
| 미선택·선택 만료 | 0 | 18(object-not-selected) |
| 다른 세션이 선택 소유 | 0 | 27(locked-by-other-client) |
| 미지원 Check 또는 Direct Cancel | 0 | 1(not-supported) |
| 허용 검사 거절·실행 핸들러 정상 실패 | 1(unknown) | 0(unknown) |

기존 핸들러 반환은 DataAccessError만 제공하므로 장치별 상세 원인은 unknown으로 표시합니다.
없는 물리 인터록 검사·설비 실패 원인을 임의로 추정하지 않습니다.
구조나 필드 범위가 잘못되면 요청과의 상관관계가 유효하지 않을 수 있으므로 진단을 만들지 않습니다.
성공·Test 수락·정상 Cancel에도 진단을 보내지 않습니다.
핸들러 예외/잘못된 반환값은 기존대로 Faulted·예외 전파이며 정상 거절로 숨기지 않습니다.
진단 자체 또는 후속 응답 송신이 실패하면 세션은 Faulted가 되고 선택을 해제하며 재송신하지 않습니다.

이것은 **normal-security 명령의 거절 진단**이며 일반 DataSet/Report나
enhanced-security CommandTermination과는 구분됩니다.

## Boolean DirectEnhanced·지연 완료

`MmsControlModel.DirectEnhanced`(ctlModel=3)는 **수락 Write 응답 뒤 별도 CommandTermination**을 송신합니다.
수락은 핸들러가 작업을 인계받았다는 의미이며, 애플리케이션이 실제 결과를 판단하여
`Complete(request, success, additionalCause)`로 예약한 뒤 소유 세션의 `ProcessControlCompletion()`이 통지합니다.
normal 모델과 달리 Complete 이전에는 Value/ST stVal이 바뀌지 않습니다.
기본 비활성·핸들러 필수이며 DirectEnhanced의 정상 거절 진단은 ReportControlErrors 설정과 무관하게 송신합니다.

아래는 **물리 출력 없는 50ms 지연 성공 시험**입니다. 실제 출력/실행 완료 감지기는 애플리케이션 책임입니다.

```csharp
var enhanced = new MmsServerControl("NFTESTLD0", "GGIO1$CO$Enhanced",
    MmsControlModel.DirectEnhanced, completionTimeoutMilliseconds: 5000);
MmsControlRequest job = null;
long simulatedReadyAt = 0;
enhanced.SetHandlers(
    delegate(MmsControlRequest request) { return -1; },
    delegate(MmsControlRequest request)
    {
        job = request;
        simulatedReadyAt = Environment.TickCount64 + 50;
        return -1;
    });
enhanced.Enabled = true;
var session = new Iec61850ServerSession(channel, new MmsServerVariable[0],
    controls: new MmsServerControl[] { enhanced });
try
{
    session.Associate();
    while (session.Associated)
    {
        if (job != null && Environment.TickCount64 >= simulatedReadyAt)
        {
            enhanced.Complete(job, true);
            job = null;
        }
        session.ProcessControlCompletion();
        if (socket.Poll(1000, SelectMode.SelectRead))
        {
            if (!session.ProcessRequest()) break;
        }
        else Thread.Sleep(1);
    }
}
finally { session.Close(); }
```

`socket`은 `channel`과 같은 연결이며, Socket API의 Poll 대기 시간은 마이크로초 단위입니다.
호출자가 연결 유휴 기한/취소/소켓 해제도 관리하십시오.
직접 `ProcessRequest()`에 계속 막혀 있으면 지연 완료/기한을 처리할 수 없으므로
**수신 준비 검사와 정기적인 ProcessControlCompletion 호출이 필수**입니다.
별도 타이머·스레드·무한 큐·백그라운드 송신은 만들지 않습니다.

| API/경계 | 계약 |
|---|---|
| `HasPendingOperation` / `HasPendingControl` | 각각 객체/연결에 진행 중 요청이 있는지 조회 |
| `Complete(request, true)` | 핸들러가 받은 동일 요청 객체의 성공 예약, 아직 송신하지 않음 |
| `Complete(request, false, cause)` | 실패 예약, cause는 1~27 중 25 제외, 기본 실패 원인은 호출자가 명시 |
| 완료 기한 | 생성자 1~60000ms(기본 5000), 요청 처리 시각 기준 단조 증가 시각, 완료 처리에서 검사 |
| 기한 경과 | Complete를 예약했어도 송신 처리 전에 만료되면 timeout 부정 완료(addCause=16)를 우선 |
| `ProcessControlCompletion()` | 최대 1개 완료 송신 시 true, 준비되지 않았으면 false, 수신 대기 없음 |
| 진행 중 추가 Oper/Cancel | 연결당/객체당 1개 제한, code=3/addCause=12 거절, 기존 실행은 유지 |
| Test | 허용 검사만 수행하고 긍정 시험 완료를 예약, 실행 핸들러·Value 변경 없음 |
| 정책 변경 | 진행 중 Enabled/SetHandlers/ReportControlErrors 변경 거절 |

Complete는 **실행 핸들러 반환 후**, 다른 세션 처리와 같은 직렬 실행 흐름에서 호출하십시오.
핸들러 내부 완료/세션 재진입·다른 스레드에서 직접 호출은 지원하지 않습니다.
비동기 드라이버의 결과는 호출자가 동기화한 뒤 이벤트 루프로 전달해야 합니다.
이전/다른 요청 객체·중복 Complete는 InvalidOperationException입니다.
Test는 자체 완료되므로 애플리케이션 Complete를 호출하지 않습니다.

긍정 완료만 Value/ST stVal을 갱신하며, 부정 완료·timeout은 이전 값을 유지합니다.
이는 애플리케이션이 보고한 값이지 독립된 센서 피드백이 아닙니다.
정상 부정 완료는 연결을 유지합니다. 완료 송신 오류·EOF·프로토콜 오류·Close는
진행 중 참조와 선택을 해제하며 자동 재송신하지 않습니다.
그 뒤 늦게 호출한 Complete는 거절하지만 **외부 물리 작업을 중단하거나 롤백하지는 않습니다.**
긍정 완료 송신 실패 시 이미 Value와 외부 작업이 변경되었을 수 있습니다.

긍정 통지는 domain-specific `$Oper` 한 항목과 수신 명령 스냅샷,
부정 통지는 VMD LastApplError와 같은 `$Oper` 두 항목을 한 InformationReport에 넣습니다.
수락 Write 응답을 먼저 보낸 뒤 완료를 처리합니다.
이것은 일반 DataSet/Report·시간예약·IEC 62351 구현이 아닙니다.
enhanced-security라는 이름은 암호화/인증 또는 안전 인증을 뜻하지 않습니다.

## Boolean SboEnhanced·SBOw

`MmsControlModel.SboEnhanced`(ctlModel=4)는 `$SBOw` Write로 먼저 선택한 뒤
같은 연결에서 `$Oper`를 수락하고 DirectEnhanced와 같은 방식으로 완료를 통지합니다.
앞 절의 생성자 모델을 SboEnhanced로 바꾸면 같은 이벤트 루프·Complete API를 사용할 수 있습니다.
기본 비활성·두 핸들러 필수이며, 별도 작업 스레드·명령 큐를 추가하지 않습니다.

SBOw에는 Oper와 같은 6개 필드가 있습니다. `$SBOw`와 부모 CO 구조는 형식 조회 전용이며 Read는 접근 거절입니다.
생성된 CF `sboTimeout`(1~60000ms, 기본 1000)·`sboClass`(0=operate-once)와 ST `stVal`은 읽을 수 있습니다.
정상 SBO의 VisibleString `$SBO` 선택과 혼용하지 않습니다.

| 단계 | 동작 |
|---|---|
| SBOw | 허용 검사만 호출, 성공 시 연결 소유권과 명령 스냅샷/기한 저장, 값 변경·실행·완료 통지 없음 |
| 선택 중 재선택 | 같은 소유자는 code=3/addCause=19, 다른 연결은 code=3/addCause=27, 기존 선택 유지 |
| 선택 기한 경과 | 다음 접근 시 해제, Oper/Cancel은 code=3/addCause=18 |
| Oper 일치 검사 | 값·origin category/identifier·ctlNum·Test가 선택과 같아야 함, 불일치 code=3/addCause=26 |
| Check | 선택/실행 모두 false만 지원, true는 code=3/addCause=1 |
| Oper 수락 | 선택을 한 번 소비하고 현재 허용 정책 재검사, non-Test만 실행 핸들러 호출 |
| Cancel | 같은 소유자·선택 명령과 일치하면 선택만 해제, 실행/완료 통지 없음 |
| Test | SBOw와 Oper 모두 허용 검사, 실행 핸들러 없이 자동 긍정 완료, 값 불변 |

각 요청의 UTC T는 달라질 수 있으며 일치 대상이나 시간예약이 아닙니다.
Check=false만 지원하므로 임의의 interlock/synchroCheck 조합을 선택해 실행하는 기능은 없습니다.
필드 불일치/미지원 Check 거절은 기존 선택을 유지하지만, 일치하는 Oper는
후속 허용 검사/실행 요청이 거절되어도 선택을 소비합니다. 자동으로 재선택하지 않습니다.
취소도 값·origin·ctlNum·Test를 검사합니다. 진행 중 Oper 이후 Cancel은 code=3/addCause=12이며
외부 작업 중단을 의미하지 않습니다.

허용 검사는 SBOw와 실제 Oper에서 **각각 다시** 수행하므로 부작용이 없어야 합니다.
두 검사에 전달되는 MmsControlRequest는 서로 다른 스냅샷입니다.
Complete에는 **실행 핸들러가 받은 요청 객체**를 사용하십시오. 선택 검사 요청으로 완료할 수 없습니다.
허용 검사만으로 물리 인터록이나 접근 인증을 구현했다고 간주하지 마십시오.
Enabled=false·핸들러 교체·해당 세션 Close/EOF/오류 시 선택 스냅샷도 해제합니다.
정상 요청 거절은 ReportControlErrors와 무관하게 SBOw/Oper/Cancel에 대응하는 LastApplError를 송신합니다.

기존 연결당/객체당 진행 중 실행 1개 제한을 공유하고, 진행 중인 연결은 추가 SBOw도
code=3/addCause=12로 거절합니다. 선택만으로 실행 슬롯을 차지하거나 완료 기한을 시작하지는 않습니다.
모든 세션/모델 접근과 완료 처리를 직렬화해야 하며 선택은 물리 동작 예약이나 보안 인증이 아닙니다.

### SboEnhanced 최신 실측: 2026-10-26

최종 PC Debug/Release·nanoFramework 빌드·할당 회귀는
`bin\iec61850-sbo-enhanced-regression-final`,
Client/Server 실물은 `bin\iec61850-sbo-enhanced-client-hardware` /
`bin\iec61850-sbo-enhanced-server-hardware`입니다.
원본 libiec61850 v1.6.2 공개 API로 구성한 피어와 양방향 수락·완료·취소·선택 만료를 확인했습니다.
PC에서는 선택 값/출처/번호/Test 일치, 소유권, 재검사 거절, 선택 응답 유실과
EOF/Close 후 해제, 잘못된/조기/중복 완료·전체 기한·선택 요청 객체로 완료 처리 금지도 확인했습니다.

MCU Server는 기존 1,688개 요청에 SBO Enhanced 기본 18개와 스트레스 770개를 더해
**8개 정상 연결·전체 2,476개 요청**을 처리했습니다.
SBOw/Oper 허용 검사 522회·실행 요청 259회·완료 통지 260회이며,
256회 값 교대 선택/실행/Readback 반복에서 ctlNum의 255→0 순환도 포함합니다.
지연 성공·실패(addCause=9)·300ms 완료 기한 초과(addCause=16)·Test·취소·선택 만료를 확인했습니다.
기존 normal 제어 512회와 잘못된 연결 2회·무응답 2회 뒤 복구도 유지했습니다.

GC 후 가용 Heap은 초기 734,880 Byte에서 첫 무응답 뒤 734,628 Byte로,
이후 normal/DirectEnhanced/SboEnhanced 및 256회 반복 종료까지 일정했습니다.
이번 시험앱은 SboEnhanced 객체와 생성된 변수 모델을 추가로 상주시킵니다.
이전 이미지와의 가용 Heap 차이를 명령당 누수나 프로토콜 자체의 고정 버퍼 증가로 해석하지 마십시오.
SBO Enhanced 세션+측정 채널은 6,684 Byte입니다.
PE는 core 26,128 / Server 24,972 / TCP 1,184 / 시험앱 6,960 Byte, BIN 135,860 Byte입니다.
이전 DirectEnhanced 단계 대비 **core +364 / Server +548 / TCP +0 Byte**로 라이브러리 PE 합계 증가는 912 Byte입니다.
새 진단은 **MSS5·32개 UInt32·136 Byte 헤더**이며 MSS4와 혼용하지 마십시오.

수신 요청 경과 카운터는 완료 이벤트 루프·의도한 50ms 지연/300ms 대기를 포함하지 않습니다.
따라서 기록된 처리 시간은 전체 제어 지연이나 완료 통지 CPU 비용이 아닙니다.
PC x64에서 10,000회 선택→실행→완료의 동작당 누적 할당은 긍정 10,248 / 부정 11,264 Byte이며,
SBOw가 없는 DirectEnhanced보다 2,728 Byte 추가였습니다. MCU 순간 할당량과 동일하지 않습니다.
기존 normal/DirectEnhanced·Read/setter의 할당 상한은 유지했습니다.
양쪽 실물 시험 후 원래 Modbus 앱을 복원하고 COM6에서 holding 0..2=123,456,65535를 확인했습니다.
배포/복원 완료와 저장한 이미지 해시·최종 라이브러리 PE 일치를 확인했으며 실제 출력은 연결하지 않았습니다.

### DirectEnhanced 이전 실측: 2026-10-26

근거는 `bin\iec61850-enhanced-regression-final`,
`bin\iec61850-enhanced-client-hardware`, `bin\iec61850-enhanced-server-hardware`입니다.
원본 v1.6.2 공개 API로 만든 피어와 PC/MCU 양방향 수락·지연 성공·실패·timeout·Test·Readback을 확인했습니다.
PC에서는 이전 요청 객체나 중복 요청의 완료 처리 거절·연결별 명령 슬롯·송신 실패·EOF/Close 해제,
Client 완료 상관관계·잘못된/조기/중복 통지·하나의 전체 기한·재전송 없음도 검사했습니다.

MCU Server는 기존 1,678개 요청에 enhanced 연결의 10개 요청을 더해
**6개 정상 연결·전체 1,688개 요청**, enhanced 허용 검사 4회·실행 3회·완료 통지 4회였습니다.
50ms 지연 성공·명시적 실패(addCause=9)·300ms 기한 초과(addCause=16)·Test를 확인했고,
정상 제어 512회 스트레스와 잘못된 연결 2회·무응답 2회 뒤 복구도 유지했습니다.
GC 후 Heap은 초기 742,272 Byte→첫 무응답 뒤 742,020 Byte로 이후 스트레스·enhanced 종료까지 일정했습니다.
세션+측정 채널은 기존 모델 6,732~6,756 Byte, enhanced 연결 6,648 Byte입니다.
PE는 core 25,764 / Server 24,424 / TCP 1,184 / 시험앱 6,444 Byte, BIN 134,432 Byte입니다.
아래 이전 제어 검증 결과와 이미지 구성을 혼동하지 마십시오.
당시 드라이버 진단은 **MSS4·29개 UInt32·124 Byte 헤더**였으며 현재는 MSS5입니다.

기존 수신 요청 경과 카운터는 완료 이벤트 루프/의도한 50ms 지연/300ms 대기를 포함하지 않으므로
enhanced 제어 전체 지연이나 CPU 처리 시간으로 해석하지 마십시오.
MCU Client의 별도 3개 enhanced 연결도 통과했으며 Heap 761,112 Byte를 유지했습니다.
두 실물 시험 모두 원래 Modbus 앱을 복원하고 COM6에서 값을 읽었습니다. 실제 출력은 연결하지 않았습니다.
PC x64에서 10,000회 완료 생명주기 기준 긍정 7,520 Byte/동작·부정 8,536 Byte/동작을 할당했고,
완료 후 진행 중 슬롯이 남지 않는지도 확인했습니다. 이는 MCU 순간 할당량과 동일하지 않습니다.
기존 Read 912 Byte/요청·setter 추가 할당 0 Byte 및 normal 제어 할당 상한은 유지했습니다.

### 제어 추가 후 검증: 2026-10-26

#### 핸들러·상세진단 포함 최신 MCU 검증

최신 실물 근거는 `bin\iec61850-control-diagnostic-hardware`입니다.
원본 libiec61850 high-level Client로 5개 정상 연결·**1,678개 요청**을 처리했습니다.
기존 4회×96개 요청에 별도 1,294개 요청을 더했으며,
추가 연결에서 Direct/SBO 합계 **512회 반복 실행·각 실행 Readback·ctlNum 순환**,
Test 2회 무실행·실행 핸들러 실패 2회와 진단 수신·값 불변을 검사했습니다.
전체 허용 검사 524회·실행 핸들러 호출 522회(실패 2회 포함)로 일치했습니다.
선택 만료 시 addCause=18도 원본 Client가 확인했고,
잘못된 연결 요청 2회·무응답 기한 2회 뒤 복구까지 검사했습니다.
물리 출력은 연결하지 않았으며 finally에서 원래 Modbus 앱을 복원하고 COM6 RTU 값을 확인했습니다.

| 항목 | ESP32-S3 실측 |
|---|---|
| GC 후 가용 Heap | 초기 747,396 Byte, 첫 무응답 뒤 747,144 Byte, 반복 제어 종료까지 유지 |
| 세션+채널 | 6,720~6,744 Byte, 측정용 채널 wrapper 포함 |
| PE 크기 | core 24,708 / Server 23,016 / TCP 1,184 / 시험앱 5,660 Byte |
| 배포 BIN | 131,184 Byte |
| 1,678개 요청 전체 경과 합 | 56,930ms, I/O 호출 내부 39,810ms 포함 |
| I/O 제외 처리 경과 합 / 최대 | 17,120ms / 50ms |
| 반복 제어 연결 1,294개 요청 | 전체 39,840ms, I/O 제외 13,140ms(평균 약 10.2ms/요청) |

시간은 `Environment.TickCount64`로 측정하며 이 보드에서는 10ms 단위입니다.
I/O 제외 값도 GC·스케줄링·시험용 카운터 비용을 포함한 **경과 시간**이지 CPU 사용률이나
최악 응답 기한 보장이 아닙니다. 선택 만료를 위한 원본의 1.2초 대기는 I/O 항목에 포함됩니다.
이 결과는 한 연결을 직렬 처리하는 격리된 평가 환경의 여유를 보여줄 뿐,
다중 연결 처리량·실제 출력 안전성·IEC 61850 성능 등급을 보장하지 않습니다.
당시 진단은 **MSS3·26개 UInt32·112 Byte 헤더**였으며 현재 드라이버는 MSS5입니다.

PC Debug/Release 회귀와 할당량 검사는 `bin\iec61850-control-diagnostic-regression-final`에 기록했습니다.
PC x64에서 10,000회/경로 기준 메모리 실행 2,528 Byte/요청,
핸들러 실행 2,648 Byte/요청, 진단 거절 6,328 Byte/요청입니다.
진단을 켠 성공 경로는 핸들러 실행과 같은 할당량이며, 진단은 정상 거절 경로에서만 추가 인코딩합니다.
이는 PC .NET 할당량으로 MCU의 순간 할당량과 동일시하지 마십시오.
Read 912 Byte/요청과 로컬 setter 추가 할당 0 Byte 계약도 유지합니다.

#### 이전 메모리 제어·핸들러 검증

애플리케이션 핸들러 추가 근거는 `bin\iec61850-control-handlers-regression`입니다.
PC Debug/Release에서 정상/거절/실패·Test 실행 금지·선택 소비·스냅샷 독립성·재진입 거절,
예외/잘못된 반환값의 Faulted 전환·실행 후 응답 송신 실패 시 자동 재실행 없음까지 검사했습니다.
원본 libiec61850 Client가 자체 Server의 등록 핸들러를 통해 Direct/SBO를 실행하는 경로도 통과했습니다.
nanoFramework core/TCP/Server Debug/Release 빌드와 기존 Read 할당 회귀도 통과했습니다.
이 핸들러 추가 단계에서는 보드를 재배포하거나 물리 출력을 연결하지 않았습니다.
아래 실물 수치는 핸들러 추가 전 메모리 제어 검증 결과입니다.

PC/MCU Debug·Release 근거는 `bin\iec61850-control-regression-final`이며
시험앱 Debug도 확인했습니다(Client Debug는 `bin\iec61850-control-regression2`).
원본 PC Client의 high-level ControlObjectClient로 실제 ctlModel/형식을 조회하고
Direct 실행·SBO 선택/실행/취소/만료 거절 후 값 불변·연결 유지를 검사합니다.
PC에서는 두 연결의 선택 경쟁·소유권·연결 종료 해제도 별도로 확인합니다.

최종 실물 근거는 `bin\iec61850-control-server-hardware-complete`입니다.
정상 연결 4회, 연결당 **96개 요청/전체 384개**로 scalar Write·Readback·복합 Read/형식·이름 탐색과
원본 제어 Client의 정상/거절 흐름을 검사했습니다.
일반 변수 4개와 제어 생성 이름 13개로 목록은 17페이지이며,
기존 69개 요청에서 이름 페이지 13개와 제어 관련 14개 요청이 추가됩니다.
이 14개 중 선택 만료를 위한 1.2초 의도적 대기가 있습니다.
따라서 `ReadTotalMilliseconds` 16,950ms/최대 1,530ms는 **다음 요청 대기까지 포함**하며
제어 처리 CPU 시간이나 과거 Read와의 성능 개선율로 해석하면 안 됩니다.
잘못된 CR 2회·무응답 2회 후 복구도 통과했습니다.
당시 진단은 **MSS2·20개 UInt32·88 Byte 헤더**였습니다. 현재 드라이버는 MSS5를 사용합니다.
호스트는 마지막 단계 번호뿐 아니라 완료 status=2까지 기다립니다.

세션+채널 생성 차이는 6,684~6,708 Byte, GC 후 Heap은 747,972→첫 무응답 뒤 747,720 Byte로 이후 일정했습니다.
core/Server/TCP/시험앱 PE는 24,708/20,788/1,184/4,648 Byte, 전체 BIN은 127,944 Byte입니다.
무응답 준비 대기 3,000ms/예외 검사까지 5,260ms로 하드 실시간 기한 보장은 없습니다.
completed·Restored=true·Error=null, 이미지 해시와 원래 Modbus 앱 첫 복원·COM6 첫 읽기를 확인했습니다.
기존 scalar Read PC 할당 912 Byte/요청·setter 추가 할당 0 Byte는 유지했습니다.
제어 모델과 진단/복합 경로에는 추가 할당이 있습니다.

## 정적 DataSet·품질/시각·제한형 URCB

```csharp
var status = new MmsServerVariable("NFTESTLD0", "GGIO1$ST$Status$stVal", true);
var quality = Iec61850ServerData.CreateQuality("NFTESTLD0", "GGIO1$ST$Status$q");
var timestamp = Iec61850ServerData.CreateTimestamp("NFTESTLD0", "GGIO1$ST$Status$t");
var variables = new MmsServerVariable[] { status, quality, timestamp };
var dataSet = new MmsServerDataSet("NFTESTLD0", "GGIO1$Measurements", variables);
var report = new MmsServerReportControl("NFTESTLD0", "GGIO1$RP$Events", "NFREPORT",
    dataSet, configurationRevision: 1, integrityMilliseconds: 1000);
var session = new Iec61850ServerSession(new MmsSocketChannel(accepted), variables,
    dataSets: new MmsServerDataSet[] { dataSet }, reports: new MmsServerReportControl[] { report });
try
{
    session.Associate();
    while (true)
    {
        session.ProcessReports();
        if (accepted.Poll(1000, SelectMode.SelectRead) && !session.ProcessRequest()) break;
        Thread.Sleep(1);
    }
}
finally { session.Close(); }
```

`System.Threading`도 참조하십시오. 실제 값을 갱신하려면 같은 직렬 이벤트 흐름에서
`status.SetBoolean`·`quality.SetQuality`·`timestamp.SetTimestamp`를 호출합니다.
품질은 논리 인덱스 13비트를 MSB-first BitString으로 변환하며 Invalid=2입니다.
UTC는 Unix 기준 시각부터의 UInt32 초·24비트 소수부·시간 품질 바이트로 구성되며 기본 품질 0x3f는 시계 미동기화·정밀도 미지정입니다.
동기화한 시계 여부는 앱이 판단하고 시간 품질을 명시해야 합니다.
로컬 setter는 추가 할당이 없으며 품질/UTC 원격 쓰기는 허용하지 않습니다.

DataSet은 **등록된 변수와 동일한 인스턴스** 1~16개를 참조하며 입력 배열만 복사합니다.
DataSet 전체 값 노드도 256개로 제한합니다. 다중 Read가 이 상한을 넘으면 resource(3)/other(0) 오류이며 연결은 유지합니다.
모델 갱신과 요청 처리를 직렬화한 상태에서 얻은 요청 하나의 스냅샷이며, 물리 입력의 동시 취득을 보장하지 않습니다.
CO/RP 객체를 멤버로 등록할 수 없습니다. DataSet 최대 8개, URCB 최대 4개입니다.
URCB는 parent와 11개 leaf 이름을 생성하므로 각각 12개씩 Server 전체 128개 이름 상한에 포함합니다.
단일 읽기 빠른 경로는 유지하며 다중 읽기·DataSet·보고 인코딩에는 추가 할당이 있습니다.

RCB는 기본 RptEna=false이고 원격 활성화로 세션 하나가 소유합니다.
Resv는 비활성 상태에서 예약/해제할 수 있으며 다른 세션의 활성화·쓰기 요청은 접근 오류 3입니다.
비활성화 시 예약이 없으면 소유권을 해제합니다. EOF·Faulted·명시적 Close 시 소유권·예약·보고 스냅샷을 정리합니다.
모델/RCB를 공유하는 여러 세션도 호출자가 직렬화해야 합니다.
서로 다른 RCB의 RptID는 한 연결 안에서 고유해야 합니다.

고정 속성은 DataSet/RptID/ConfRev/OptFlds/TrgOps/BufTm입니다. 원격 변경은 명시적으로 거절합니다.
OptFlds는 seqNum/reason/DataSet/confRev, TrgOps는 dchg/qchg/integrity/GI이며 BufTm=0입니다.
비활성 상태에서만 IntgPd=0 또는 최소 송신 간격~60000ms를 지정할 수 있습니다.
최소 송신 간격은 생성 시 1~60000ms(기본 100ms)이며 값 변경과 GI를 다음 poll로 모읍니다.
품질 말단 변수의 변경은 qchg, 나머지 멤버의 변경은 dchg입니다. 복합 멤버 내부의 품질 변경을 별도 원인으로 분류하지 않습니다.
GI/integrity는 전체 멤버를 포함합니다. 변경 보고는 마지막 송신 스냅샷과 달라진 멤버만 포함합니다.
poll 사이 여러 변경은 합쳐지며 다시 원래 값으로 돌아온 중간 변화도 보존하지 않습니다.
보고 없는 poll은 추가 할당이 없고, 변경을 새로 인코딩한 경우에는 값이 최종적으로 같아도 할당할 수 있습니다.

**비버퍼형**이므로 단절·비활성 중 이벤트 보존, 재전송, 큐, 분할 보고는 없습니다.
보고가 협상 PDU를 넘거나 송신에 실패하면 예외·Faulted로 종료합니다.
`ProcessReports`는 블록당 보고 최대 1개이며 백그라운드 스레드/타이머를 만들지 않습니다.
서버 앱이 수신 준비를 검사하지 않고 `ProcessRequest`에서 대기하면 GI/integrity/변경 송신이 지연됩니다.
Enhanced 제어도 함께 사용하는 이벤트 루프는 기존 `ProcessControlCompletion`도 계속 호출해야 합니다.
PC [SCL 생성기](../VagabondK.nF.IEC61850.SCL/README.md)는 위 제한을 만족하는 leaf/DataSet/URCB 정적 코드를 생성합니다.

### 확장 후 PC·ESP32-S3 확인

`bin\iec61850-data-regression-verified`에서 Debug/Release MCU 빌드, 기존 제어/쓰기 회귀,
원본 libiec61850 양방향 다중 Read·DataSet·GI 보고·품질/UTC 상호운용을 확인했습니다.
품질 8,192개 조합과 UTC 범위/소수부, DataSet 전체 256노드 경계, 보고 구성/bitmap/reason/노드/후행 데이터 거절,
예약 소유권·해제·변경 합치기·integrity·callback 재진입 및 서비스 대기 중 보고 전달도 검사했습니다.
SCL 예제의 생성 코드는 실제 C# 7.3으로 컴파일하고 초기 값/모델/세션을 확인했습니다.
기존 scalar Read 할당은 **912 Byte/요청**, scalar·품질·UTC setter와 변경 없는 보고 poll은 추가 할당 0 Byte입니다.
보고 생성과 값 스냅샷에는 메모리 할당이 있으므로 전체 통신 경로에 할당이 없다는 뜻은 아닙니다.

`bin\iec61850-data-server-hardware-verified`에서 원본 PC Client 상대
3개 데이터 연결의 27개 요청·GI 보고 3개를 포함하여, 정상 연결 11개·전체 2,503개 요청을 확인했습니다.
잘못된 Association 2개·무응답 기한 2개와 기존 normal/enhanced/SBO enhanced 반복 제어도 포함합니다.
데이터 연결 3회 후 가용 Heap은 모두 731,820 Byte였습니다.
데이터 경로 생성 측정은 12,780 Byte이며 **채널·세션뿐 아니라 변수 5개·DataSet·RCB와 생성 leaf도 포함**합니다.
Socket 전체 비용이나 활성 상태의 최대 순간 Heap 사용량은 이 숫자에 포함하지 않습니다.

core/Server/TCP/시험앱 PE는 33,268/32,428/1,184/7,616 Byte, 전체 BIN은 151,112 Byte입니다.
SboEnhanced 시점 대비 core+Server PE 증가는 **14,596 Byte(약 14.3 KiB)**입니다.
completed·Restored=true·Error=null, 배포 BIN 해시와 최종 라이브러리/앱 PE의 Byte 일치를 확인했습니다.
시험 후 원래 Modbus 앱을 복원하고 COM6에서 holding 0..2=123,456,65535를 읽었습니다.
이 결과는 제한된 모델의 반복 상호운용이며 실제 IED·장시간 운전·설비 안전성이나 적합성 보장은 아닙니다.

## DPC/APC와 정상 종료 확장

`MmsServerControl(..., valueKind: MmsControlValueKind.DoublePoint)`는 Boolean 명령과
`$ST$...$stVal`의 2비트 Dbpos 상태를 제공합니다. 초기 상태는 Intermediate이며
수락된 명령 중 Test가 아닌 명령은 false=Off, true=On을 기록합니다.
`Iec61850ServerData.CreateDoublePoint`/`SetDoublePoint`와
`Iec61850Data.ReadDoublePoint`는 별도 상태 leaf에도 사용하며 setter는 추가 할당이 없습니다.
PC SCL 생성기는 Dbpos와 명시적 초기 상태도 변환합니다.

`valueKind: MmsControlValueKind.AnalogFloat32`는 APC 단일 AnalogueValue.f 명령과
`$MX$...$mxVal` 구조체/`$mxVal$f` leaf를 제공합니다.
현재 값은 `control.AnalogValue`, 핸들러 요청은 `request.ValueKind`/`request.AnalogValue`입니다.
기존 Boolean Value를 APC에서 읽으면 예외입니다. 유한 Float32만 허용하고
값 범위와 운전 허용 정책은 애플리케이션의 검증기에서 구현합니다.
정수형 APC·setMag·아날로그 램프·실물 피드백은 구현하지 않습니다.
네 제어 모델의 기존 기본 비활성·핸들러·Test 무변경·소유권·기한·enhanced 완료 계약을 그대로 적용합니다.

MMS Conclude 뒤 ACSE Release 또는 직접 Session FINISH 요청에 응답한 경우
`ProcessRequest()`는 false, Associated=false/Faulted=false로 종료하고 SBO·URCB 소유권을 해제합니다.
Conclude부터 Release까지 같은 요청 전체 기한을 유지하며 Socket은 호출자가 닫아야 합니다.
지원하지 않는 **정상 confirmed service**에는 invoke가 일치하는 unrecognized-service Reject를 보내고 연결을 유지합니다.
형식 오류를 정상 거절로 숨기지는 않습니다.

### URCB 연속·장애 및 DPC/APC 실물 재실행

```powershell
.\Build\Test-Iec61850ReportHardware.ps1 -AllowDeviceChanges `
  -DurationMinutes 30 -OutputPath .\bin\iec61850-soak-new
.\Build\Test-Iec61850ReportHardware.ps1 -AllowDeviceChanges `
  -TypedControls -OutputPath .\bin\iec61850-typed-server-new
.\Build\Test-Iec61850ReportHardware.ps1 -AllowDeviceChanges `
  -TypedControls -TypedClient -OutputPath .\bin\iec61850-typed-client-new
```

**순차 실행**해야 합니다. COM3의 격리된 ESP32-S3와 저장된 Wi-Fi를 사용하며
각 실행의 finally에서 원래 Modbus 앱을 복원하고 COM6 holding 값을 확인합니다.
출력 폴더는 새 경로여야 하며 배포 이미지·라이브러리 크기/SHA256·진단·클라이언트 로그·복원 결과를 보존합니다.
PC 시험 DLL도 실행별로 복사하여 진행 중인 시험과 후속 빌드의 파일 잠금 충돌을 피합니다.

URCB 시험은 실행 시간을 반으로 나누고 같은 이미지를 한 번 재배포합니다.
각 구간은 최소 6개 정상 연결과 장애 경로를 완료하므로 짧은 지정 시간은 이를 위해 초과할 수 있습니다.
GI·dchg·qchg·integrity, 순번 255→0, 보고/Read 혼합, RST,
활성 예약 상태 FIN과 부분 COTP EOF 이후 같은 모델/RCB의 재접속을 확인합니다.
**소프트웨어 재시작이지 물리 전원 차단 또는 Wi-Fi 링크 차단 시험은 아닙니다.**
6번째 연결의 GC 후 가용 Heap을 기준으로 이후 최소값의 감소를 512 Byte 이내로 제한합니다.
이는 장기 누수 징후 검사이며 순간 최대 메모리·하드 실시간·장기 운전 보장은 아닙니다.

Typed Server 시험은 PC 클라이언트 8개 연결과 원본 libiec61850 클라이언트 1개 연결,
Typed Client 시험은 MMS core/Client/TCP와 IEC core/Client를 참조한 MCU 클라이언트와
원본 서버의 8개 연결을 사용합니다. 두 Server DLL은 포함하지 않습니다.
두 방향 모두 DPC/APC × 네 제어 모델의 실행·Test·읽기·SBO 취소·enhanced 완료를 확인합니다.
진단의 `Reports`는 이 모드에서 확인된 enhanced 완료 수이며,
Typed Client의 `Requests`는 **검사 수이지 실제 MMS 요청 수가 아닙니다.**
실제 보호계전기/IED와의 상호운용 및 정식 적합성은 별도 검증이 필요합니다.

### 확장 후 최종 확인: 2026-10-26

| 실물 시험 | 연결·보고 결과 | GC 후 가용 Heap 기준/최소 |
|---|---|---|
| 기존 DataSet/URCB 이미지 30분 지정, 약 15분씩 2구간 | 정상 연결 225개·부분 프레임 연결 74개, MCU 송신 5,009개/PC 수신 5,005개, 순번 순환·GI/dchg/qchg/integrity·재접속 통과 | 각 구간 752,604/752,604 Byte |
| 최종 확장 이미지 2분 지정, 최소 연결 조건까지 81.109초+72.547초 | 각 구간 정상 연결 6개·부분 프레임 2개, 보고 370개, 예상 RST/부분 프레임 오류 4개 이후 복구 | 각 구간 752,256/752,256 Byte |
| 최종 Typed Server, PC/원본 Client | 9개 연결·MMS 요청 756개·enhanced 완료 108개, 오류 0개 | 706,152/706,152 Byte |
| 최종 Typed Client, 원본 Server | 8개 연결·검사 768개·enhanced 완료 96개, 오류 0개 | 760,272/760,272 Byte |

30분 시험은 **DPC/APC·정상 Release 추가 전의 보존 이미지와 PC DLL**을 사용했습니다.
실행 중 바이너리를 교체하지 않았으며 보존 PC DLL/SHA256과 배포 이미지 해시를 남겼습니다.
송신/수신 차이는 RST 등 강제 종료 구간의 미수신 보고이며 보고 재전송을 보장하지 않습니다.
**최종 제품 이미지의 30분 시험으로 해석하면 안 됩니다.**
최종 이미지에서는 정상 Release를 포함한 단기 재시작 시험과 양방향 typed 제어를 별도로 확인했습니다.
첫 단기 실행에서 warm-up 연결 수가 부족한 것을 검출하여,
최소 6개 정상 연결을 완료하도록 하네스를 보강한 뒤 재실행했습니다.

근거는 `bin\iec61850-report-soak-final`, `bin\iec61850-report-final-verified`,
`bin\iec61850-typed-server-final`, `bin\iec61850-typed-client-complete`입니다.
이전 30분 스크립트의 status.json은 종료 직전 표본이므로 구간 최종 합계는 실행 출력/클라이언트 로그와 구분합니다.
최종 스크립트는 정지 후 마지막 진단도 저장합니다.
모든 성공 실행에서 completed·Restored=true·Error=null과 원래 Modbus 앱 복원,
COM6 holding 0..2=123,456,65535를 확인했습니다.

최종 core/Server/TCP PE는 **35,136/34,396/1,184 Byte**입니다.
이전 DataSet/URCB 이미지 대비 core+Server 증가는 **3,836 Byte(약 3.75 KiB)**입니다.
보고 시험 앱 PE/BIN은 3,912/151,244 Byte,
Typed Server는 4,208/151,540 Byte, core+TCP만 사용하는 Typed Client는 4,376/117,312 Byte입니다.
서로 모델이 다른 앱의 Heap 차이를 라이브러리 단독 비용으로 해석하지 마십시오.

`bin\iec61850-final-regression`에서 Debug/Release 빌드·전체 PC 회귀·원본 양방향 상호운용과 할당 회귀를 확인했습니다.
서비스 거절 후 다음 Read, Reject 이유 0~9와 invoke/context/잘림 경계, 정상 종료의 SBO/진행 제어/URCB 해제도 포함합니다.
최종 PC 보고 루프백은 정상 연결 6개·보고 370개·순번 순환·RST/부분 프레임 복구를 통과했습니다.
단일 Read **912 Byte/요청**, scalar/Dbpos setter **추가 할당 0 Byte**를 유지했습니다.
기존 MCU Client의 TryRead·쓰기·Boolean 제어·보고 회귀와 MCU Server의 2,503개 요청·11개 Association,
잘못된 Association/무응답 기한 각 2개도 재확인했습니다.
Server 회귀의 GC 후 Heap은 기준 732,156/최소 731,904 Byte로 252 Byte 감소했으며,
typed/URCB 전용 모델의 고정 Heap 결과와 구분합니다.
해당 근거는 `bin\iec61850-final-client-regression`, `bin\iec61850-final-server-regression`입니다.

최종 런타임 패키지 3개는 `bin\nuget\iec61850-release-verified`에 생성했습니다.
패키지 자산과 최종 빌드의 Byte 일치, 오프라인 의존성 복원, 소스/ProjectReference 없는 전체·Core·Server·TCP 소비자 빌드를 확인했습니다.
역할별 출력 폴더를 분리하고 Core/Server에 TCP·System.Net PE가 섞이지 않는지,
TCP 소비자에 Server PE가 섞이지 않는지도 검사했습니다.
PC 전용 SCL 도구는 `bin\nuget\iec61850-scl-final`에서 외부 소스를 차단한 로컬 설치와 실제 모델 생성을 확인했습니다.

### 가상 IED 장애 주입과 공유 Client 예제

실제 IED 없이 원본 libiec61850 `typedserver`의 TCP 응답을 시험 채널에서 중계하며
정상 지연, 응답 중간 EOF, 응답 시점 단절, Write 응답 유실, enhanced CommandTermination 유실을 주입합니다.
완성된 TPKT를 읽은 뒤 해당 MMS PDU에만 장애를 적용하므로,
실행 전 요청 유실과 실행 후 응답 유실을 혼동하지 않습니다.
유실 시 Client.Faulted, 기존 연결 재사용 거절, Oper 요청 송신 1회와
새 연결에서 이미 적용된 상태를 확인합니다. 응답이 유실되어도 적용된 변경이 롤백되지는 않습니다.
실제 독립 피어 프로세스의 종료가 활성 연결을 무효화하고,
같은 포트에서 새 프로세스를 시작한 뒤 새 Association/Read가 되는지도 검사합니다.
이는 PC의 결정적 전송 장애 모사이며 물리 Wi-Fi 링크 차단 시험은 아닙니다.

```powershell
dotnet run -c Release --project .\Tests\IEC61850\VagabondK.nF.IEC61850.Tests `
  -- --virtual-faults .\bin\protocol-tools\iec61850-build\Release\VagabondKMmsPeer.exe
dotnet run -c Release --project .\Tests\IEC61850\VagabondK.nF.IEC61850.Tests `
  -- --typed-soak .\bin\protocol-tools\iec61850-build\Release\VagabondKMmsPeer.exe 3600
```

`--interop`에도 장애 시험을 포함합니다.
typed soak는 같은 원본 서버 모델을 재사용하는 NF Client 연결과,
NF Server/원본 Client 반대 방향 연결을 반복하여 DPC/APC × 네 제어 모델을 확인합니다.
주기마다 실행 48개(Test 포함)·enhanced 완료 24개를 검사하고 최소 8주기를 완료합니다.
출력의 managedBaseline/Maximum/Last는 PC 관리 Heap 관측치이며 MCU Heap·무누수 판정값이 아닙니다.

[TCP Client 예제](../../../Samples/IEC61850/VagabondK.nF.IEC61850.TCPClient/README.md)의 세션 코드를
PC 시험에도 그대로 링크합니다. 탐색·DataSet·보고·Test 무변경과,
실제 실행 뒤 응답 유실→재접속에서 핸들러 실행 1회 유지를 확인합니다.
원본 가상 IED의 두 프로세스 수명에서도 보고를 다시 구독합니다.
기본 예제는 읽기 전용이며 제어는 명시적 경로와 Test 정책이 필요합니다.
SCL 생성기의 Enum·기본 비활성 SPC/DPC/APC 제어 변환은
[생성기 범위](../VagabondK.nF.IEC61850.SCL/README.md)를 참조하십시오.

2026-10-26 PC 양방향 typed 1시간 시험은 **2,667주기·제어 요청 128,016개(Test 포함)·
enhanced 완료 64,008개**를 3,601,313ms 동안 통과했습니다.
PC 관리 Heap의 8주기 기준/이후 최대/마지막 관측값은 339,624/347,904/339,056 Byte입니다.
근거는 `bin\iec61850-typed-soak-1h.log`이며 MCU 장시간 보고 시험과는 별개의 PC 제어 시험입니다.
`bin\iec61850-stability-regression`에서 Debug/Release 통합 시험·독립 장애 주입·공유 예제와
기존 912 Byte/Read 할당 회귀를 확인했습니다.
확장 SCL 도구는 `bin\nuget\iec61850-scl-stability`,
런타임 3종 패키지는 `bin\nuget\iec61850-stability`에서 로컬 설치/소비를 검증했습니다.

### 최종 이미지 URCB 2시간 시험과 후속 복원 확인

`bin\iec61850-current-soak-2h`에서 최종 Release 이미지를 약 60분씩 두 구간 실행했습니다.
중간에는 같은 BIN을 재배포했으며 물리 전원/Wi-Fi 차단 시험은 아닙니다.

| 구간 | MCU 연결/송신 보고/예상 장애 | PC 정상/부분 프레임 연결·수신 보고 | GC 후 가용 Heap 기준/최소 |
|---|---|---|---|
| 1 | 601/9,317/308 | 451/150·9,280 | 751,824/751,824 Byte |
| 2 | 595/9,225/307 | 447/149·9,203 | 752,256/752,256 Byte |

합계 1,196개 연결·18,542개 송신 보고와 615개 예상 장애 이후의 복구를 확인했습니다.
PC 수신은 18,483개이며 강제 종료 구간의 차이를 URCB 재전송 보장으로 해석하지 마십시오.
각 구간의 6번째 연결 GC 후 기준에서 이후 최소 가용 Heap 감소는 없었습니다.
이는 해당 모델·기간의 관측 결과이며 최대 RAM 사용량이나 무기한 누수 부재 보장은 아닙니다.
`images.json`의 core/Server/TCP PE는 35,136/34,396/1,184 Byte이고,
보존 배포 BIN은 151,244 Byte입니다. 이후 가상 장애·SCL 도구·예제 확장으로 제품 런타임은 변경하지 않았습니다.

**보고·장애·Heap 검사는 통과했지만 전체 실행은 복원 확인 실패로 failed입니다.**
finally에서 원래 Modbus 이미지를 두 번 정상 배포했으나 COM6 RTU 읽기는 매번 수신 0 Byte로 만료됐습니다.
복원 BIN의 SHA256 일치, COM3/COM6 장치 인식, 이후 디버거의 정상 재부팅 수락과
`ProgramRunning` 상태까지 확인했지만 holding 0..2=123,456,65535는 재확인하지 못했습니다.
RS-485 전원·배선·어댑터 등 실물 경로 확인이 필요하며 원인은 아직 확정하지 않았습니다.
추가 디버거 확인에서 원래 nanoEdge Modbus 앱의 주 스레드가
`ReceiveByLength→TryReadTo→SerialPortChannel.Read→Thread.Sleep` 수신 루프에 있었습니다.
실행 재개도 수락됐으나 응답은 없었으며 PC 어댑터의 RTS/DTR 네 조합도 모두 수신 0 Byte였습니다.
펌웨어 갱신·전체 지우기·설정 초기화는 수행하지 않았습니다.
원본 `status.json`의 failed/Restored=false를 보존하고 후속 진단은 별도 로그와
`recovery-status.json`에 기록했습니다. 통신 성공 전에는 복원 완료로 간주하지 않습니다.

이후 사용자가 USB를 재연결한 뒤 같은 원래 Modbus 앱의 COM6 RTU 9600 8E1 읽기가 성공했습니다.
RTS/DTR=true에서 holding 0..2=123,456,65535를 확인하여 후속 복원 검증을 완료했습니다.
근거는 `restore-after-usb-reconnect.log`와 별도 `recovery-status.json`의 recovered/Restored=true입니다.
최초 실행의 failed/Restored=false는 그대로 보존했으며 USB 재연결 전 무응답의 근본 원인은 확정하지 않았습니다.

## 미지원·안전 경계

일반 복합/다중 변수 쓰기·실제 설비 출력 드라이버·시간예약·동적 명명 변수 목록 생성/삭제·
BRCB·전체 IEC 61850 데이터 모델·ACSE 인증·TLS는 없습니다.
SCL은 별도 PC 생성기의 제한된 정적 leaf/DataSet/URCB 변환만 제공합니다.
형식이 잘못되거나 지원하지 않는 OSI 옵션은 명시적 프로토콜 예외로 세션을 종료할 수 있습니다.
일반 IED Client의 전체 데이터 모델·추가 서비스는 지원하지 않습니다.
Initiate에서 기존 서비스와, DataSet을 등록한 경우 GetNamedVariableListAttributes를 광고합니다.
GOOSE·SV·IEC 62351 보안은 제공하지 않습니다. 격리된 평가 환경에서만 사용하십시오.

## 빌드·PC 상호운용

```powershell
.\Build\Build-Iec61850ValidationPeer.ps1
.\Build\Test-Iec61850.ps1 -OutputPath .\bin\iec61850-server-new
```

PC/MCU Debug·Release에서 기존 Client·core·TCP·Server를 검사합니다.
PC 회귀는 분할 I/O·모든 Association/Read 잘림 위치·잘못된 선택자/컨텍스트·CR·기한·EOF·용량,
invoke ID UInt32 상한·없는 이름·값 업데이트·타입 고정·Faulted/EOF 이후 재사용 거절을 확인합니다.
원본 libiec61850 v1.6.2 Client가 관리 코드 Server에 두 번 연결하여
Boolean·Int32·Float32 60회와 DataAccessError 20회를 확인합니다.
이름 페이지 크기를 1로 제한한 상태에서 원본 Client의 자동 이어받기,
domain 1개·변수 4개·세 scalar 형식·없는 변수 ConfirmedError와 후속 Read도 검사합니다.
네 번째 변수는 Boolean과 Integer 배열을 가진 구조체 `bundle`이며 원본 Client도 형식과 값을 확인합니다.
두 연결은 값이 다른 모델을 사용합니다.
자체 Client의 TPDU128 연결로 Association 분할·응답 재조립도 검사합니다.
작은 PDU/nesting 협상 결과와 optional Read 필드의 허용·거절도 검사합니다.
첫 구현 근거 `bin\iec61850-server-regression-final`에서 구성당 기반 assertion 289,613개,
Client 264개, Server 263개와 실제 Socket·양방향 독립 피어 검사를 통과했습니다.
실물 시험앱 Debug 빌드는 `bin\iec61850-server-regression\hardware-Debug.log`에 기록했습니다.

독립 PC 피어는 원본 `libiec61850 v1.6.2`, revision
`f0f797bf955da763353f7081c892a1ff6b55e5a1`입니다.
MCU 라이브러리에 원본 구현 코드를 복사하거나 링크하지 않습니다.
별도 PC 시험 바이너리의 GPLv3/상용 라이선스 경계와 미확보 정식 규격은
[Client 안내](../VagabondK.nF.IEC61850.Client/README.md)를 참조하십시오.

## ESP32-S3 실물 재실행

```powershell
.\Build\Test-Iec61850ServerHardware.ps1 -AllowDeviceChanges `
  -DeviceAddress 192.168.0.187 -OutputPath .\bin\iec61850-server-hardware-new
```

Geekble Nano ESP32-S3의 COM3 배포와 저장된 Wi-Fi를 사용합니다.
시험앱은 `Tests\IEC61850\VagabondK.nF.IEC61850.Server.Hardware`이며 TCP 18103,
시작·진단 전용 UDP 20004를 사용합니다. 진단 채널은 인증이 없는 시험용입니다.
이 앱은 미리 정한 정상/장애 순서만 처리하는 fixture이며 제품용 listener가 아닙니다.
Wi-Fi 자격 증명·펌웨어·방화벽을 변경하지 않습니다.

현재 원본 Client는 `VagabondKMmsPeer.exe controlverify 18103 <MCU IPv4> <sequence>`로 실행합니다.
각 연결에서 sequence에 따라 Boolean은 홀수=true/짝수=false,
Int32는 `-123456+sequence`, Float32는 `12.5+sequence`를 기대합니다.
현재 제어 포함 변수 목록은 17페이지이며, 제어 확장 이전의 기본 모델 시험은
이름 페이지 크기 1에서 domain 1회·변수 목록 4페이지·형식 3회·없는 형식 1회,
bundle 형식 1회·Read 3회와 10순회 × scalar 3개·없는 변수 1개를 확인합니다.
추가로 세 scalar의 원값 Read·Write·Readback·원값 복원(12회)과
읽기 전용·형식·없는 이름·범위 거절(4회)을 검사하여 연결당 전체 69개 요청이었습니다.
현재는 위 제어 검증을 더해 연결당 96개 요청입니다.
MCU는 각 정상 연결 전에 같은 변수 객체를 업데이트하므로 캐시된 이전 값도 감지합니다.

호스트 드라이버는 원본 revision/수정 여부/identity와 원래 Modbus 복원 이미지 SHA256,
COM3·새 출력 폴더를 검사하고 PC 상호운용·MCU 빌드 후에만 배포합니다.
성공·실패와 관계없이 finally에서 소유 PC Client를 정리하고 원래 Modbus 앱을 복원합니다.
복원 실패 시 경고 후 1회 재시도하고 COM6 holding 0..2=`123,456,65535`까지 확인합니다.
복원 이미지·해시와 수동 복원법은 [Client 실물 안내](../VagabondK.nF.IEC61850.Client/README.md)를 참조하십시오.

### scalar Write 추가 후 실물 결과: 2026-10-26

근거 `bin\iec61850-write-server-hardware`는 completed·Restored=true·Error=null입니다.
정상 연결 4회의 전체 276개 MMS 요청 중 Write 성공 24회·거절 16회이며,
기존 scalar Read 120회·접근 오류 40회·복합 형식 4회/Read 12회도 포함합니다.
쓰기 전 원값과 Readback을 확인하고 복원한 뒤 기존 Read를 계속 검사합니다.
잘못된 CR 2회·무응답 2회 후 복구, 원래 Modbus 앱 첫 복원·COM6 첫 읽기를 통과했습니다.
요청 처리 합계 6,320ms/최대 40ms, 세션+채널 6,468~6,492 Byte,
GC 후 Heap은 758,436→첫 무응답 후 758,184 Byte로 이후 일정했습니다.
core/Server/TCP/시험앱 PE는 20,000/17,352/1,184/4,364 Byte, 전체 BIN은 119,516 Byte입니다.
PC/MCU Debug·Release 최종 근거는 `bin\iec61850-write-regression-final`이며
시험앱 양쪽 Debug는 `bin\iec61850-write-regression2`에 기록했습니다.
기존 scalar Read PC 할당 912 Byte/요청, setter 추가 할당 0 Byte를 유지했습니다.
Write는 값 스냅샷·TLV를 할당합니다. 모델과 요청 수가 달라 과거 결과와 개선율을 직접 비교하지 않습니다.
무응답 준비 대기 3,000ms/예외 검사까지 5,260ms로 하드 실시간 기한은 보장하지 않습니다.

### 구조체·배열 추가 후 실물 결과: 2026-10-26

근거는 `bin\iec61850-composite-server-hardware-final`이며 completed·Restored=true·Error=null입니다.
정상 연결 4회에서 원본 Client가 Boolean·Integer 배열을 포함하는 bundle의 이름/형식/값을 확인하고,
기존 scalar·오류 요청과 반복 장애 복구도 통과했습니다.

| 항목 | 실측 |
|---|---|
| 전체 MMS 요청 | 212회, 정상 연결 4회 |
| 기존 Read | scalar 120회·DataAccessError 40회 |
| 복합 형식/Read | bundle 형식 4회·복합 Read 12회 |
| 나머지 탐색 | domain 4회·변수 목록 16페이지·scalar 형식 12회·없는 형식 4회 |
| 장애·복구 | 잘못된 CR 2회·무응답 2회와 후속 정상 처리 |
| 요청 수신·처리·송신 | 212회 합계 4,890ms, 최대 40ms |
| 세션·채널 생성 차이 | 6,468~6,492 Byte |
| GC 후 가용 Heap | 초기 758,832 Byte, 첫 무응답 뒤 758,580 Byte, 이후 일정 |
| 무응답 | 준비 대기 3,000ms·예외 검사까지 5,260ms |
| 배포 PE | core 18,996 / Server 16,072 / TCP 1,184 / 시험앱 4,168 Byte |
| 전체 BIN | 117,036 Byte |
| 원래 앱 복원 | 첫 배포·COM6 첫 읽기 성공 |

탐색 버전 대비 core PE는 1,784 Byte, Server PE는 948 Byte 증가했습니다.
모델/시험앱/처리 요청도 달라졌으므로 과거 결과와 직접 지연/상주 RAM 개선율을 비교하지 않습니다.
첫 무응답 뒤 252 Byte 감소와 이후 일정한 패턴은 기존과 같으며 장시간 누수 부재를 보증하지 않습니다.
`Reads`와 시간 진단 필드는 계속 전체 요청을 집계합니다.
PC/MCU Debug·Release 회귀와 시험앱 Debug 빌드 근거는 `bin\iec61850-composite-regression-final`이며,
scalar Read 요청당 PC 할당 912 Byte와 scalar setter 추가 할당 0 Byte는 유지했습니다.
복합 모델 생성·복합 응답의 무할당을 주장하지 않습니다.
Client 실물 결과는 [Client 안내](../VagabondK.nF.IEC61850.Client/README.md)를 참조하십시오.

### 탐색·형식 조회 추가 후 실물 결과: 2026-10-26

최종 근거는 `bin\iec61850-discovery-server-hardware-final`이며
`status.json`은 completed·Restored=true·Error=null입니다.
고정 모델 3개에서 `maximumNamesPerPage: 1`로 제한하여
원본 libiec61850 Client가 세 변수 이름을 이어받아 모두 검증하도록 했습니다.

| 항목 | 실측 |
|---|---|
| 정상 연결·변경 모델 | 4회, sequence 1~4 |
| 전체 MMS 요청 | 192회: 기존 Read 160회 + 탐색/형식/서비스 오류 32회 |
| 새 서비스 세부 | domain 4회·변수 목록 12페이지·scalar 형식 12회·없는 형식 ConfirmedError 4회 |
| 기존 Read | scalar 120회·DataAccessError 40회, 서비스 오류 이후에도 정상 처리 |
| 장애·복구 | 잘못된 CR 2회·무응답 2회, 각각 후속 정상 연결 |
| 요청 수신·처리·송신 | 192회 합계 4,190ms, 최대 40ms |
| 정상 Association | 30~80ms |
| 세션·채널 생성 비용 | GC 후 차이 6,456~6,480 Byte |
| GC 후 가용 Heap | 초기 759,840 Byte, 첫 무응답 뒤 759,588 Byte, 이후 반복·복구에서 일정 |
| 무응답 | 준비 대기 3,000ms, 예외 검사까지 5,260ms |
| 실제 배포 출력 PE | core 17,212 / Server 15,124 / TCP 1,184 / 시험앱 4,024 Byte |
| 전체 배포 BIN | 114,160 Byte |
| 복원 | 첫 배포 성공, COM6 첫 읽기에서 원래 Modbus 값 정상 |

진단의 기존 `Reads`·`ReadTotalMilliseconds`·`ReadMaximumMilliseconds` 필드는 호환된 위치를 유지하지만
이번 버전에서는 **탐색을 포함한 전체 요청 수/시간**입니다.
따라서 이전 Read 전용 평균과 직접 비교해 지연 개선율을 주장하지 않습니다.
첫 무응답 뒤 252 Byte가 감소하고 이후 일정한 패턴은 기존 시험과 같습니다. 감소 원인이나 장시간 누수 부재는 확정하지 않았습니다.
최적화 후 Read 전용 버전보다 core PE는 4,248 Byte, Server PE는 1,820 Byte 증가했습니다.
추가 기능의 코드 크기 비용이며, 탐색 호출은 무할당 경로가 아닙니다.

최종 PC/MCU Debug·Release 근거는 `bin\iec61850-discovery-regression-final`입니다.
Client 352개·Server 7,028개·기반 289,613개 assertion과 양방향 원본 피어를 통과했고,
기존 Read 요청당 PC 할당 912 Byte·setter 추가 할당 0 Byte도 유지했습니다.
다중 domain 정렬/중복 제거·없는 continuation·빈 최종 페이지·64자 이름 32개를
PDU1024에 실제 3페이지로 나누기·작은 PDU에 이름 하나도 못 넣는 오류·잘못된 요청의 Faulted 전환을 포함합니다.
Client 실물 결과는 [Client 안내](../VagabondK.nF.IEC61850.Client/README.md)를 참조하십시오.

### 첫 반복 장애 완료: 2026-10-26

`bin\iec61850-server-hardware-final4`의 `status.json`은
`completed`·`Restored=true`·`Error=null`입니다.
`measurements.jsonl`·`native-*.log`·`sizes.json`·`deployed-image.bin`·복원 로그를 보존합니다.

| 항목 | 실측 |
|---|---|
| 정상 연결·변경 모델 | 4회, sequence 1~4의 서로 다른 Boolean·정수·실수 |
| 실제 Read | 총 160회: scalar 120회, DataAccessError 40회 |
| 장애와 복구 | 잘못된 TPKT/CR 2회, 연결 후 무응답 2회, 각각 이후 정상 연결 |
| 정상 Association | 40~70ms |
| Read 수신·처리·송신 | 160회 합계 3,970ms, 평균 24.81ms, 최대 40ms |
| 세션·채널 생성 비용 | GC 후 차이 6,444~6,468 Byte |
| GC 후 가용 Heap | 정상 초기 760,068 Byte, 첫 무응답 처리 후 759,816 Byte, 이후 반복/복구에서 일정 |
| 무응답 | 준비 대기 3,000ms, 예외 검사 완료까지 5,260ms |
| 실제 배포 출력 PE | core 12,964 Byte, Server 12,788 Byte, TCP 1,184 Byte, 시험앱 4,004 Byte |
| 전체 배포 BIN | 107,556 Byte, 시험앱·nanoFramework 참조 포함 |
| 원래 앱 복원 | 첫 배포 COM3 연결 실패(E2001), 1회 재시도 성공, COM6 첫 읽기 정상 |

Read 시간은 서버의 요청 대기부터 응답 송신까지이며 순수 처리 시간이나 PC Client의 왕복 시간을 뜻하지 않습니다.
가용 Heap은 전체 시험앱·소켓·진단 스레드를 포함한 시점 측정입니다.
첫 무응답 처리 뒤 252 Byte가 감소한 원인은 확정하지 않았으며, 두 번째 장애 순환에서는 추가 감소가 없었습니다.
이를 누수 부재·장시간 안정성·최대 RAM 사용량 보장으로 해석하지 마십시오.
세션 생성 비용에는 연결된 Socket 자체와 Wi-Fi native 메모리가 포함되지 않습니다.
PE는 실제 배포 출력 폴더 기준이며 BIN을 Server DLL 단독 크기로 보지 마십시오.
다른 펌웨어·일반 IED Client·Edition 전체에 대한 호환성/적합성 인증 결과가 아닙니다.

### Server 할당 최적화: 2026-10-26

이름 문자열/문자 배열 생성, 중첩 TLV 응답 배열 생성과 복사, setter의 매번 배열 생성을 제거했습니다.
변수 검색은 최대 128개 모델의 선형 검색을 유지하며, 별도 검색 인덱스나 추가 세션 버퍼는 도입하지 않았습니다.
고정 scalar 응답은 UInt32 invoke ID 최댓값을 포함해 최소 TPDU128 안에 들어가므로
이미 소비한 수신 패킷 버퍼를 사용합니다. Association 분할·재조립 경로는 그대로입니다.
setter와 세션 접근은 여전히 직렬화해야 하며 버퍼를 공유하는 동시 업데이트는 허용하지 않습니다.

PC .NET 10 Release에서 준비된 요청을 재생하고, 1,000회 예열 뒤 각 10,000회 반복하여
`GC.GetAllocatedBytesForCurrentThread`로 측정했습니다. 채널은 실제 네트워크 대신 미리 만든
Boolean·Int32·Float32·없는 이름 요청을 순환하며 송신 버퍼를 보관하지 않습니다.
setter 계측은 매회 세 타입을 모두 변경합니다. 준비·Association·콘솔 출력 비용은 측정에서 제외합니다.

| 항목 | 최적화 전 | 최적화 후 |
|---|---:|---:|
| 요청당 PC managed 할당 | 2,360 Byte | 912 Byte |
| Read 10,000회 총 할당 | 23,600,000 Byte | 9,120,000 Byte |
| setter 3종 × 10,000회 할당 | 3,920,000 Byte | 0 Byte |
| 실물 Read 대기·처리·송신 평균 | 24.81ms | 23.81ms |
| 실물 Read 최대 | 40ms | 40ms |
| 실물 세션·채널 생성 비용 | 6,444~6,468 Byte | 6,444~6,468 Byte |
| 실물 최종 GC 후 가용 Heap | 759,816 Byte | 759,336 Byte |
| Server PE | 12,788 Byte | 13,304 Byte |
| 전체 배포 BIN | 107,556 Byte | 108,072 Byte |

**요청당 PC 할당은 약 61.4% 감소했고 setter의 정상 반복 추가 할당은 0 Byte입니다.**
PC 할당량은 MCU의 객체 크기나 native 메모리를 의미하지 않습니다.
PC 처리 시간도 재생 채널/JIT/타이머의 영향을 받으므로 MCU 지연 개선율로 환산하지 않습니다.
실물 평균의 1ms 차이는 네트워크·10ms 타이머 변동을 포함한 한 번씩의 짧은 시험으로,
유의한 지연 개선이라고 주장하지 않습니다.
이번 개선은 **할당/GC 부담 감소**이며 총 상주 RAM 감소나 모든 호출의 가속을 보장하지 않습니다.
변수별 고정 인코딩 저장과 코드 변경의 비용이 있고, Server PE는 516 Byte 증가했습니다.
시험앱 전체 가용 Heap은 480 Byte 작게 관측됐으며 그 차이를 일으킨 원인을 모두 확정하지는 않았습니다.

최적화 후에도 정상 연결 4회, scalar 120회·DataAccessError 40회,
잘못된 프레임·무응답 접속 각각 2회와 후속 복구를 동일하게 완료했습니다.
처음 무응답 처리 뒤 252 Byte 감소, 이후 안정 패턴도 같았습니다.
준비 대기 3,000ms·예외 처리 포함 5,260ms는 그대로이며 Timeout 예외 비용은 개선하지 않았습니다.
원래 Modbus 앱은 첫 복원 배포와 COM6 첫 읽기에서 정상 복원했습니다.

전후 PC 계측은 `bin\iec61850-server-perf-before.log`, `bin\iec61850-server-perf-after.log`,
최종 PC/MCU Debug·Release 및 시험앱 Debug 빌드는 `bin\iec61850-server-optimized-regression`,
실물 근거는 `bin\iec61850-server-optimized-hardware`에 있습니다.
실물 전후는 같은 원본 Client 바이너리·시험앱 소스·순서를 사용하되 Server DLL과 배포 이미지가 다릅니다.
기능 회귀는 invoke ID·Int32 길이 변화·Float32 ±0/극값/비정규/무한대/NaN과 없는 이름 응답을
기존 TLV 인코더로 만든 기대 Byte와 대조하며, 구성당 Server assertion 6,888개를 통과했습니다.

다음 명령으로 PC 할당 회귀만 실행할 수 있습니다.
전체 `Test-Iec61850.ps1`도 각 구성에서 같은 검사를 실행하며, 요청당 1,000 Byte 초과 또는
반복 setter 추가 할당이 발생하면 실패합니다. 시간 수치는 환경 변동 때문에 통과 기준으로 사용하지 않습니다.

```powershell
dotnet run -c Release --project `
  .\Tests\IEC61850\VagabondK.nF.IEC61850.Tests\VagabondK.nF.IEC61850.Tests.csproj `
  -- --server-perf
```
