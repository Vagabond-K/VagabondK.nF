using System;
using System.Collections.Generic;


namespace VagabondK.nF.DNP3.Tests
{
    // Link/Transport/Application, 수치·품질·시각 경계, Database 이벤트, 제어와 유실 복구를 메모리에서 검증합니다.
    // 잘못된 입력은 거부하고 재전송·중복 수신이 측정값이나 제어를 잘못 중복 전달하지 않는지 확인합니다.
    internal static class Dnp3StackCases
    {
        private static void Check(bool condition, string reason)
        {
            if (!condition)
                throw new Exception("DNP3 stack: " + reason);
            Cases.Check(true);
        }

        private static void Throws<T>(Action action) where T : Exception
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
            throw new Exception("DNP3 stack: expected " + typeof(T).Name);
        }

        private static void CheckSameCommand(Dnp3Command expected, Dnp3Command actual)
        {
            Check(actual.Index == expected.Index && actual.Group == expected.Group && actual.Variation == expected.Variation &&
                actual.ControlCode == expected.ControlCode && actual.Count == expected.Count &&
                actual.OnMilliseconds == expected.OnMilliseconds && actual.OffMilliseconds == expected.OffMilliseconds &&
                (expected.Variation == 3 && expected.Group == 41 ? (float)actual.AnalogValue == (float)expected.AnalogValue :
                    actual.AnalogValue.Equals(expected.AnalogValue)), "command fields survive the wire");
        }

        internal static byte[][] Frames(byte[] wire)
        {
            var frames = new List<byte[]>();
            for (int p = 0; wire != null && p < wire.Length;)
            {
                var header = new byte[3];
                Array.Copy(wire, p, header, 0, 3);
                int length = Dnp3Link.FrameLength(header);
                var frame = new byte[length];
                Array.Copy(wire, p, frame, 0, length);
                frames.Add(frame);
                p += length;
            }
            return frames.ToArray();
        }

        private static byte[] Application(Dnp3Channel receiver, byte[] wire, long now = 0)
        {
            byte[] result = null;
            foreach (var frame in Frames(wire))
            {
                byte[] current = receiver.Receive(frame, now, out var link);
                Check(link == null, "unconfirmed link does not ACK");
                if (current != null)
                {
                    Check(result == null, "one application");
                    result = current;
                }
            }
            return result;
        }

        private static void Invalid(Action action)
        {
            try
            {
                action();
            }
            catch (Dnp3ProtocolException)
            {
                Cases.Check(true);
                return;
            }
            throw new Exception("DNP3 stack: malformed input accepted");
        }

        private static void Timeout(Action action)
        {
            try
            {
                action();
            }
            catch (TimeoutException)
            {
                Cases.Check(true);
                return;
            }
            throw new Exception("DNP3 stack: expected timeout");
        }

        internal static void Run()
        {
            ChannelCases();
            CodecCases();
            MetadataBoundaryCases();
            NumericBoundaryCases();
            DatabaseCases();
            SessionCases();
            ControlsCases();
            LossCases();
            ConcurrentStreams();
            DeliveryCases();
            FuzzCases();
            NegativeCases();
        }

        private static Dnp3Database Binaries(int count)
        {
            var points = new Dnp3PointDefinition[count];
            for (int i = 0; i < count; i++)
                points[i] = new Dnp3PointDefinition(Dnp3PointType.Binary, (ushort)i, Dnp3Value.Bits((ulong)(i & 1)), 0);
            return new Dnp3Database(points, 16);
        }

        private static int[] Range(byte[] response)
        {
            var indices = new List<int>();
            for (int p = 4; p < response.Length;)
            {
                Check(response.Length - p >= 3 && response[p] == 1, "binary static object header");
                int variation = response[p + 1], qualifier = response[p + 2];
                Check(qualifier == 0 || qualifier == 1, "range qualifier");
                int width = qualifier == 0 ? 1 : 2;
                int start = (int)Dnp3Objects.Read(response, p + 3, width), stop = (int)Dnp3Objects.Read(response, p + 3 + width, width);
                for (int i = start; i <= stop; i++)
                    indices.Add(i);
                p += 3 + 2 * width + (stop - start + 1) * Dnp3Objects.Width(Dnp3PointType.Binary, false, variation);
            }
            return indices.ToArray();
        }

