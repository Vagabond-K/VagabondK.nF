using System;
using VagabondK.nF.DNP3;

namespace VagabondK.nF.Virtual.Tests
{
    /// <summary>DNP3의 부호·실수·48비트 시간, 분할 응답 및 제어를 메모리 내 Master/Outstation으로 검증합니다.</summary>
    internal static class Dnp3Virtual
    {
        private const long MaxTimestamp = 281474976710655L;

        internal static void Run()
        {
            var handler = new Handler();
            var db = new Dnp3Database(new[] {
                new Dnp3PointDefinition(Dnp3PointType.Binary, 0, Dnp3Value.Bits(1), 1, 0, 2, 2),
                new Dnp3PointDefinition(Dnp3PointType.DoubleBitBinary, 0, Dnp3Value.Bits(2), 1, 0, 2, 2),
                new Dnp3PointDefinition(Dnp3PointType.Counter, 0, Dnp3Value.Bits(0xFFFFFFFF), 1, 0, 1, 5),
                new Dnp3PointDefinition(Dnp3PointType.FrozenCounter, 0, Dnp3Value.Bits(0x80000000), 1, 0, 1, 5),
                new Dnp3PointDefinition(Dnp3PointType.Analog, 0, Dnp3Value.Analog(-123456), 1, 0, 1, 3),
                new Dnp3PointDefinition(Dnp3PointType.Analog, 1, Dnp3Value.Analog(-1234), 1, 0, 2, 4),
                new Dnp3PointDefinition(Dnp3PointType.Analog, 2, Dnp3Value.Analog(-2.5), 1, 0, 5, 7),
                new Dnp3PointDefinition(Dnp3PointType.Analog, 3, Dnp3Value.Analog(-12345.678901234), 1, 0, 6, 8),
                new Dnp3PointDefinition(Dnp3PointType.BinaryOutputStatus, 0, Dnp3Value.Bits(1), 1, 0, 2, 2),
                new Dnp3PointDefinition(Dnp3PointType.AnalogOutputStatus, 0, Dnp3Value.Analog(-70000), 1, 0, 1, 3),
                new Dnp3PointDefinition(Dnp3PointType.AnalogOutputStatus, 1, Dnp3Value.Analog(-0.75), 1, 0, 3, 7)
            }, 64);
            var station = new Dnp3Station(db, 1, 2, handler, handler, 64);
            var master = new Dnp3Master(2, 1, handler);

            Exchange(station, master, station.Poll(0), false, 0);
            Exchange(station, master, master.ClearRestart(1), true, 1);
            Program.Check(master.Result == Dnp3MasterResult.Success && (master.Iin1 & 0x80) == 0, "DNP3 clear restart");

            Exchange(station, master, master.ReadClasses(1, 10), true, 10);
            Program.Check(master.Result == Dnp3MasterResult.Success && handler.Count == 11, "DNP3 integrity poll over 64-byte fragments");
            Program.Check(Raw(handler, Dnp3PointType.Binary, 0) == 1 && Raw(handler, Dnp3PointType.DoubleBitBinary, 0) == 2, "DNP3 binary statics");
            Program.Check(Raw(handler, Dnp3PointType.Counter, 0) == 0xFFFFFFFF, "DNP3 counter 0xFFFFFFFF");
            Program.Check(Raw(handler, Dnp3PointType.FrozenCounter, 0) == 0x80000000, "DNP3 frozen counter sign bit");
            Program.Check(Analog(handler, Dnp3PointType.Analog, 0) == -123456, "DNP3 g30v1 negative");
            Program.Check(Analog(handler, Dnp3PointType.Analog, 1) == -1234, "DNP3 g30v2 negative");
            Program.Check(Analog(handler, Dnp3PointType.Analog, 2) == -2.5, "DNP3 g30v5 float");
            Program.Check(Analog(handler, Dnp3PointType.Analog, 3) == -12345.678901234, "DNP3 g30v6 double");
            Program.Check(Analog(handler, Dnp3PointType.AnalogOutputStatus, 0) == -70000, "DNP3 g40v1 negative");
            Program.Check(Analog(handler, Dnp3PointType.AnalogOutputStatus, 1) == -0.75, "DNP3 g40v3 float");

            db.Update(Dnp3PointType.Binary, 0, Dnp3Value.Bits(0, 1, MaxTimestamp));
            db.Update(Dnp3PointType.Counter, 0, Dnp3Value.Bits(0xFFFFFFFE, 1, MaxTimestamp));
            db.Update(Dnp3PointType.Analog, 0, Dnp3Value.Analog(-654321, 1, MaxTimestamp));
            db.Update(Dnp3PointType.Analog, 1, Dnp3Value.Analog(-32768, 1, MaxTimestamp));
            db.Update(Dnp3PointType.Analog, 2, Dnp3Value.Analog(-0.125, 1, MaxTimestamp));
            db.Update(Dnp3PointType.Analog, 3, Dnp3Value.Analog(-1e300, 1, MaxTimestamp));
            Program.Check(db.EventCount == 6, "DNP3 event buffering");
            Exchange(station, master, master.ReadClasses(14, 20), true, 20);
            bool timed = handler.Count == 6;
            for (int i = 0; i < handler.Count; i++)
                timed &= handler.Values[i].IsEvent && handler.Values[i].Value.Timestamp == MaxTimestamp;
            Program.Check(master.Result == Dnp3MasterResult.Success && timed && db.EventCount == 0, "DNP3 48-bit event timestamps");
            Program.Check(Raw(handler, Dnp3PointType.Binary, 0) == 0 && Raw(handler, Dnp3PointType.Counter, 0) == 0xFFFFFFFE, "DNP3 binary/counter events");
            Program.Check(Analog(handler, Dnp3PointType.Analog, 0) == -654321 && Analog(handler, Dnp3PointType.Analog, 1) == -32768, "DNP3 integer analog events");
            Program.Check(Analog(handler, Dnp3PointType.Analog, 2) == -0.125 && Analog(handler, Dnp3PointType.Analog, 3) == -1e300, "DNP3 float/double analog events");

            Exchange(station, master, master.SynchronizeTime(1700000000000L, 30), true, 30);
            Program.Check(master.Result == Dnp3MasterResult.Success && handler.Utc == 1700000000000L, "DNP3 time synchronization");

            Command(station, master, handler, Dnp3Command.Relay(0, 3), "relay");
            Command(station, master, handler, Dnp3Command.Analog(0, -100000, 1), "g41v1");
            Command(station, master, handler, Dnp3Command.Analog(0, -32768, 2), "g41v2");
            Command(station, master, handler, Dnp3Command.Analog(0, -2.5, 3), "g41v3");
            Command(station, master, handler, Dnp3Command.Analog(0, -1e100, 4), "g41v4");

            byte[] wire = master.ReadClasses(1, 50);
            var decoder = new Dnp3StreamDecoder();
            byte[] decoded = null;
            for (int i = 0; i < wire.Length; i++)
            {
                byte[] frame = decoder.Push(wire[i], 50);
                if (frame != null)
                    decoded = frame;
            }
            Program.Check(decoded != null && Same(decoded, wire), "DNP3 stream decoder byte by byte");
            Recovery();
        }

