using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using VagabondK.nF.Modbus;
using VagabondK.nF.Modbus.ASCII;
using VagabondK.nF.Modbus.Client;
using VagabondK.nF.Modbus.RTU;
using VagabondK.nF.Modbus.Server;
using VagabondK.nF.Modbus.TCP;

// 프레임 벡터, 부분 읽기, 수신 상태, Client/Server 정책 및 로컬 TCP 왕복을 검증하는 호스트 회귀 테스트입니다.
// 기본 실행은 유한한 검사이며 --soak는 같은 정상·오류 경로를 장시간 반복합니다. 외부 장치는 필요하지 않습니다.
internal static class Program
{
    private static int _assertions;

    private static void Check(bool valid, string message)
    {
        _assertions++;
        if (!valid)
            throw new Exception(message);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        _assertions++;
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private static void Equal(byte[] expected, byte[] actual, int count)
    {
        Check(expected.Length == count, "Length mismatch");
        for (int i = 0; i < count; i++)
            Check(expected[i] == actual[i], "Byte mismatch at " + i);
    }

    private static void Main(string[] args)
    {
        if (SoakRunner.TryRun(args, "Modbus", () => TcpLoopback(100), TcpFaults,
            () => { TcpFaults(); TcpLoopback(); })) return;
        if (args.Length != 0) throw new ArgumentException("Use no arguments or --soak.");
        Frames();
        TransportBoundaries();
        RtuLengthBoundaries();
        ReceiveStatuses();
        Roles(false);
        Roles(true);
        ServerValidation();
        ClientValidation();
        SerialRoundTrip(false);
        SerialRoundTrip(false, true);
        SerialRoundTrip(true);
        TcpLoopback();
        TcpFaults();
        UartReadCounts();
        Fuzz();
        Console.WriteLine("PASS: " + _assertions + " assertions");
    }

    private static void Fuzz()
    {
        var random = new Random(502);
        var frame = new byte[AsciiFrame.MaxLength + 8];
        var pdu = new byte[256];
        const string hex = "0123456789ABCDEFabcdefG:\r\n";
        for (int iteration = 0; iteration < 20000; iteration++)
        {
            int count = random.Next(frame.Length + 1);
            random.NextBytes(frame);
            if (iteration % 2 == 0 && count >= 4 && count <= RtuFrame.MaxLength)
            {
                ushort crc = RtuFrame.ComputeCrc(frame, 0, count - 2);
                frame[count - 2] = (byte)crc;
                frame[count - 1] = (byte)(crc >> 8);
            }
            byte unitId;
            int length;
            ReceiveStatus rtu = RtuFrame.TryDecode(frame, count, pdu, out unitId, out length);
            Check(rtu == ReceiveStatus.Success ? length == count - 3 && unitId <= 247 :
                (rtu == ReceiveStatus.InvalidLength || rtu == ReceiveStatus.CrcMismatch ||
                rtu == ReceiveStatus.InvalidUnitId) && length == 0 && unitId == 0, "RTU fuzz status");

            count = random.Next(AsciiFrame.MaxLength + 2);
            for (int i = 0; i < count; i++)
                frame[i] = (byte)hex[random.Next(i == 0 || i >= count - 2 ? hex.Length : 22)];
            if (count >= 3 && iteration % 3 != 0)
            {
                frame[0] = (byte)':';
                frame[count - 2] = 13;
                frame[count - 1] = 10;
            }
            ReceiveStatus ascii = AsciiFrame.TryDecode(frame, count, pdu, out unitId, out length);
            Check(ascii == ReceiveStatus.Success ? length == (count - 3) / 2 - 2 && unitId <= 247 :
                (ascii == ReceiveStatus.InvalidLength || ascii == ReceiveStatus.InvalidCharacter ||
                ascii == ReceiveStatus.LrcMismatch || ascii == ReceiveStatus.InvalidUnitId) &&
                length == 0 && unitId == 0, "ASCII fuzz status");
        }

        var store = new ArrayDataStore(64, 64, 64, 64);
        for (int serial = 0; serial < 2; serial++)
        {
            var link = new RequestTransport(serial == 1);
            link.Server = new ModbusServer(1, new ServerTransport(link), store);
            for (int iteration = 0; iteration < 20000; iteration++)
            {
                var request = new byte[1 + random.Next(253)];
                random.NextBytes(request);
                if (iteration % 2 == 0)
                    request[0] = (byte)(1 + random.Next(16));
                link.Send(new FrameContext(1, (ushort)iteration), request, request.Length);
                link.Server.ProcessNextRequest();
                byte[] response = link.Response;
                Check(response != null && response.Length >= 2 && response.Length <= 253,
                    "Server fuzz always replies within PDU limit");
                if ((response[0] & 0x80) != 0)
                    Check(response.Length == 2 && response[0] == (byte)(request[0] | 0x80) &&
                        response[1] >= 1 && response[1] <= 4, "Server fuzz exception response");
                else
                    Check(response[0] == request[0], "Server fuzz echoes function");
            }
        }
    }

    private static void UartReadCounts()
    {
        var port = new System.IO.Ports.SerialPort();
        var channel = new VagabondK.nF.Modbus.Serial.SerialPortChannel(port);
        var buffer = new byte[257];
        port.Incoming.Enqueue(1);
        port.Incoming.Enqueue(3);
        Check(channel.Read(buffer, 2, 255, 100) == 2, "UART returns short buffered input");
        Check(port.LastReadCount == 2 && buffer[2] == 1 && buffer[3] == 3, "UART count and offset");
        port.Incoming.Enqueue(4);
        port.Incoming.Enqueue(5);
        Check(channel.Read(buffer, 0, 1, 100) == 1 && port.BytesToRead == 1, "UART caps count");
        Check(channel.Read(buffer, 0, 1, 100) == 1 && buffer[0] == 5, "UART retains remainder");
        Check(channel.Read(buffer, 0, 257, 5) == 0, "UART empty timeout");
        port.Incoming.Enqueue(1);
        port.ReadError = new InvalidOperationException("UART failure");
        Throws<InvalidOperationException>(() => channel.Read(buffer, 0, 1, 100));
        port.ReadError = new TimeoutException();
        Check(channel.Read(buffer, 0, 1, 100) == 0, "UART native timeout signal");
    }

    private static void Frames()
    {
        byte[] request = { 3, 0, 0, 0, 10 };
        byte[] frame = new byte[513];
        byte[] pdu = new byte[253];
        byte unit;
        int length = RtuFrame.Encode(1, request, request.Length, frame);
        Equal(new byte[] { 1, 3, 0, 0, 0, 10, 0xC5, 0xCD }, frame, length);
        Equal(request, pdu, RtuFrame.Decode(frame, length, pdu, out unit));
        Check(unit == 1, "RTU unit");
        frame[3] ^= 1;
        Throws<ModbusProtocolException>(() => RtuFrame.Decode(frame, length, pdu, out unit));
        Throws<ModbusProtocolException>(() => RtuFrame.Decode(frame, 3, pdu, out unit));
        Throws<ArgumentOutOfRangeException>(() => RtuFrame.Encode(248, request, 5, frame));

        length = AsciiFrame.Encode(1, request, request.Length, frame);
        Equal(Encoding.ASCII.GetBytes(":01030000000AF2\r\n"), frame, length);
        Equal(request, pdu, AsciiFrame.Decode(frame, length, pdu, out unit));
        frame[length - 3] ^= 1;
        Throws<ModbusProtocolException>(() => AsciiFrame.Decode(frame, length, pdu, out unit));
        frame[3] = (byte)'G';
        Throws<ModbusProtocolException>(() => AsciiFrame.Decode(frame, length, pdu, out unit));

        byte[] maximum = new byte[253];
        maximum[0] = 16;
        for (int i = 1; i < maximum.Length; i++)
            maximum[i] = (byte)i;
        length = RtuFrame.Encode(247, maximum, maximum.Length, frame);
        Check(length == 256, "Maximum RTU ADU");
        Equal(maximum, pdu, RtuFrame.Decode(frame, length, pdu, out unit));
        length = AsciiFrame.Encode(247, maximum, maximum.Length, frame);
        Check(length == 513, "Maximum ASCII ADU");
        Equal(maximum, pdu, AsciiFrame.Decode(frame, length, pdu, out unit));
        Throws<ArgumentOutOfRangeException>(() => AsciiFrame.Encode(1, new byte[254], 254, frame));
    }

    private static void TransportBoundaries()
    {
        byte[] pdu = { 3, 0, 0, 0, 1 };
        byte[] output = new byte[253];
        FrameContext context;
        var channel = new MemoryChannel();
        var tcp = new TcpTransport(channel);
        tcp.Send(new FrameContext(255, 0x1234), pdu, 5);
        Equal(new byte[] { 0x12, 0x34, 0, 0, 0, 6, 255, 3, 0, 0, 0, 1 },
            channel.Written.ToArray(), channel.Written.Count);
        byte[] frame = channel.Written.ToArray();
        channel.Feed(frame);
        channel.Feed(frame);
        channel.MaxRead = 1;
        Equal(pdu, output, tcp.Receive(output, out context));
        Check(context.UnitId == 255 && context.TransactionId == 0x1234, "TCP context");
        Equal(pdu, output, tcp.Receive(output, out context));
        Check(channel.Input.Count == 0, "No over-read into next TCP frame");
        frame[2] = 1;
        channel.Feed(frame);
        Throws<ModbusProtocolException>(() => tcp.Receive(output, out context));
        channel.Input.Clear();
        frame[2] = 0;
        frame[4] = 0xFF;
        channel.Feed(frame);
        Throws<ModbusProtocolException>(() => tcp.Receive(output, out context));
        channel.Input.Clear();
        frame[4] = 0;
        frame[5] = 1;
        channel.Feed(frame);
        Throws<ModbusProtocolException>(() => tcp.Receive(output, out context));
        channel.Input.Clear();
        channel.Feed(new byte[] { 0 });
        Throws<TimeoutException>(() => tcp.Receive(output, out context));
        channel.Input.Clear();
        channel.Feed(new byte[] { 0, 7, 0, 0, 0, 2, 9, 0x2B });
        Check(tcp.Receive(output, out context) == 1 && output[0] == 0x2B && context.UnitId == 9, "MBAP length 2 is the minimum accepted");
        var largest = new byte[7 + 253];
        largest[5] = 254;
        largest[7] = 0x10;
        largest[259] = 0xA5;
        channel.Feed(largest);
        Check(tcp.Receive(output, out context) == 253 && output[252] == 0xA5 && channel.Input.Count == 0, "MBAP length 254 is the maximum accepted");
        channel.Feed(new byte[] { 0, 1, 0, 0, 0, 255, 1 });
        Throws<ModbusProtocolException>(() => tcp.Receive(output, out context));
        channel.Input.Clear();

        channel = new MemoryChannel();
        var ascii = new AsciiTransport(channel);
        ascii.Send(new FrameContext(1, 0), pdu, 5);
        byte[] asciiFrame = channel.Written.ToArray();
        channel.Feed(Encoding.ASCII.GetBytes("noise:FFFF"));
        channel.Feed(asciiFrame);
        channel.Feed(asciiFrame);
        Equal(pdu, output, ascii.Receive(output, out context));
        Equal(pdu, output, ascii.Receive(output, out context));
        channel.Feed(Encoding.ASCII.GetBytes(":" + new string('0', 513)));
        Throws<ModbusProtocolException>(() => ascii.Receive(output, out context));
        channel.Input.Clear();
        channel.Feed(Encoding.ASCII.GetBytes(":0103\r\n"));
        Throws<ModbusProtocolException>(() => ascii.Receive(output, out context));

        channel = new MemoryChannel { MaxRead = 1 };
        var rtu = new RtuTransport(channel, 9600);
        rtu.Send(new FrameContext(1, 0), pdu, 5);
        channel.Feed(channel.Written.ToArray());
        Equal(pdu, output, rtu.Receive(output, out context));
        Throws<TimeoutException>(() => rtu.Receive(output, out context));
        channel.Feed(new byte[257]);
        Throws<ModbusProtocolException>(() => rtu.Receive(output, out context));
    }

    private static void RtuLengthBoundaries()
    {
        var output = new byte[253];
        var frame = new byte[256];
        FrameContext context;
        foreach (RtuReceiveMode mode in new[] { RtuReceiveMode.RequestLength, RtuReceiveMode.ResponseLength })
        {
            foreach (byte function in new byte[] { 1, 2, 3, 4, 5, 6, 15, 16 })
            {
                var channel = new MemoryChannel { MaxRead = 1 };
                var transport = new RtuTransport(channel, 9600, mode);
                int size = mode == RtuReceiveMode.RequestLength && function >= 15 ||
                    mode == RtuReceiveMode.ResponseLength && function <= 4 ? 252 : 5;
                var pdu = new byte[size];
                pdu[0] = function;
                if (size == 252)
                {
                    if (mode == RtuReceiveMode.RequestLength)
                        pdu[5] = 246;
                    else
                        pdu[1] = 250;
                    for (int i = 6; i < size; i++)
                        pdu[i] = (byte)i;
                }
                int length = RtuFrame.Encode(1, pdu, size, frame);
                for (int repeat = 0; repeat < 2; repeat++)
                    for (int i = 0; i < length; i++)
                        channel.Input.Enqueue(frame[i]);
                Equal(pdu, output, transport.Receive(output, out context));
                Check(context.UnitId == 1 && context.TransactionId == 0, "Length-framed context");
                Check(channel.Input.Count == length, "No over-read into next length-framed ADU");
                Equal(pdu, output, transport.Receive(output, out context));
                Check(channel.Input.Count == 0, "Two length-framed ADUs consumed exactly");
                Throws<TimeoutException>(() => transport.Receive(output, out context));
                frame[length - 1] ^= 1;
                for (int i = 0; i < length; i++)
                    channel.Input.Enqueue(frame[i]);
                Throws<ModbusProtocolException>(() => transport.Receive(output, out context));
                for (int i = 0; i < length - 1; i++)
                    channel.Input.Enqueue(frame[i]);
                Throws<TimeoutException>(() => transport.Receive(output, out context));
            }
        }

        var request = new byte[252];
        request[0] = 16;
        request[4] = 123;
        request[5] = 246;
        int requestLength = RtuFrame.Encode(1, request, request.Length, frame);
        var batched = new DelayedBatchChannel(frame, requestLength, 120, 140);
        var receiver = new RtuTransport(batched, 9600, RtuReceiveMode.RequestLength, timeoutMilliseconds: 2000);
        Equal(request, output, receiver.Receive(output, out context));
        Check(batched.Delays == 2, "Two delayed UART batches assembled");

        batched = new DelayedBatchChannel(frame, requestLength, 120, 140);
        var legacy = new RtuTransport(batched, 9600, timeoutMilliseconds: 2000);
        Throws<ModbusProtocolException>(() => legacy.Receive(output, out context));

        batched = new DelayedBatchChannel(frame, requestLength, 120, 140);
        receiver = new RtuTransport(batched, 9600, RtuReceiveMode.RequestLength, timeoutMilliseconds: 220);
        Throws<TimeoutException>(() => receiver.Receive(output, out context));

        var memory = new MemoryChannel();
        receiver = new RtuTransport(memory, 9600, RtuReceiveMode.RequestLength);
        memory.Feed(new byte[] { 1, 16, 0, 0, 0, 123, 248 });
        Throws<ModbusProtocolException>(() => receiver.Receive(output, out context));
        memory.Input.Clear();
        memory.Feed(new byte[] { 1 });
        Throws<TimeoutException>(() => receiver.Receive(output, out context));
        memory.Input.Clear();
        memory.Feed(new byte[] { 1, 16, 0 });
        Throws<TimeoutException>(() => receiver.Receive(output, out context));

        receiver = new RtuTransport(memory, 9600, RtuReceiveMode.ResponseLength);
        memory.Feed(new byte[] { 1, 3, 252 });
        Throws<ModbusProtocolException>(() => receiver.Receive(output, out context));
        memory.Input.Clear();
        int exceptionLength = RtuFrame.Encode(1, new byte[] { 0x83, 2 }, 2, frame);
        for (int i = 0; i < exceptionLength; i++)
            memory.Input.Enqueue(frame[i]);
        Equal(new byte[] { 0x83, 2 }, output, receiver.Receive(output, out context));

        receiver = new RtuTransport(memory, 9600, RtuReceiveMode.RequestLength);
        int unknownLength = RtuFrame.Encode(1, new byte[] { 0x7F }, 1, frame);
        for (int i = 0; i < unknownLength; i++)
            memory.Input.Enqueue(frame[i]);
        Equal(new byte[] { 0x7F }, output, receiver.Receive(output, out context));
        Throws<ArgumentOutOfRangeException>(() =>
            new RtuTransport(memory, 9600, (RtuReceiveMode)100));
    }

    // 예상 가능한 잘못된 프레임은 예외 없이 상태로 반환하고 출력 길이·Context를 초기화하며 후속 정상 입력은 처리해야 합니다.
    private static void ReceiveStatuses()
    {
        var pdu = new byte[253];
        var frame = new byte[257];
        var channel = new MemoryChannel { MaxRead = 1 };
        var receiver = new RtuTransport(channel, 9600, RtuReceiveMode.RequestLength, timeoutMilliseconds: 10);
        FrameContext context;
        int length;
        int exceptions = 0;
        EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> onException =
            (sender, args) => exceptions++;
        AppDomain.CurrentDomain.FirstChanceException += onException;
        try
        {
            Check(receiver.TryReceive(pdu, out context, out length) == ReceiveStatus.Idle &&
                length == 0 && context.UnitId == 0, "Status idle clears output");
            channel.Feed(new byte[] { 1 });
            Check(receiver.TryReceive(pdu, out context, out length) == ReceiveStatus.Timeout &&
                length == 0, "Status partial header timeout");
            channel.Feed(new byte[] { 1, 16, 0, 0, 0, 1, 2, 0 });
            Check(receiver.TryReceive(pdu, out context, out length) == ReceiveStatus.Timeout,
                "Status partial body timeout");
            channel.Feed(new byte[] { 1, 16, 0, 0, 0, 1, 248 });
            Check(receiver.TryReceive(pdu, out context, out length) == ReceiveStatus.FrameTooLong,
                "Status oversized frame");
            int count = RtuFrame.Encode(1, new byte[] { 3, 0, 0, 0, 1 }, 5, frame);
            frame[count - 1] ^= 1;
            for (int i = 0; i < count; i++)
                channel.Input.Enqueue(frame[i]);
            pdu[0] = 99;
            Check(receiver.TryReceive(pdu, out context, out length) == ReceiveStatus.CrcMismatch &&
                length == 0 && context.UnitId == 0 && pdu[0] == 99, "Status CRC leaves payload untouched");
            frame[count - 1] ^= 1;
            frame[0] = 248;
            ushort crc = RtuFrame.ComputeCrc(frame, 0, count - 2);
            frame[count - 2] = (byte)crc;
            frame[count - 1] = (byte)(crc >> 8);
            for (int i = 0; i < count; i++)
                channel.Input.Enqueue(frame[i]);
            Check(receiver.TryReceive(pdu, out context, out length) == ReceiveStatus.InvalidUnitId,
                "Status invalid unit");
            count = RtuFrame.Encode(1, new byte[] { 3, 0, 0, 0, 1 }, 5, frame);
            for (int i = 0; i < count; i++)
                channel.Input.Enqueue(frame[i]);
            Check(receiver.TryReceive(pdu, out context, out length) == ReceiveStatus.Success &&
                length == 5 && context.UnitId == 1, "Status recovery after invalid input");

            receiver = new RtuTransport(channel, 9600, timeoutMilliseconds: 10);
            channel.Feed(new byte[] { 1, 3, 0 });
            Check(receiver.TryReceive(pdu, out context, out length) == ReceiveStatus.InvalidLength,
                "Status invalid gap-delimited length");
            channel.Feed(new byte[257]);
            Check(receiver.TryReceive(pdu, out context, out length) == ReceiveStatus.FrameTooLong,
                "Status oversized gap-delimited frame");

            byte unit;
            Check(RtuFrame.TryDecode(frame, 0, pdu, out unit, out length) == ReceiveStatus.InvalidLength &&
                unit == 0 && length == 0, "Codec status invalid length");
            var deadline = new OperationDeadline(1);
            // Windows의 TickCount64 해상도는 약 15.6ms입니다.
            Thread.Sleep(40);
            int remaining;
            Check(!deadline.TryGetRemainingMilliseconds(out remaining) && remaining == 0,
                "Nonthrowing expired deadline");
            var port = new System.IO.Ports.SerialPort();
            var uart = new VagabondK.nF.Modbus.Serial.SerialPortChannel(port);
            Check(uart.Read(frame, 0, frame.Length, 2) == 0, "UART idle has no internal exception");

            receiver = new RtuTransport(channel, 9600, RtuReceiveMode.RequestLength, timeoutMilliseconds: 10);
            var store = new ArrayDataStore(1, 0, 1, 0);
            var server = new ModbusServer(1, receiver, store);
            Check(server.TryProcessNextRequest() == ReceiveStatus.Idle && channel.Written.Count == 0,
                "Polling server idle");
            count = RtuFrame.Encode(1, new byte[] { 6, 0, 0, 0, 42 }, 5, frame);
            for (int i = 0; i < count; i++)
                channel.Input.Enqueue(frame[i]);
            Check(server.TryProcessNextRequest() == ReceiveStatus.Success && channel.Written.Count == 8,
                "Polling server successful write");
            var values = new ushort[1];
            store.ReadRegisters(false, 0, 1, values);
            Check(values[0] == 42, "Polling server updates store");
            channel.Written.Clear();
            frame[count - 1] ^= 1;
            for (int i = 0; i < count; i++)
                channel.Input.Enqueue(frame[i]);
            Check(server.TryProcessNextRequest() == ReceiveStatus.CrcMismatch &&
                channel.Written.Count == 0, "Polling server rejects CRC without response");
            count = RtuFrame.Encode(0, new byte[] { 6, 0, 0, 0, 43 }, 5, frame);
            for (int i = 0; i < count; i++)
                channel.Input.Enqueue(frame[i]);
            Check(server.TryProcessNextRequest() == ReceiveStatus.Success && channel.Written.Count == 0,
                "Polling server broadcast");
            store.ReadRegisters(false, 0, 1, values);
            Check(values[0] == 43, "Polling broadcast updates store");
            count = RtuFrame.Encode(2, new byte[] { 6, 0, 0, 0, 44 }, 5, frame);
            for (int i = 0; i < count; i++)
                channel.Input.Enqueue(frame[i]);
            Check(server.TryProcessNextRequest() == ReceiveStatus.Success && channel.Written.Count == 0,
                "Polling server ignores other units");
            store.ReadRegisters(false, 0, 1, values);
            Check(values[0] == 43, "Other unit does not modify store");

            var asciiChannel = new MemoryChannel();
            var ascii = new AsciiTransport(asciiChannel, 20);
            var asciiFrame = new byte[514];
            Check(ascii.TryReceive(pdu, out context, out length) == ReceiveStatus.Idle &&
                length == 0 && context.UnitId == 0, "ASCII status idle");
            asciiChannel.Feed(new byte[] { (byte)':', (byte)'0' });
            Check(ascii.TryReceive(pdu, out context, out length) == ReceiveStatus.Timeout, "ASCII partial timeout");
            asciiChannel.Feed(new byte[] { 1 });
            Check(ascii.TryReceive(pdu, out context, out length) == ReceiveStatus.Timeout, "ASCII noise is not idle");
            count = AsciiFrame.Encode(1, new byte[] { 6, 0, 0, 0, 42 }, 5, asciiFrame);
            asciiFrame[count - 3] = asciiFrame[count - 3] == '0' ? (byte)'1' : (byte)'0';
            pdu[0] = 99;
            for (int i = 0; i < count; i++)
                asciiChannel.Input.Enqueue(asciiFrame[i]);
            Check(ascii.TryReceive(pdu, out context, out length) == ReceiveStatus.LrcMismatch &&
                length == 0 && context.UnitId == 0 && pdu[0] == 99, "ASCII LRC preserves output");
            asciiFrame[3] = (byte)'G';
            for (int i = 0; i < count; i++)
                asciiChannel.Input.Enqueue(asciiFrame[i]);
            Check(ascii.TryReceive(pdu, out context, out length) == ReceiveStatus.InvalidCharacter, "ASCII hex status");
            count = AsciiFrame.Encode(1, new byte[] { 6, 0, 0, 0, 42 }, 5, asciiFrame);
            asciiFrame[count - 2] = (byte)'0';
            for (int i = 0; i < count; i++)
                asciiChannel.Input.Enqueue(asciiFrame[i]);
            Check(ascii.TryReceive(pdu, out context, out length) == ReceiveStatus.InvalidLength, "ASCII CRLF status");
            for (int i = 0; i < asciiFrame.Length; i++)
                asciiFrame[i] = (byte)'0';
            asciiFrame[0] = (byte)':';
            asciiChannel.Feed(asciiFrame);
            Check(ascii.TryReceive(pdu, out context, out length) == ReceiveStatus.FrameTooLong, "ASCII overflow status");
            byte[] invalidUnit = System.Text.Encoding.ASCII.GetBytes(":F8030000000104\r\n");
            asciiChannel.Feed(invalidUnit);
            Check(ascii.TryReceive(pdu, out context, out length) == ReceiveStatus.InvalidUnitId, "ASCII unit status");
            var asciiServer = new ModbusServer(1, ascii, store);
            asciiChannel.Feed(new byte[] { (byte)':', (byte)'0' });
            count = AsciiFrame.Encode(1, new byte[] { 6, 0, 0, 0, 45 }, 5, asciiFrame);
            for (int i = 0; i < count; i++)
                asciiChannel.Input.Enqueue(asciiFrame[i]);
            Check(asciiServer.TryProcessNextRequest() == ReceiveStatus.Success, "ASCII polling colon restart");
            store.ReadRegisters(false, 0, 1, values);
            Check(values[0] == 45 && asciiChannel.Written.Count == count, "ASCII polling reply/store");
            Check(AsciiFrame.TryDecode(asciiFrame, 0, pdu, out unit, out length) == ReceiveStatus.InvalidLength &&
                unit == 0 && length == 0, "ASCII codec output reset");
            var maximumPdu = new byte[253];
            count = AsciiFrame.Encode(247, maximumPdu, 253, asciiFrame);
            Check(count == 513 && AsciiFrame.TryDecode(asciiFrame, count, pdu, out unit, out length) ==
                ReceiveStatus.Success && unit == 247 && length == 253, "ASCII maximum codec status");
            count = AsciiFrame.Encode(1, new byte[] { 7 }, 1, asciiFrame);
            Check(count == 9 && AsciiFrame.TryDecode(asciiFrame, count, pdu, out unit, out length) ==
                ReceiveStatus.Success && unit == 1 && length == 1 && pdu[0] == 7, "ASCII minimum frame accepted");
            var shortFrame = System.Text.Encoding.ASCII.GetBytes(":01FF\r\n");
            Check(AsciiFrame.TryDecode(shortFrame, shortFrame.Length, pdu, out unit, out length) ==
                ReceiveStatus.InvalidLength && unit == 0 && length == 0, "ASCII frame below minimum rejected");
            Check(AsciiFrame.TryDecode(asciiFrame, 8, pdu, out unit, out length) == ReceiveStatus.InvalidLength,
                "ASCII even/truncated frame rejected");
            Check(AsciiFrame.TryDecode(maximumPdu, 0, pdu, out unit, out length) == ReceiveStatus.InvalidLength,
                "ASCII empty frame rejected");
            count = AsciiFrame.Encode(1, new byte[] { 6, 0, 0, 0, 45 }, 5, asciiFrame);
            var asciiBatches = new DelayedBatchChannel(asciiFrame, count, 7, 30);
            var delayedAscii = new AsciiTransport(asciiBatches, 1000);
            Check(delayedAscii.TryReceive(pdu, out context, out length) == ReceiveStatus.Success &&
                asciiBatches.Delays == 2, "ASCII sub-second gaps accepted");
            asciiBatches = new DelayedBatchChannel(asciiFrame, count, 7, 100);
            delayedAscii = new AsciiTransport(asciiBatches, 150);
            Check(delayedAscii.TryReceive(pdu, out context, out length) == ReceiveStatus.Timeout,
                "ASCII shares total deadline across characters");
            asciiBatches = new DelayedBatchChannel(asciiFrame, count, 7, 1100);
            delayedAscii = new AsciiTransport(asciiBatches, 3000);
            Check(delayedAscii.TryReceive(pdu, out context, out length) == ReceiveStatus.Timeout,
                "ASCII character gap capped at one second");
            var restartBytes = new byte[count + 1];
            restartBytes[0] = (byte)':';
            Array.Copy(asciiFrame, 0, restartBytes, 1, count);
            asciiBatches = new DelayedBatchChannel(restartBytes, restartBytes.Length, 1, 20);
            delayedAscii = new AsciiTransport(asciiBatches, 70);
            Check(delayedAscii.TryReceive(pdu, out context, out length) == ReceiveStatus.Timeout,
                "ASCII colon restart does not reset total deadline");
            count = AsciiFrame.Encode(0, new byte[] { 6, 0, 0, 0, 46 }, 5, asciiFrame);
            asciiChannel.Written.Clear();
            for (int i = 0; i < count; i++)
                asciiChannel.Input.Enqueue(asciiFrame[i]);
            Check(asciiServer.TryProcessNextRequest() == ReceiveStatus.Success &&
                asciiChannel.Written.Count == 0, "ASCII polling broadcast has no reply");
            store.ReadRegisters(false, 0, 1, values);
            Check(values[0] == 46, "ASCII polling broadcast updates store");
            count = AsciiFrame.Encode(2, new byte[] { 6, 0, 0, 0, 47 }, 5, asciiFrame);
            for (int i = 0; i < count; i++)
                asciiChannel.Input.Enqueue(asciiFrame[i]);
            Check(asciiServer.TryProcessNextRequest() == ReceiveStatus.Success &&
                asciiChannel.Written.Count == 0, "ASCII polling ignores other unit");
            store.ReadRegisters(false, 0, 1, values);
            Check(values[0] == 46, "ASCII other unit preserves store");
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= onException;
        }
        Check(exceptions == 0, "Expected receive outcomes must never throw internally");

        var failedPort = new System.IO.Ports.SerialPort();
        var failedReceiver = new RtuTransport(new VagabondK.nF.Modbus.Serial.SerialPortChannel(failedPort),
            9600, RtuReceiveMode.RequestLength);
        failedPort.Incoming.Enqueue(1);
        failedPort.ReadError = new InvalidOperationException("UART failed");
        Throws<InvalidOperationException>(() => failedReceiver.TryReceive(pdu, out context, out length));
        var failedServer = new ModbusServer(1, failedReceiver, new ArrayDataStore(1, 0, 1, 0));
        Throws<InvalidOperationException>(() => failedServer.TryProcessNextRequest());
        var failedAscii = new AsciiTransport(new VagabondK.nF.Modbus.Serial.SerialPortChannel(failedPort));
        Throws<InvalidOperationException>(() => failedAscii.TryReceive(pdu, out context, out length));
        Throws<ArgumentOutOfRangeException>(() => failedAscii.TryReceive(new byte[252], out context, out length));
        var legacyAsciiChannel = new MemoryChannel();
        var legacyAscii = new AsciiTransport(legacyAsciiChannel, 10);
        Throws<TimeoutException>(() => legacyAscii.Receive(pdu, out context));
        legacyAsciiChannel.Feed(System.Text.Encoding.ASCII.GetBytes(":010300000001FA\r\n"));
        Throws<ModbusProtocolException>(() => legacyAscii.Receive(pdu, out context));
        legacyAsciiChannel.Feed(System.Text.Encoding.ASCII.GetBytes(":01G300000001FB\r\n"));
        Throws<ModbusProtocolException>(() => legacyAscii.Receive(pdu, out context));
        Throws<ArgumentOutOfRangeException>(() => receiver.TryReceive(new byte[252], out context, out length));
        var unsupported = new ModbusServer(1, new RequestTransport(false), new ArrayDataStore(1, 0, 1, 0));
        Throws<NotSupportedException>(() => unsupported.TryProcessNextRequest());
    }

    private static void Roles(bool serial)
    {
        var store = new ArrayDataStore(2000, 2000, 125, 125);
        var clientSide = new RequestTransport(serial);
        var serverSide = new ServerTransport(clientSide);
        var server = new ModbusServer(1, serverSide, store);
        clientSide.Server = server;
        var client = new ModbusClient(clientSide);

        bool[] bits = new bool[2002];
        client.WriteSingleCoil(1, 2, true);
        client.ReadCoils(1, 2, 1, bits, 1);
        Check(bits[1], "Single coil");
        for (int i = 0; i < bits.Length; i++)
            bits[i] = i % 3 == 0;
        client.WriteMultipleCoils(1, 0, 1968, bits, 1);
        bool[] result = new bool[2000];
        client.ReadCoils(1, 0, 2000, result, 0);
        for (int i = 0; i < 1968; i++)
            Check(result[i] == bits[i + 1], "Multiple coils");
        store.SetDiscreteInputs(0, 2000, bits);
        client.ReadDiscreteInputs(1, 0, 2000, result, 0);
        for (int i = 0; i < 2000; i++)
            Check(result[i] == bits[i], "Discrete input");

        ushort[] registers = new ushort[127];
        for (int i = 0; i < registers.Length; i++)
            registers[i] = (ushort)(65535 - i);
        client.WriteSingleRegister(1, 0, 65535);
        client.ReadHoldingRegisters(1, 0, 1, registers, 0);
        Check(registers[0] == 65535, "Unsigned register");
        client.WriteMultipleRegisters(1, 1, 123, registers, 2);
        ushort[] read = new ushort[125];
        client.ReadHoldingRegisters(1, 0, 125, read, 0);
        for (int i = 0; i < 123; i++)
            Check(read[i + 1] == registers[i + 2], "Multiple registers");
        store.SetInputRegisters(0, 125, registers);
        client.ReadInputRegisters(1, 0, 125, read, 0);
        for (int i = 0; i < 125; i++)
            Check(read[i] == registers[i], "Input register");

        Throws<ModbusException>(() => client.ReadHoldingRegisters(1, 125, 1, read, 0));
        if (serial)
        {
            int receives = clientSide.ReceiveCount;
            client.WriteSingleRegister(0, 0, 123);
            client.WriteSingleCoil(0, 0, false);
            client.WriteMultipleCoils(0, 0, 2, bits, 0);
            client.WriteMultipleRegisters(0, 1, 2, registers, 0);
            Check(clientSide.ReceiveCount == receives, "Broadcast must not wait for response");
            client.ReadHoldingRegisters(1, 0, 1, read, 0);
            Check(read[0] == 123, "Broadcast applied");
            Throws<ArgumentOutOfRangeException>(() => client.ReadCoils(0, 0, 1, bits, 0));
            Throws<ArgumentOutOfRangeException>(() => client.WriteSingleRegister(248, 0, 1));
        }
        else
        {
            var zeroLink = new RequestTransport(false);
            zeroLink.Server = new ModbusServer(0, new ServerTransport(zeroLink), store);
            new ModbusClient(zeroLink).ReadHoldingRegisters(0, 0, 1, read, 0);
            Check(zeroLink.ReceiveCount == 1, "TCP unit zero is not a broadcast");
        }
    }

    private static void ServerValidation()
    {
        var store = new ArrayDataStore(4, 0, 4, 0);
        var link = new RequestTransport(false);
        link.Server = new ModbusServer(1, new ServerTransport(link), store);
        CheckException(link, new byte[] { 99 }, ExceptionCode.IllegalFunction);
        CheckException(link, new byte[] { 3 }, ExceptionCode.IllegalDataValue);
        CheckException(link, new byte[] { 3, 0, 0, 0, 0 }, ExceptionCode.IllegalDataValue);
        CheckException(link, new byte[] { 3, 0xFF, 0xFF, 0, 2 }, ExceptionCode.IllegalDataAddress);
        CheckException(link, new byte[] { 5, 0, 0, 0, 1 }, ExceptionCode.IllegalDataValue);
        CheckException(link, new byte[] { 16, 0, 0, 0, 2, 2, 0, 1 }, ExceptionCode.IllegalDataValue);
        CheckException(link, new byte[] { 15, 0, 0, 0, 2, 0 }, ExceptionCode.IllegalDataValue);
        CheckException(link, new byte[] { 16, 0, 3, 0, 2, 4, 0, 1, 0, 2 }, ExceptionCode.IllegalDataAddress);
        ushort[] read = new ushort[4];
        store.ReadRegisters(false, 0, 4, read);
        for (int i = 0; i < 4; i++)
            Check(read[i] == 0, "Failed range must not partially write");
        link.Response = null;
        link.Send(new FrameContext(2, 1), new byte[] { 3, 0, 0, 0, 1 }, 5);
        link.Server.ProcessNextRequest();
        Check(link.Response == null, "Ignore another unit");
        Throws<ArgumentOutOfRangeException>(() => new ModbusServer(0, new RequestTransport(true), store));
        ValidatorAndBroadcast();
    }

    private sealed class ValidatingStore : IModbusDataStore, IModbusDataStoreValidator
    {
        public readonly ArrayDataStore Inner = new ArrayDataStore(16, 16, 16, 16);
        public int Validations, Writes;
        public byte LastFunction;
        public ushort LastAddress, LastCount, Limit = 8;

        public bool IsValidRange(FunctionCode function, ushort address, ushort count)
        {
            Validations++;
            LastFunction = (byte)function;
            LastAddress = address;
            LastCount = count;
            return address + count <= Limit;
        }

        public void ReadBits(bool discreteInputs, ushort address, ushort count, bool[] values)
        { Inner.ReadBits(discreteInputs, address, count, values); }

        public void ReadRegisters(bool inputRegisters, ushort address, ushort count, ushort[] values)
        { Inner.ReadRegisters(inputRegisters, address, count, values); }

        public void WriteCoils(ushort address, ushort count, bool[] values)
        { Writes++; Inner.WriteCoils(address, count, values); }

        public void WriteHoldingRegisters(ushort address, ushort count, ushort[] values)
        { Writes++; Inner.WriteHoldingRegisters(address, count, values); }
    }

    private static void CheckValidation(ValidatingStore store, byte function, ushort address, ushort count)
    {
        Check(store.LastFunction == function && store.LastAddress == address && store.LastCount == count,
            "Validator receives the requested range");
    }

    private static void BroadcastFails(RequestTransport link, byte[] request, ExceptionCode code)
    {
        bool thrown = false;
        try
        {
            link.Send(new FrameContext(0, 0), request, request.Length);
        }
        catch (ModbusException error)
        {
            thrown = error.Code == code;
        }
        Check(thrown && link.Response == null, "Broadcast failure is reported locally without a reply");
    }

    private static void ValidatorAndBroadcast()
    {
        var store = new ValidatingStore();
        var link = new RequestTransport(false);
        link.Server = new ModbusServer(1, new ServerTransport(link), store);
        foreach (byte function in new byte[] { 1, 2, 3, 4 })
        {
            CheckException(link, new byte[] { function, 0, 7, 0, 2 }, ExceptionCode.IllegalDataAddress);
            CheckValidation(store, function, 7, 2);
        }
        CheckException(link, new byte[] { 5, 0, 8, 0xFF, 0 }, ExceptionCode.IllegalDataAddress);
        CheckValidation(store, 5, 8, 1);
        CheckException(link, new byte[] { 6, 0, 8, 0, 1 }, ExceptionCode.IllegalDataAddress);
        CheckValidation(store, 6, 8, 1);
        CheckException(link, new byte[] { 15, 0, 7, 0, 2, 1, 3 }, ExceptionCode.IllegalDataAddress);
        CheckValidation(store, 15, 7, 2);
        CheckException(link, new byte[] { 16, 0, 7, 0, 2, 4, 0, 1, 0, 2 }, ExceptionCode.IllegalDataAddress);
        CheckValidation(store, 16, 7, 2);
        Check(store.Writes == 0, "Validator rejection prevents writes");

        int validations = store.Validations;
        CheckException(link, new byte[] { 15, 0xFF, 0xFF, 0, 2, 1, 3 }, ExceptionCode.IllegalDataAddress);
        CheckException(link, new byte[] { 16, 0xFF, 0xFF, 0, 2, 4, 0, 1, 0, 2 }, ExceptionCode.IllegalDataAddress);
        CheckException(link, new byte[] { 15, 0, 0, 0, 0, 0 }, ExceptionCode.IllegalDataValue);
        CheckException(link, new byte[] { 15, 0, 0, 0x07, 0xB1, 0xF7 }, ExceptionCode.IllegalDataValue);
        CheckException(link, new byte[] { 16, 0, 0, 0, 0, 0 }, ExceptionCode.IllegalDataValue);
        CheckException(link, new byte[] { 16, 0, 0, 0, 124, 248 }, ExceptionCode.IllegalDataValue);
        CheckException(link, new byte[] { 15, 0, 0, 0, 9, 1, 0xFF }, ExceptionCode.IllegalDataValue);
        CheckException(link, new byte[] { 15, 0, 0, 0, 8, 1, 0xFF, 0 }, ExceptionCode.IllegalDataValue);
        CheckException(link, new byte[] { 16, 0, 0, 0, 1, 2, 0 }, ExceptionCode.IllegalDataValue);
        CheckException(link, new byte[] { 6, 0, 0, 0, 1, 0 }, ExceptionCode.IllegalDataValue);
        CheckException(link, new byte[] { 1, 0, 0, 0x07, 0xD1 }, ExceptionCode.IllegalDataValue);
        CheckException(link, new byte[] { 3, 0, 0, 0, 126 }, ExceptionCode.IllegalDataValue);
        Check(store.Validations == validations && store.Writes == 0, "Malformed requests fail before validation");

        var bits = new bool[16];
        link.Send(new FrameContext(1, 7), new byte[] { 15, 0, 1, 0, 3, 1, 0xFF }, 7);
        var response = new byte[253];
        FrameContext context;
        int length = link.Receive(response, out context);
        Equal(new byte[] { 15, 0, 1, 0, 3 }, response, length);
        store.Inner.ReadBits(false, 0, 6, bits);
        Check(!bits[0] && bits[1] && bits[2] && bits[3] && !bits[4] && !bits[5], "FC15 writes exactly the requested coils");
        CheckValidation(store, 15, 1, 3);
        link.Send(new FrameContext(1, 8), new byte[] { 16, 0, 6, 0, 2, 4, 0x12, 0x34, 0xAB, 0xCD }, 10);
        length = link.Receive(response, out context);
        Equal(new byte[] { 16, 0, 6, 0, 2 }, response, length);
        var registers = new ushort[4];
        store.Inner.ReadRegisters(false, 5, 4, registers);
        Check(registers[0] == 0 && registers[1] == 0x1234 && registers[2] == 0xABCD && registers[3] == 0,
            "FC16 writes exactly the requested registers");

        var serial = new RequestTransport(true);
        var serialStore = new ValidatingStore();
        serial.Server = new ModbusServer(1, new ServerTransport(serial), serialStore);
        BroadcastFails(serial, new byte[] { 6, 0, 8, 0, 1 }, ExceptionCode.IllegalDataAddress);
        BroadcastFails(serial, new byte[] { 6, 0, 0, 0, 1, 0 }, ExceptionCode.IllegalDataValue);
        BroadcastFails(serial, new byte[] { 5, 0, 0, 0, 1 }, ExceptionCode.IllegalDataValue);
        BroadcastFails(serial, new byte[] { 16, 0, 7, 0, 2, 4, 0, 1, 0, 2 }, ExceptionCode.IllegalDataAddress);
        Check(serialStore.Writes == 0, "Rejected broadcast does not write");
        int before = serialStore.Validations;
        foreach (byte function in new byte[] { 1, 2, 3, 4, 7, 99 })
        {
            serial.Send(new FrameContext(0, 0), new byte[] { function, 0, 0, 0, 1 }, 5);
            Check(serial.Response == null, "Broadcast read/unsupported request is ignored");
        }
        Check(serialStore.Validations == before, "Ignored broadcast is not validated");
        serial.Send(new FrameContext(0, 0), new byte[] { 6, 0, 2, 0x55, 0xAA }, 5);
        serialStore.Inner.ReadRegisters(false, 2, 1, registers);
        Check(serial.Response == null && registers[0] == 0x55AA && serialStore.Writes == 1, "Valid broadcast writes silently");

        var small = new RequestTransport(true);
        small.Server = new ModbusServer(1, new ServerTransport(small), new ArrayDataStore(4, 0, 4, 0));
        BroadcastFails(small, new byte[] { 6, 0, 10, 0, 1 }, ExceptionCode.IllegalDataAddress);
        BroadcastFails(small, new byte[] { 15, 0, 3, 0, 2, 1, 3 }, ExceptionCode.IllegalDataAddress);
    }

    private static void CheckException(RequestTransport link, byte[] request, ExceptionCode code)
    {
        link.Send(new FrameContext(1, 42), request, request.Length);
        byte[] response = new byte[253];
        FrameContext context;
        int length = link.Receive(response, out context);
        Equal(new byte[] { (byte)(request[0] | 0x80), (byte)code }, response, length);
        Check(context.TransactionId == 42, "Echo transaction on exception");
    }

    private static void ClientValidation()
    {
        var transport = new FaultTransport();
        var client = new ModbusClient(transport);
        ushort[] values = new ushort[126];
        Throws<ArgumentOutOfRangeException>(() => client.ReadHoldingRegisters(1, 0, 126, values, 0));
        Throws<ArgumentOutOfRangeException>(() => client.ReadHoldingRegisters(1, 65535, 2, values, 0));
        Throws<ArgumentOutOfRangeException>(() => client.WriteMultipleRegisters(1, 0, 124, values, 0));
        Throws<ArgumentOutOfRangeException>(() => client.ReadHoldingRegisters(1, 0, 1, values, -1));
        Throws<ArgumentNullException>(() => client.ReadHoldingRegisters(1, 0, 1, null, 0));
        for (int mode = 0; mode < 7; mode++)
        {
            transport.Mode = mode;
            Throws<ModbusProtocolException>(() => client.ReadHoldingRegisters(1, 0, 1, values, 0));
        }
        transport.Mode = 7;
        Throws<ModbusException>(() => client.ReadHoldingRegisters(1, 0, 1, values, 0));
        transport.Mode = 8;
        Throws<ModbusProtocolException>(() => client.WriteSingleRegister(1, 0, 1));
    }

    private static void TcpLoopback(int iterations = 1)
    {
        using (var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        using (var connecting = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);
            connecting.Connect(listener.LocalEndPoint);
            using (Socket accepted = listener.Accept())
            {
                connecting.NoDelay = accepted.NoDelay = true;
                var serverTransport = new TcpTransport(new SocketChannel(accepted), 2000);
                var server = new ModbusServer(1, serverTransport, new ArrayDataStore(1, 1, 1, 1));
                Exception failure = null;
                var thread = new Thread(() =>
                {
                    try
                    {
                        for (int i = 0; i < iterations; i++)
                        {
                            server.ProcessNextRequest();
                            server.ProcessNextRequest();
                        }
                    }

                    catch (Exception error)
                    {
                        failure = error;
                    }
                });
                thread.Start();
                try
                {
                    var client = new ModbusClient(new TcpTransport(new SocketChannel(connecting), 2000));
                    var read = new ushort[1];
                    for (int i = 0; i < iterations; i++)
                    {
                        long started = SoakRunner.StartExchange();
                        ushort value = (ushort)(0xFEDC ^ i);
                        client.WriteSingleRegister(1, 0, value);
                        client.ReadHoldingRegisters(1, 0, 1, read, 0);
                        Check(read[0] == value, "Real TCP socket round trip");
                        SoakRunner.EndExchange(started);
                    }
                }
                finally
                {
                    Check(thread.Join(5000), "Server thread completed");
                }
                if (failure != null)
                    throw new Exception("TCP server failure", failure);
                var channel = new SocketChannel(accepted);
                Check(channel.Read(new byte[1], 0, 1, 10) == 0, "Socket idle timeout");
                connecting.Shutdown(SocketShutdown.Both);
                Throws<ModbusProtocolException>(() => channel.Read(new byte[1], 0, 1, 1000));
            }
        }
    }

    private static void TcpFaults()
    {
        var output = new byte[253];
        FrameContext context;
        TcpPair((remote, local) =>
        {
            var channel = new SocketChannel(local);
            var buffer = new byte[4];
            Throws<ArgumentOutOfRangeException>(() => channel.Read(buffer, 0, 0, 1000));
            Throws<ArgumentOutOfRangeException>(() => channel.Write(buffer, 0, 0, 1000));
            Throws<ArgumentOutOfRangeException>(() => channel.Read(buffer, 0, 1, 0));
            Throws<ArgumentOutOfRangeException>(() => channel.Write(buffer, 0, 1, 0));
            channel.Write(new byte[] { 0x5A }, 0, 1, 1);
            Check(remote.Receive(buffer) == 1 && buffer[0] == 0x5A, "1 ms socket write deadline is accepted");
            remote.Send(new byte[] { 0xA5 });
            Thread.Sleep(50);
            Check(channel.Read(buffer, 0, 4, 1) == 1 && buffer[0] == 0xA5, "1 ms socket read deadline is accepted");
        });
        TcpPair((remote, local) =>
        {
            remote.Send(new byte[] { 0, 1, 0, 0 });
            remote.Shutdown(SocketShutdown.Send);
            Throws<ModbusProtocolException>(() => new TcpTransport(new SocketChannel(local), 1000).Receive(output, out context));
        });
        TcpPair((remote, local) =>
        {
            remote.Send(new byte[] { 0, 1, 0, 0, 0, 6, 1, 3, 0 });
            remote.Shutdown(SocketShutdown.Send);
            Throws<ModbusProtocolException>(() => new TcpTransport(new SocketChannel(local), 1000).Receive(output, out context));
        });
        TcpPair((remote, local) =>
        {
            var transport = new TcpTransport(new SocketChannel(local), 300);
            var trickle = new Thread(() =>
            {
                remote.Send(new byte[] { 0, 1, 0, 0, 0, 6, 1 });
                for (int i = 0; i < 4; i++)
                {
                    Thread.Sleep(120);
                    remote.Send(new byte[] { 3 });
                }
            });
            var clock = System.Diagnostics.Stopwatch.StartNew();
            trickle.Start();
            Throws<TimeoutException>(() => transport.Receive(output, out context));
            Check(clock.ElapsedMilliseconds >= 280 && clock.ElapsedMilliseconds < 1000, "TCP receive deadline spans the whole ADU");
            trickle.Join();
        });
        TcpPair((remote, local) =>
        {
            remote.Send(new byte[] { 0, 1, 0, 0, 0, 255, 1, 3 });
            Throws<ModbusProtocolException>(() => new TcpTransport(new SocketChannel(local), 1000).Receive(output, out context));
        });
        TcpPair((remote, local) =>
        {
            var client = new ModbusClient(new TcpTransport(new SocketChannel(local), 1000));
            var responder = new Thread(() =>
            {
                var request = new byte[12];
                int read = 0;
                while (read < 12)
                    read += remote.Receive(request, read, 12 - read, SocketFlags.None);
                remote.Send(new byte[] { request[0], (byte)(request[1] + 1), 0, 0, 0, 5, 1, 3, 2, 0, 7 });
            });
            responder.Start();
            Throws<ModbusProtocolException>(() => client.ReadHoldingRegisters(1, 0, 1, new ushort[1], 0));
            responder.Join();
        });
        TcpPair((remote, local) =>
        {
            var server = new ModbusServer(1, new TcpTransport(new SocketChannel(local), 1000), new ArrayDataStore(1, 1, 1, 1));
            remote.Send(new byte[] { 0, 9, 0, 0, 0, 6, 1, 6 });
            remote.Shutdown(SocketShutdown.Send);
            Throws<ModbusProtocolException>(() => server.ProcessNextRequest());
        });
    }

    private static void TcpPair(Action<Socket, Socket> body)
    {
        using (var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        using (var local = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);
            local.Connect(listener.LocalEndPoint);
            using (Socket remote = listener.Accept())
                body(remote, local);
        }
    }

    private static void SerialRoundTrip(bool ascii, bool lengthFraming = false)
    {
        var clientChannel = new LinkedChannel();
        var serverChannel = new LinkedChannel();
        clientChannel.Peer = serverChannel;
        serverChannel.Peer = clientChannel;
        IModbusTransport clientTransport = ascii ?
            (IModbusTransport)new AsciiTransport(clientChannel) :
            new RtuTransport(clientChannel, 9600,
                lengthFraming ? RtuReceiveMode.ResponseLength : RtuReceiveMode.SilentInterval);
        IModbusTransport serverTransport = ascii ?
            (IModbusTransport)new AsciiTransport(serverChannel) :
            new RtuTransport(serverChannel, 9600,
                lengthFraming ? RtuReceiveMode.RequestLength : RtuReceiveMode.SilentInterval);
        var server = new ModbusServer(1, serverTransport, new ArrayDataStore(16, 0, 16, 0));
        clientChannel.OnWrite = server.ProcessNextRequest;
        var client = new ModbusClient(clientTransport);
        client.WriteMultipleRegisters(1, 0, 3, new ushort[] { 0, 0x1234, 0xFFFF }, 0);
        var registers = new ushort[3];
        client.ReadHoldingRegisters(1, 0, 3, registers, 0);
        Check(registers[0] == 0 && registers[1] == 0x1234 && registers[2] == 0xFFFF,
            "Serial transport role integration");
        client.WriteMultipleCoils(1, 1, 3, new bool[] { true, false, true }, 0);
        var bits = new bool[3];
        client.ReadCoils(1, 1, 3, bits, 0);
        Check(bits[0] && !bits[1] && bits[2], "Serial bit integration");
        client.WriteSingleRegister(0, 0, 77);
        client.ReadHoldingRegisters(1, 0, 1, registers, 0);
        Check(registers[0] == 77, "Serial broadcast integration");
    }

    private sealed class LinkedChannel : IByteChannel
    {
        private readonly Queue<byte> _input = new Queue<byte>();
        public LinkedChannel Peer;
        public Action OnWrite;

        public int Read(byte[] buffer, int offset, int count, int timeoutMilliseconds)
        {
            int read = Math.Min(count, _input.Count);
            for (int i = 0; i < read; i++)
                buffer[offset + i] = _input.Dequeue();
            return read;
        }

        public void Write(byte[] buffer, int offset, int count, int timeoutMilliseconds)
        {
            for (int i = 0; i < count; i++)
                Peer._input.Enqueue(buffer[offset + i]);
            if (OnWrite != null)
                OnWrite();
        }
    }

    private sealed class DelayedBatchChannel : IByteChannel
    {
        private readonly byte[] _bytes;
        private readonly int _length;
        private readonly int _batchSize;
        private readonly int _delayMilliseconds;
        private int _offset;
        public int Delays;

        public DelayedBatchChannel(byte[] bytes, int length, int batchSize, int delayMilliseconds)
        {
            _bytes = bytes;
            _length = length;
            _batchSize = batchSize;
            _delayMilliseconds = delayMilliseconds;
        }

        public int Read(byte[] buffer, int offset, int count, int timeoutMilliseconds)
        {
            if (_offset == _length)
                return 0;
            if (_offset > 0 && _offset % _batchSize == 0)
            {
                if (timeoutMilliseconds <= _delayMilliseconds)
                {
                    Thread.Sleep(timeoutMilliseconds);
                    return 0;
                }
                Thread.Sleep(_delayMilliseconds);
                Delays++;
            }
            int read = Math.Min(count, Math.Min(_length - _offset, _batchSize - _offset % _batchSize));
            Array.Copy(_bytes, _offset, buffer, offset, read);
            _offset += read;
            return read;
        }

        public void Write(byte[] buffer, int offset, int count, int timeoutMilliseconds)
        {
            throw new InvalidOperationException("Read-only UART batch fixture.");
        }
    }

    private sealed class MemoryChannel : IByteChannel
    {
        public readonly Queue<byte> Input = new Queue<byte>();
        public readonly List<byte> Written = new List<byte>();
        public int MaxRead = int.MaxValue;

        public void Feed(byte[] bytes)
        {
            foreach (byte value in bytes)
                Input.Enqueue(value);
        }

        public int Read(byte[] buffer, int offset, int count, int timeoutMilliseconds)
        {
            int read = Math.Min(Math.Min(count, MaxRead), Input.Count);
            for (int i = 0; i < read; i++)
                buffer[offset + i] = Input.Dequeue();
            return read;
        }

        public void Write(byte[] buffer, int offset, int count, int timeoutMilliseconds)
        {
            for (int i = 0; i < count; i++)
                Written.Add(buffer[offset + i]);
        }
    }

    private sealed class RequestTransport : IModbusTransport
    {
        public bool IsSerial { get; private set; }
        public ModbusServer Server;
        public byte[] Request;
        public byte[] Response;
        public FrameContext Context;
        public int ReceiveCount;

        public RequestTransport(bool serial)
        {
            IsSerial = serial;
        }

        public void Send(FrameContext context, byte[] pdu, int count)
        {
            Context = context;
            Request = new byte[count];
            Array.Copy(pdu, Request, count);
            Response = null;
            if (IsSerial && context.UnitId == 0)
                Server.ProcessNextRequest();
        }

        public int Receive(byte[] pdu, out FrameContext context)
        {
            ReceiveCount++;
            Server.ProcessNextRequest();
            context = Context;
            if (Response == null)
                throw new TimeoutException();
            Array.Copy(Response, pdu, Response.Length);
            return Response.Length;
        }
    }

    private sealed class ServerTransport : IModbusTransport
    {
        private readonly RequestTransport _link;

        public bool IsSerial
        {
            get
            {
                return _link.IsSerial;
            }
        }

        public ServerTransport(RequestTransport link)
        {
            _link = link;
        }

        public void Send(FrameContext context, byte[] pdu, int count)
        {
            _link.Response = new byte[count];
            Array.Copy(pdu, _link.Response, count);
        }

        public int Receive(byte[] pdu, out FrameContext context)
        {
            context = _link.Context;
            Array.Copy(_link.Request, pdu, _link.Request.Length);
            return _link.Request.Length;
        }
    }

    private sealed class FaultTransport : IModbusTransport
    {
        public int Mode;
        private FrameContext _context;
        private byte _function;

        public bool IsSerial
        {
            get
            {
                return false;
            }
        }

        public void Send(FrameContext context, byte[] pdu, int count)
        {
            _context = context;
            _function = pdu[0];
        }

        public int Receive(byte[] pdu, out FrameContext context)
        {
            context = _context;
            pdu[0] = _function;
            pdu[1] = 2;
            pdu[2] = 0;
            pdu[3] = 1;
            switch (Mode)
            {
                case 0:
                    context.UnitId++;
                    return 4;
                case 1:
                    context.TransactionId++;
                    return 4;
                case 2:
                    pdu[0] = 4;
                    return 4;
                case 3:
                    return 3;
                case 4:
                    pdu[1] = 1;
                    return 4;
                case 5:
                    return 0;
                case 6:
                    pdu[0] |= 128;
                    return 3;
                case 7:
                    pdu[0] |= 128;
                    return 2;
                case 8:
                    return 5;
                default:
                    throw new Exception("Unknown mode");
            }
        }
    }
}
