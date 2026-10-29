using VagabondK.nF.Modbus.Mapping;

namespace VagabondK.nF.MappingPackageSmoke
{
#if MAPPING_CLIENT
    [ModbusMap(ModbusMapRole.Client, MaxReadRegisters = 4, MaxReadBits = 8, BufferBudgetBytes = 9)]
#else
    [ModbusMap(ModbusMapRole.Server, BufferBudgetBytes = 8)]
#endif
    public partial class DeviceData
    {
#if MAPPING_CLIENT
        [ModbusPoint(ModbusArea.Coil, 1, Writable = true, ReadGroup = "Status")]
#else
        [ModbusPoint(ModbusArea.Coil, 1, Writable = true)]
#endif
        public bool Enabled { get; set; }

#if !MAPPING_BITS_ONLY
        [ModbusPoint(ModbusArea.HoldingRegister, 100, Writable = true)]
        public int Value { get; private set; }

        [ModbusPoint(ModbusArea.InputRegister, 0)]
        public short Signed16 { get; set; }

        [ModbusPoint(ModbusArea.InputRegister, 1)]
        public ushort Unsigned16 { get; set; }

        [ModbusPoint(ModbusArea.InputRegister, 2)]
        public uint Unsigned32 { get; set; }

        [ModbusPoint(ModbusArea.InputRegister, 4)]
        public float Single { get; set; }

        [ModbusPoint(ModbusArea.HoldingRegister, 102, Writable = true, WordOrder = ModbusWordOrder.LowWordFirst)]
        public long Signed64 { get; set; }

        [ModbusPoint(ModbusArea.HoldingRegister, 106, Writable = true, ByteOrder = ModbusByteOrder.LowByteFirst)]
        public ulong Unsigned64 { get; set; }

        [ModbusPoint(ModbusArea.HoldingRegister, 110, Writable = true,
            WordOrder = ModbusWordOrder.LowWordFirst, ByteOrder = ModbusByteOrder.LowByteFirst)]
        public double Double { get; set; }
#endif
    }

    public static class Program
    {
        public static void Main()
        {
            var model = new DeviceData();
#if MAPPING_CLIENT
            try
            {
                model.ReadModbusGroupStatus(null, 1);
            }
            catch (System.ArgumentNullException)
            {
                // 분석기 패키지를 직접 사용해 선택한 그룹의 코드를 생성하고 컴파일합니다.
            }
            try
            {
                model.ReadModbus(null, 1);
            }
            catch (System.ArgumentNullException)
            {
                // 컴파일만 확인하는 기본 점검이며 전송 계층은 구성하지 않습니다.
            }
#else
            var store = new DeviceData.ModbusStore(model, new object());
            store.WriteCoils(1, 1, new bool[] { true });
            if (!model.Enabled)
                throw new System.InvalidOperationException("Generated mapping failed.");
#endif
        }
    }
}
