using System;
using System.Collections.Generic;

using VagabondK.nF.DNP3.Serial;

namespace VagabondK.nF.DNP3.Tests
{
    // 가상 시계와 메모리 채널로 Decoder 복구, Confirmed Link ACK/재시도, Faulted/Reset 및 UART 모드 계약을 검사합니다.
    internal static class Dnp3SerialCases
    {
        private static void Check(bool condition, string reason)
        {
            if (!condition)
                throw new Exception("DNP3 serial: " + reason);
            Cases.Check(true);
        }

        private static void Fails<T>(Action action) where T : Exception
        {
            try
            {
                action();
            }
            catch (T)
            {
                Cases.Check(true);
                return;
            }
            throw new Exception("DNP3 serial expected " + typeof(T).Name);
        }

        private sealed class Clock
        {
            internal long Now;
        }

        private sealed class Pipe : IDnp3ByteChannel
        {
            internal readonly Queue<byte> Input = new Queue<byte>();
            internal readonly List<byte[]> Writes = new List<byte[]>();
            internal readonly Clock Clock;
            internal Pipe Peer;
            internal int Chunk = 64, WriteDelay;
            internal Func<byte[], bool> Drop;
            internal bool Echo, FailWrite;

            internal Pipe(Clock clock)
            {
                Clock = clock;
            }

            public long Milliseconds
            {
                get
                {
                    return Clock.Now;
                }
            }

            public int Read(byte[] b, int offset, int count, int timeout)
            {
                int n = Math.Min(count, Math.Min(Chunk, Input.Count));
                for (int i = 0; i < n; i++)
                    b[offset + i] = Input.Dequeue();
                return n;
            }

            public void Write(byte[] b, int offset, int count, int timeout)
            {
                if (FailWrite)
                    throw new InvalidOperationException("Injected write failure.");
                var frame = new byte[count];
                Array.Copy(b, offset, frame, 0, count);
                Writes.Add(frame);
                Clock.Now += WriteDelay;
                if (Drop != null && Drop(frame))
                    return;
                foreach (byte value in frame)
                {
                    Peer.Input.Enqueue(value);
                    if (Echo)
                        Input.Enqueue(value);
                }
            }
        }

        private sealed class Endpoint : IDnp3Endpoint
        {
            internal readonly Dnp3Channel Channel;
            internal int Deliveries;
            internal byte[] Application;

            public ushort LocalAddress
            {
                get
                {
                    return Channel.LocalAddress;
                }
            }

            public ushort RemoteAddress
            {
                get
                {
                    return Channel.RemoteAddress;
                }
            }

            public bool IsMaster
            {
                get
                {
                    return Channel.IsMaster;
                }
            }

            internal Endpoint(bool master)
            {
                Channel = new Dnp3Channel(master ? (ushort)2 : (ushort)1, master ? (ushort)1 : (ushort)2, master, timeoutMilliseconds: 30000);
            }

            public byte[] Process(byte[] frame, long now)
            {
                byte[] app = Channel.Receive(frame, now, out var reply);
                if (app != null)
                {
                    Deliveries++;
                    Application = app;
                }
                return reply;
            }

            public byte[] Poll(long now)
            {
                Channel.CheckTimeout(now);
                return null;
            }

            public void ResetConnection()
            {
                Channel.Reset();
            }
        }

        private static void Drain(Clock clock, Pipe a, Pipe b, Dnp3SerialSession master, Dnp3SerialSession station)
        {
            for (int i = 0; i < 20000; i++)
            {
                master.RunOnce();
                station.RunOnce();
                clock.Now++;
                if (master.TransmitIdle && station.TransmitIdle && a.Input.Count == 0 && b.Input.Count == 0)
                    return;
            }
            throw new Exception("DNP3 serial drain deadline");
        }

        internal static void Run()
        {
            Decoder();
            Link();
            Failures();
            Native();
            Uart();
        }

