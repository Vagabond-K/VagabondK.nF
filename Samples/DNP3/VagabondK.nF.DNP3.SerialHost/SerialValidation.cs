using System;
using VagabondK.nF.DNP3.Serial;

namespace VagabondK.nF.DNP3.SerialHost
{
    /// <summary>동일한 검증 시나리오를 Serial 또는 TCP 세션에서 실행하기 위한 예제용 어댑터 계약입니다.</summary>
    public abstract class ValidationSession
    {
        public abstract long Milliseconds { get; }

        public abstract bool TransmitIdle { get; }

        public abstract void Send(byte[] bytes);

        public abstract void RunOnce();
    }

    public sealed class ValidationSerialSession : ValidationSession
    {
        private readonly IDnp3ByteChannel _channel;
        private readonly Dnp3SerialSession _session;

        public ValidationSerialSession(IDnp3ByteChannel channel, Dnp3SerialSession session)
        {
            _channel = channel;
            _session = session;
        }

        public override long Milliseconds
        {
            get
            {
                return _channel.Milliseconds;
            }
        }

        public override bool TransmitIdle
        {
            get
            {
                return _session.TransmitIdle;
            }
        }

        public override void Send(byte[] bytes)
        {
            _session.Send(bytes);
        }

        public override void RunOnce()
        {
            _session.RunOnce(10);
        }
    }

    // 격리된 시험 데이터만 사용하며 제어 콜백은 물리 출력을 조작하지 않습니다.
    // 일곱 종류의 Static/Event, 범위 읽기, 제어, 시각 및 경계값을 검증합니다. 테스트 명령은 메모리의 시험 데이터를 다음 세대로 갱신합니다.
    public sealed class SerialValidation : IDnp3MasterHandler, IDnp3ControlHandler, IDnp3TimeHandler
    {
        public const int PointsPerType = 8;
        public const int ExpectedOperations = 45;
        public readonly Dnp3Database Database;

        public int Checks { get; private set; }

        public int Operations { get; private set; }

        public long Utc { get; private set; }
        private int _generation, _count;
        private string _phase = "startup";
        private readonly Dnp3Measurement[] _values = new Dnp3Measurement[1024];
        private static readonly byte[] StaticGroups = { 1, 3, 20, 21, 30, 10, 40 };
        private static readonly byte[] EventGroups = { 2, 4, 22, 23, 32, 11, 42 };

        public SerialValidation()
        {
            var points = new Dnp3PointDefinition[7 * PointsPerType];
            for (int type = 0; type < 7; type++)
                for (int i = 0; i < PointsPerType; i++)
                    points[type * PointsPerType + i] = new Dnp3PointDefinition((Dnp3PointType)type, (ushort)i, Value(type, i, 0));
            Database = new Dnp3Database(points, 512);
        }

        private static Dnp3Value Value(int type, int index, int generation)
        {
            if (generation >= 32 && generation <= 35)
                return BoundaryValue(type, index, generation);
            double analog = index + generation + 0.25;
            if ((index & 1) == 0)
                analog = -analog;
            return type == 4 || type == 6 ? Dnp3Value.Analog(analog, timestamp: 1700000000000 + generation) :
                Dnp3Value.Bits(type == 0 || type == 5 ? (ulong)((index + generation) & 1) :
                    type == 1 ? (ulong)(1 + ((index + generation) & 1)) : (ulong)(1000 + index + generation),
                    timestamp: 1700000000000 + generation);
        }