        private static void NegativeCases()
        {
            var handler = new Handler();
            var station = new Dnp3Station(Binaries(5), 1, 2);
            var master = new Dnp3Master(2, 1, handler);
            byte[] request = master.ReadGroup(1, 0, 0, 1, 3);
            byte[] body = Application(new Dnp3Channel(1, 2, false), request);
            Check(body.Length == 9 && body[1] == 1 && body[2] == 1 && body[3] == 0 && body[4] == 1 &&
                body[5] == 1 && body[6] == 0 && body[7] == 3 && body[8] == 0, "range read uses qualifier 0x01");
            Exchange(station, master, request, true);
            Check(master.Result == Dnp3MasterResult.Success && handler.Values.Length == 3 &&
                handler.Values[0].Index == 1 && handler.Values[2].Index == 3, "range read returns exactly requested points");
            foreach (var range in new[] { new[] { -1, 3 }, new[] { 0, -1 }, new[] { -2, -2 }, new[] { 4, 3 }, new[] { 0, 65536 } })
                Throws<ArgumentException>(() => new Dnp3Master(2, 1, handler).ReadGroup(1, 0, 0, range[0], range[1]));
            Throws<ArgumentException>(() => new Dnp3Master(2, 1, handler).ReadGroup(99, 0, 0));
            Throws<ArgumentException>(() => new Dnp3Master(2, 1, handler).ReadGroup(1, 99, 0));
            Check(new Dnp3Master(2, 1, handler).ReadGroup(1, 0, 0, 0, 65535) != null, "full range accepted");
            Throws<ArgumentException>(() => new Dnp3Master(2, 1, null));
            Throws<ArgumentException>(() => new Dnp3Master(2, 1, handler, responseTimeout: 0));
            Throws<ArgumentException>(() => new Dnp3Master(2, 1, handler, retries: -1));
            Throws<ArgumentException>(() => new Dnp3Master(2, 1, handler, retries: 11));
            Throws<ArgumentException>(() => new Dnp3Master(2, 1, handler, maximumMeasurements: 0));
            Throws<ArgumentException>(() => new Dnp3Master(2, 1, handler, maximumMeasurements: 8193));
            new Dnp3Master(2, 1, handler, responseTimeout: 1, retries: 10, maximumMeasurements: 8192);
            new Dnp3Master(2, 1, handler, retries: 0, maximumMeasurements: 1);

            foreach (byte iin2 in new byte[] { 1, 2, 4, 7, 8, 0x20 })
            {
                var m = new Dnp3Master(2, 1, new Handler());
                var outstation = new Dnp3Channel(1, 2, false);
                m.ReadClasses(1, 0);
                foreach (var frame in Frames(outstation.Encode(new byte[] { 0xc0, 0x81, 0, iin2 })))
                    m.Process(frame, 0);
                bool error = (iin2 & 7) != 0;
                Check(m.Iin2 == iin2 && !m.Busy &&
                    m.Result == (error ? Dnp3MasterResult.RemoteError : Dnp3MasterResult.Success), "IIN2 error bits map to RemoteError");
            }

            var unsHandler = new Handler();
            var unsMaster = new Dnp3Master(2, 1, unsHandler, responseTimeout: 100);
            var unsChannel = new Dnp3Channel(1, 2, false);
            foreach (var frame in Frames(unsChannel.Encode(new byte[] { 0xb3, 0x82, 0, 0 })))
                unsMaster.Process(frame, 1000);
            Check(unsMaster.Poll(1099) == null, "unsolicited continuation pending before deadline");
            Timeout(() => unsMaster.Poll(1100));
            Check(unsMaster.Poll(1200) == null, "unsolicited timeout is reported once");
            Invalid(() =>
            {
                foreach (var frame in Frames(unsChannel.Encode(new byte[] { 0x74, 0x82, 0, 0 })))
                    unsMaster.Process(frame, 1200);
            });
            foreach (var frame in Frames(unsChannel.Encode(new byte[] { 0xd5, 0x82, 0, 0 })))
                unsMaster.Process(frame, 1300);
            Check(!unsMaster.Busy, "new unsolicited FIR accepted after continuation timeout");

            var mc = new Dnp3Channel(2, 1, true);
            foreach (var test in new[] { new[] { 7, 2 }, new[] { 8, 3 }, new[] { 7, 0 }, new[] { 6, 5 } })
            {
                var st = new Dnp3Station(Binaries(5), 1, 2);
                byte[] read = test[0] == 7 ? new byte[] { 0xc0, 1, 1, 0, 7, (byte)test[1] } :
                    test[0] == 8 ? new byte[] { 0xc0, 1, 1, 0, 8, (byte)test[1], 0 } : new byte[] { 0xc0, 1, 1, 0, 6 };
                byte[] response = null;
                foreach (var frame in Frames(mc.Encode(read)))
                {
                    byte[] reply = st.Process(frame, 0);
                    if (reply != null)
                        response = Application(mc, reply);
                }
                Check(response != null && response[1] == 0x81 && (response[3] & 7) == 0, "count-limited read response");
                if (test[1] == 0)
                    Check(response.Length == 4, "zero count read returns no objects");
                else
                {
                    int[] r = Range(response);
                    Check(r.Length == test[1] && r[0] == 0 && r[r.Length - 1] == test[1] - 1, "qualifier 7/8 limits object count");
                }
            }
            byte[] single = null, duplicated = null;
            foreach (bool twice in new[] { false, true })
            {
                var st = new Dnp3Station(Binaries(5), 1, 2);
                byte[] read = twice ? new byte[] { 0xc0, 1, 1, 0, 6, 1, 0, 6 } : new byte[] { 0xc0, 1, 1, 0, 6 };
                foreach (var frame in Frames(mc.Encode(read)))
                {
                    byte[] reply = st.Process(frame, 0);
                    if (reply != null)
                    {
                        if (twice)
                            duplicated = Application(mc, reply);
                        else
                            single = Application(mc, reply);
                    }
                }
            }
            Check(Dnp3Channel.Equal(single, duplicated), "duplicate read headers return each point once");

            var big = new Dnp3Station(Database(210, 512), 1, 2, fragmentSize: 64);
            var link = new Dnp3Channel(2, 1, true);
            byte[] first = null;
            foreach (var frame in Frames(link.Encode(new byte[] { 0xc1, 1, 60, 1, 6 })))
            {
                byte[] reply = big.Process(frame, 0);
                if (reply != null)
                    first = Application(link, reply);
            }
            Check(first != null && (first[0] & 0xe0) == 0xa0, "first of multi-fragment response requests confirmation");
            byte seq = (byte)(first[0] & 15);
            Check(Application(link, big.Process(Frames(link.Encode(new byte[] { (byte)(0xc0 | ((seq - 1) & 15)), 0 }))[0], 1)) == null,
                "stale confirm before any continuation is ignored");
            byte[] second = Application(link, big.Process(Frames(link.Encode(new byte[] { (byte)(0xc0 | seq), 0 }))[0], 2));
            Check(second != null && (second[0] & 0x80) == 0 && (second[0] & 15) == ((seq + 1) & 15), "confirm advances to next fragment");
            byte[] again = Application(link, big.Process(Frames(link.Encode(new byte[] { (byte)(0xc0 | seq), 0 }))[0], 3));
            Check(Dnp3Channel.Equal(second, again), "stale confirm retransmits current continuation");
            Check(Application(link, big.Process(Frames(link.Encode(new byte[] { (byte)(0xc0 | ((seq + 5) & 15)), 0 }))[0], 4)) == null,
                "unrelated confirm sequence is ignored");
            Console.WriteLine("DNP3 NEGATIVE PATHS PASS: range reads, limits, IIN2 errors, unsolicited timeout, qualifier 7/8, dedupe, stale confirm.");
        }