        // CRC 손상이나 부분 프레임 만료가 제어 실행으로 이어지지 않고 다음 정상 요청은 처리되어야 합니다.
        private static void Recovery()
        {
            for (int role = 0; role < 2; role++)
            {
                for (int kind = 0; kind < 3; kind++)
                {
                    var handler = new Handler();
                    var db = new Dnp3Database(new[] {
                        new Dnp3PointDefinition(Dnp3PointType.Counter, 0, Dnp3Value.Bits(123))
                    }, 8);
                    var station = new Dnp3Station(db, 1, 2, handler);
                    var master = new Dnp3Master(2, 1, handler);
                    bool toStation = role == 0;
                    byte[] request = toStation ? master.Command(Dnp3Command.Relay(0, 3), false, 0) :
                        master.ReadClasses(1, 0);
                    byte[] good = toStation ? request : station.Process(request, 0);
                    var decoder = new Dnp3StreamDecoder(10);
                    int delivered = 0;
                    if (kind < 2)
                    {
                        byte[] bad = Program.Copy(good, good.Length);
                        bad[kind == 0 ? 8 : bad.Length - 1] ^= 1;
                        for (int i = 0; i < bad.Length; i++)
                            if (decoder.Push(bad[i], 0) != null) delivered++;
                        Program.Check(decoder.CrcErrors == 1 && decoder.DiscardedBytes == bad.Length,
                            "DNP3 exact CRC rejection role/kind " + role + "/" + kind);
                    }
                    else
                    {
                        decoder.Push(5, 0);
                        decoder.Push(0x64, 0);
                        decoder.Push(20, 0);
                        Program.Check(decoder.Take(10) == null && decoder.FrameTimeouts == 1 &&
                            decoder.DiscardedBytes == 3, "DNP3 partial expiry role " + role);
                    }
                    Program.Check(delivered == 0 && handler.Count == 0 && handler.Operations == 0,
                        "DNP3 corrupt input not delivered role/kind " + role + "/" + kind);
                    for (int i = 0; i < good.Length; i++)
                    {
                        byte[] frame = decoder.Push(good[i], 11);
                        if (frame == null) continue;
                        delivered++;
                        Exchange(station, master, frame, toStation, 11);
                    }
                    Program.Check(delivered == 1 && master.Result == Dnp3MasterResult.Success &&
                        (toStation ? handler.Operations == 1 : handler.Count == 1 &&
                            Raw(handler, Dnp3PointType.Counter, 0) == 123),
                        "DNP3 normal control/poll after rejection role/kind " + role + "/" + kind);
                }
            }
        }