        private static void Decoder()
        {
            var channel = new Dnp3Channel(2, 1, true);
            byte[] frame = channel.Encode(new byte[249]);
            Check(frame.Length == 292, "maximum link size");
            var decoder = new Dnp3StreamDecoder(10);
            for (int i = 0; i < frame.Length - 1; i++)
                Check(decoder.Push(frame[i], 0) == null, "partial frame");
            Check(Dnp3Channel.Equal(frame, decoder.Push(frame[frame.Length - 1], 0)), "one-byte decode");
            for (int index = 0; index < frame.Length; index++)
            {
                decoder = new Dnp3StreamDecoder(10);
                var corrupt = (byte[])frame.Clone();
                corrupt[index] ^= 0x80;
                foreach (byte value in corrupt)
                    Check(decoder.Push(value, 0) == null, "single byte corruption not delivered " + index);
                byte[] recovered = null;
                foreach (byte value in frame)
                {
                    byte[] next = decoder.Push(value, 1);
                    if (next != null)
                        recovered = next;
                }
                byte[] late = decoder.Take(11);
                if (late != null)
                    recovered = late;
                Check(Dnp3Channel.Equal(recovered, frame) && decoder.DiscardedBytes >= frame.Length, "CRC resync " + index);
            }
            decoder = new Dnp3StreamDecoder(10);
            for (int i = 0; i < 10; i++)
                decoder.Push(frame[i], 0);
            byte[] shortFrame = channel.Encode(new byte[] { 0xc0, 1 });
            foreach (byte value in shortFrame)
                Check(decoder.Push(value, 1) == null, "truncated frame holds trailing sync until timeout");
            Check(Dnp3Channel.Equal(shortFrame, decoder.Take(10)) && decoder.FrameTimeouts == 1, "timeout salvages embedded next frame");
            decoder = new Dnp3StreamDecoder(10);
            for (int i = 0; i < shortFrame.Length - 1; i++)
                decoder.Push(shortFrame[i], 0);
            Check(decoder.Push(shortFrame[shortFrame.Length - 1], 10) == null && decoder.FrameTimeouts == 1,
                "final byte at absolute deadline cannot revive an expired frame");
            byte[] fresh = null;
            foreach (byte value in shortFrame)
            {
                byte[] next = decoder.Push(value, 11);
                if (next != null)
                    fresh = next;
            }
            Check(Dnp3Channel.Equal(fresh, shortFrame), "recovery after final-byte timeout");
            decoder = new Dnp3StreamDecoder(10);
            for (int i = 0; i < 10000; i++)
                decoder.Push(0xff, i);
            Check(decoder.DiscardedBytes == 10000 && decoder.Take(10001) == null, "bounded garbage");
            var random = new Random(6271);
            for (int i = 0; i < 1000; i++)
            {
                int size = random.Next(2, 2050);
                foreach (byte value in channel.Encode(new byte[size > 2048 ? 2048 : size]))
                    decoder.Push(value, i + 11000);
            }
            decoder.Reset();
            Check(decoder.Take(100000) == null, "explicit reset");
        }

        private static void Link()
        {
            foreach (bool confirmed in new[] { false, true })
                foreach (int chunk in new[] { 1, 7, 64 })
                {
                    var clock = new Clock();
                    var a = new Pipe(clock) { Chunk = chunk, Echo = true };
                    var b = new Pipe(clock) { Chunk = chunk, Echo = true };
                    a.Peer = b;
                    b.Peer = a;
                    var tx = new Endpoint(true);
                    var rx = new Endpoint(false);
                    var master = new Dnp3SerialSession(a, tx, confirmed, 2000, frameTimeoutMilliseconds: 1000);
                    var station = new Dnp3SerialSession(b, rx, confirmed, 2000, frameTimeoutMilliseconds: 1000);
                    for (int round = 0; round < 70; round++)
                    {
                        var body = new byte[round % 2 == 0 ? 2048 : 2];
                        body[0] = 0xc0;
                        body[1] = (byte)round;
                        master.Send(tx.Channel.Encode(body));
                        station.Send(rx.Channel.Encode(body));
                        Drain(clock, a, b, master, station);
                        Check(tx.Deliveries == round + 1 && rx.Deliveries == round + 1, "bidirectional FCB/transport wrap");
                        Check(Dnp3Channel.Equal(tx.Application, body) && Dnp3Channel.Equal(rx.Application, body), "fragmented exact payload");
                    }
                    Check(master.ForeignFrames > 0 && station.ForeignFrames > 0, "local echo ignored");
                }
            var time = new Clock();
            var x = new Pipe(time);
            var y = new Pipe(time);
            x.Peer = y;
            y.Peer = x;
            var sender = new Endpoint(true);
            var receiver = new Endpoint(false);
            var left = new Dnp3SerialSession(x, sender, ackTimeoutMilliseconds: 20);
            var right = new Dnp3SerialSession(y, receiver, ackTimeoutMilliseconds: 20);
            int acknowledgements = 0;
            y.Drop = frame => (frame[3] & 0x40) == 0 && (++acknowledgements == 1 || acknowledgements == 3);
            byte[] payload = sender.Channel.Encode(new byte[2048]);
            left.Send(payload);
            Drain(time, x, y, left, right);
            Check(receiver.Deliveries == 1 && left.Retransmissions == 2, "reset/data ACK loss and duplicate suppression");
            Check(Dnp3Channel.Equal(x.Writes[0], x.Writes[1]) && Dnp3Channel.Equal(x.Writes[2], x.Writes[3]), "retries retain exact bytes and FCB");
            left.Reset();
            right.Reset();
            left.Send(sender.Channel.Encode(new byte[] { 0xc0, 1 }));
            Drain(time, x, y, left, right);
            Check(receiver.Deliveries == 2, "explicit link reconnect");
        }

