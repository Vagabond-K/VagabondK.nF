using System;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using VagabondK.nF.Modbus.Client;
using VagabondK.nF.Modbus.Server;
using VagabondK.nF.Modbus.TCP;

namespace VagabondK.nF.Modbus.Mapping.Hardware
{
    // MAPPING_HARDWARE_MASTER에 따라 보드 역할을 선택합니다. 저장된 Wi-Fi와 15020 포트의 전용 PC 피어를 준비합니다.
    // 원격 값을 반복해서 쓰므로 운영 설비에 연결하지 않습니다. 네트워크 초기 할당은 힙 측정에서 제외합니다.
    internal static class Program
    {
        private const string PeerAddress = "192.168.0.210";
        private const int Port = 15020;
        private const int Iterations = 1000;
        private static readonly ushort[] Report = new ushort[32];

        public static void Main()
        {
#if MAPPING_HARDWARE_MASTER
            WaitForNetwork();
            RunMaster();
#else
            WaitForNetwork();
            // Wi-Fi 기동 시 할당은 매핑 측정 구간에서 제외합니다.
            Thread.Sleep(2000);
            var store = Benchmark();
            RunSlave(store);
#endif
        }

        private static void Require(bool valid, string message)
        {
            if (!valid)
                throw new InvalidOperationException("MAPPING HARDWARE FAIL: " + message);
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
                        Debug.WriteLine("Mapping hardware IP: " + address);
                        return;
                    }
                }
                Thread.Sleep(1000);
            }
        }

        private static TestStore Benchmark()
        {
            var sync = new object();
            var values = new ushort[ScaleModel.Count];
            for (int i = 0; i < values.Length; i++)
                values[i] = (ushort)(i + 1);
            uint before = nanoFramework.Runtime.Native.GC.Run(true);
            var model = new ScaleModel();
            var scale = new ScaleModel.ModbusStore(model, sync);
            uint initialized = nanoFramework.Runtime.Native.GC.Run(true);
            for (int i = 0; i < 100; i++)
            {
                scale.WriteHoldingRegisters(0, ScaleModel.Count, values);
                scale.ReadRegisters(false, 0, ScaleModel.Count, values);
            }
            uint warm = nanoFramework.Runtime.Native.GC.Run(true);
            long elapsed = 0;
            uint final = warm;
            for (int batch = 0; batch < 3; batch++)
            {
                long start = Environment.TickCount64;
                for (int i = 0; i < Iterations; i++)
                {
                    scale.WriteHoldingRegisters(0, ScaleModel.Count, values);
                    scale.ReadRegisters(false, 0, ScaleModel.Count, values);
                }
                elapsed += Environment.TickCount64 - start;
                final = nanoFramework.Runtime.Native.GC.Run(true);
                Put32(16 + batch * 2, final);
            }
            for (int i = 0; i < values.Length; i++)
                Require(values[i] == i + 1, "Scaling result");
            Report[0] = 0x4D50;
            Report[1] = 1;
            Report[2] = ScaleModel.Count;
            Report[3] = Iterations;
            Put32(4, before);
            Put32(6, initialized);
            Put32(8, warm);
            Put32(10, final);
            Put32(12, (uint)elapsed);
            Report[15] = 0x600D;
            Report[28] = 3;
            Report[31] = Debugger.IsAttached ? (ushort)1 : (ushort)0;
            var data = new ScalarData();
            return new TestStore(scale, new ScalarData.ModbusStore(data, sync), data);
        }

        private static void Put32(int offset, uint value)
        {
            Report[offset] = (ushort)(value >> 16);
            Report[offset + 1] = (ushort)value;
        }

        private static void RunSlave(TestStore store)
        {
            using (var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                listener.Bind(new IPEndPoint(IPAddress.Any, Port));
                listener.Listen(1);
                while (true)
                {
                    using (Socket connection = listener.Accept())
                    {
                        Report[22]++;
                        var server = new ModbusServer(1,
                            new ReportTransport(new TcpTransport(new SocketChannel(connection), 10000)), store);
                        try
                        {
                            while (true)
                                server.ProcessNextRequest();
                        }
                        catch (TimeoutException error)
                        {
                            Report[25]++;
                            Debug.WriteLine("Slave connection timeout: " + error.Message);
                        }
                        catch (ModbusProtocolException error)
                        {
                            Report[26]++;
                            Debug.WriteLine("Slave connection ended: " + error.Message);
                        }
                        catch (SocketException error)
                        {
                            Report[27]++;
                            Debug.WriteLine("Slave socket error: " + error.ErrorCode);
                        }
                    }
                }
            }
        }

        private sealed class ReportTransport : IModbusTransport
        {
            private readonly IModbusTransport _inner;
            public ReportTransport(IModbusTransport inner) { _inner = inner; }
            public bool IsSerial { get { return false; } }
            public int Receive(byte[] pdu, out FrameContext context) { return _inner.Receive(pdu, out context); }
            public void Send(FrameContext context, byte[] pdu, int count)
            {
                _inner.Send(context, pdu, count);
                Report[23]++;
                if ((pdu[0] & 0x80) != 0) Report[24]++;
            }
        }

        private static Socket Connect()
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                socket.Connect(new IPEndPoint(IPAddress.Parse(PeerAddress), Port));
                return socket;
            }
            catch
            {
                socket.Close();
                throw;
            }
        }

        private static void Validate(RemoteData data)
        {
            Require(data.I16 == -12345 && data.U16 == 0xFEDC, "Master int16");
            Require(data.I32 == int.MinValue && data.U32 == 0x89ABCDEF, "Master int32");
            Require(data.F32 == -12.5f && data.F64 == -12345.125, "Master floating point");
            Require(data.I64 == -9223372036854775685L && data.U64 == 0xFEDCBA9876543210UL, "Master int64");
            Require(data.Enabled && data.Ready && data.Status == -123, "Master bits/status");
        }

        private static void RunMaster()
        {
            var remote = new RemoteData();
            using (Socket socket = Connect())
            {
                var client = new ModbusClient(new TcpTransport(new SocketChannel(socket), 1000));
                remote.ReadModbus(client, 1);
                Validate(remote);
                remote.WriteModbusI16(client, 1);
                remote.WriteModbusU16(client, 1);
                remote.WriteModbusI32(client, 1);
                remote.WriteModbusU32(client, 1);
                remote.WriteModbusF32(client, 1);
                remote.WriteModbusI64(client, 1);
                remote.WriteModbusU64(client, 1);
                remote.WriteModbusF64(client, 1);
                remote.WriteModbusEnabled(client, 1);
                for (int i = 0; i < 100; i++)
                {
                    remote.ReadModbus(client, 1);
                    Validate(remote);
                }
                remote.Enabled = false;
                remote.I16 = 0;
                remote.ReadModbusGroupStatus(client, 1);
                Require(remote.Enabled && remote.I16 == 0, "Master status group isolation");
                remote.ReadModbusGroupSettings(client, 1);
                Validate(remote);
                client.WriteSingleRegister(1, 900, 1);
                bool detected = false;
                try { remote.ReadModbus(client, 1); }
                catch (ModbusException error) { detected = error.Code == ExceptionCode.IllegalDataAddress; }
                Require(detected, "Master exception response");
            }
            using (Socket socket = Connect())
            {
                var client = new ModbusClient(new TcpTransport(new SocketChannel(socket), 250));
                client.WriteSingleRegister(1, 900, 2);
                bool detected = false;
                try { remote.ReadModbus(client, 1); }
                catch (TimeoutException) { detected = true; }
                Require(detected, "Master timeout");
            }
            using (Socket socket = Connect())
            {
                var client = new ModbusClient(new TcpTransport(new SocketChannel(socket), 1000));
                client.WriteSingleRegister(1, 900, 3);
                bool detected = false;
                try { remote.ReadModbus(client, 1); }
                catch (ModbusProtocolException) { detected = true; }
                catch (SocketException) { detected = true; }
                Require(detected, "Master peer closure");
            }
            for (ushort stage = 4; stage <= 5; stage++)
            {
                using (Socket socket = Connect())
                {
                    var client = new ModbusClient(new TcpTransport(new SocketChannel(socket), 1000));
                    client.WriteSingleRegister(1, 900, stage);
                    remote.Enabled = false;
                    bool detected = false;
                    try { remote.ReadModbus(client, 1); }
                    catch (ModbusProtocolException) { detected = true; }
                    Require(detected && !remote.Enabled, "Mismatched transaction/unit must not update mapped model");
                }
            }
            using (Socket socket = Connect())
            {
                var client = new ModbusClient(new TcpTransport(new SocketChannel(socket), 1000));
                remote.ReadModbus(client, 1);
                Validate(remote);
                client.WriteSingleRegister(1, 900, 0x600D);
            }
            Debug.WriteLine("MAPPING HARDWARE MASTER PASS");
            Thread.Sleep(Timeout.Infinite);
        }

        private sealed class TestStore : IModbusDataStore, IModbusDataStoreValidator
        {
            private readonly IModbusDataStore _scale;
            private readonly IModbusDataStore _scalars;
            private readonly ScalarData _data;

            internal TestStore(IModbusDataStore scale, IModbusDataStore scalars, ScalarData data)
            {
                _scale = scale; _scalars = scalars; _data = data;
            }

            public bool IsValidRange(FunctionCode function, ushort address, ushort count)
            {
                if (function == FunctionCode.ReadInputRegisters && address >= 1000 && (int)address + count <= 1032)
                    return count > 0;
                if ((function == FunctionCode.WriteSingleRegister || function == FunctionCode.WriteMultipleRegisters) &&
                    (address == 1100 || address == 1101 || address == 1102) && count == 1)
                    return true;
                bool scale = (function == FunctionCode.ReadHoldingRegisters || function == FunctionCode.WriteSingleRegister ||
                    function == FunctionCode.WriteMultipleRegisters) && address < 200;
                return ((IModbusDataStoreValidator)(scale ? _scale : _scalars)).IsValidRange(function, address, count);
            }

            public void ReadBits(bool discreteInputs, ushort address, ushort count, bool[] values)
            {
                _scalars.ReadBits(discreteInputs, address, count, values);
            }

            public void ReadRegisters(bool inputRegisters, ushort address, ushort count, ushort[] values)
            {
                if (inputRegisters && address >= 1000 && (int)address + count <= 1032)
                {
                    Report[31] = Debugger.IsAttached ? (ushort)1 : (ushort)0;
                    for (int i = 0; i < count; i++) values[i] = Report[address - 1000 + i];
                }
                else if (!inputRegisters && address < 200)
                    _scale.ReadRegisters(false, address, count, values);
                else
                    _scalars.ReadRegisters(inputRegisters, address, count, values);
            }

            public void WriteCoils(ushort address, ushort count, bool[] values)
            {
                _scalars.WriteCoils(address, count, values);
            }

            public void WriteHoldingRegisters(ushort address, ushort count, ushort[] values)
            {
                if (address == 1102 && count == 1 && values[0] <= 8)
                    _data.ChangeLocal(values[0]);
                else if (address == 1101 && count == 1 && values[0] == 0xCAFE)
                    Put32(29, nanoFramework.Runtime.Native.GC.Run(true));
                else if (address == 1100 && count == 1 && values[0] == 0xCAFE)
                {
                    bool valid = _data.I16 == -12345 && _data.U16 == 0xFEDC &&
                        _data.I32 == int.MinValue && _data.U32 == 0x89ABCDEF &&
                        _data.F32 == -12.5f && _data.F64 == -12345.125 &&
                        _data.I64 == -9223372036854775685L && _data.U64 == 0xFEDCBA9876543210UL &&
                        _data.Enabled;
                    Report[14] = valid ? (ushort)0x600D : (ushort)0xDEAD;
                    if (!valid) throw new ModbusException(ExceptionCode.ServerDeviceFailure);
                }
                else if (address < 200)
                    _scale.WriteHoldingRegisters(address, count, values);
                else
                    _scalars.WriteHoldingRegisters(address, count, values);
            }
        }
    }
}
