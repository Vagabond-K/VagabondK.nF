using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using VagabondK.nF.Modbus;
using VagabondK.nF.Modbus.Server;
using VagabondK.nF.Modbus.TCP;

// 보드 TCPClient 샘플의 PC peer입니다. 지정한 바인딩 주소와 허용 MCU 주소만 사용하며 TCP 15020을 엽니다.
// 정상 요청·힙 보고·오류 주입·재접속 완료 순서를 함께 확인하므로 전용 테스트망에서 실행합니다.
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: ClientFixture <PC bind IPv4> <allowed MCU IPv4>");
            return 2;
        }
        var state = new FixtureState();
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Parse(args[0]), 15020));
        listener.Listen(1);
        Console.WriteLine("READY: " + args[0] + ":15020; only " + args[1] + "; 15-minute deadline.");
        long expires = Environment.TickCount64 + 900000;
        try
        {
            while (!state.Completed)
            {
                if (Environment.TickCount64 >= expires)
                    throw new TimeoutException("Fixture deadline expired.");
                if (!listener.Poll(1000000, SelectMode.SelectRead))
                    continue;
                using Socket socket = listener.Accept();
                socket.NoDelay = true;
                if (!((IPEndPoint)socket.RemoteEndPoint).Address.Equals(IPAddress.Parse(args[1])))
                    throw new InvalidOperationException("Unexpected peer.");
                state.Connections++;
                Console.WriteLine("Connection " + state.Connections);
                var transport = new FixtureTransport(socket, state);
                var server = new ModbusServer(1, transport, state.Store);
                while (!state.Completed)
                {
                    if (Environment.TickCount64 >= expires)
                        throw new TimeoutException("Fixture deadline expired.");
                    try
                    {
                        server.ProcessNextRequest();
                    }
                    catch (ModbusProtocolException error)
                    {
                        Console.WriteLine("Connection ended: " + error.Message);
                        break;
                    }
                    catch (SocketException error) when (error.SocketErrorCode == SocketError.ConnectionReset)
                    {
                        Console.WriteLine("Peer reset connection.");
                        break;
                    }
                }
            }
            if (state.Connections != 5 || state.Reads < 10000 ||
                state.Functions.Count != 8 || state.Faults.Count != 4 || state.Reports != 5)
                throw new InvalidOperationException("Incomplete test phases.");
            Console.WriteLine("PASS: MCU completion marker received; 8 functions; maximum quantities; " +
                state.Reads + " maximum reads; exception/timeout/transaction mismatch/closure/reconnect.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: " + error);
            return 1;
        }
    }

    private sealed class FixtureState
    {
        public readonly ArrayDataStore Store = new ArrayDataStore(2000, 2000, 256, 128);
        public readonly HashSet<byte> Functions = new HashSet<byte>();
        public readonly HashSet<ushort> Faults = new HashSet<ushort>();
        public int Connections;
        public int Reads;
        public int Reports;
        public bool Completed;

        public FixtureState()
        {
            Store.SetInputRegisters(0, 3, new ushort[] { 100, 200, 300 });
            Store.SetDiscreteInputs(0, 4, new bool[] { true, false, true, false });
        }
    }

    private sealed class FixtureTransport : IModbusTransport
    {
        private readonly Socket _socket;
        private readonly FixtureState _state;
        private readonly TcpTransport _transport;
        private ushort _address;
        private byte _function;
        private bool _complete;

        public bool IsSerial
        {
            get
            {
                return false;
            }
        }

        public FixtureTransport(Socket socket, FixtureState state)
        {
            _socket = socket;
            _state = state;
            // TCP 패킷 경계와 무관하게 모든 정상 응답을 나누어 전송합니다.
            _transport = new TcpTransport(new FragmentChannel(new SocketChannel(socket)), 10000);
        }

        public int Receive(byte[] pdu, out FrameContext context)
        {
            int count = _transport.Receive(pdu, out context);
            if (count < 5)
                throw new InvalidOperationException("Unexpected fixture request.");
            _function = pdu[0];
            _address = Protocol.ReadUInt16(pdu, 1);
            _state.Functions.Add(_function);
            if (_function == 3 && _address == 0 && Protocol.ReadUInt16(pdu, 3) == 125)
                _state.Reads++;
            _complete = _function == 6 && _address == 127 && Protocol.ReadUInt16(pdu, 3) == 0x600D;
            if (_address >= 60000)
            {
                _state.Faults.Add(_address);
                Console.WriteLine("Fault injection " + _address);
            }
            if (_function == 16 && _address == 200)
            {
                if (count != 18 || Protocol.ReadUInt16(pdu, 3) != 6 ||
                    Protocol.ReadUInt16(pdu, 16) != 0xCAFE)
                    throw new InvalidOperationException("Invalid heap report.");
                uint before = ((uint)Protocol.ReadUInt16(pdu, 6) << 16) | Protocol.ReadUInt16(pdu, 8);
                uint after = ((uint)Protocol.ReadUInt16(pdu, 10) << 16) | Protocol.ReadUInt16(pdu, 12);
                int iteration = Protocol.ReadUInt16(pdu, 14);
                _state.Reports++;
                if (iteration != _state.Reports * 2000)
                    throw new InvalidOperationException("Missing heap sample.");
                Console.WriteLine("HEAP after forced GC: reads=" + iteration + ", baseline=" + before +
                    ", free=" + after + ", delta=" + ((long)after - before));
            }
            return count;
        }

        public void Send(FrameContext context, byte[] pdu, int count)
        {
            // 예약 주소로 무응답, Transaction 불일치, 연결 종료를 주입해 Client가 오류 연결을 폐기하는지 확인합니다.
            if (_address == 60001)
                return; // 의도적으로 응답을 생략하며 클라이언트는 시간 초과를 감지해야 합니다.
            if (_address == 60002)
                context.TransactionId++;
            if (_address == 60003)
            {
                _socket.Shutdown(SocketShutdown.Both);
                throw new ModbusProtocolException("Injected peer closure.");
            }
            _transport.Send(context, pdu, count);
            if (_complete)
                _state.Completed = true;
        }
    }

    private sealed class FragmentChannel : IByteChannel
    {
        private readonly IByteChannel _inner;

        public FragmentChannel(IByteChannel inner)
        {
            _inner = inner;
        }

        public int Read(byte[] buffer, int offset, int count, int timeoutMilliseconds)
        {
            return _inner.Read(buffer, offset, count, timeoutMilliseconds);
        }

        public void Write(byte[] buffer, int offset, int count, int timeoutMilliseconds)
        {
            var deadline = new OperationDeadline(timeoutMilliseconds);
            int prefix = Math.Min(count, 3);
            _inner.Write(buffer, offset, prefix, deadline.RemainingMilliseconds);
            if (count > prefix)
                _inner.Write(buffer, offset + prefix, count - prefix, deadline.RemainingMilliseconds);
        }
    }
}
