using System;
using System.Diagnostics;
using System.IO.Ports;
using System.Threading;
using nanoFramework.Hardware.Esp32;
using VagabondK.nF.Modbus;
using VagabondK.nF.Modbus.ASCII;
using VagabondK.nF.Modbus.Client;
using VagabondK.nF.Modbus.RTU;
using VagabondK.nF.Modbus.Serial;

namespace VagabondK.nF.Samples.SerialClient
{
    /// <summary>PC SerialClientFixture와 정상·오류·복구 요청을 교환하는 보드 측 Client입니다.</summary>
    /// <remarks>GPIO17/18, COM2 및 9600 통신 설정을 보드와 어댑터에 맞춰 확인합니다.
    /// 다량의 쓰기와 Broadcast를 수행하므로 운영 장치를 연결하지 않습니다. MODBUS_ASCII 사용 시 상대도 7E1 ASCII로 맞춥니다.</remarks>
    public static class Program
    {
        private const int Iterations = 500;
        private static readonly byte[] Discard = new byte[256];

        /// <summary>UART를 연 뒤 정상 요청과 주소별 오류 주입을 검사하고 완료 표식을 PC에 보냅니다.</summary>
        public static void Main()
        {
            Configuration.SetPinFunction(17, DeviceFunction.COM2_TX);
            Configuration.SetPinFunction(18, DeviceFunction.COM2_RX);
#if MODBUS_ASCII
            const int dataBits = 7;
            const ushort lastFault = 60012;
#else
            const int dataBits = 8;
            const ushort lastFault = 60005;
#endif
            using (var port = new SerialPort("COM2", 9600, Parity.Even, dataBits, StopBits.One))
            {
                port.Open();
#if MODBUS_ASCII
                var client = new ModbusClient(new AsciiTransport(new SerialPortChannel(port), 3000));
#else
                var client = new ModbusClient(new RtuTransport(new SerialPortChannel(port),
                    9600, RtuReceiveMode.ResponseLength, 11, 1500));
#endif
                // 첫 RS-485 요청 전에 배포용 USB 통신이 끝나도록 기다립니다.
                Thread.Sleep(10000);
                try
                {
                    TestNormal(client);
                    for (ushort address = 60000; address <= lastFault; address++)
                        TestFault(client, port, address);
                    client.WriteSingleRegister(1, 127, 0x600D);
                }
                catch (Exception error)
                {
                    Debug.WriteLine("Serial Client fixture failed: " + error.ToString());
                    throw;
                }
                Thread.Sleep(Timeout.Infinite);
            }
        }

        private static void Require(bool valid, string message)
        {
            if (!valid)
                throw new InvalidOperationException("Test failed: " + message);
        }