        private static void NumericBoundaryCases()
        {
            double[] analogs = { -double.MaxValue, -float.MaxValue, -2147483649.0, -2147483648.0,
                -32769, -32768, -1.75, -double.Epsilon, -0.0, 0.0, double.Epsilon, 1.75,
                32767, 32768, 2147483647.0, 2147483648.0, float.MaxValue, double.MaxValue };
            int cases = 0;
            foreach (var type in new[] { Dnp3PointType.Analog, Dnp3PointType.AnalogOutputStatus })
                foreach (bool events in new[] { false, true })
                    for (int variation = 1; variation <= 8; variation++)
                    {
                        if (!Dnp3Objects.Supported(type, events, variation))
                            continue;
                        int format = events ? (variation <= 4 ? ((variation & 1) == 1 ? 1 : 2) : ((variation & 1) == 1 ? 3 : 4)) :
                            type == Dnp3PointType.Analog ? (variation >= 5 ? variation - 2 : variation) : variation;
                        foreach (double input in analogs)
                        {
                            double number = input;
                            byte flags = 1;
                            if (format <= 2)
                            {
                                double min = format == 1 ? int.MinValue : short.MinValue;
                                double max = format == 1 ? int.MaxValue : short.MaxValue;
                                if (number < min)
                                {
                                    number = min;
                                    flags |= 32;
                                }
                                if (number > max)
                                {
                                    number = max;
                                    flags |= 32;
                                }
                                number = (int)number;
                            }
                            else if (format == 3)
                            {
                                if (number < -float.MaxValue)
                                {
                                    number = -float.MaxValue;
                                    flags |= 32;
                                }
                                if (number > float.MaxValue)
                                {
                                    number = float.MaxValue;
                                    flags |= 32;
                                }
                                number = (float)number;
                            }
                            byte[] expected = format == 1 ? BitConverter.GetBytes((int)number) :
                                format == 2 ? BitConverter.GetBytes((short)number) :
                                format == 3 ? BitConverter.GetBytes((float)number) : BitConverter.GetBytes(number);
                            var wire = new byte[Dnp3Objects.Width(type, events, variation)];
                            Dnp3Objects.WriteValue(wire, 0, type, events, variation, Dnp3Value.Analog(input));
                            Check(wire[0] == flags, "analog boundary overrange flags");
                            for (int b = 0; b < expected.Length; b++)
                                Check(wire[b + 1] == expected[b], "analog exact independent numeric bytes");
                            int p = 0;
                            var decoded = Dnp3Objects.Decode(wire, ref p, type, events, variation);
                            Check(decoded.Raw == unchecked((ulong)BitConverter.DoubleToInt64Bits(number)) &&
                                decoded.Flags == flags, "analog numeric boundary exact bits");
                            cases++;
                        }
                    }
            foreach (var type in new[] { Dnp3PointType.Counter, Dnp3PointType.FrozenCounter })
                foreach (bool events in new[] { false, true })
                    for (int variation = 1; variation <= 6; variation++)
                    {
                        if (!Dnp3Objects.Supported(type, events, variation))
                            continue;
                        foreach (ulong number in new ulong[] { 0, 1, 65535, 65536, 2147483647, 2147483648, 4294967295 })
                        {
                            var wire = new byte[Dnp3Objects.Width(type, events, variation)];
                            Dnp3Objects.WriteValue(wire, 0, type, events, variation, Dnp3Value.Bits(number, 255));
                            int width = (variation & 1) == 1 ? 4 : 2;
                            for (int b = 0; b < width; b++)
                                Check(wire[1 + b] == (byte)(number >> (8 * b)), "counter boundary wire");
                            int p = 0;
                            var decoded = Dnp3Objects.Decode(wire, ref p, type, events, variation);
                            Check(decoded.Raw == (width == 2 ? number & 65535UL : number) && decoded.Flags == 255, "counter rollover boundary");
                            cases++;
                        }
                    }
            Console.WriteLine("DNP3 NUMERIC BOUNDARIES PASS: " + cases +
                " cases; signed limits, fractional truncation, overrange flags/clamping, float bits and counter low-word rollover.");
        }