        public static Dnp3Value BoundaryValue(int type, int index, int generation)
        {
            int scenario = generation - 32;
            long[] times = { 0, 1, 65535, 65536, 4294967295L, 4294967296L, 281474976710654L, 281474976710655L };
            long time = scenario >= 2 ? 1 : times[scenario == 0 ? index : 7 - index];
            byte[] flags = { 0, 128, 2, 4, 8, 16, 32, 64 };
            byte quality = (byte)(flags[index] ^ (scenario == 0 ? 0 : scenario == 1 ? 255 : scenario == 2 ? 85 : 170));
            quality &= (byte)(type == 1 ? 63 : type == 0 || type == 5 ? 127 : 255);
            if (type == 4 || type == 6)
            {
                double[] numbers = { -2147483648.0, -32768, -1.25, 0, 1.25, 32767, 2147483520.0, 16777216 };
                return Dnp3Value.Analog(numbers[(index + scenario) & 7], quality, time);
            }
            ulong[] counters = { 0, 1, 65535, 65536, 2147483647, 2147483648, 4294967294, 4294967295 };
            return Dnp3Value.Bits(type == 1 ? (ulong)((index + scenario) & 3) :
                type == 0 || type == 5 ? (ulong)((index + scenario) & 1) : counters[(index + scenario) & 7], quality, time);
        }

        private void Check(bool value, string reason)
        {
            if (!value)
                throw new InvalidOperationException("DNP3 UART validation: " + reason);
            Checks++;
        }

        public Dnp3CommandStatus Validate(Dnp3Command command)
        {
            return command.Group == 12 && command.Index == 65500 || command.Group == 41 && command.Index == 0 ?
                Dnp3CommandStatus.Success : Dnp3CommandStatus.NotSupported;
        }

        public Dnp3CommandStatus Operate(Dnp3Command command, bool selected)
        {
            Operations++;
            if (command.Group == 12)
            {
                _generation++;
                for (int type = 0; type < 7; type++)
                    for (int i = 0; i < PointsPerType; i++)
                        Check(Database.Update((Dnp3PointType)type, (ushort)i, Value(type, i, _generation)), "event overflow");
            }
            else
                Check(Database.Update(Dnp3PointType.AnalogOutputStatus, 0, Dnp3Value.Analog(command.AnalogValue)), "analog output event");
            return Dnp3CommandStatus.Success;
        }

        public void SetUtcMilliseconds(long milliseconds)
        {
            Utc = milliseconds;
        }

        public void OnMeasurements(Dnp3Measurement[] values, Dnp3ResponseInfo response)
        {
            Check(_count <= _values.Length - values.Length, "callback capacity");
            Array.Copy(values, 0, _values, _count, values.Length);
            _count += values.Length;
        }

        private static bool Supported(int type, bool events, int variation)
        {
            if (type == 4)
                return events ? variation <= 8 : variation == 1 || variation == 2 || variation == 5 || variation == 6;
            if (type == 6)
                return events ? variation <= 8 : variation <= 4;
            if (type == 2 || type == 3)
                return events ? variation == 1 || variation == 2 || variation == 5 || variation == 6 : variation <= 2;
            return events ? variation <= 2 : variation == 2;
        }

        private void Values(int type, bool events, int variation, int generation)
        {
            Check(_count == PointsPerType, "measurement count");
            var seen = new bool[PointsPerType];
            for (int i = 0; i < _count; i++)
            {
                Dnp3Measurement point = _values[i];
                Check(point.Type == (Dnp3PointType)type && point.Index < PointsPerType && !seen[point.Index], "type/index uniqueness");
                seen[point.Index] = true;
                Dnp3Value expected = Value(type, point.Index, generation);
                bool analog = type == 4 || type == 6;
                bool timed = events && (analog ? variation == 3 || variation == 4 || variation == 7 || variation == 8 :
                    type == 2 || type == 3 ? variation >= 5 : variation == 2);
                bool integerAnalog = analog && (events ? variation <= 4 : variation <= 2);
                Check(point.IsEvent == events && point.Value.Flags == expected.Flags &&
                    point.Value.Timestamp == (timed ? expected.Timestamp : 0), "flags/timestamp");
                Check(analog ? point.Value.AsAnalog() == (integerAnalog ? (int)expected.AsAnalog() : expected.AsAnalog()) :
                    point.Value.Raw == expected.Raw, "exact measurement");
            }
        }