        // 최대 요청 크기, UART 배치 경계, 반복 읽기 후 가용 힙 및 응답 없는 브로드캐스트의 실제 반영을 확인합니다.
        private static void TestNormal(ModbusClient client)
        {
            var bits = new bool[2000];
            var registers = new ushort[125];
            client.ReadInputRegisters(1, 0, 125, registers, 0);
            Require(registers[0] == 100 && registers[1] == 200 && registers[2] == 300, "Input registers");
            for (int i = 3; i < registers.Length; i++)
                Require(registers[i] == 0, "Input register boundary");
            client.ReadDiscreteInputs(1, 0, 2000, bits, 0);
            for (int i = 0; i < bits.Length; i++)
                Require(bits[i] == (i == 0 || i == 2), "Discrete inputs");
            client.WriteSingleCoil(1, 0, true);
            client.ReadCoils(1, 0, 1, bits, 0);
            Require(bits[0], "Single coil");
            for (int i = 0; i < 1968; i++)
                bits[i] = i % 3 == 0;
            client.WriteMultipleCoils(1, 0, 1968, bits, 0);
            client.ReadCoils(1, 0, 2000, bits, 0);
            for (int i = 0; i < bits.Length; i++)
                Require(bits[i] == (i < 1968 && i % 3 == 0), "Maximum coils");
            ushort[] bitCounts = new ushort[] { 920, 1880 };
            for (int n = 0; n < bitCounts.Length; n++)
            {
                client.ReadCoils(1, 0, bitCounts[n], bits, 0);
                for (int i = 0; i < bitCounts[n]; i++)
                    Require(bits[i] == (i % 3 == 0), "Exact UART batch boundary");
            }
            client.WriteSingleRegister(1, 124, 65535);
            client.ReadHoldingRegisters(1, 124, 1, registers, 0);
            Require(registers[0] == 65535, "Single register");
            for (int i = 0; i < 123; i++)
                registers[i] = (ushort)(i * 257);
            client.WriteMultipleRegisters(1, 0, 123, registers, 0);

            // 응답 ADU 길이는 네이티브 UART의 120바이트 배치 경계 전후를 포함합니다.
            ushort[] counts = new ushort[] { 57, 58, 59, 117, 118, 119, 125 };
            for (int n = 0; n < counts.Length; n++)
            {
                client.ReadHoldingRegisters(1, 0, counts[n], registers, 0);
                CheckRegisters(registers, counts[n]);
            }
            for (int i = 0; i < 10; i++)
                client.ReadHoldingRegisters(1, 0, 125, registers, 0);
            var report = new ushort[6];
            uint baseline = nanoFramework.Runtime.Native.GC.Run(true);
            for (int batch = 0; batch < 5; batch++)
            {
                for (int i = 0; i < Iterations / 5; i++)
                {
                    client.ReadHoldingRegisters(1, 0, 125, registers, 0);
                    CheckRegisters(registers, 125);
                }
                uint free = nanoFramework.Runtime.Native.GC.Run(true);
                report[0] = (ushort)(baseline >> 16);
                report[1] = (ushort)baseline;
                report[2] = (ushort)(free >> 16);
                report[3] = (ushort)free;
                report[4] = (ushort)((batch + 1) * (Iterations / 5));
                report[5] = 0xCAFE;
                client.WriteMultipleRegisters(1, 200, 6, report, 0);
            }

            client.WriteSingleCoil(0, 0, false);
            Thread.Sleep(100);
            client.ReadCoils(1, 0, 1, bits, 0);
            Require(!bits[0], "Broadcast single coil");
            client.WriteSingleRegister(0, 0, 42);
            Thread.Sleep(100);
            client.ReadHoldingRegisters(1, 0, 1, registers, 0);
            Require(registers[0] == 42, "Broadcast single register");
            bits[0] = false;
            bits[1] = true;
            client.WriteMultipleCoils(0, 0, 2, bits, 0);
            Thread.Sleep(100);
            client.ReadCoils(1, 0, 2, bits, 0);
            Require(!bits[0] && bits[1], "Broadcast multiple coils");
            registers[0] = 43;
            registers[1] = 44;
            client.WriteMultipleRegisters(0, 0, 2, registers, 0);
            Thread.Sleep(100);
            client.ReadHoldingRegisters(1, 0, 2, registers, 0);
            Require(registers[0] == 43 && registers[1] == 44, "Broadcast multiple registers");
        }

        private static void CheckRegisters(ushort[] values, int count)
        {
            for (int i = 0; i < count; i++)
                Require(values[i] == (i < 123 ? i * 257 : (i == 124 ? 65535 : 0)), "Register boundary");
        }

        // 60000번대는 PC fixture의 오류 선택 주소입니다. 늦은 응답을 비운 후 정상 읽기로 복구 여부를 확인합니다.
        private static void TestFault(ModbusClient client, SerialPort port, ushort address)
        {
            var values = new ushort[1];
            bool detected = false;
            try
            {
                client.ReadHoldingRegisters(1, address, 1, values, 0);
                detected = (address == 60004 || address == 60010 || address == 60011) && values[0] == 0x1234;
            }
            catch (ModbusException error)
            {
                detected = address == 60000 && error.Code == ExceptionCode.IllegalDataAddress;
            }
            catch (TimeoutException)
            {
                detected = address == 60001 || address == 60003 || address == 60005 || address == 60009;
            }
            catch (ModbusProtocolException)
            {
                detected = address == 60002 || address == 60006 || address == 60007 ||
                    address == 60008 || address == 60012;
            }
            Require(detected, "Fault detection");
            // 지연 응답에는 트랜잭션 ID가 없습니다. 시험기의 늦은 응답보다 오래 기다린 뒤,
            // 무통신 구간이 관측될 때까지 잔여 데이터를 비우고 다음 요청을 보냅니다.
#if MODBUS_ASCII
            Thread.Sleep(4000);
#else
            Thread.Sleep(3000);
#endif
            var deadline = new OperationDeadline(5000);
            do
            {
                int remaining = deadline.RemainingMilliseconds;
                int count = port.BytesToRead;
                if (count > Discard.Length)
                    count = Discard.Length;
                if (count > 0)
                    port.Read(Discard, 0, count);
                Thread.Sleep(remaining < 100 ? remaining : 100);
            } while (port.BytesToRead != 0);
            client.ReadInputRegisters(1, 0, 1, values, 0);
            Require(values[0] == 100, "Recovery read");
            client.WriteSingleRegister(1, 110, address);
        }
    }
}
