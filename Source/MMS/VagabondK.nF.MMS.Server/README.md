# 일반 MMS Server

[`VagabondK.nF.MMS`](../VagabondK.nF.MMS/README.md)만 참조하는 고정 모델 Server입니다.
소켓 수락·채널 수명·모델 업데이트·세션 처리는 호출자가 직렬화합니다.
MMS.Client DLL은 참조하지 않으며 일반 Client 없이 Server만 사용할 수 있습니다.

```csharp
var value = new MmsServerVariable("Plant", "Temperature", 42);
value.EnableWrite(candidate => candidate.Integer < 0 ? 11 : -1);
var list = new MmsServerNamedVariableList("Plant", "Measurements", new[] { value });
var session = new MmsServerSession(channel, new[] { value }, namedVariableLists: new[] { list });
try
{
    session.Associate();
    while (session.ProcessRequest())
    {
        // 송신 정책은 호출자가 결정하며 별도 이벤트 버퍼를 제공하지 않습니다.
        session.SendInformationReport(list);
    }
}
finally { session.Close(); }
```

`VagabondK.nF.MMS`와 `VagabondK.nF.MMS.Server` 네임스페이스를 사용합니다.
명명 변수 목록이 필요 없으면 `new MmsServerSession(channel, variables)`로 생성합니다.
2~5개 인수 오버로드는 뒤에서부터 제한 인수를 생략할 수 있으며 기본값은
PDU 4096 Byte, 기한 10000ms, 페이지당 이름 16개입니다.
예를 들어 `new MmsServerSession(channel, variables, 2048, 5000, 8)`로 모두 지정할 수 있습니다.
이 오버로드는 nanoFramework 메타데이터 최소화 시 목록 형식 참조 오류를 피합니다.
중간 제한 인수를 생략하는 명명 인수 호출은 기존 6개 인수 생성자를 선택하므로,
nanoFramework에서는 앞선 제한 인수도 지정하십시오. 기존 `namedVariableLists:` 호출은 변경하지 않아도 됩니다.
변수 최대 128개, 명명 변수 목록 최대 8개·각 최대 16개 참조이며 등록된 동일 변수 인스턴스를 참조해야 합니다.
Boolean/Int32/Float32와 고정 복합 모델의 읽기·형식 조회·이름 탐색, 스칼라 쓰기, 정적 목록 조회·읽기,
정상 Conclude/Release와 일반 InformationReport 송신을 지원합니다.
원격 쓰기는 기본적으로 거절하며 EnableWrite의 검증은 부작용과 대기가 없는 범위 검사입니다.
`EnableWrite(validator, handler)`는 형식/범위·앱 검증을 통과한 뒤 모델 반영기를 한 번 호출하고,
그 반영기가 -1을 반환해야 MMS 메모리 값을 갱신합니다. 0~11은 거절이며
잘못된 반환값이나 예외가 발생하면 세션을 Faulted 상태로 전환하고 예외를 전파합니다. 부작용을 롤백하거나 자동 재시도하지 않습니다.
설비 제어 API가 아닌 일반 모델 setter 연결용이며,
[정적 객체 매핑 생성기](../VagabondK.nF.MMS.Mapping/README.md)가 이 경로를 사용합니다.
로컬 반복 게시에는 `UpdateBoolean/UpdateInteger/UpdateFloatingPoint`를 사용하면
전송 값이 같은 경우 버퍼·변경 버전 갱신을 생략합니다. Float32는 +0/-0 비트를 구별합니다.
이 메서드는 쓰기 검증기/반영기를 호출하지 않으며 기존 `Set*`/원격 Write 의미는 바꾸지 않습니다.
InformationReport는 등록된 목록에 대해서만 송신할 수 있습니다. 보고가 협상된 PDU 크기를 넘으면 예외를 전파하고 Faulted 상태로 종료합니다.

IEC 61850 제어 선택/실행/완료·URCB·품질/UTC 의미는 없습니다.
그 기능은 별도 `VagabondK.nF.IEC61850.Server`가 이 세션을 확장합니다.
일반 세션은 `$CO$`나 `$RP$`가 들어 있는 이름도 특별 취급하지 않습니다.
통신 실패 뒤 응답만 유실되어 값이 이미 변경됐을 수 있으며 자동 재시도하지 않습니다.