        private static void MetadataBoundaryCases()
        {
            long[] times = { 0, 1, 65535, 65536, 4294967295L, 4294967296L, 281474976710654L, 281474976710655L };
            int cases = 0;
            for (int t = 0; t < 7; t++)
                for (int e = 0; e < 2; e++)
                    for (int variation = 1; variation <= 8; variation++)
                    {
                        var type = (Dnp3PointType)t;
                        bool events = e != 0;
                        if (!Dnp3Objects.Supported(type, events, variation))
                            continue;
                        bool analog = t == 4 || t == 6;
                        bool timed = events && (analog ? variation == 3 || variation == 4 || variation == 7 || variation == 8 :
                            t == 2 || t == 3 ? variation >= 5 : variation == 2);
                        foreach (long time in times)
                            for (int flags = 0; flags < 256; flags++)
                            {
                                ulong bits = t == 1 ? (ulong)(flags & 3) : t == 0 || t == 5 ? (ulong)(flags & 1) : 12345UL;
                                var value = analog ? Dnp3Value.Analog(-1234, (byte)flags, time) : Dnp3Value.Bits(bits, (byte)flags, time);
                                var bytes = new byte[Dnp3Objects.Width(type, events, variation)];
                                Dnp3Objects.WriteValue(bytes, 0, type, events, variation, value);
                                int mask = t == 1 ? 63 : t == 0 || t == 5 ? 127 : 255;
                                int wireFlags = (flags & mask) | (t == 1 ? (int)bits << 6 : t == 0 || t == 5 ? (int)bits << 7 : 0);
                                Check(bytes[0] == wireFlags, "independent expected flags byte");
                                if (timed)
                                    for (int b = 0; b < 6; b++)
                                        Check(bytes[bytes.Length - 6 + b] == (byte)(time >> (8 * b)),
                                            "independent expected 48-bit little-endian time byte");
                                int p = 0;
                                var decoded = Dnp3Objects.Decode(bytes, ref p, type, events, variation);
                                Check(p == bytes.Length && decoded.Raw == value.Raw && decoded.Flags == (flags & mask) &&
                                    decoded.Timestamp == (timed ? time : 0), "all metadata combinations exact");
                                cases++;
                            }
                    }
            foreach (long invalid in new[] { -1L, 281474976710656L, long.MaxValue })
            {
                bool rejected = false;
                try
                {
                    Dnp3Value.Bits(0, timestamp: invalid);
                }
                catch (ArgumentOutOfRangeException)
                {
                    rejected = true;
                }
                Check(rejected, "out-of-range 48-bit timestamp rejected");
            }
            Console.WriteLine("DNP3 METADATA BOUNDARIES PASS: " + cases +
                " cases; 45 variations x 256 flag bytes x 8 timestamp boundaries, exact wire bytes and decoded metadata.");
        }

        private static void ChannelCases()
        {
            var tx = new Dnp3Channel(2, 1, true);
            var rx = new Dnp3Channel(1, 2, false);
            foreach (int size in new[] { 2, 248, 249, 250, 498, 1024, 2048 })
                for (int round = 0; round < 64; round++)
                {
                    var app = new byte[size];
                    for (int i = 0; i < size; i++)
                        app[i] = (byte)(i + round);
                    Check(Dnp3Channel.Equal(app, Application(rx, tx.Encode(app))), "transport size/sequence wrap " + size);
                }
            byte[][] split = Frames(tx.Encode(new byte[1024]));
            Application(rx, split[0]);
            Invalid(() => Application(rx, split[2]));
            rx.Reset();
            Application(rx, split[0]);
            Timeout(() => rx.CheckTimeout(3000));
            rx.CheckTimeout(3001);
            Check(Application(rx, tx.Encode(new byte[] { 0xc0, 1 })) != null, "timeout recovery");
            byte[] corrupt = tx.Encode(new byte[] { 0xc0, 1 });
            corrupt[10] ^= 1;
            Invalid(() => Application(rx, corrupt));
            for (int size = 0; size < 10; size++)
                Invalid(() => rx.Receive(new byte[size], 0, out _));
            byte[] normal = tx.Encode(new byte[] { 0xc0, 1 });
            var reset = new byte[10];
            Array.Copy(normal, reset, 10);
            reset[2] = 5;
            reset[3] = 0xc0;
            Dnp3Channel.PutWord(reset, 8, Dnp3Link.Crc(reset, 0, 8));
            Check(rx.Receive(reset, 0, out var resetAck) == null && resetAck[3] == 0, "link reset ACK");
            normal[3] = 0xf3;
            Dnp3Channel.PutWord(normal, 8, Dnp3Link.Crc(normal, 0, 8));
            Check(rx.Receive(normal, 0, out var ack) != null && ack[3] == 0, "confirmed link first FCB");
            Check(rx.Receive(normal, 0, out var duplicateAck) == null && Dnp3Channel.Equal(ack, duplicateAck), "FCB suppress duplicate");
        }

        private static void CodecCases()
        {
            for (int t = 0; t < 7; t++)
                for (int e = 0; e < 2; e++)
                    for (int v = 1; v <= 8; v++)
                    {
                        var type = (Dnp3PointType)t;
                        bool events = e != 0;
                        if (!Dnp3Objects.Supported(type, events, v))
                            continue;
                        var value = t == 4 || t == 6 ? Dnp3Value.Analog(-1234, 1, 0x123456789abc) :
                            Dnp3Value.Bits(t == 0 || t == 5 ? 1UL : t == 1 ? 3UL : 12345UL, 1, 0x123456789abc);
                        var bytes = new byte[Dnp3Objects.Width(type, events, v)];
                        Check(Dnp3Objects.WriteValue(bytes, 0, type, events, v, value) == bytes.Length, "codec width");
                        int p = 0;
                        var decoded = Dnp3Objects.Decode(bytes, ref p, type, events, v);
                        Check(p == bytes.Length && decoded.Raw == value.Raw && decoded.Flags == value.Flags, "all supported codec roundtrip");
                        for (int cut = 0; cut < bytes.Length; cut++)
                        {
                            var truncated = new byte[cut];
                            Array.Copy(bytes, truncated, cut);
                            Invalid(() =>
{
    int offset = 0;
    Dnp3Objects.Decode(truncated, ref offset, type, events, v);
});
                        }
                    }
            foreach (double value in new[] { -0.0, double.Epsilon, -double.Epsilon, double.MaxValue, double.MinValue })
            {
                var bytes = new byte[9];
                Dnp3Objects.WriteValue(bytes, 0, Dnp3PointType.Analog, false, 6, Dnp3Value.Analog(value));
                int p = 0;
                Check(Dnp3Objects.Decode(bytes, ref p, Dnp3PointType.Analog, false, 6).Raw == Dnp3Value.Analog(value).Raw,
                    "float64 boundary bits");
            }
            var clamped = new byte[3];
            Dnp3Objects.WriteValue(clamped, 0, Dnp3PointType.Analog, false, 2, Dnp3Value.Analog(1e20));
            Check(clamped[0] == 0x21 && clamped[1] == 255 && clamped[2] == 127, "integer overrange flag");
            foreach (double value in new double[] { -2147483648.0, -123.0, -1.0, 0.0, 2147483647.0 })
            {
                foreach (var type in new[] { Dnp3PointType.Analog, Dnp3PointType.AnalogOutputStatus })
                    foreach (bool events in new[] { false, true })
                        foreach (int variation in events ? new[] { 1, 3 } : new[] { 1 })
                        {
                            var bytes = new byte[Dnp3Objects.Width(type, events, variation)];
                            Dnp3Objects.WriteValue(bytes, 0, type, events, variation, Dnp3Value.Analog(value));
                            int p = 0;
                            Check(Dnp3Objects.Decode(bytes, ref p, type, events, variation).AsAnalog() == value,
                                "signed32 static/event boundary");
                        }
                byte[] command = Dnp3Controls.Encode(Dnp3Command.Analog(0, value, 1));
                int offset = 7;
                Check(Dnp3Controls.Parse(command, ref offset, 0, 41, 1).AnalogValue == value && offset == command.Length,
                    "signed32 command boundary");
            }
        }

