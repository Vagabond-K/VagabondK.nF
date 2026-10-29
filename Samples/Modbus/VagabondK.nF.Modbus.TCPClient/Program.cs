using System;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using VagabondK.nF.Modbus;
using VagabondK.nF.Modbus.Client;
using VagabondK.nF.Modbus.TCP;

namespace VagabondK.nF.Samples.TCPClient
{
    /// <summary>PC ClientFixture에 접속해 최대 요청, 반복 읽기 및 오류 후 재접속을 검증하는 보드 Client입니다.</summary>
    /// <remarks>저장된 Wi-Fi 설정과 ServerAddress를 확인하고 전용 테스트 피어만 연결합니다. Coil/Register 쓰기를 운영 장치에 실행하지 않습니다.</remarks>
    public static class Program
    {
        // 시험기 전용입니다. 이 쓰기 시험의 대상을 운영 장비로 지정하지 마십시오.
        private const string ServerAddress = "192.168.0.210";
        private const int ServerPort = 15020;
        private const int Iterations = 10000;

        /// <summary>정상·오류 단계를 순서대로 실행한 뒤 새 연결에서 복구 읽기와 완료 표식을 보냅니다.</summary>
        public static void Main()
        {
            Debug.WriteLine("VagabondK.nF TCP Client test fixture starting.");
            WaitForNetwork();
            TestNormal();
            TestFault(60001);
            TestFault(60002);
            TestFault(60003);
            using (Socket socket = Connect())
            {
                var client = CreateClient(socket);
                var values = new ushort[1];
                client.ReadInputRegisters(1, 0, 1, values, 0);
                Require(values[0] == 100, "Recovery read");
                // PC는 예상한 모든 단계를 확인한 뒤에만 이 완료 표식을 인정합니다.
                client.WriteSingleRegister(1, 127, 0x600D);
            }
            Debug.WriteLine("PASS: all TCP Client fixture tests.");
            Thread.Sleep(Timeout.Infinite);
        }

        private static void Require(bool valid, string message)
        {
            if (!valid)
                throw new InvalidOperationException("Test failed: " + message);
        }

        private static void WaitForNetwork()
        {
            while (true)
            {
                NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
                for (int i = 0; i < interfaces.Length; i++)
                {
                    string address = interfaces[i].IPv4Address;
                    if (!string.IsNullOrEmpty(address) && address != "0.0.0.0" && address != "127.0.0.1")
                    {
                        Debug.WriteLine("Client IP: " + address);
                        return;
                    }
                }
                Debug.WriteLine("Waiting for saved Wi-Fi AutoConnect configuration.");
                Thread.Sleep(5000);
            }
        }

        private static Socket Connect()
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.NoDelay, true);
                socket.Connect(new IPEndPoint(IPAddress.Parse(ServerAddress), ServerPort));
                return socket;
            }
            catch
            {
                socket.Close();
                throw;
            }
        }

        private static ModbusClient CreateClient(Socket socket)
        {
            return new ModbusClient(new TcpTransport(new SocketChannel(socket), 1000));
        }

        private static void TestNormal()
        {
            using (Socket socket = Connect())
            {
                ModbusClient client = CreateClient(socket);
                var bits = new bool[2000];
                var registers = new ushort[125];
                client.ReadInputRegisters(1, 0, 3, registers, 0);
                Require(registers[0] == 100 && registers[1] == 200 && registers[2] == 300, "Input registers");
                client.ReadDiscreteInputs(1, 0, 4, bits, 0);
                Require(bits[0] && !bits[1] && bits[2] && !bits[3], "Discrete inputs");

                client.WriteSingleCoil(1, 0, true);
                client.ReadCoils(1, 0, 1, bits, 0);
                Require(bits[0], "Single coil");
                for (int i = 0; i < 1968; i++)
                    bits[i] = i % 3 == 0;
                client.WriteMultipleCoils(1, 0, 1968, bits, 0);
                client.ReadCoils(1, 0, 2000, bits, 0);
                for (int i = 0; i < 2000; i++)
                    Require(bits[i] == (i < 1968 && i % 3 == 0), "Maximum coils");

                client.WriteSingleRegister(1, 124, 65535);
                client.ReadHoldingRegisters(1, 124, 1, registers, 0);
                Require(registers[0] == 65535, "Single unsigned register");
                for (int i = 0; i < 123; i++)
                    registers[i] = (ushort)(i * 257);
                client.WriteMultipleRegisters(1, 0, 123, registers, 0);
                client.ReadHoldingRegisters(1, 0, 125, registers, 0);
                CheckRegisters(registers);
                bool rejected = false;
                try
                {
                    client.ReadHoldingRegisters(1, 60000, 1, registers, 0);
                }
                catch (ModbusException error)
                {
                    rejected = error.Code == ExceptionCode.IllegalDataAddress;
                }
                Require(rejected, "Modbus exception response");

                // 측정 경로를 미리 실행한 뒤 강제 GC 후의 가용 힙을 측정합니다.
                for (int i = 0; i < 10; i++)
                    client.ReadHoldingRegisters(1, 0, 125, registers, 0);
                var report = new ushort[6];
                uint before = nanoFramework.Runtime.Native.GC.Run(true);
                for (int batch = 0; batch < 5; batch++)
                {
                    for (int i = 0; i < Iterations / 5; i++)
                    {
                        client.ReadHoldingRegisters(1, 0, 125, registers, 0);
                        CheckRegisters(registers);
                    }
                    uint after = nanoFramework.Runtime.Native.GC.Run(true);
                    report[0] = (ushort)(before >> 16);
                    report[1] = (ushort)before;
                    report[2] = (ushort)(after >> 16);
                    report[3] = (ushort)after;
                    report[4] = (ushort)((batch + 1) * (Iterations / 5));
                    report[5] = 0xCAFE;
                    client.WriteMultipleRegisters(1, 200, 6, report, 0);
                }
            }
        }

        private static void CheckRegisters(ushort[] registers)
        {
            for (int i = 0; i < 123; i++)
                Require(registers[i] == i * 257, "Maximum registers");
            Require(registers[123] == 0 && registers[124] == 65535, "Register boundaries");
        }

        // 시험기의 60001/60002/60003은 각각 시간 초과, 잘못된 응답, 연결 종료를 유발합니다. 실패한 스트림은 폐기합니다.
        private static void TestFault(ushort address)
        {
            using (Socket socket = Connect())
            {
                ModbusClient client = CreateClient(socket);
                var values = new ushort[1];
                bool detected = false;
                try
                {
                    client.ReadHoldingRegisters(1, address, 1, values, 0);
                }
                catch (TimeoutException)
                {
                    detected = address == 60001;
                }
                catch (ModbusProtocolException)
                {
                    detected = address == 60002 || address == 60003;
                }
                catch (SocketException error)
                {
                    detected = address == 60003 &&
                        (error.ErrorCode == 10054 || error.ErrorCode == 10053);
                }
                Require(detected, "Fault detection at " + address);
                Debug.WriteLine("Expected failure detected at " + address + "; connection disposed.");
            }
        }
    }
}
