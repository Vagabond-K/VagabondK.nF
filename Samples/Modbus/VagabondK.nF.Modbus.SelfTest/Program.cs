using System;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using VagabondK.nF.Modbus;
using VagabondK.nF.Modbus.ASCII;
using VagabondK.nF.Modbus.Client;
using VagabondK.nF.Modbus.RTU;
using VagabondK.nF.Modbus.Server;
using VagabondK.nF.Modbus.TCP;

namespace VagabondK.nF.Samples.SelfTest
{
    /// <summary>보드에서 메모리 채널로 RTU/ASCII를 자체 검사하고 TCP로 결과를 제공하는 예제입니다.</summary>
    /// <remarks>실제 UART·RS485 배선이나 타이밍을 검증하지 않습니다. 결과 조회를 위해 저장된 네트워크 설정과 TCP 502 접근이 필요합니다.</remarks>
    public static class Program
    {
        private static int _checks;

        /// <summary>CRC/LRC 기준 벡터, Client/Server 왕복 및 반복 읽기 시의 힙을 검사하고 결과 조회를 기다립니다.</summary>
        public static void Main()
        {
            Debug.WriteLine("Protocol self-test: in-memory channels, NOT UART/RS485.");
            CheckVectors();
            uint rtuBefore;
            uint rtuAfter;
            uint asciiBefore;
            uint asciiAfter;
            TestSerial(false, out rtuBefore, out rtuAfter);
            TestSerial(true, out asciiBefore, out asciiAfter);
            var report = new ushort[12];
            report[0] = 0x600D;
            report[1] = (ushort)(_checks >> 16);
            report[2] = (ushort)_checks;
            Put(report, 3, rtuBefore);
            Put(report, 5, rtuAfter);
            Put(report, 7, asciiBefore);
            Put(report, 9, asciiAfter);
            report[11] = 1000;
            Debug.WriteLine("PASS: " + _checks + " checks; waiting for TCP report query.");
            Publish(report);
        }

        private static void Put(ushort[] registers, int offset, uint value)
        {
            registers[offset] = (ushort)(value >> 16);
            registers[offset + 1] = (ushort)value;
        }

        private static void Check(bool valid)
        {
            _checks++;
            if (!valid)
                throw new InvalidOperationException("Self-test failure: " + _checks);
        }

        // 알려진 정상 프레임을 확인한 뒤 한 바이트를 손상시켜 CRC/LRC 검사가 실제로 거부하는지 확인합니다.
        private static void CheckVectors()
        {
            byte[] pdu = { 3, 0, 0, 0, 10 };
            byte[] frame = new byte[513];
            byte[] expected = { 1, 3, 0, 0, 0, 10, 0xC5, 0xCD };
            int count = RtuFrame.Encode(1, pdu, 5, frame);
            Check(count == expected.Length);
            for (int i = 0; i < count; i++)
                Check(frame[i] == expected[i]);
            byte[] decoded = new byte[253];
            byte unit;
            Check(RtuFrame.Decode(frame, count, decoded, out unit) == 5 && unit == 1);
            frame[2] ^= 1;
            bool rejected = false;
            try
            {
                RtuFrame.Decode(frame, count, decoded, out unit);
            }
            catch (ModbusProtocolException)
            {
                rejected = true;
            }
            Check(rejected);
            count = AsciiFrame.Encode(1, pdu, 5, frame);
            string text = ":01030000000AF2\r\n";
            Check(count == text.Length);
            for (int i = 0; i < count; i++)
                Check(frame[i] == text[i]);
            Check(AsciiFrame.Decode(frame, count, decoded, out unit) == 5 && unit == 1);
            frame[count - 3] = (byte)'3';
            rejected = false;
            try
            {
                AsciiFrame.Decode(frame, count, decoded, out unit);
            }
            catch (ModbusProtocolException)
            {
                rejected = true;
            }
            Check(rejected);
        }

