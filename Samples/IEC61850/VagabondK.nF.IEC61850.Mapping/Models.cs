using VagabondK.nF.IEC61850;
using VagabondK.nF.IEC61850.Mapping;
#if !CLIENT_ONLY
using VagabondK.nF.IEC61850.Server;
#endif

namespace VagabondK.nF.IEC61850.Mapping.Sample
{
#if !CLIENT_ONLY
    // origin 범주는 예제 정책일 뿐 인증이 아닙니다. 실행기는 메모리만 변경하고 enhanced 완료는 별도로 통지합니다.
    [Iec61850ServerMap(Domain = "NFTESTLD0")]
    internal partial class LocalData
    {
        [Iec61850Point("GGIO1$ST$Status$stVal")]
        public bool Status = true;
        [Iec61850Point("GGIO1$ST$Status$q")]
        public IecQuality Quality = IecQuality.Good;
        [Iec61850Point("GGIO1$ST$Status$t", TimeQuality = nameof(TimeQuality))]
        public long Time = 1700000000000L;
        public byte TimeQuality = 0x3f;
        [Iec61850Point("GGIO1$ST$Position$stVal")]
        public IecDoublePoint Position = IecDoublePoint.On;
        public int Executions { get; private set; }
        public MmsControlRequest Pending { get; private set; }
        private int Check(MmsControlRequest request) { return request.OriginCategory == 3 ? -1 : 3; }
        [Iec61850Control("NFTESTLD0", "GGIO1$CO$Switch", MmsControlModel.DirectEnhanced, Validate = nameof(Check))]
        private int Execute(MmsControlRequest request) { Executions++; Status = request.Value; Pending = request; return -1; }
    }
#endif
#if !SERVER_ONLY
    [Iec61850ClientMap(Domain = "NFTESTLD0")]
    [Iec61850RemoteControl("NFTESTLD0", "GGIO1$CO$Switch")]
    internal partial class RemoteData
    {
        [Iec61850Point("GGIO1$ST$Status$stVal")]
        public bool Status { get; private set; }
        [Iec61850Point("GGIO1$ST$Status$q")]
        public IecQuality Quality { get; private set; }
        [Iec61850Point("GGIO1$ST$Status$t", TimeQuality = nameof(TimeQuality))]
        public long Time { get; private set; }
        public byte TimeQuality { get; private set; }
        [Iec61850Point("GGIO1$ST$Position$stVal")]
        public IecDoublePoint Position { get; private set; }
    }
#endif
}
