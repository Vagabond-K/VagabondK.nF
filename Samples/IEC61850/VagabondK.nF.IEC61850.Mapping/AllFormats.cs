using VagabondK.nF.IEC61850;
using VagabondK.nF.IEC61850.Mapping;
using VagabondK.nF.IEC61850.Server;

namespace VagabondK.nF.IEC61850.Mapping.Sample
{
    // 일반 스칼라 쓰기와 상태·품질·시각 매핑, SPC/DPC/APC의 서로 다른 제어 모델을 함께 보여 줍니다.
    // 공통 Domain은 Point에만 적용됩니다. TimeQuality 멤버를 생략한 UTC는 기본 0x3f를 사용합니다.
    // 아래 실행기는 요청만 기록하므로 enhanced 모델은 호출자가 실제 결과를 확인한 뒤 Complete해야 합니다.
    [Iec61850ServerMap(Domain = "P")]
    internal partial class TypedLocal
    {
        [Iec61850Point("Value", Writable = true)] public bool Value = true;
        [Iec61850Point("Count", Writable = true)] public int Count = -42;
        [Iec61850Point("Temperature", Writable = true)] public float Temperature = 12.5f;
        [Iec61850Point("Position")] public IecDoublePoint Position = IecDoublePoint.On;
        [Iec61850Point("Quality")] public IecQuality Quality = IecQuality.Good;
        [Iec61850Point("Time")] public long Time = 1700000000000L;
        public int Executions { get; private set; }
        public MmsControlRequest Pending { get; private set; }
        public int PendingIndex { get; private set; }
        private int Check(MmsControlRequest request) { return -1; }
        private int Apply(MmsControlRequest request, int index)
        { Executions++; Pending = request; PendingIndex = index; return -1; }
        [Iec61850Control("P", "L$CO$SPC", MmsControlModel.DirectNormal, Validate = nameof(Check))]
        private int Spc(MmsControlRequest request) { return Apply(request, 0); }
        [Iec61850Control("P", "L$CO$DPC", MmsControlModel.SboNormal, ValueKind = MmsControlValueKind.DoublePoint, Validate = nameof(Check))]
        private int Dpc(MmsControlRequest request) { return Apply(request, 1); }
        [Iec61850Control("P", "L$CO$DPC2", MmsControlModel.DirectEnhanced, ValueKind = MmsControlValueKind.DoublePoint, Validate = nameof(Check))]
        private int Dpc2(MmsControlRequest request) { return Apply(request, 2); }
        [Iec61850Control("P", "L$CO$APC", MmsControlModel.SboEnhanced, ValueKind = MmsControlValueKind.AnalogFloat32, Validate = nameof(Check))]
        private int Apc(MmsControlRequest request) { return Apply(request, 3); }
    }

    [Iec61850ClientMap(Domain = "P")]
    [Iec61850RemoteControl("P", "L$CO$SPC")]
    [Iec61850RemoteControl("P", "L$CO$DPC", ValueKind = MmsControlValueKind.DoublePoint)]
    [Iec61850RemoteControl("P", "L$CO$DPC2", ValueKind = MmsControlValueKind.DoublePoint)]
    [Iec61850RemoteControl("P", "L$CO$APC", ValueKind = MmsControlValueKind.AnalogFloat32)]
    internal partial class TypedRemote
    {
        [Iec61850Point("Value", Writable = true)] public bool Value { get; set; }
        [Iec61850Point("Count", Writable = true)] public int Count { get; set; }
        [Iec61850Point("Temperature", Writable = true)] public float Temperature { get; set; }
        [Iec61850Point("Position")] public IecDoublePoint Position { get; private set; }
        [Iec61850Point("Quality")] public IecQuality Quality { get; private set; }
        [Iec61850Point("Time")] public long Time { get; private set; }
    }
}
