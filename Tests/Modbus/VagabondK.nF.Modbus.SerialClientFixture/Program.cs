using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Ports;
using System.Threading;
using VagabondK.nF.Modbus;
using VagabondK.nF.Modbus.ASCII;
using VagabondK.nF.Modbus.RTU;
using VagabondK.nF.Modbus.Serial;
using VagabondK.nF.Modbus.Server;

// 보드 SerialClient 샘플의 PC 상대입니다. COM6 전용이며 Rtu/Ascii 선택과 9600 8E1/7E1을 보드와 맞춥니다.
// 오류와 지연 응답을 의도적으로 보내므로 운영 버스와 분리합니다. 완료 표식만이 아니라 오류 후 복구 단계도 검사합니다.
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 1 || args.Length > 2 || args[0] != "COM6" ||
            (args.Length == 2 && args[1] != "Ascii" && args[1] != "Rtu"))
        {
            Console.Error.WriteLine("Usage: SerialClientFixture COM6 [Rtu|Ascii] (isolated PC test store only)");
            return 2;
        }
        try
        {
            bool ascii = args.Length == 2 && args[1] == "Ascii";
            using var port = new SerialPort(args[0], 9600, Parity.Even, ascii ? 7 : 8, StopBits.One);
            port.Handshake = Handshake.None;
            port.RtsEnable = true;
            port.DtrEnable = true;
            port.ParityReplace = 0;
            port.Open();
            var fixture = new FixtureTransport(new SerialPortChannel(port), ascii);
            var store = new ArrayDataStore(2000, 2000, 256, 128);
            store.SetInputRegisters(0, 3, new ushort[] { 100, 200, 300 });
            store.SetDiscreteInputs(0, 4, new bool[] { true, false, true, false });
            var server = new ModbusServer(1, fixture, store);
            long deadline = Environment.TickCount64 + 900000;
            var elapsed = Stopwatch.StartNew();
            Console.WriteLine("READY: COM6 " + (ascii ? "ASCII 9600 7E1" : "RTU 9600 8E1") +
                "; 15-minute deadline; COM3 untouched.");
            while (!fixture.Completed)
            {
                if (Environment.TickCount64 >= deadline)
                    throw new TimeoutException("Fixture deadline.");
                ReceiveStatus status = server.TryProcessNextRequest();
                if (status != ReceiveStatus.Success && status != ReceiveStatus.Idle)
                    throw new InvalidOperationException("Invalid MCU request: " + status);
            }
            Console.WriteLine("PASS: MCU completion; 8 functions; maximum frames; 500 repeated reads; " +
                "4 broadcasts; " + (ascii ? 13 : 6) + " fault/recovery phases; 5 heap reports; " +
                elapsed.Elapsed.TotalSeconds.ToString("F1") + "s.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: " + error);
            return 1;
        }
    }

    private sealed class FixtureTransport : IModbusPollingTransport
    {
        private readonly IByteChannel _channel;
        private readonly IModbusPollingTransport _inner;
        private readonly bool _ascii;
        private readonly HashSet<byte> _functions = new HashSet<byte>();
        private readonly HashSet<byte> _broadcasts = new HashSet<byte>();
        private readonly HashSet<ushort> _boundaries = new HashSet<ushort>();
        private readonly HashSet<ushort> _coilCounts = new HashSet<ushort>();
        private bool _maxInputs;
        private bool _maxDiscreteInputs;
        private bool _maxCoilWrite;
        private bool _maxRegisterWrite;
        private long _faultStarted;
        private ushort _address;
        private ushort _value;
        private byte _function;
        private bool _completion;
        private int _reads;
        private int _reports;
        private int _faults;
        private int _recoveries;
        private bool _recoveryRead;
        private uint _baseline;
        private int _requests;

        public bool Completed { get; private set; }

        public bool IsSerial
        {
            get
            {
                return true;
            }
        }

        public FixtureTransport(IByteChannel channel, bool ascii)
        {
            _channel = channel;
            _ascii = ascii;
            _inner = ascii ? (IModbusPollingTransport)new AsciiTransport(channel, 3000) :
                new RtuTransport(channel, 9600, RtuReceiveMode.RequestLength, 11, 3000);
        }

        public int Receive(byte[] pdu, out FrameContext context)
        {
            return _inner.Receive(pdu, out context);
        }

        public ReceiveStatus TryReceive(byte[] pdu, out FrameContext context, out int length)
        {
            ReceiveStatus status = _inner.TryReceive(pdu, out context, out length);
            if (status != ReceiveStatus.Success)
                return status;
            if (length < 5 || context.UnitId > 1)
                throw new InvalidOperationException("Unexpected MCU request.");
            _function = pdu[0];
            _address = Protocol.ReadUInt16(pdu, 1);
            _value = Protocol.ReadUInt16(pdu, 3);
            _requests++;
            if (_requests <= 30 || _address >= 60000)
                Console.WriteLine("Request " + _requests + ": unit=" + context.UnitId +
                    "; function=" + _function + "; address=" + _address + "; value=" + _value);
            _functions.Add(_function);
            if (context.UnitId == 0)
                _broadcasts.Add(_function);
            if (_function == 1 && _address == 0)
                _coilCounts.Add(_value);
            if (_function == 4 && _address == 0 && _value == 125)
                _maxInputs = true;
            if (_function == 2 && _address == 0 && _value == 2000)
                _maxDiscreteInputs = true;
            if (_function == 15 && _address == 0 && _value == 1968)
                _maxCoilWrite = true;
            if (_function == 16 && _address == 0 && _value == 123)
                _maxRegisterWrite = true;
            if (_function == 3 && _address == 0)
            {
                _boundaries.Add(_value);
                if (_value == 125)
                {
                    _reads++;
                    if (_reads % 100 == 0)
                        Console.WriteLine("Maximum reads: " + _reads);
                }
            }
            if (_function == 3 && _address >= 60000)
            {
                if (_address != 60000 + _faults || _faults != _recoveries || _value != 1)
                    throw new InvalidOperationException("Unexpected fault sequence.");
                _faults++;
                _faultStarted = Environment.TickCount64;
                _recoveryRead = false;
                Console.WriteLine("Injecting fault " + _address);
            }
            if (_function == 4 && _address == 0 && _value == 1 && _faults == _recoveries + 1)
                _recoveryRead = true;
            if (_function == 6 && _address == 110)
            {
                if (!_recoveryRead || _faults != _recoveries + 1 || _value != 60000 + _recoveries)
                    throw new InvalidOperationException("Unexpected recovery marker.");
                _recoveries++;
                Console.WriteLine("Recovery confirmed: " + _value + "; elapsed=" +
                    (Environment.TickCount64 - _faultStarted) + "ms (includes recovery wait/drain).");
            }
            if (_function == 16 && _address == 200)
            {
                if (length != 18 || _value != 6 || Protocol.ReadUInt16(pdu, 16) != 0xCAFE)
                    throw new InvalidOperationException("Invalid heap report.");
                uint baseline = ((uint)Protocol.ReadUInt16(pdu, 6) << 16) | Protocol.ReadUInt16(pdu, 8);
                uint free = ((uint)Protocol.ReadUInt16(pdu, 10) << 16) | Protocol.ReadUInt16(pdu, 12);
                _reports++;
                if (_reports == 1)
                    _baseline = baseline;
                if (_reports > 5 || baseline != _baseline || Protocol.ReadUInt16(pdu, 14) != _reports * 100)
                    throw new InvalidOperationException("Unexpected heap report sequence.");
                Console.WriteLine("HEAP: reads=" + (_reports * 100) + "; baseline=" + baseline +
                    "; free=" + free + "; delta=" + ((long)free - baseline));
            }
            _completion = _function == 6 && _address == 127 && _value == 0x600D;
            if (_completion && (_functions.Count != 8 || _broadcasts.Count != 4 ||
                !_boundaries.IsSupersetOf(new ushort[] { 57, 58, 59, 117, 118, 119, 125 }) ||
                !_coilCounts.IsSupersetOf(new ushort[] { 920, 1880, 2000 }) || !_maxInputs || !_maxDiscreteInputs ||
                !_maxCoilWrite || !_maxRegisterWrite ||
                _reads != 511 || _reports != 5 || _faults != (_ascii ? 13 : 6) || _recoveries != _faults))
                throw new InvalidOperationException("Completion before all required phases.");
            return status;
        }

        public void Send(FrameContext context, byte[] pdu, int count)
        {
            // 60001~60012는 무응답·검사합/문자/길이 오류·부분 수신·지연·재시작 구분자 검증용 예약 주소입니다.
            if (_function == 3 && _address >= 60001 && _address <= (_ascii ? 60012 : 60005))
            {
                if (_address == 60001)
                    return;
                var payload = new byte[] { 3, 2, 0x12, 0x34 };
                var frame = new byte[_ascii ? 15 : 7];
                if (_ascii)
                    AsciiFrame.Encode(1, payload, payload.Length, frame);
                else
                    RtuFrame.Encode(1, payload, payload.Length, frame);
                if (_address == 60002)
                {
                    if (_ascii)
                        frame[frame.Length - 3] = frame[frame.Length - 3] == '0' ? (byte)'1' : (byte)'0';
                    else
                        frame[6] ^= 1;
                }
                if (_address == 60004)
                    Thread.Sleep(500);
                if (_address == 60005)
                    Thread.Sleep(_ascii ? 4000 : 2500);
                if (_address == 60006)
                    frame[3] = (byte)'G';
                if (_address == 60007)
                    frame[frame.Length - 2] = (byte)'0';
                if (_address == 60008)
                {
                    frame = new byte[514];
                    frame[0] = (byte)':';
                    for (int i = 1; i < frame.Length; i++)
                        frame[i] = (byte)'0';
                }
                if (_address == 60012)
                {
                    payload = new byte[253];
                    payload[0] = 3;
                    payload[1] = 251;
                    frame = new byte[513];
                    AsciiFrame.Encode(1, payload, payload.Length, frame);
                }
                Thread.Sleep(5);
                if (_address == 60009 || _address == 60010)
                {
                    _channel.Write(frame, 0, 7, 3000);
                    Thread.Sleep(_address == 60009 ? 1400 : 300);
                    _channel.Write(frame, 7, frame.Length - 7, 3000);
                }
                else
                {
                    if (_address == 60011)
                    {
                        _channel.Write(new byte[] { (byte)':', (byte)'0', (byte)'1' }, 0, 3, 3000);
                        Thread.Sleep(300);
                    }
                    _channel.Write(frame, 0, _address == 60003 ? 3 : frame.Length, 3000);
                }
            }
            else
                _inner.Send(context, pdu, count);
            if (_completion)
                Completed = true;
        }
    }
}