        private static void Command(Dnp3Station station, Dnp3Master master, Handler handler, Dnp3Command command, string name)
        {
            for (int sbo = 0; sbo < 2; sbo++)
            {
                handler.Operations = 0;
                Exchange(station, master, master.Command(command, sbo == 1, 40), true, 40);
                var last = handler.LastCommand;
                Program.Check(master.Result == Dnp3MasterResult.Success && handler.Operations == 1 && handler.Selected == (sbo == 1) &&
                    last.Index == command.Index && last.Group == command.Group && last.Variation == command.Variation &&
                    last.ControlCode == command.ControlCode && last.AnalogValue == command.AnalogValue,
                    "DNP3 " + name + (sbo == 1 ? " select/operate" : " direct operate"));
            }
        }

        private static ulong Raw(Handler handler, Dnp3PointType type, ushort index)
        {
            for (int i = 0; i < handler.Count; i++)
                if (handler.Values[i].Type == type && handler.Values[i].Index == index)
                    return handler.Values[i].Value.Raw;
            return ulong.MaxValue;
        }

        private static double Analog(Handler handler, Dnp3PointType type, ushort index)
        {
            for (int i = 0; i < handler.Count; i++)
                if (handler.Values[i].Type == type && handler.Values[i].Index == index)
                    return handler.Values[i].Value.AsAnalog();
            return double.NaN;
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i])
                    return false;
            return true;
        }

        private static void Exchange(Dnp3Station station, Dnp3Master master, byte[] wire, bool toStation, long now)
        {
            var queue = new byte[64][];
            var direction = new bool[64];
            int head = 0, tail = 0, rounds = 0;
            if (wire != null)
            {
                queue[tail] = wire;
                direction[tail++] = toStation;
            }
            var header = new byte[3];
            while (head != tail)
            {
                if (rounds++ > 512)
                    throw new Exception("Unbounded DNP3 exchange.");
                byte[] item = queue[head];
                bool target = direction[head];
                head = (head + 1) % queue.Length;
                for (int offset = 0; offset < item.Length;)
                {
                    Array.Copy(item, offset, header, 0, 3);
                    int length = Dnp3Link.FrameLength(header);
                    var frame = new byte[length];
                    Array.Copy(item, offset, frame, 0, length);
                    offset += length;
                    byte[] next = target ? station.Process(frame, now) : master.Process(frame, now);
                    if (next != null)
                    {
                        queue[tail] = next;
                        direction[tail] = !target;
                        tail = (tail + 1) % queue.Length;
                    }
                }
            }
        }

        private sealed class Handler : IDnp3MasterHandler, IDnp3ControlHandler, IDnp3TimeHandler
        {
            internal readonly Dnp3Measurement[] Values = new Dnp3Measurement[64];
            internal int Count, Operations;
            internal bool Selected;
            internal long Utc;
            internal Dnp3Command LastCommand;

            public void OnMeasurements(Dnp3Measurement[] values, Dnp3ResponseInfo response)
            {
                if (response.First)
                    Count = 0;
                for (int i = 0; i < values.Length; i++)
                    Values[Count++] = values[i];
            }

            public Dnp3CommandStatus Validate(Dnp3Command command)
            {
                return Dnp3CommandStatus.Success;
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
    }
}
