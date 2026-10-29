using VagabondK.nF.DNP3.Mapping;

namespace VagabondK.nF.DNP3.MasterMapping.Sample
{
    // 각 Point의 값·품질·시각 저장소를 분리합니다. 수신 콜백은 모델 반영 후 공유 잠금 안에서 호출됩니다.
    [Dnp3MasterMap]
    internal partial class ReceivedData
    {
        [BinaryInput(0, Flags = nameof(ContactFlags), Timestamp = nameof(ContactTime))]
        public bool Contact { get; private set; }
        public byte ContactFlags { get; private set; }
        public long ContactTime { get; private set; }
        [DoubleBitInput(0, Flags = nameof(StateFlags), Timestamp = nameof(StateTime))]
        public byte State { get; private set; }
        public byte StateFlags { get; private set; }
        public long StateTime { get; private set; }
        [Counter(0, Flags = nameof(TotalFlags), Timestamp = nameof(TotalTime))]
        public uint Total { get; private set; }
        public byte TotalFlags { get; private set; }
        public long TotalTime { get; private set; }
        [FrozenCounter(0, Flags = nameof(FrozenFlags), Timestamp = nameof(FrozenTime))]
        public uint Frozen { get; private set; }
        public byte FrozenFlags { get; private set; }
        public long FrozenTime { get; private set; }
        [AnalogInput(0, Flags = nameof(TemperatureFlags), Timestamp = nameof(TemperatureTime))]
        public double Temperature { get; private set; }
        public byte TemperatureFlags { get; private set; }
        public long TemperatureTime { get; private set; }
        [BinaryOutput(0, Flags = nameof(EnabledFlags), Timestamp = nameof(EnabledTime))]
        public bool Enabled { get; private set; }
        public byte EnabledFlags { get; private set; }
        public long EnabledTime { get; private set; }
        [AnalogOutput(0, Flags = nameof(SetpointFlags), Timestamp = nameof(SetpointTime))]
        public double Setpoint { get; private set; }
        public byte SetpointFlags { get; private set; }
        public long SetpointTime { get; private set; }
        public int Fragments { get; private set; }
        public int Events { get; private set; }
        public int Unmapped { get; private set; }
        public IDnp3MasterHandler Observer { get; set; }

        // 원본 Fragment에는 미매핑 Point도 포함되므로 통계를 내고 별도 float 모델에도 전달할 수 있습니다.
        [Dnp3Received]
        private void Received(Dnp3Measurement[] measurements, Dnp3ResponseInfo response)
        {
            Fragments++;
            for (int i = 0; i < measurements.Length; i++)
            {
                if (measurements[i].IsEvent) Events++;
                if (measurements[i].Index != 0) Unmapped++;
            }
            if (Observer != null) Observer.OnMeasurements(measurements, response);
        }
    }
}
