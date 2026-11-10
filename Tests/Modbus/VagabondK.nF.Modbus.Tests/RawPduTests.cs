using System;
using System.Reflection;
using VagabondK.nF.Modbus;
using VagabondK.nF.Modbus.Client;
using VagabondK.nF.Modbus.Server;

internal static class RawPduTests
{
    private static int checks;

    private static void Check(bool condition)
    {
        checks++;
        if (!condition) throw new Exception("Raw PDU check failed: " + checks);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        checks++;
        try { action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name + " at raw check " + checks);
    }

    private static object Field(object instance, string name)
    {
        return instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(instance);
    }

    private sealed class Transport : IModbusPollingTransport
    {
        public bool IsSerial { get; set; }
        internal Action<FrameContext, byte[], int> OnSend;
        internal Func<byte[], FrameContext> OnReceive;
        internal int Length = 1;
        internal ReceiveStatus Status = ReceiveStatus.Success;
        internal int Sends;
        public void Send(FrameContext context, byte[] pdu, int count)
        {
            Sends++;
            if (OnSend != null) OnSend(context, pdu, count);
        }
        public int Receive(byte[] pdu, out FrameContext context)
        {
            context = OnReceive(pdu);
            return Length;
        }
        public ReceiveStatus TryReceive(byte[] pdu, out FrameContext context, out int length)
        {
            context = new FrameContext();
            length = 0;
            if (Status == ReceiveStatus.Success) length = Receive(pdu, out context);
            return Status;
        }
    }

    private sealed class Handler : IModbusPduHandler
    {
        internal Func<FrameContext, byte[], int, int> Action;
        internal int Calls;
        public int Process(FrameContext context, byte[] pdu, int length)
        {
            Calls++;
            return Action(context, pdu, length);
        }
    }

    internal static void Run()
    {
        var buffer = new byte[253];
        var upstream = new Transport();
        var downstream = new Transport { IsSerial = true };
        var client = new ModbusClient(downstream);
        FrameContext original = new FrameContext(7, 0xFE12);
        byte function = 65;
        int requestLength = 253;
        int responseLength = 253;
        bool exception = false;
        FrameContext sent = new FrameContext();
        upstream.OnReceive = pdu =>
        {
            Check(object.ReferenceEquals(buffer, pdu));
            for (int i = 0; i < pdu.Length; i++) pdu[i] = (byte)i;
            pdu[0] = function;
            upstream.Length = requestLength;
            return original;
        };
        downstream.OnSend = (context, pdu, length) =>
        {
            Check(object.ReferenceEquals(buffer, pdu) && context.UnitId == 7 && length == requestLength);
            Check(pdu[0] == function);
            sent = context;
        };
        downstream.OnReceive = pdu =>
        {
            Check(object.ReferenceEquals(buffer, pdu));
            for (int i = 0; i < pdu.Length; i++) pdu[i] = 0xCC;
            pdu[0] = exception ? (byte)(function | 0x80) : function;
            pdu[1] = 0x0B;
            downstream.Length = responseLength;
            return new FrameContext(7, 0);
        };
        var handler = new Handler
        {
            Action = (context, pdu, length) =>
            {
                Check(context.UnitId == 7 && context.TransactionId == original.TransactionId);
                int result = client.ExchangePdu(context.UnitId, pdu, length);
                context.UnitId = 99;
                context.TransactionId = 0;
                return result;
            }
        };
        var server = new ModbusServer(7, upstream, handler, buffer);
        upstream.OnSend = (context, pdu, length) =>
        {
            Check(context.UnitId == 7 && context.TransactionId == original.TransactionId);
            Check(object.ReferenceEquals(buffer, pdu) && length == responseLength);
            Check(pdu[0] == (exception ? (function | 0x80) : function));
            if (exception) Check(pdu[1] == 0x0B);
        };
        server.ProcessNextRequest();
        Check(sent.TransactionId == 1);
        for (int f = 1; f <= 127; f++)
        {
            function = (byte)f;
            requestLength = responseLength = 1;
            Check(server.TryProcessNextRequest() == ReceiveStatus.Success);
            exception = true;
            responseLength = 2;
            server.ProcessNextRequest();
            exception = false;
        }
        Check(Field(client, "_request") == null && Field(client, "_response") == null);
        foreach (string field in new[] { "_request", "_response", "_bits", "_registers" })
            Check(Field(server, field) == null);

        int calls = handler.Calls;
        original.UnitId = 8;
        server.ProcessNextRequest();
        Check(handler.Calls == calls);
        original.UnitId = 7;
        upstream.Status = ReceiveStatus.CrcMismatch;
        Check(server.TryProcessNextRequest() == ReceiveStatus.CrcMismatch && handler.Calls == calls);
        upstream.Status = ReceiveStatus.Success;
        upstream.OnSend = (context, pdu, length) =>
        {
            Check(context.UnitId == 7 && context.TransactionId == original.TransactionId);
            Check(length == 2 && pdu[0] == (function | 0x80) && pdu[1] == 1);
        };
        foreach (byte invalid in new byte[] { 0, 128, 255 })
        {
            function = invalid;
            server.ProcessNextRequest();
        }
        Check(handler.Calls == calls);
        function = 65;
        upstream.OnSend = (context, pdu, length) =>
            Check(length == 2 && pdu[0] == 0xC1 && pdu[1] == 4 && context.TransactionId == original.TransactionId);
        handler.Action = (context, pdu, length) =>
        {
            pdu[0] = 99;
            throw new ModbusException(ExceptionCode.ServerDeviceFailure);
        };
        server.ProcessNextRequest();
        int sends = upstream.Sends;
        foreach (int invalid in new[] { 0, 254, -1 })
        {
            handler.Action = (context, pdu, length) => invalid;
            Throws<ModbusProtocolException>(() => server.ProcessNextRequest());
        }
        handler.Action = (context, pdu, length) => { pdu[0] = 66; return 1; };
        Throws<ModbusProtocolException>(() => server.ProcessNextRequest());
        handler.Action = (context, pdu, length) => { pdu[0] = 0xC1; return 3; };
        Throws<ModbusProtocolException>(() => server.ProcessNextRequest());
        handler.Action = (context, pdu, length) => { throw new InvalidOperationException(); };
        Throws<InvalidOperationException>(() => server.ProcessNextRequest());
        Check(upstream.Sends == sends);
        foreach (int invalid in new[] { 0, 254 })
        {
            requestLength = invalid;
            Throws<ModbusProtocolException>(() => server.ProcessNextRequest());
        }
        requestLength = 1;
        upstream.IsSerial = true;
        original.UnitId = 0;
        calls = handler.Calls;
        foreach (byte broadcast in new byte[] { 5, 6, 15, 16, 65 })
        {
            function = broadcast;
            server.ProcessNextRequest();
        }
        Check(handler.Calls == calls && upstream.Sends == sends);

        Throws<ArgumentNullException>(() => new ModbusServer(1, null, handler, buffer));
        Throws<ArgumentNullException>(() => new ModbusServer(1, upstream, (IModbusPduHandler)null, buffer));
        Throws<ArgumentNullException>(() => new ModbusServer(1, upstream, handler, null));
        Throws<ArgumentOutOfRangeException>(() => new ModbusServer(1, upstream, handler, new byte[252]));
        Throws<ArgumentOutOfRangeException>(() => new ModbusServer(0, upstream, handler, buffer));
        Throws<ArgumentOutOfRangeException>(() => new ModbusServer(248, upstream, handler, buffer));

        ClientValidation();
        Console.WriteLine("RAW PDU PASS: " + checks + " checks.");
    }