        private static void TestSerial(bool ascii, out uint before, out uint after)
        {
            var outgoing = new LoopChannel();
            var incoming = new LoopChannel();
            outgoing.Peer = incoming;
            incoming.Peer = outgoing;
            IModbusTransport clientTransport = ascii ?
                (IModbusTransport)new AsciiTransport(outgoing) : new RtuTransport(outgoing, 9600);
            IModbusTransport serverTransport = ascii ?
                (IModbusTransport)new AsciiTransport(incoming) : new RtuTransport(incoming, 9600);
            var store = new ArrayDataStore(2000, 2000, 125, 125);
            var server = new ModbusServer(1, serverTransport, store);
            outgoing.OnWrite = server.ProcessNextRequest;
            var client = new ModbusClient(clientTransport);
            var bits = new bool[2000];
            var registers = new ushort[125];
            for (int i = 0; i < bits.Length; i++)
                bits[i] = i % 3 == 0;
            store.SetDiscreteInputs(0, 2000, bits);
            client.ReadDiscreteInputs(1, 0, 2000, bits, 0);
            for (int i = 0; i < bits.Length; i++)
                Check(bits[i] == (i % 3 == 0));
            client.WriteSingleCoil(1, 0, true);
            client.ReadCoils(1, 0, 1, bits, 0);
            Check(bits[0]);
            for (int i = 0; i < 1968; i++)
                bits[i] = i % 3 == 0;
            client.WriteMultipleCoils(1, 0, 1968, bits, 0);
            client.ReadCoils(1, 0, 2000, bits, 0);
            for (int i = 0; i < bits.Length; i++)
                Check(bits[i] == (i < 1968 && i % 3 == 0));
            for (int i = 0; i < registers.Length; i++)
                registers[i] = (ushort)(65535 - i);
            store.SetInputRegisters(0, 125, registers);
            client.ReadInputRegisters(1, 0, 125, registers, 0);
            for (int i = 0; i < 125; i++)
                Check(registers[i] == 65535 - i);
            client.WriteSingleRegister(1, 124, 65535);
            client.WriteMultipleRegisters(1, 0, 123, registers, 0);
            client.ReadHoldingRegisters(1, 0, 125, registers, 0);
            Check(registers[0] == 65535 && registers[122] == 65413 && registers[124] == 65535);
            client.WriteSingleRegister(0, 123, 42);
            client.WriteSingleCoil(0, 1999, true);
            client.WriteMultipleCoils(0, 0, 2, new bool[] { false, true }, 0);
            client.WriteMultipleRegisters(0, 120, 2, new ushort[] { 12, 34 }, 0);
            client.ReadHoldingRegisters(1, 120, 5, registers, 120);
            Check(registers[120] == 12 && registers[121] == 34 && registers[123] == 42);
            client.ReadCoils(1, 1999, 1, bits, 0);
            Check(bits[0]);
            bool rejected = false;
            try
            {
                client.ReadHoldingRegisters(1, 125, 1, registers, 0);
            }
            catch (ModbusException error)
            {
                rejected = error.Code == ExceptionCode.IllegalDataAddress;
            }
            Check(rejected);
            for (int i = 0; i < 10; i++)
                client.ReadHoldingRegisters(1, 0, 125, registers, 0);
            before = nanoFramework.Runtime.Native.GC.Run(true);
            for (int i = 0; i < 1000; i++)
            {
                client.ReadHoldingRegisters(1, 0, 125, registers, 0);
                Check(registers[120] == 12 && registers[121] == 34 && registers[123] == 42);
            }
            after = nanoFramework.Runtime.Native.GC.Run(true);
        }

        private static void Publish(ushort[] report)
        {
            bool connected = false;
            while (!connected)
            {
                NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
                for (int i = 0; i < interfaces.Length; i++)
                {
                    string address = interfaces[i].IPv4Address;
                    if (!string.IsNullOrEmpty(address) && address != "0.0.0.0" && address != "127.0.0.1")
                        connected = true;
                }
                if (!connected)
                    Thread.Sleep(1000);
            }
            var store = new ArrayDataStore(0, 0, 0, 12);
            store.SetInputRegisters(0, 12, report);
            using (var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                listener.Bind(new IPEndPoint(IPAddress.Any, 502));
                listener.Listen(1);
                while (true)
                {
                    using (Socket socket = listener.Accept())
                    {
                        var server = new ModbusServer(1, new TcpTransport(new SocketChannel(socket), 5000), store);
                        try
                        {
                            server.ProcessNextRequest();
                        }
                        catch (TimeoutException error)
                        {
                            Debug.WriteLine(error.Message);
                        }
                        catch (ModbusProtocolException error)
                        {
                            Debug.WriteLine(error.Message);
                        }
                        catch (SocketException error)
                        {
                            Debug.WriteLine("Socket error: " + error.ErrorCode);
                        }
                    }
                }
            }
        }

        private sealed class LoopChannel : IByteChannel
        {
            private readonly byte[] _bytes = new byte[513];
            private int _offset;
            private int _count;
            public LoopChannel Peer;
            public Action OnWrite;

            public int Read(byte[] buffer, int offset, int count, int timeoutMilliseconds)
            {
                int read = count < _count ? count : _count;
                Array.Copy(_bytes, _offset, buffer, offset, read);
                _offset += read;
                _count -= read;
                return read;
            }

            public void Write(byte[] buffer, int offset, int count, int timeoutMilliseconds)
            {
                if (Peer._count != 0)
                    throw new InvalidOperationException("Unconsumed frame.");
                Array.Copy(buffer, offset, Peer._bytes, 0, count);
                Peer._offset = 0;
                Peer._count = count;
                if (OnWrite != null)
                    OnWrite();
            }
        }
    }
}