        private static Dnp3Database Database(int count = 7, int events = 128)
        {
            var points = new Dnp3PointDefinition[count];
            for (int i = 0; i < count; i++)
            {
                var type = (Dnp3PointType)(i % 7);
                var value = type == Dnp3PointType.Analog || type == Dnp3PointType.AnalogOutputStatus ?
                    Dnp3Value.Analog(100) : Dnp3Value.Bits(0);
                points[i] = new Dnp3PointDefinition(type, (ushort)i, value, (byte)(i % 3 + 1));
            }
            return new Dnp3Database(points, events);
        }

        private static void DatabaseCases()
        {
            for (int kind = 0; kind < 7; kind++)
            {
                var type = (Dnp3PointType)kind;
                bool analog = type == Dnp3PointType.Analog || type == Dnp3PointType.AnalogOutputStatus;
                var initial = analog ? Dnp3Value.Analog(0) : Dnp3Value.Bits(0);
                var ranges = new Dnp3Database(new[] {
                    new Dnp3PointDefinition(type, 65535, initial, 0),
                    new Dnp3PointDefinition(type, 0, initial, 0),
                    new Dnp3PointDefinition(type, 123, initial, 0) });
                ulong maximum = kind == 0 || kind == 5 ? 1UL : kind == 1 ? 3UL : uint.MaxValue;
                var valid = analog ? Dnp3Value.Analog(double.MaxValue, 4, 123) : Dnp3Value.Bits(maximum, 4, 123);
                foreach (ushort index in new ushort[] { 0, 123, 65535 })
                {
                    Check(ranges.Update(type, index, valid) && ranges.Read(type, index).Raw == valid.Raw,
                        "sorted sparse index boundaries and maximum valid value");
                    Check(ranges.Read(type, index).Flags == 4 && ranges.Read(type, index).Timestamp == 123, "metadata preserved");
                    ulong[] invalid = analog ? new[] { 0x7ff0000000000000UL, 0xfff0000000000000UL, 0x7ff8000000000000UL } :
                        new[] { maximum + 1, ulong.MaxValue };
                    foreach (ulong raw in invalid)
                    {
                        bool rejected = false;
                        try { ranges.Update(type, index, Dnp3Value.Bits(raw)); }
                        catch (ArgumentException) { rejected = true; }
                        Check(rejected && ranges.Read(type, index).Raw == valid.Raw && ranges.EventCount == 0,
                            "invalid range or nonfinite raw bits cannot mutate Static/events");
                    }
                }
                bool missing = false;
                try { ranges.Read(type, 124); }
                catch (ArgumentException) { missing = true; }
                Check(missing, "missing sparse index rejected");
                missing = false;
                try { ranges.Update((Dnp3PointType)((kind + 1) % 7), 0, initial); }
                catch (ArgumentException) { missing = true; }
                Check(missing, "same index with absent type rejected");
            }
            bool invalidDeadband = false;
            try
            {
                new Dnp3PointDefinition(Dnp3PointType.Counter, 0, Dnp3Value.Bits(0), deadband: 1);
            }
            catch (ArgumentException)
            {
                invalidDeadband = true;
            }
            Check(invalidDeadband, "unsupported deadband configuration is rejected, not ignored");
            var db = new Dnp3Database(new[] { new Dnp3PointDefinition(Dnp3PointType.Analog, 1, Dnp3Value.Analog(0), 2, 10) }, 2);
            Check(db.Update(Dnp3PointType.Analog, 1, Dnp3Value.Analog(6)) && db.EventCount == 0, "deadband suppress");
            db.Update(Dnp3PointType.Analog, 1, Dnp3Value.Analog(11));
            Check(db.EventCount == 1 && db.Classes() == 4, "cumulative deadband");
            var first = db.Snapshot(false, 14, 0, 0, 0, 65535, 1);
            db.Update(Dnp3PointType.Analog, 1, Dnp3Value.Analog(12, 4));
            Check(!db.Update(Dnp3PointType.Analog, 1, Dnp3Value.Analog(99)) && db.Overflow && db.Read(Dnp3PointType.Analog, 1).AsAnalog() == 99,
                "overflow explicitly reported, static retained");
            db.Confirm(first, 0, first.Length);
            Check(db.EventCount == 1, "ack exact generation only");
            db.Confirm(first, 0, first.Length);
            Check(db.EventCount == 1, "duplicate ack no newer event loss");
            var rest = db.Snapshot(false, 14, 0, 0, 0, 65535, int.MaxValue);
            db.Confirm(rest, 0, rest.Length);
            db.ClearOverflow();
            Check(db.EventCount == 0 && !db.Overflow, "overflow clear after drain");
            var timeOnly = new Dnp3Database(new[] {
                new Dnp3PointDefinition(Dnp3PointType.Analog, 1, Dnp3Value.Analog(123, 1, 0)) });
            Check(timeOnly.Update(Dnp3PointType.Analog, 1, Dnp3Value.Analog(123, 1, 281474976710655L)) &&
                timeOnly.EventCount == 0 && timeOnly.Read(Dnp3PointType.Analog, 1).Timestamp == 281474976710655L,
                "timestamp-only update changes static metadata without manufacturing a change event");
        }

