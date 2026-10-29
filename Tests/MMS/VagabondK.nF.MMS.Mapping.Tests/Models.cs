using VagabondK.nF.MMS.Mapping;
using VagabondK.nF.IEC61850;
using VagabondK.nF.IEC61850.Mapping;
using VagabondK.nF.IEC61850.Server;

// 응답 유실 전에 setter가 실행된 횟수를 기록하여 재시도가 중복 부작용을 낳을 수 있음을 검증합니다.
[MmsServerMap]
internal partial class LostResponseModel
{
    private int value;
    internal int Executions { get; private set; }
    [MmsPoint("P", "Value", Writable = true)]
    public int Value { get { return value; } private set { this.value = value; Executions++; } }
}
