namespace VagabondK.nF.Modbus.Mapping.Sample
{
    /// <summary>원격 상태와 설정을 서로 다른 읽기 주기로 갱신하는 Client 모델입니다.</summary>
    /// <remarks>주소는 0 기반이며 Temperature는 하위 워드가 먼저 오는 두 Register입니다. Writable은 자동 송신이 아니라 명시적 쓰기 메서드를 생성합니다.</remarks>
    [ModbusMap(ModbusMapRole.Client, MaxReadRegisters = 2, MaxReadBits = 8, BufferBudgetBytes = 5)]
    public partial class RemoteData
    {
        [InputRegister(100, WordOrder = ModbusWordOrder.LowWordFirst, ReadGroup = "Status")]
        public float Temperature { get; private set; }

        [InputRegister(102, ReadGroup = "Status")]
        public short Status { get; private set; }

        [HoldingRegister(200, Writable = true, ReadGroup = "Settings")]
        public int Setpoint { get; set; }

        [Coil(10, Writable = true, ReadGroup = "Status")]
        public bool Enabled { get; set; }
    }

    /// <summary>동일한 주소 배치를 로컬 메모리로 제공하는 Server 모델입니다.</summary>
    /// <remarks>앱에서 직접 값을 갱신하거나 읽을 때는 ModbusStore 생성 시 전달한 잠금 객체를 공유해야 합니다.</remarks>
    [ModbusMap(ModbusMapRole.Server, BufferBudgetBytes = 4)]
    public partial class LocalData
    {
        [InputRegister(100, WordOrder = ModbusWordOrder.LowWordFirst)]
        public float Temperature { get; set; }

        [InputRegister(102)]
        public short Status { get; set; }

        [HoldingRegister(200, Writable = true)]
        public int Setpoint { get; set; }

        [Coil(10, Writable = true)]
        public bool Enabled { get; set; }
    }
}