        private static void Failures()
        {
            var clock = new Clock();
            var a = new Pipe(clock) { WriteDelay = 40 };
            var b = new Pipe(clock);
            a.Peer = b;
            b.Peer = a;
            var endpoint = new Endpoint(true);
            Fails<ArgumentNullException>(() => new Dnp3SerialSession(null, endpoint));
            Fails<ArgumentNullException>(() => new Dnp3SerialSession(a, null));
            Fails<ArgumentException>(() => new Dnp3SerialSession(a, endpoint, ackTimeoutMilliseconds: 0));
            Fails<ArgumentException>(() => new Dnp3SerialSession(a, endpoint, retries: -1));
            Fails<ArgumentException>(() => new Dnp3SerialSession(a, endpoint, retries: 11));
            Fails<ArgumentException>(() => new Dnp3SerialSession(a, endpoint, writeTimeoutMilliseconds: 0));
            Fails<ArgumentException>(() => new Dnp3SerialSession(a, endpoint, queueCapacity: 291));
            Fails<ArgumentException>(() => new Dnp3SerialSession(a, endpoint, queueCapacity: 32769));
            Fails<ArgumentOutOfRangeException>(() => new Dnp3SerialSession(a, endpoint, frameTimeoutMilliseconds: 0));
            new Dnp3SerialSession(a, endpoint, ackTimeoutMilliseconds: 1, retries: 0, frameTimeoutMilliseconds: 1,
                writeTimeoutMilliseconds: 1, queueCapacity: 292);
            new Dnp3SerialSession(a, endpoint, retries: 10, queueCapacity: 32768);
            Cases.Check(true);
            var session = new Dnp3SerialSession(a, endpoint, ackTimeoutMilliseconds: 20, retries: 1);
            session.Send(endpoint.Channel.Encode(new byte[] { 0xc0, 1 }));
            session.RunOnce();
            session.RunOnce();
            Check(a.Writes.Count == 1, "ACK deadline starts after Write completes");
            clock.Now += 20;
            session.RunOnce();
            clock.Now += 20;
            Fails<TimeoutException>(() => session.RunOnce());
            Check(session.Faulted && session.Retransmissions == 1, "retry exhaustion latches fault");
            Fails<InvalidOperationException>(() => session.RunOnce());
            session.Reset();
            a.FailWrite = true;
            session.Send(endpoint.Channel.Encode(new byte[] { 0xc0, 1 }));
            Fails<InvalidOperationException>(() => session.RunOnce());
            Check(session.Faulted, "UART failure surfaces");
            a.FailWrite = false;
            session.Reset();
            session = new Dnp3SerialSession(a, endpoint, queueCapacity: 292);
            Fails<InvalidOperationException>(() => session.Send(endpoint.Channel.Encode(new byte[498])));
            Check(a.Writes.Count == 2 && session.Faulted, "capacity checked before any I/O");
            session.Reset();
            byte[] corrupt = endpoint.Channel.Encode(new byte[] { 0xc0, 1 });
            corrupt[10] ^= 1;
            Fails<Dnp3ProtocolException>(() => session.Send(corrupt));
            session.Reset();
            Fails<Dnp3ProtocolException>(() => session.Send(new Dnp3Channel(3, 1, true).Encode(new byte[] { 0xc0, 1 })));
            foreach (byte status in new byte[] { 1, 0x10, 15 })
            {
                session.Reset();
                a.Input.Clear();
                b.Input.Clear();
                session.Send(endpoint.Channel.Encode(new byte[] { 0xc0, 1 }));
                session.RunOnce();
                byte[] reply = new byte[10];
                reply[0] = 5;
                reply[1] = 0x64;
                reply[2] = 5;
                reply[3] = status;
                Dnp3Channel.PutWord(reply, 4, 2);
                Dnp3Channel.PutWord(reply, 6, 1);
                Dnp3Channel.PutWord(reply, 8, Dnp3Link.Crc(reply, 0, 8));
                foreach (byte value in reply)
                    a.Input.Enqueue(value);
                Fails<InvalidOperationException>(() => session.RunOnce());
                Check(session.Faulted, "NACK/DFC/not-supported explicit rejection");
            }
        }

        private sealed class Handler : IDnp3MasterHandler, IDnp3ControlHandler
        {
            internal int Count, Operations;

            public void OnMeasurements(Dnp3Measurement[] values, Dnp3ResponseInfo info)
            {
                Count += values.Length;
            }

            public Dnp3CommandStatus Validate(Dnp3Command command)
            {
                return Dnp3CommandStatus.Success;
            }

            public Dnp3CommandStatus Operate(Dnp3Command command, bool selected)
            {
                Operations++;
                return Dnp3CommandStatus.Success;
            }
        }