        private sealed class Handler : IDnp3MasterHandler, IDnp3ControlHandler, IDnp3TimeHandler
        {
            internal Dnp3Measurement[] Values;
            internal int Batches, Operations;
            internal bool Unsolicited, Selected;
            internal long Utc;
            internal bool FailNextDelivery;
            internal Dnp3Command LastCommand;
            private readonly List<Dnp3Measurement> _solicited = new List<Dnp3Measurement>(), _unsolicited = new List<Dnp3Measurement>();

            public void OnMeasurements(Dnp3Measurement[] values, Dnp3ResponseInfo response)
            {
                if (FailNextDelivery)
                {
                    FailNextDelivery = false;
                    throw new InvalidOperationException("Fixture delivery failure.");
                }
                var all = response.Unsolicited ? _unsolicited : _solicited;
                if (response.First)
                    all.Clear();
                all.AddRange(values);
                Values = all.ToArray();
                if (values.Length > 0)
                    Batches++;
                Unsolicited = response.Unsolicited;
            }

            public Dnp3CommandStatus Validate(Dnp3Command command)
            {
                return command.Index == 99 ? Dnp3CommandStatus.Local : Dnp3CommandStatus.Success;
            }

            public Dnp3CommandStatus Operate(Dnp3Command command, bool selected)
            {
                Operations++;
                Selected = selected;
                LastCommand = command;
                return Dnp3CommandStatus.Success;
            }

            public void SetUtcMilliseconds(long milliseconds)
            {
                Utc = milliseconds;
            }
        }

        internal static void Exchange(Dnp3Station station, Dnp3Master master, byte[] wire, bool toStation, long now = 0)
        {
            var queue = new Queue<Tuple<byte[], bool>>();
            if (wire != null)
                queue.Enqueue(Tuple.Create(wire, toStation));
            int rounds = 0;
            while (queue.Count > 0)
            {
                Check(rounds++ < 1024, "bounded exchange");
                var item = queue.Dequeue();
                foreach (var frame in Frames(item.Item1))
                {
                    byte[] next = item.Item2 ? station.Process(frame, now) : master.Process(frame, now);
                    if (next != null)
                        queue.Enqueue(Tuple.Create(next, !item.Item2));
                }
            }
        }

        private static void SessionCases()
        {
            var handler = new Handler();
            var db = Database(210, 512);
            var station = new Dnp3Station(db, 1, 2, fragmentSize: 64);
            var master = new Dnp3Master(2, 1, handler);
            Exchange(station, master, station.Poll(0), false);
            Check(station.Poll(0) == null, "startup null confirmed");
            for (int i = 0; i < 40; i++)
            {
                Exchange(station, master, master.ReadClasses(1, i * 10), true, i * 10);
                Check(master.Result == Dnp3MasterResult.Success && handler.Values.Length == 210, "all seven static types, fragmentation and sequence wrap");
            }
            for (int i = 0; i < 210; i++)
            {
                var type = (Dnp3PointType)(i % 7);
                db.Update(type, (ushort)i, type == Dnp3PointType.Analog || type == Dnp3PointType.AnalogOutputStatus ?
                    Dnp3Value.Analog(200, 1, 123456789) : Dnp3Value.Bits(1, 1, 123456789));
            }
            Exchange(station, master, master.ReadClasses(14, 500), true, 500);
            Check(master.Result == Dnp3MasterResult.Success && handler.Values.Length == 210 && db.EventCount == 0, "all class events ack/drain");
            foreach (var value in handler.Values)
                Check(value.IsEvent && value.Value.Timestamp == 123456789, "absolute time events");
            Exchange(station, master, master.SetUnsolicited(14, true, 600), true, 600);
            db.Update(Dnp3PointType.Counter, 2, Dnp3Value.Bits(99, 1, 999));
            Exchange(station, master, station.Poll(601), false, 601);
            Check(handler.Unsolicited && handler.Values.Length == 1 && db.EventCount == 0, "enabled unsolicited");
            Exchange(station, master, master.SetUnsolicited(14, false, 700), true, 700);
            db.Update(Dnp3PointType.Counter, 2, Dnp3Value.Bits(100));
            Check(station.Poll(701) == null && db.EventCount == 1, "disabled unsolicited retains events");
            station.ResetConnection();
            master.ResetConnection();
            Exchange(station, master, station.Poll(800), false, 800);
            Check(db.EventCount == 1, "reconnect preserves events");
        }

