using System;

namespace VagabondK.nF.DNP3.SerialHost
{
    // 격리된 통신 시험용 벡터입니다. CROB는 데이터만 준비하며 물리 출력을 구동하지 않습니다.
    // 부호 있는 0, 비정규 수, Single 경계 및 48비트 시각을 Static/Event Variation으로 왕복 검증합니다.
    // 예약 CROB 인덱스 65500/65501은 벡터 준비와 종료 표식이며 실제 장치 제어에 사용하면 안 됩니다.
    public sealed class NumericValidation : IDnp3MasterHandler, IDnp3ControlHandler
    {
        public const int Points = 84;
        public readonly Dnp3Database Database;

        public int Operations { get; private set; }

        public int Measurements { get; private set; }

        public bool Completed { get; private set; }
        private readonly Dnp3Measurement[] _values = new Dnp3Measurement[256];
        private int _count;
        private string _phase = "startup";
        private static readonly byte[] Groups = { 30, 30, 40, 40, 32, 32, 32, 32, 42, 42, 42, 42 };
        private static readonly byte[] Variations = { 5, 6, 3, 4, 5, 6, 7, 8, 5, 6, 7, 8 };
        private static readonly ulong[] Boundaries = {
            0, 0x8000000000000000UL, 1, 0x8000000000000001UL,
            0x000fffffffffffffUL, 0x800fffffffffffffUL, 0x0010000000000000UL, 0x8010000000000000UL,
            0x36a0000000000000UL, 0xb6a0000000000000UL, 0x380fffffc0000000UL, 0xb80fffffc0000000UL,
            0x3810000000000000UL, 0xb810000000000000UL, 0x3ff4000000000000UL, 0xbff4000000000000UL,
            0x47efffffe0000000UL, 0xc7efffffe0000000UL, 0x3ff0000000000001UL, 0xbff0000000000001UL
        };
        private static readonly long[] Times = {
            0, 1, 65535, 65536, 4294967295L, 4294967296L, 281474976710654L, 281474976710655L
        };
        private static long Timestamp(int index)
        {
#if DNP3_NUMERIC_SOAK
            return Times[index & 7] == 0 ? 1 : Times[index & 7];
#else
            return Times[index & 7];
#endif
        }

        public static double Number(int index)
        {
            if (index < Boundaries.Length)
                return BitConverter.Int64BitsToDouble(unchecked((long)Boundaries[index]));
            uint bits = unchecked((uint)(index - 20) * 2654435761U) & 0xfeffffffU;
            ulong sign = (ulong)(bits & 0x80000000U) << 32;
            uint fraction = bits & 0x007fffffU;
            int exponent = (int)((bits >> 23) & 255);
            if (exponent == 0 && fraction == 0)
                return BitConverter.Int64BitsToDouble(unchecked((long)sign));
            if (exponent == 0)
            {
                exponent = 1;
                while ((fraction & 0x00800000U) == 0)
                {
                    fraction <<= 1;
                    exponent--;
                }
                fraction &= 0x007fffffU;
            }
            ulong raw = sign | ((ulong)(exponent + 896) << 52) | ((ulong)fraction << 29);
            return BitConverter.Int64BitsToDouble(unchecked((long)raw));
        }

        public static byte Flags(int index, int operation)
        {
            return (byte)((1 | ((index * 37) & 254)) ^ ((operation & 1) == 0 ? 128 : 0));
        }

        private static void Check(bool value, string reason)
        {
            if (!value)
                throw new InvalidOperationException("DNP3 numeric: " + reason);
        }

        public NumericValidation()
        {
            var points = new Dnp3PointDefinition[Points * 2];
            for (int t = 0; t < 2; t++)
                for (ushort i = 0; i < Points; i++)
                    points[t * Points + i] = new Dnp3PointDefinition(
                        t == 0 ? Dnp3PointType.Analog : Dnp3PointType.AnalogOutputStatus, i,
                        Dnp3Value.Analog(0, 0), staticVariation: (byte)(t == 0 ? 6 : 4), eventVariation: 8);
            Database = new Dnp3Database(points, 256);
        }

        public Dnp3CommandStatus Validate(Dnp3Command command)
        {
            if (command.Group != 12 || command.ControlCode != 3 || command.Count != 1 ||
                command.OffMilliseconds != 0)
                return Dnp3CommandStatus.NotSupported;
#if DNP3_NUMERIC_SOAK
            if (command.Index == 65501 && Operations >= 12 && !Completed && command.OnMilliseconds == 0)
                return Dnp3CommandStatus.Success;
            return command.Index == 65500 && !Completed &&
                command.OnMilliseconds == unchecked((uint)(Operations + 1)) ?
                Dnp3CommandStatus.Success : Dnp3CommandStatus.OutOfRange;
#else
            if (command.Index == 65501 && Operations == 12 && !Completed && command.OnMilliseconds == 0)
                return Dnp3CommandStatus.Success;
            return command.Index == 65500 && !Completed && command.OnMilliseconds == Operations + 1 && Operations < 12 ?
                Dnp3CommandStatus.Success : Dnp3CommandStatus.OutOfRange;
#endif
        }