        private void Complete(Dnp3Master master, ValidationSession session)
        {
            long deadline = session.Milliseconds + 90000;
            while (master.Busy || !session.TransmitIdle)
            {
                Check(session.Milliseconds < deadline, "operation deadline");
                session.RunOnce();
            }
            Check(master.Result == Dnp3MasterResult.Success && master.Iin2 == 0,
                _phase + " result=" + master.Result + " IIN=" + master.Iin1 + "/" + master.Iin2);
        }

        private void Request(byte[] bytes, Dnp3Master master, ValidationSession session)
        {
            _count = 0;
            session.Send(bytes);
            Complete(master, session);
        }

        public void RunMaster(IDnp3ByteChannel channel, Dnp3Master master, Dnp3SerialSession session, int iterations)
        {
            RunMaster(master, new ValidationSerialSession(channel, session), iterations);
        }

        public void RunMaster(Dnp3Master master, ValidationSession session, int iterations)
        {
            // 반이중 선로를 점유하기 전에 시작 시의 빈 비요청 응답 확인 절차가 끝나도록 기다립니다.
            long startup = session.Milliseconds + 4000;
            long startupDeadline = startup + 30000;
            while (session.Milliseconds < startup || !session.TransmitIdle)
            {
                Check(session.Milliseconds < startupDeadline, "startup deadline");
                session.RunOnce();
            }
            Request(master.ClearRestart(session.Milliseconds), master, session);
            Request(master.SetUnsolicited(14, false, session.Milliseconds), master, session);
            int generation = 0, variations = 0;
            for (int e = 0; e < 2; e++)
                for (int type = 0; type < 7; type++)
                    for (byte variation = 1; variation <= 8; variation++)
                    {
                        bool events = e == 1;
                        if (!Supported(type, events, variation))
                            continue;
                        _phase = "group=" + (events ? EventGroups[type] : StaticGroups[type]) + " variation=" + variation;
                        if (events)
                        {
                            Request(master.Command(Dnp3Command.Relay(65500, 3), false, session.Milliseconds), master, session);
                            generation++;
                        }
                        Request(master.ReadGroup(events ? EventGroups[type] : StaticGroups[type], variation, session.Milliseconds),
                            master, session);
                        Values(type, events, variation, generation);
                        variations++;
                        if (events)
                        {
                            _phase += " drain events";
                            Request(master.ReadClasses(14, session.Milliseconds), master, session);
                        }
                    }
            Check(variations == 45, "all supported variations");
            for (byte variation = 1; variation <= 4; variation++)
            {
                _phase = "analog command variation=" + variation;
                double value = variation <= 2 ? -123 : 1.25;
                Request(master.Command(Dnp3Command.Analog(0, value, variation), true, session.Milliseconds), master, session);
                Request(master.ReadGroup(40, 4, session.Milliseconds, 0, 0), master, session);
                Check(_count == 1 && _values[0].Value.AsAnalog() == value, "selected analog command readback");
            }
            _phase = "signed32 boundaries";
            Request(master.ReadClasses(14, session.Milliseconds), master, session);
            foreach (double value in new double[] { -2147483648.0, -123.0, -1.0, 0.0, 2147483647.0 })
            {
                Request(master.Command(Dnp3Command.Analog(0, value, 1), true, session.Milliseconds), master, session);
                Request(master.ReadGroup(40, 1, session.Milliseconds, 0, 0), master, session);
                Check(_count == 1 && _values[0].Value.AsAnalog() == value, "signed32 static boundary");
                Request(master.ReadGroup(42, 1, session.Milliseconds), master, session);
                Check(_count == 1 && _values[0].IsEvent && _values[0].Value.AsAnalog() == value, "signed32 event boundary");
            }
            _phase = "time sync";
            Request(master.SynchronizeTime(1700000001234, session.Milliseconds), master, session);
            Request(master.ReadClasses(14, session.Milliseconds), master, session);
            Request(master.SetUnsolicited(14, true, session.Milliseconds), master, session);
            Request(master.Command(Dnp3Command.Relay(65500, 3), true, session.Milliseconds), master, session);
            generation++;
            long unsDeadline = session.Milliseconds + 90000;
            while (_count < 7 * PointsPerType || !session.TransmitIdle)
            {
                Check(session.Milliseconds < unsDeadline, "unsolicited deadline");
                session.RunOnce();
            }
            Check(_count == 7 * PointsPerType, "unsolicited exact count");
            var allSeen = new bool[7 * PointsPerType];
            for (int i = 0; i < _count; i++)
            {
                Dnp3Measurement value = _values[i];
                int slot = (int)value.Type * PointsPerType + value.Index;
                Check(slot >= 0 && slot < allSeen.Length && !allSeen[slot] && value.IsEvent &&
                    value.Value.Raw == Value((int)value.Type, value.Index, generation).Raw &&
                    value.Value.Flags == 1 && value.Value.Timestamp == 1700000000000 + generation, "unsolicited exact value");
                allSeen[slot] = true;
            }
            Request(master.SetUnsolicited(14, false, session.Milliseconds), master, session);
            for (int i = 0; i < iterations; i++)
            {
                Request(master.ReadGroup(30, 6, session.Milliseconds), master, session);
                Values(4, false, 6, generation);
            }
            RunBoundaries(master, session);
            // 물리 출력이 아닌 시험 전용 완료 표식입니다.
            Request(master.Command(Dnp3Command.Analog(0, 24589, 1), false, session.Milliseconds), master, session);
            Request(master.ReadGroup(40, 4, session.Milliseconds, 0, 0), master, session);
            Check(_count == 1 && _values[0].Value.AsAnalog() == 24589, "completion readback");
        }