        private static void ControlsCases()
        {
            var handler = new Handler();
            var station = new Dnp3Station(Database(), 1, 2, handler, handler);
            var master = new Dnp3Master(2, 1, handler);
            Exchange(station, master, master.ClearRestart(0), true);
            Check((master.Iin1 & 0x80) == 0, "clear restart");
            Exchange(station, master, master.SynchronizeTime(1700000000000, 10), true, 10);
            Check(master.Result == Dnp3MasterResult.Success && handler.Utc == 1700000000000 && (master.Iin1 & 16) == 0, "delay/time sync");
            foreach (var command in new[] { Dnp3Command.Relay(1, 3), Dnp3Command.Analog(2, -100, 1),
                Dnp3Command.Analog(3, -100, 2), Dnp3Command.Analog(4, 1.25, 3), Dnp3Command.Analog(5, 1.25, 4) })
            {
                byte[] wire = master.Command(command, true, 20);
                int operations = handler.Operations;
                Exchange(station, master, wire, true, 20);
                Check(master.Result == Dnp3MasterResult.Success && handler.Operations == operations + 1 && handler.Selected, "select/operate typed command");
                CheckSameCommand(command, handler.LastCommand);
                handler.LastCommand = default(Dnp3Command);
                Exchange(station, master, master.Command(command, false, 30), true, 30);
                Check(master.Result == Dnp3MasterResult.Success && !handler.Selected, "direct operate");
                CheckSameCommand(command, handler.LastCommand);
            }
            foreach (var variation in new byte[] { 1, 2, 3, 4 })
            {
                double[] values = variation == 1 ? new double[] { int.MinValue, -1, 0, 1, int.MaxValue } :
                    variation == 2 ? new double[] { short.MinValue, -100, -1, 0, 1, 255, 256, short.MaxValue } :
                    variation == 3 ? new double[] { -3.5, 0, 1.25, 16777216 } :
                    new double[] { -1e300, -0.1, 0, 1.25, 1e300 };
                foreach (double value in values)
                {
                    var command = Dnp3Command.Analog(7, value, variation);
                    byte[] body = Dnp3Controls.Encode(command);
                    Check(body.Length == 8 + (variation == 2 ? 2 : variation == 4 ? 8 : 4), "analog output width");
                    int p = 7;
                    var parsed = Dnp3Controls.Parse(body, ref p, 7, 41, variation);
                    Check(p == body.Length, "analog output parse consumes object");
                    CheckSameCommand(command, parsed);
                }
            }
            Throws<ArgumentException>(() => Dnp3Command.Analog(0, short.MaxValue + 1.0, 2));
            Throws<ArgumentException>(() => Dnp3Command.Analog(0, short.MinValue - 1.0, 2));
            Throws<ArgumentException>(() => Dnp3Command.Analog(0, 1, 5));
            Exchange(station, master, master.Command(Dnp3Command.Relay(99, 3), true, 40), true, 40);
            Check(master.Result == Dnp3MasterResult.CommandRejected && master.CommandStatus == Dnp3CommandStatus.Local, "handler rejection surfaced");
            var client = new Dnp3Channel(2, 1, true);
            var response = new Dnp3Channel(2, 1, true);
            byte[] commandBody = Dnp3Controls.Encode(Dnp3Command.Relay(1, 3));
            byte[] app = new byte[2 + commandBody.Length];
            app[0] = 0xc0;
            app[1] = 5;
            Array.Copy(commandBody, 0, app, 2, commandBody.Length);
            station.ResetConnection();
            int before = handler.Operations;
            Application(response, station.Process(client.Encode(app), 100));
            Application(response, station.Process(client.Encode(app), 101));
            Application(response, station.Process(client.Encode(app), 6000));
            Check(handler.Operations == before + 1, "direct operate retry does not execute twice");
            station.ResetConnection();
            app[1] = 6;
            before = handler.Operations;
            Check(station.Process(client.Encode(app), 200) == null && station.Process(client.Encode(app), 201) == null &&
                handler.Operations == before + 1, "no-ack direct operate duplicate suppression");
            station.ResetConnection();
            app[1] = 3;
            Application(response, station.Process(client.Encode(app), 300));
            app[0] = 0xc1;
            app[1] = 4;
            byte[] expired = Application(response, station.Process(client.Encode(app), 5300));
            Check(expired[expired.Length - 1] == (byte)Dnp3CommandStatus.NoSelect, "selection expiry");
            station.ResetConnection();
            app[0] = 0xc0;
            app[1] = 5;
            var malformed = new byte[app.Length + 1];
            Array.Copy(app, malformed, app.Length);
            before = handler.Operations;
            byte[] rejected = Application(response, station.Process(client.Encode(malformed), 6000));
            Check(rejected[3] == 4 && handler.Operations == before, "validate whole command before side effects");
            station.ResetConnection();
            var timedMaster = new Dnp3Master(2, 1, handler);
            byte[] delayReply = station.Process(timedMaster.SynchronizeTime(1000, 0), 0);
            bool busyRejected = false;
            try
            {
                timedMaster.SynchronizeTime(9000, 1);
            }
            catch (InvalidOperationException)
            {
                busyRejected = true;
            }
            byte[] timeWrite = timedMaster.Process(delayReply, 20);
            timedMaster.Process(station.Process(timeWrite, 20), 20);
            Check(busyRejected && handler.Utc == 1030 && timedMaster.Result == Dnp3MasterResult.Success,
                "time sync advances UTC to write time and compensates estimated one-way latency");
        }

