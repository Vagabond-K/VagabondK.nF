using System;
using System.Collections.Generic;

using VagabondK.nF.DNP3.Serial;

namespace VagabondK.nF.DNP3.Tests
{
    // 보레이트와 문자당 비트 수로 바이트 도착을 예약하여 느린 전송, CRC 손상 및 ACK 유실의 시간 경계를 검증합니다.
    // 가상 선로 시뮬레이션이며 실제 UART 드라이버의 정지 비트·RS485 방향 제어를 보증하는 시험은 아닙니다.
    internal static class Dnp3SerialTimingCases
    {
        private static void Check(bool condition, string message)
        {
            if (!condition)
                throw new Exception("DNP3 serial timing: " + message);
            Cases.Check(true);
        }

        private sealed class Clock
        {
            internal long Now;
        }

        private struct Arrival
        {
            internal long Tick;
            internal byte Value;
        }

        // OS COM 포트가 아닌 가상 선로입니다. Write는 예약된 정지 비트가 전송되기 전에 반환합니다.
        private sealed class Wire : IDnp3ByteChannel
        {
            private readonly Clock _clock;
            private readonly int _baud, _bits;
            private readonly Queue<Arrival> _input = new Queue<Arrival>();
            private long _finish;
            internal Wire Peer;
            internal bool CorruptNextData, DropNextAck;
            internal int Corruptions, DroppedAcks;
            internal readonly List<byte[]> Writes = new List<byte[]>();

            internal bool Empty
            {
                get
                {
                    return _input.Count == 0;
                }
            }

            internal Wire(Clock clock, int baud, int bits)
            {
                _clock = clock;
                _baud = baud;
                _bits = bits;
            }

            public long Milliseconds
            {
                get
                {
                    return _clock.Now;
                }
            }

            public int Read(byte[] buffer, int offset, int count, int timeoutMilliseconds)
            {
                int read = 0;
                while (read < Math.Min(count, 7) && _input.Count > 0 && _input.Peek().Tick <= _clock.Now)
                    buffer[offset + read++] = _input.Dequeue().Value;
                return read;
            }

            public void Write(byte[] buffer, int offset, int count, int timeoutMilliseconds)
            {
                var frame = new byte[count];
                Array.Copy(buffer, offset, frame, 0, count);
                Writes.Add((byte[])frame.Clone());
                bool drop = DropNextAck && (frame[3] & 0x4f) == 0;
                if (drop)
                {
                    DropNextAck = false;
                    DroppedAcks++;
                }
                if (CorruptNextData && (frame[3] & 0x4f) == 0x43)
                {
                    frame[10] ^= 1;
                    CorruptNextData = false;
                    Corruptions++;
                }
                long start = Math.Max(_finish, _clock.Now) + 5;
                for (int i = 0; i < count; i++)
                {
                    long tick = start + ((long)(i + 1) * _bits * 1000 + _baud - 1) / _baud;
                    if (!drop)
                        Peer._input.Enqueue(new Arrival { Tick = tick, Value = frame[i] });
                    _finish = tick;
                }
            }
        }

        private sealed class Handler : IDnp3MasterHandler, IDnp3ControlHandler
        {
            internal readonly List<Dnp3Measurement> Values = new List<Dnp3Measurement>();
            internal int Operations;