        private static void Uart()
        {
            var port = new System.IO.Ports.SerialPort();
            SerialPortChannel.ConfigureRs485(port);
            Check(port.Mode == System.IO.Ports.SerialMode.RS485 && port.Handshake == System.IO.Ports.Handshake.None, "native RTS mode configuration");
            var io = new SerialPortChannel(port, true);
            port.IsOpen = true;
            Fails<InvalidOperationException>(() => SerialPortChannel.ConfigureRs485(port));
            var buffer = new byte[292];
            port.Bytes.Enqueue(9);
            port.Bytes.Enqueue(8);
            Check(io.Read(buffer, 3, 289, 100) == 2 && port.LastRead == 2 && buffer[3] == 9 && buffer[4] == 8,
                "adapter reads only currently buffered bytes into selected range");
            Check(io.Read(buffer, 0, 292, 2) == 0, "idle UART read deadline");
            io.Write(buffer, 3, 2, 77);
            Check(port.LastWrite == 2 && port.WriteTimeout == 77, "UART write timeout");
            port.WriteFailure = true;
            Fails<InvalidOperationException>(() => io.Write(buffer, 0, 1, 1));
            port.WriteFailure = false;
            port.ReadTimeoutFailure = true;
            port.Bytes.Enqueue(1);
            Check(io.Read(buffer, 0, 10, 100) == 0, "native read deadline returns no bytes");
            port.Mode = System.IO.Ports.SerialMode.Normal;
            Fails<InvalidOperationException>(() => io.Write(buffer, 0, 1, 1));
            Fails<InvalidOperationException>(() => new SerialPortChannel(port, true));
            port.Handshake = System.IO.Ports.Handshake.RequestToSend;
            Fails<InvalidOperationException>(() => new SerialPortChannel(port));
            Fails<ArgumentOutOfRangeException>(() => io.Read(buffer, 292, 1, 100));
            Fails<ArgumentNullException>(() => new SerialPortChannel(null));
        }

        private static void Native()
        {
            var clock = new Clock();
            var a = new Pipe(clock) { Chunk = 7 };
            var b = new Pipe(clock) { Chunk = 3 };
            a.Peer = b;
            b.Peer = a;
            var handler = new Handler();
            var points = new Dnp3PointDefinition[490];
            for (int i = 0; i < points.Length; i++)
            {
                var type = (Dnp3PointType)(i / 70);
                points[i] = new Dnp3PointDefinition(type, (ushort)(i % 70),
                    type == Dnp3PointType.Analog || type == Dnp3PointType.AnalogOutputStatus ? Dnp3Value.Analog(i) : Dnp3Value.Bits(0));
            }
            var database = new Dnp3Database(points, 512);
            var stationEndpoint = new Dnp3Station(database, 1, 2, handler, fragmentSize: 256, confirmationTimeout: 30000, transportTimeout: 30000);
            var masterEndpoint = new Dnp3Master(2, 1, handler, responseTimeout: 30000, transportTimeout: 30000);
            var master = new Dnp3SerialSession(a, masterEndpoint, ackTimeoutMilliseconds: 1000);
            var station = new Dnp3SerialSession(b, stationEndpoint, ackTimeoutMilliseconds: 1000);
            Drain(clock, a, b, master, station);
            master.Send(masterEndpoint.ReadClasses(1, clock.Now));
            Drain(clock, a, b, master, station);
            Check(!masterEndpoint.Busy && masterEndpoint.Result == Dnp3MasterResult.Success && handler.Count == 490, "native 7-type multi-fragment integrity");
            master.Send(masterEndpoint.SetUnsolicited(14, true, clock.Now));
            Drain(clock, a, b, master, station);
            for (int i = 0; i < points.Length; i++)
            {
                var type = points[i].Type;
                database.Update(type, points[i].Index, type == Dnp3PointType.Analog || type == Dnp3PointType.AnalogOutputStatus ?
                    Dnp3Value.Analog(i + 1, timestamp: 1234) : Dnp3Value.Bits(1, timestamp: 1234));
            }
            handler.Count = 0;
            Drain(clock, a, b, master, station);
            Check(handler.Count == 490 && database.EventCount == 0, "unsolicited events confirmed over serial");
            bool lost = false;
            b.Drop = frame =>
{
    if (!lost && (frame[3] & 0x40) == 0)
    {
        lost = true;
        return true;
    }
    return false;
};
            master.Send(masterEndpoint.Command(Dnp3Command.Relay(0, 3), false, clock.Now));
            Drain(clock, a, b, master, station);
            Check(lost && handler.Operations == 1 && masterEndpoint.Result == Dnp3MasterResult.Success, "lost ACK never operates relay twice");
            Check(master.Retransmissions == 1 && !master.Faulted && !station.Faulted, "native ACK loss recovers");
        }
    }
}