        private static void LossCases()
        {
            var db = Database();
            var handler = new Handler();
            var station = new Dnp3Station(db, 1, 2, fragmentSize: 64, confirmationTimeout: 100, retries: 1);
            var master = new Dnp3Master(2, 1, handler, responseTimeout: 100, retries: 1);
            Exchange(station, master, station.Poll(0), false);
            db.Update(Dnp3PointType.Counter, 2, Dnp3Value.Bits(1));
            byte[] response = station.Process(master.ReadClasses(14, 1), 1);
            byte[] confirm = master.Process(response, 1);
            Check(db.EventCount == 1 && handler.Batches == 1, "events retained before confirmation");
            byte[] retried = station.Poll(101);
            byte[] confirmAgain = master.Process(retried, 101);
            Check(confirm != null && confirmAgain != null && handler.Batches == 1, "lost confirmation retry deduplicated");
            db.Update(Dnp3PointType.Counter, 2, Dnp3Value.Bits(2));
            station.Process(confirmAgain, 102);
            Check(db.EventCount == 1, "new event not removed by older confirm");
            byte[] next = station.Process(master.ReadClasses(14, 103), 103);
            Check(next != null, "next read");
            station.Poll(203);
            Timeout(() => station.Poll(303));
            Check(db.EventCount == 1, "retry exhaustion retains events");
            station.ResetConnection();
            master.ResetConnection();
            var noResponse = master.ReadClasses(1, 400);
            Check(noResponse != null && master.Poll(500) != null, "master request retry");
            Timeout(() => master.Poll(600));
            Check(master.Result == Dnp3MasterResult.TimedOut, "timeout result explicit");
            master.ResetConnection();
            station.ResetConnection();
            byte[] first = station.Process(master.ReadClasses(1, 700), 700);
            byte[] lostConfirm = master.Process(first, 700);
            byte[] repeatedConfirm = master.Poll(800);
            Check(lostConfirm != null && repeatedConfirm != null, "master retries missing continuation confirm");
            Exchange(station, master, repeatedConfirm, true, 800);
            Check(master.Result == Dnp3MasterResult.Success && handler.Values.Length == 7, "continuation recovers lost confirm");
        }

        private static void ConcurrentStreams()
        {
            var db = Database();
            var handler = new Handler();
            var station = new Dnp3Station(db, 1, 2, confirmationTimeout: 100, retries: 1);
            var master = new Dnp3Master(2, 1, handler, responseTimeout: 100);
            Exchange(station, master, station.Poll(0), false);
            Exchange(station, master, master.SetUnsolicited(14, true, 1), true, 1);
            db.Update(Dnp3PointType.Counter, 2, Dnp3Value.Bits(123));
            byte[] unsolicited = station.Poll(2);
            byte[] lost = master.Process(unsolicited, 2);
            Check(lost != null && handler.Batches == 1 && db.EventCount == 1, "unsolicited delivered, confirmation lost");
            Exchange(station, master, master.ReadClasses(14, 3), true, 3);
            Check(handler.Batches == 1 && db.EventCount == 1, "solicited read excludes in-flight unsolicited generation");
            Exchange(station, master, station.Poll(102), false, 102);
            Check(handler.Batches == 1 && db.EventCount == 0, "independent unsolicited retry survives solicited request");
            db.Update(Dnp3PointType.Counter, 2, Dnp3Value.Bits(124));
            var wire = station.Poll(103);
            byte[] confirm = master.Process(wire, 103);
            Check(confirm != null && handler.Batches == 2, "next unsolicited sequence accepted");
            station.Process(confirm, 103);
        }

        private static void FuzzCases()
        {
            var random = new Random(20261023);
            var db = Database();
            var handler = new Handler();
            var station = new Dnp3Station(db, 1, 2, handler);
            var sender = new Dnp3Channel(2, 1, true);
            for (int i = 0; i < 10000; i++)
            {
                var application = new byte[random.Next(2, 200)];
                random.NextBytes(application);
                application[0] = (byte)(0xc0 | (i & 15));
                station.ResetConnection();
                try
                {
                    station.Process(sender.Encode(application), i);
                }
                catch (Dnp3ProtocolException) { }
                Check(handler.Operations == 0, "malformed commands cannot execute");
            }
        }

        private static void DeliveryCases()
        {
            var db = Database();
            var handler = new Handler { FailNextDelivery = true };
            var station = new Dnp3Station(db, 1, 2, confirmationTimeout: 100);
            var master = new Dnp3Master(2, 1, handler);
            db.Update(Dnp3PointType.Counter, 2, Dnp3Value.Bits(12));
            byte[] reply = station.Process(master.ReadClasses(14, 0), 0);
            bool failed = false;
            try
            {
                master.Process(reply, 0);
            }
            catch (InvalidOperationException error) when (error.Message == "Fixture delivery failure.")
            {
                failed = true;
            }
            Check(failed && db.EventCount == 1 && handler.Batches == 0 && master.Busy, "delivery failure never confirms or silently succeeds");
            Exchange(station, master, station.Poll(100), false, 100);
            Check(db.EventCount == 0 && handler.Batches == 1, "delivery retry publishes before confirming");
            handler = new Handler();
            master = new Dnp3Master(2, 1, handler);
            master.ReadClasses(1, 0);
            var app = new byte[19];
            app[0] = 0xc0;
            app[1] = 0x81;
            int p = Dnp3Objects.Write(app, 4, new Dnp3Record
            {
                Point = new Dnp3PointDefinition(Dnp3PointType.Counter, 2, Dnp3Value.Bits(12)),
                Value = Dnp3Value.Bits(12),
                Variation = 1
            });
            app[p++] = 20;
            app[p++] = 1;
            app[p] = 1;
            var tx = new Dnp3Channel(1, 2, false);
            Invalid(() => master.Process(tx.Encode(app), 0));
            Check(handler.Batches == 0, "malformed later object cannot publish earlier part of a fragment");
            var valid = new byte[16];
            Array.Copy(app, valid, 16);
            master.Process(tx.Encode(valid), 0);
            Check(handler.Batches == 1 && handler.Values.Length == 1 && master.Result == Dnp3MasterResult.Success, "valid retry after rejected fragment");
            handler = new Handler();
            master = new Dnp3Master(2, 1, handler, maximumMeasurements: 1);
            master.ReadClasses(1, 0);
            var tooMany = new byte[28];
            Array.Copy(valid, tooMany, valid.Length);
            Array.Copy(valid, 4, tooMany, 16, 12);
            Invalid(() => master.Process(tx.Encode(tooMany), 0));
            Check(handler.Batches == 0 && master.Busy, "measurement capacity rejects whole fragment, no partial publication");
        }
    }
}