            public void OnMeasurements(Dnp3Measurement[] measurements, Dnp3ResponseInfo info)
            {
                Values.AddRange(measurements);
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

        private sealed class Pair
        {
            internal readonly Clock Clock = new Clock();
            internal readonly Wire A, B;
            internal readonly Handler Handler = new Handler();
            internal readonly Dnp3Database Database;
            internal readonly Dnp3Master Master;
            internal readonly Dnp3SerialSession Left, Right;

            internal Pair(int baud, int bits, int ackTimeout, int frameTimeout)
            {
                A = new Wire(Clock, baud, bits);
                B = new Wire(Clock, baud, bits);
                A.Peer = B;
                B.Peer = A;
                var points = new Dnp3PointDefinition[210];
                for (int t = 0; t < 7; t++)
                    for (int i = 0; i < 30; i++)
                        points[t * 30 + i] = new Dnp3PointDefinition((Dnp3PointType)t, (ushort)i, Value(t, i, 0));
                Database = new Dnp3Database(points, 512);
                Master = new Dnp3Master(2, 1, Handler, responseTimeout: 120000, transportTimeout: 120000);
                var station = new Dnp3Station(Database, 1, 2, Handler, fragmentSize: 256,
                    confirmationTimeout: 120000, selectTimeout: 120000, transportTimeout: 120000);
                Left = new Dnp3SerialSession(A, Master, ackTimeoutMilliseconds: ackTimeout, frameTimeoutMilliseconds: frameTimeout);
                Right = new Dnp3SerialSession(B, station, ackTimeoutMilliseconds: ackTimeout, frameTimeoutMilliseconds: frameTimeout);
            }

            internal void Drain()
            {
                long deadline = Clock.Now + 240000;
                do
                {
                    Left.RunOnce();
                    Right.RunOnce();
                    Clock.Now++;
                    if (Left.TransmitIdle && Right.TransmitIdle && A.Empty && B.Empty && !Master.Busy)
                        return;
                } while (Clock.Now < deadline);
                throw new TimeoutException("Virtual baud-rate test did not settle.");
            }
        }

        private static Dnp3Value Value(int type, int index, int generation)
        {
            return type == 4 || type == 6 ? Dnp3Value.Analog(index + 0.25 + generation, timestamp: 1700000000000 + generation) :
                Dnp3Value.Bits(type == 0 || type == 5 ? (ulong)((index + generation) & 1) :
                    type == 1 ? (ulong)(1 + ((index + generation) & 1)) : (ulong)(1000 + index + generation),
                    timestamp: 1700000000000 + generation);
        }

        private static void Values(Handler handler, int generation, bool events)
        {
            Check(handler.Values.Count == 210, "all seven types delivered exactly once");
            var seen = new bool[210];
            foreach (Dnp3Measurement value in handler.Values)
            {
                int slot = (int)value.Type * 30 + value.Index;
                Check(slot >= 0 && slot < seen.Length && !seen[slot], "unique type/index");
                seen[slot] = true;
                Dnp3Value expected = Value((int)value.Type, value.Index, generation);
                Check(value.IsEvent == events && value.Value.Raw == expected.Raw && value.Value.Flags == expected.Flags &&
                    value.Value.Timestamp == (events ? expected.Timestamp : 0), "exact value/flags/event timestamp");
            }
            handler.Values.Clear();
        }

        internal static void Run()
        {
            foreach (int baud in new[] { 1200, 9600, 115200 })
                foreach (int bits in new[] { 10, 11 })
                {
                    var pair = new Pair(baud, bits, 5000, 4000);
                    pair.Drain();
                    pair.Handler.Values.Clear();
                    pair.Left.Send(pair.Master.ReadClasses(1, pair.Clock.Now));
                    pair.Drain();
                    Check(pair.Master.Result == Dnp3MasterResult.Success, "integrity success");
                    Values(pair.Handler, 0, false);
                    Check(pair.Left.Retransmissions == 0 && pair.Right.Retransmissions == 0, "no spurious retry with valid baud profile");
                    pair.Left.Send(pair.Master.SetUnsolicited(14, true, pair.Clock.Now));
                    pair.Drain();
                    pair.Handler.Values.Clear();
                    for (int type = 0; type < 7; type++)
                        for (int i = 0; i < 30; i++)
                            Check(pair.Database.Update((Dnp3PointType)type, (ushort)i, Value(type, i, 1)), "event retained");
                    pair.B.CorruptNextData = true;
                    pair.Drain();
                    Values(pair.Handler, 1, true);
                    Check(pair.Database.EventCount == 0 && pair.B.Corruptions == 1 &&
                        pair.Right.Retransmissions == 1 && pair.Left.Decoder.CrcErrors == 1, "on-wire CRC error recovers with confirmation");
                    pair.B.DropNextAck = true;
                    pair.Left.Send(pair.Master.Command(Dnp3Command.Relay(0, 3), false, pair.Clock.Now));
                    pair.Drain();
                    Check(pair.Master.Result == Dnp3MasterResult.Success && pair.Handler.Operations == 1 &&
                        pair.B.DroppedAcks == 1 && pair.Left.Retransmissions == 1, "lost ACK recovers without repeating control");
                    bool same = false;
                    for (int i = 1; i < pair.A.Writes.Count; i++)
                        if (Dnp3Channel.Equal(pair.A.Writes[i - 1], pair.A.Writes[i]))
                            same = true;
                    Check(same && !pair.Left.Faulted && !pair.Right.Faulted, "byte-identical retry on queued wire");
                    Console.WriteLine("DNP3 VIRTUAL BAUD PASS: " + baud + " baud, " + bits +
                        " bits/character; 210 points/7 types, unsolicited/CRC recovery, lost ACK/exactly-once control; simulated " +
                        pair.Clock.Now + " ms. No COM port or electrical validation.");
                }
            TooShortProfile();
        }

        private static void TooShortProfile()
        {
            var pair = new Pair(1200, 11, 5000, 1000);
            pair.Drain();
            pair.Left.Send(pair.Master.ReadClasses(1, pair.Clock.Now));
            try
            {
                pair.Drain();
            }
            catch (TimeoutException)
            {
                Check(pair.Left.Faulted || pair.Right.Faulted, "invalid slow-baud profile faults instead of success");
                Check(pair.Left.Decoder.FrameTimeouts > 0, "slow frame exceeds absolute receive deadline");
                Check(pair.Master.Result != Dnp3MasterResult.Success, "partial scan is never a successful task");
                Console.WriteLine("DNP3 VIRTUAL BAUD NEGATIVE PASS: 1200 baud/11 bits with 1000 ms frame deadline rejected.");
                return;
            }
            throw new Exception("DNP3 serial timing: invalid frame-timeout profile unexpectedly passed.");
        }
    }
}
