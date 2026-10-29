using VagabondK.nF.DNP3.Mapping;

namespace VagabondK.nF.DNP3.Mapping.Sample
{
    // 일곱 Point 종류와 제어 매핑의 예입니다. 제어 콜백은 물리 출력을 구동하지 않으며 상태 게시도 자동으로 수행하지 않습니다.
    [Dnp3Map]
    internal partial class DeviceData
    {
        public byte Flags { get; set; } = 1;
        public long Timestamp { get; set; } = 1700000000000L;

        [BinaryInput(0, Flags = nameof(Flags), Timestamp = nameof(Timestamp))]
        public bool Contact { get; set; } = true;

        [DoubleBitInput(0, Flags = nameof(Flags), Timestamp = nameof(Timestamp))]
        public byte State { get; set; } = 2;

        [Counter(0, Flags = nameof(Flags), Timestamp = nameof(Timestamp))]
        public uint Total { get; set; } = 1000;

        [FrozenCounter(0, Flags = nameof(Flags), Timestamp = nameof(Timestamp))]
        public uint Frozen { get; set; } = 999;

        [AnalogInput(0, Deadband = 0.5, Flags = nameof(Flags), Timestamp = nameof(Timestamp),
            StaticVariation = 6, EventVariation = 8)]
        public double Temperature { get; set; } = 250;

        [BinaryOutput(0, Flags = nameof(Flags), Timestamp = nameof(Timestamp))]
        public bool Enabled { get; private set; } = true;

        [AnalogOutput(0, Flags = nameof(Flags), Timestamp = nameof(Timestamp),
            StaticVariation = 4, EventVariation = 8)]
        public double Setpoint { get; private set; } = 12.5;

        private Dnp3CommandStatus CheckRelay(Dnp3Command command)
        {
            // 펄스 시간과 Trip/Close 의미를 임의의 bool 대입으로 대체하지 않습니다.
            return (command.ControlCode == 3 || command.ControlCode == 4) && command.Count == 1 ?
                Dnp3CommandStatus.Success : Dnp3CommandStatus.NotSupported;
        }

        [Dnp3Control(12, 0, Validate = nameof(CheckRelay))]
        private Dnp3CommandStatus Relay(Dnp3Command command, bool selected)
        {
            Enabled = command.ControlCode == 3;
            Timestamp++;
            return Dnp3CommandStatus.Success;
        }

        private Dnp3CommandStatus CheckAnalog(Dnp3Command command)
        {
            return command.AnalogValue >= -1000 && command.AnalogValue <= 1000 ?
                Dnp3CommandStatus.Success : Dnp3CommandStatus.OutOfRange;
        }

        [Dnp3Control(41, 0, Validate = nameof(CheckAnalog))]
        private Dnp3CommandStatus Analog(Dnp3Command command, bool selected)
        {
            Setpoint = command.AnalogValue;
            Timestamp++;
            return Dnp3CommandStatus.Success;
        }
    }
}