    private static void ClientValidation()
    {
        var transport = new Transport();
        var client = new ModbusClient(transport);
        var buffer = new byte[253];
        FrameContext sent = new FrameContext();
        FrameContext received = new FrameContext();
        transport.OnSend = (context, pdu, length) => { sent = context; received = context; };
        transport.OnReceive = pdu => { pdu[0] = 65; return received; };
        Action exchange = () => { buffer[0] = 65; client.ExchangePdu(7, buffer, 1); };
        exchange();
        Check(sent.UnitId == 7 && sent.TransactionId == 1);
        foreach (byte unit in new byte[] { 0, 255 })
        {
            buffer[0] = 65;
            Check(client.ExchangePdu(unit, buffer, 1) == 1);
        }
        transport.OnReceive = pdu => { pdu[0] = 65; received.UnitId++; return received; };
        Throws<ModbusProtocolException>(exchange);
        transport.OnReceive = pdu => { pdu[0] = 65; received.TransactionId++; return received; };
        Throws<ModbusProtocolException>(exchange);
        transport.OnReceive = pdu => { pdu[0] = 66; return received; };
        Throws<ModbusProtocolException>(exchange);
        transport.OnReceive = pdu => { pdu[0] = 65; return received; };
        foreach (int length in new[] { 0, 254 })
        {
            transport.Length = length;
            Throws<ModbusProtocolException>(exchange);
        }
        transport.OnReceive = pdu => { pdu[0] = 0xC1; pdu[1] = 0x7F; return received; };
        foreach (int length in new[] { 1, 3 })
        {
            transport.Length = length;
            Throws<ModbusProtocolException>(exchange);
        }
        transport.Length = 2;
        buffer[0] = 65;
        Check(client.ExchangePdu(7, buffer, 1) == 2 && buffer[1] == 0x7F);
        transport.OnReceive = pdu => { throw new TimeoutException(); };
        Throws<TimeoutException>(exchange);
        Throws<ArgumentNullException>(() => client.ExchangePdu(7, null, 1));
        Throws<ArgumentOutOfRangeException>(() => client.ExchangePdu(7, new byte[252], 1));
        foreach (int length in new[] { -1, 0, 254 })
            Throws<ArgumentOutOfRangeException>(() => client.ExchangePdu(7, buffer, length));
        foreach (byte function in new byte[] { 0, 128, 255 })
        {
            buffer[0] = function;
            Throws<ArgumentOutOfRangeException>(() => client.ExchangePdu(7, buffer, 1));
        }
        buffer[0] = 65;
        transport.IsSerial = true;
        int sends = transport.Sends;
        foreach (byte unit in new byte[] { 0, 248, 255 })
            Throws<ArgumentOutOfRangeException>(() => client.ExchangePdu(unit, buffer, 1));
        Check(transport.Sends == sends);
        Check(Field(client, "_request") == null && Field(client, "_response") == null);
        transport.OnReceive = pdu => { pdu[0] = 65; return new FrameContext(7, 999); };
        transport.Length = 1;
        exchange();
        client.WriteSingleRegister(0, 2, 123);
        Check(Field(client, "_request") != null && Field(client, "_response") != null);
        Check(transport.Sends == sends + 2);
        transport.IsSerial = false;
        transport.OnReceive = pdu => { pdu[0] = 0x83; pdu[1] = 2; transport.Length = 2; return received; };
        Throws<ModbusException>(() => client.ReadHoldingRegisters(7, 0, 1, new ushort[1], 0));
    }
}
