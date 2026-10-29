using System;
using VagabondK.nF.Modbus.Client;
using VagabondK.nF.Modbus.Server;

namespace VagabondK.nF.Modbus.Mapping.Sample
{
    // 실제 Socket/UART 없이 생성 저장소와 Client를 연결하여 읽기와 명시적 쓰기의 모델 반영을 확인합니다.
    internal static class LoopbackSample
    {
        // 이 메모리 전송 계층은 소켓·UART 하드웨어 없이 두 역할의 동작을 보여 줍니다.
        public static void Main()
        {
            var data = new LocalData { Temperature = 12.5f, Status = -1 };
            var sync = new object();
            IModbusDataStore store = new LocalData.ModbusStore(data, sync);
            var registers = new ushort[3];
            store.ReadRegisters(true, 100, 3, registers);
            if (registers[0] != 0 || registers[1] != 0x4148 || registers[2] != 0xFFFF)
                throw new InvalidOperationException("Generated Modbus mapping failed.");
            store.WriteHoldingRegisters(200, 2, new ushort[] { 0xFFFF, 0xFFFE });
            if (data.Setpoint != -2)
                throw new InvalidOperationException("Generated Modbus write failed.");

            var remote = new RemoteData();
            var transport = new SampleLoopback(store);
            var client = new ModbusClient(transport);
            remote.ReadModbus(client, 1);
            if (remote.Temperature != data.Temperature || remote.Status != data.Status || remote.Setpoint != data.Setpoint)
                throw new InvalidOperationException("Generated Client read failed.");

            remote.Setpoint = -100;
            remote.WriteModbusSetpoint(client, 1);
            remote.Enabled = true;
            remote.WriteModbusEnabled(client, 1);
            lock (sync)
            {
                if (data.Setpoint != -100 || !data.Enabled)
                    throw new InvalidOperationException("Generated Client write failed.");
            }
        }
    }
}
