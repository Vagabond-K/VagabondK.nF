using VagabondK.nF.DNP3.Mapping;

namespace VagabondK.nF.DNP3.MasterMapping.Sample
{
    // 인덱스 1의 Analog 계열을 float로 받는 보조 모델입니다. 수신 Double이 Single 범위를 벗어나면 매핑이 거부됩니다.
    [Dnp3MasterMap]
    internal partial class FloatData
    {
        [AnalogInput(1)]
        public float Measurement { get; private set; }
        [AnalogOutput(1)]
        public float Output { get; private set; }
        public int Fragments { get; private set; }
        [Dnp3Received]
        private void Received(Dnp3Measurement[] measurements, Dnp3ResponseInfo response) { Fragments++; }
    }
}