        public Dnp3CommandStatus Operate(Dnp3Command command, bool selected)
        {
            Dnp3CommandStatus status = Validate(command);
            if (status != Dnp3CommandStatus.Success)
                return status;
            if (command.Index == 65501)
            {
                Completed = true;
                return Dnp3CommandStatus.Success;
            }
            Operations++;
            for (int t = 0; t < 2; t++)
                for (ushort i = 0; i < Points; i++)
                    Check(Database.Update(t == 0 ? Dnp3PointType.Analog : Dnp3PointType.AnalogOutputStatus,
                        i, Dnp3Value.Analog(Number(i), Flags(i, Operations), Timestamp(i))), "event overflow");
            return Dnp3CommandStatus.Success;
        }

        public void OnMeasurements(Dnp3Measurement[] values, Dnp3ResponseInfo response)
        {
            Check(!response.Unsolicited && _count + values.Length <= _values.Length, "callback capacity/solicited");
            Array.Copy(values, 0, _values, _count, values.Length);
            _count += values.Length;
        }

        private void Request(byte[] bytes, Dnp3Master master, ValidationSession session)
        {
            _count = 0;
            session.Send(bytes);
            long deadline = session.Milliseconds + 90000;
            while (master.Busy || !session.TransmitIdle)
            {
                Check(session.Milliseconds < deadline, "request deadline");
                session.RunOnce();
            }
            Check(master.Result == Dnp3MasterResult.Success && master.Iin2 == 0,
                _phase + " result=" + master.Result + " IIN=" + master.Iin1 + "/" + master.Iin2);
        }

        public void RunMaster(Dnp3Master master, ValidationSession session)
        {
            long startup = session.Milliseconds + 4000;
            while (session.Milliseconds < startup)
                session.RunOnce();
            _phase = "clear restart";
            Request(master.ClearRestart(session.Milliseconds), master, session);
            // 격리된 Go 피어는 설정에서 비요청 기능을 비활성화합니다.
            for (int n = 0; n < Groups.Length; n++)
            {
                _phase = "arm=" + (n + 1);
                Request(master.Command(Dnp3Command.Relay(65500, 3, onMilliseconds: (uint)(n + 1)),
                    false, session.Milliseconds), master, session);
                byte group = Groups[n], variation = Variations[n];
                _phase = "group=" + group + " variation=" + variation;
                Request(master.ReadGroup(group, variation, session.Milliseconds), master, session);
                Check(_count == Points, "measurement count g=" + group + " v=" + variation);
                var seen = new bool[Points];
                for (int j = 0; j < _count; j++)
                {
                    Dnp3Measurement point = _values[j];
                    int i = point.Index;
                    bool events = group == 32 || group == 42;
                    Check(i < Points && !seen[i] && point.IsEvent == events &&
                        point.Type == (group == 30 || group == 32 ? Dnp3PointType.Analog : Dnp3PointType.AnalogOutputStatus),
                        "index/type/event");
                    seen[i] = true;
                    double expected = Number(i);
                    if (group == 30 && variation == 5 || group == 40 && variation == 3 ||
                        events && (variation == 5 || variation == 7))
                        expected = (float)expected;
                    long timestamp = events && variation >= 7 ? Times[i & 7] : 0;
                    // Go Outstation API에서 시각 0은 현재 시각을 자동 지정한다는 뜻입니다.
                    if (events && variation >= 7 && timestamp == 0)
                        timestamp = 1;
                    Check(point.Value.Raw == unchecked((ulong)BitConverter.DoubleToInt64Bits(expected)),
                        "exact IEEE754 g=" + group + " v=" + variation + " index=" + i);
                    Check(point.Value.Flags == Flags(i, n + 1) && point.Value.Timestamp == timestamp,
                        "quality/time g=" + group + " v=" + variation + " index=" + i);
                    Measurements++;
                }
                _phase = "drain=" + (n + 1);
                Request(master.ReadClasses(14, session.Milliseconds), master, session);
                Check(_count == (group == 32 || group == 42 ? Points : Points * 2), "remaining event count");
            }
            _phase = "completion";
            Request(master.Command(Dnp3Command.Relay(65501, 3), false, session.Milliseconds), master, session);
            Check(Measurements == Points * 12, "numeric totals");
        }
    }
}
