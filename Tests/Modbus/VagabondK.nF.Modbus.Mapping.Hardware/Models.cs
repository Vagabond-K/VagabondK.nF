using VagabondK.nF.Modbus.Mapping;

namespace VagabondK.nF.Modbus.Mapping.Hardware
{
    // 형식별 경계값·바이트·워드 순서·읽기 전용 주소를 실제 보드 매핑과 PC 피어 사이에서 비교하는 고정 배치입니다.
    [ModbusMap(ModbusMapRole.Server, BufferBudgetBytes = 8)]
    internal partial class ScalarData
    {
        internal void ChangeLocal(int slot)
        {
            if (slot < 0 || slot > 8) throw new System.ArgumentOutOfRangeException("slot");
            switch (slot)
            {
                case 0: I16 = 1234; break;
                case 1: U16 = 0x1234; break;
                case 2: I32 = 0x12345678; break;
                case 3: U32 = 0x76543210; break;
                case 4: F32 = 123.25f; break;
                case 5: I64 = 0x1234567890ABCDEFL; break;
                case 6: U64 = 0x0123456789ABCDEFUL; break;
                case 7: F64 = 1234567.25; break;
                case 8: Enabled = false; break;
            }
        }

        [ModbusPoint(ModbusArea.HoldingRegister, 200, Writable = true)]
        public short I16 { get; private set; }

        [ModbusPoint(ModbusArea.HoldingRegister, 201, Writable = true, ByteOrder = ModbusByteOrder.LowByteFirst)]
        public ushort U16 { get; private set; }

        [ModbusPoint(ModbusArea.HoldingRegister, 202, Writable = true)]
        public int I32 { get; private set; }

        [ModbusPoint(ModbusArea.HoldingRegister, 204, Writable = true, WordOrder = ModbusWordOrder.LowWordFirst)]
        public uint U32 { get; private set; }

        [ModbusPoint(ModbusArea.HoldingRegister, 206, Writable = true, ByteOrder = ModbusByteOrder.LowByteFirst)]
        public float F32 { get; private set; }

        [ModbusPoint(ModbusArea.HoldingRegister, 208, Writable = true, WordOrder = ModbusWordOrder.LowWordFirst)]
        public long I64 { get; private set; }

        [ModbusPoint(ModbusArea.HoldingRegister, 212, Writable = true, ByteOrder = ModbusByteOrder.LowByteFirst)]
        public ulong U64 { get; private set; }

        [ModbusPoint(ModbusArea.HoldingRegister, 216, Writable = true,
            WordOrder = ModbusWordOrder.LowWordFirst, ByteOrder = ModbusByteOrder.LowByteFirst)]
        public double F64 { get; private set; }

        [ModbusPoint(ModbusArea.Coil, 10, Writable = true)]
        public bool Enabled { get; private set; }

        [ModbusPoint(ModbusArea.DiscreteInput, 20)]
        public bool Ready { get { return true; } }

        [ModbusPoint(ModbusArea.InputRegister, 100)]
        public short Status { get { return -123; } }

        [ModbusPoint(ModbusArea.HoldingRegister, 220)]
        public ushort ReadOnly { get { return 0x55AA; } }
    }

    [ModbusMap(ModbusMapRole.Client, MaxReadRegisters = 4, MaxReadBits = 2, BufferBudgetBytes = 9)]
    internal partial class RemoteData
    {
        [ModbusPoint(ModbusArea.HoldingRegister, 200, Writable = true, ReadGroup = "Settings")]
        public short I16 { get; set; }

        [ModbusPoint(ModbusArea.HoldingRegister, 201, Writable = true, ByteOrder = ModbusByteOrder.LowByteFirst)]
        public ushort U16 { get; set; }

        [ModbusPoint(ModbusArea.HoldingRegister, 202, Writable = true)]
        public int I32 { get; set; }

        [ModbusPoint(ModbusArea.HoldingRegister, 204, Writable = true, WordOrder = ModbusWordOrder.LowWordFirst)]
        public uint U32 { get; set; }

        [ModbusPoint(ModbusArea.HoldingRegister, 206, Writable = true, ByteOrder = ModbusByteOrder.LowByteFirst)]
        public float F32 { get; set; }

        [ModbusPoint(ModbusArea.HoldingRegister, 208, Writable = true, WordOrder = ModbusWordOrder.LowWordFirst)]
        public long I64 { get; set; }

        [ModbusPoint(ModbusArea.HoldingRegister, 212, Writable = true, ByteOrder = ModbusByteOrder.LowByteFirst)]
        public ulong U64 { get; set; }

        [ModbusPoint(ModbusArea.HoldingRegister, 216, Writable = true,
            WordOrder = ModbusWordOrder.LowWordFirst, ByteOrder = ModbusByteOrder.LowByteFirst)]
        public double F64 { get; set; }

        [ModbusPoint(ModbusArea.Coil, 10, Writable = true, ReadGroup = "Status")]
        public bool Enabled { get; set; }

        [ModbusPoint(ModbusArea.DiscreteInput, 20)]
        public bool Ready { get; private set; }

        [ModbusPoint(ModbusArea.InputRegister, 100)]
        public short Status { get; private set; }
    }
}
