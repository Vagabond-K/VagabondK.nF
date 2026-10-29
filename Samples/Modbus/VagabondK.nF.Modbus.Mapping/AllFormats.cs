using System;
using VagabondK.nF.Modbus.Client;

namespace VagabondK.nF.Modbus.Mapping.Sample
{
    [ModbusMap(ModbusMapRole.Server)]
    internal partial class FormatSource
    {
        [Coil(0, Writable = true)]
        public bool Output = true;
        [DiscreteInput(0)]
        public bool Input = true;
        [HoldingRegister(0, Writable = true)]
        public short Signed16 = -123;
        [HoldingRegister(1, Writable = true, ByteOrder = ModbusByteOrder.LowByteFirst)]
        public ushort Unsigned16 = 60000;
        [HoldingRegister(2, Writable = true)]
        public int Signed32 = -123456;
        [HoldingRegister(4, Writable = true)]
        public uint Unsigned32 = 3000000000U;
        [HoldingRegister(6, Writable = true)]
        public float Float32 = 12.5f;
        [HoldingRegister(8, Writable = true, WordOrder = ModbusWordOrder.LowWordFirst)]
        public long Signed64 = -1234567890123L;
        [HoldingRegister(12, Writable = true)]
        public ulong Unsigned64 = 12345678901234567890UL;
        [HoldingRegister(16, Writable = true, WordOrder = ModbusWordOrder.LowWordFirst)]
        public double Float64 = -12345.625;
    }

    [ModbusMap(ModbusMapRole.Client)]
    internal partial class FormatTarget
    {
        [Coil(0, Writable = true)]
        public bool Output { get; set; }
        [DiscreteInput(0)]
        public bool Input { get; private set; }
        [HoldingRegister(0, Writable = true)]
        public short Signed16 { get; set; }
        [HoldingRegister(1, Writable = true, ByteOrder = ModbusByteOrder.LowByteFirst)]
        public ushort Unsigned16 { get; set; }
        [HoldingRegister(2, Writable = true)]
        public int Signed32 { get; set; }
        [HoldingRegister(4, Writable = true)]
        public uint Unsigned32 { get; set; }
        [HoldingRegister(6, Writable = true)]
        public float Float32 { get; set; }
        [HoldingRegister(8, Writable = true, WordOrder = ModbusWordOrder.LowWordFirst)]
        public long Signed64 { get; set; }
        [HoldingRegister(12, Writable = true)]
        public ulong Unsigned64 { get; set; }
        [HoldingRegister(16, Writable = true, WordOrder = ModbusWordOrder.LowWordFirst)]
        public double Float64 { get; set; }
    }

    // bool 및 16/32/64비트 정수·실수를 메모리 피어로 왕복시켜 바이트·워드 순서와 명시적 쓰기를 확인합니다.
    internal static class AllFormats
    {
        internal static void Run()
        {
            var source = new FormatSource();
            var target = new FormatTarget();
            var client = new ModbusClient(new SampleLoopback(new FormatSource.ModbusStore(source, new object())));
            target.ReadModbus(client, 1);
            if (!target.Input || !target.Output || target.Signed16 != -123 || target.Unsigned16 != 60000 ||
                target.Signed32 != -123456 || target.Unsigned32 != 3000000000U || target.Float32 != 12.5f ||
                target.Signed64 != -1234567890123L || target.Unsigned64 != 12345678901234567890UL || target.Float64 != -12345.625)
                throw new InvalidOperationException("All-format Client read.");
            target.Output = false; target.WriteModbusOutput(client, 1);
            target.Signed16 = short.MinValue; target.WriteModbusSigned16(client, 1);
            target.Unsigned16 = ushort.MaxValue; target.WriteModbusUnsigned16(client, 1);
            target.Signed32 = int.MinValue; target.WriteModbusSigned32(client, 1);
            target.Unsigned32 = uint.MaxValue; target.WriteModbusUnsigned32(client, 1);
            target.Float32 = -0.5f; target.WriteModbusFloat32(client, 1);
            target.Signed64 = long.MinValue; target.WriteModbusSigned64(client, 1);
            target.Unsigned64 = ulong.MaxValue; target.WriteModbusUnsigned64(client, 1);
            target.Float64 = 123.125; target.WriteModbusFloat64(client, 1);
            if (source.Output || source.Signed16 != short.MinValue || source.Unsigned16 != ushort.MaxValue ||
                source.Signed32 != int.MinValue || source.Unsigned32 != uint.MaxValue || source.Float32 != -0.5f ||
                source.Signed64 != long.MinValue || source.Unsigned64 != ulong.MaxValue || source.Float64 != 123.125)
                throw new InvalidOperationException("All-format Client write.");
        }
    }
}