        private void RunBoundaries(Dnp3Master master, ValidationSession session)
        {
            for (int scenario = 0; scenario < 4; scenario++)
            {
                _phase = "time/quality boundary scenario=" + scenario;
                Request(master.Command(Dnp3Command.Relay(65500, 3), false, session.Milliseconds), master, session);
                // 어느 쪽도 읽기 전에 시각이 같은 두 세대의 이벤트를 큐에 넣습니다.
                if (scenario == 2)
                    continue;
                for (int type = 0; type < 7; type++)
                {
                    byte stat = (byte)(type == 4 ? 6 : type == 6 ? 4 : type == 2 || type == 3 ? 1 : 2);
                    byte ev = (byte)(type == 4 || type == 6 ? 8 : type == 2 || type == 3 ? 5 : 2);
                    Request(master.ReadGroup(StaticGroups[type], stat, session.Milliseconds), master, session);
                    BoundaryValues(type, false, scenario);
                    Request(master.ReadGroup(EventGroups[type], ev, session.Milliseconds), master, session);
                    BoundaryValues(type, true, scenario);
                }
            }
        }

        private void BoundaryValues(int type, bool events, int scenario)
        {
            int generations = events && scenario == 3 ? 2 : 1;
            Check(_count == PointsPerType * generations, "boundary count");
            var seen = new int[PointsPerType];
            for (int i = 0; i < _count; i++)
            {
                Dnp3Measurement point = _values[i];
                Check(point.Type == (Dnp3PointType)type && point.Index < PointsPerType, "boundary type/index");
                int occurrence = seen[point.Index]++;
                Check(occurrence < generations, "boundary duplicate");
                int generation = 32 + (generations == 2 ? 2 + occurrence : scenario);
                Dnp3Value expected = BoundaryValue(type, point.Index, generation);
                Check(point.IsEvent == events && point.Value.Raw == expected.Raw &&
                    point.Value.Flags == expected.Flags && point.Value.Timestamp == (events ? expected.Timestamp : 0),
                    "boundary exact value/flags/time/order");
            }
        }
    }
}
