using System;


namespace VagabondK.nF.SNMP.Tests
{
    public static class Cases
    {
        public static int Checks;

        public static void Check(bool condition)
        {
            Checks++;
            if (!condition)
                throw new InvalidOperationException("Protocol assertion " + Checks);
        }

        public static void Throws<T>(Action action) where T : Exception
        {
            bool thrown = false;
            try
            {
                action();
            }
            catch (T)
            {
                thrown = true;
            }
            Check(thrown);
        }
    }

    internal sealed class Store : ISnmpValueStore
    {
        internal const string E = "1.3.6.1.4.1.32473.1.";
        public readonly SnmpObject[] Objects;
        public readonly SnmpValue[] Values;
        public int Writes;

        public Store()
        {
            Objects = new[] {
                new SnmpObject(SnmpOid.Parse(E + "1.0"), SnmpType.Integer32, true),
                new SnmpObject(SnmpOid.Parse(E + "2.0"), SnmpType.OctetString, true, 16),
                new SnmpObject(SnmpOid.Parse(E + "3.0"), SnmpType.Counter32),
                new SnmpObject(SnmpOid.Parse(E + "4.0"), SnmpType.Gauge32, true),
                new SnmpObject(SnmpOid.Parse(E + "5.0"), SnmpType.TimeTicks, true),
                new SnmpObject(SnmpOid.Parse(E + "6.0"), SnmpType.Counter64, true),
                new SnmpObject(SnmpOid.Parse(E + "7.0"), SnmpType.IpAddress, true),
                new SnmpObject(SnmpOid.Parse(E + "8.0"), SnmpType.ObjectIdentifier, true),
                new SnmpObject(SnmpOid.Parse("1.3.6.1.2.1.2.1.0"), SnmpType.Integer32)
            };
            Values = new[] {
                SnmpValue.Integer32(-5), SnmpValue.Text("hello"), SnmpValue.Counter32(4000000000),
                SnmpValue.Gauge32(7), SnmpValue.TimeTicks(100), SnmpValue.Counter64(ulong.MaxValue),
                SnmpValue.IpAddress(new byte[] { 192, 168, 0, 1 }), SnmpValue.ObjectIdentifier(new uint[] { 1, 3, 6 }),
                SnmpValue.Integer32(1)
            };
        }

        public Store(SnmpObject[] objects, SnmpValue[] values)
        {
            Objects = objects;
            Values = values;
        }

        public int Count
        {
            get
            {
                return Objects.Length;
            }
        }

        public SnmpObject GetObject(int index)
        {
            return Objects[index];
        }

        public SnmpValue Read(int index)
        {
            return Values[index];
        }

        public bool AcceptsWrite(int index, SnmpValue value)
        {
            return !(value.Type == SnmpType.Integer32 && value.Integer == 666);
        }

        public void QueueWrite(int index, SnmpValue value)
        {
            Values[index] = value;
            Writes++;
        }
    }

    /// <summary>SNMP 에이전트와 UDP 세션 단위 테스트 진입점입니다. 외부 장비 없이 메모리와 루프백 UDP만 사용합니다.</summary>
    /// <remarks>soak 인수가 있으면 <c>SoakRunner</c>가 정상 교환·잘못된 메시지·UDP 검사를 반복하고, 없으면 <see cref="AgentCases"/>와 <see cref="UdpCases"/>를 한 번 실행합니다.</remarks>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                if (SoakRunner.TryRun(args, "SNMP", UdpCases.SoakNormal, AgentCases.Malformed, UdpCases.Run)) return 0;
                if (args.Length != 0)
                    throw new ArgumentException("No options are supported.");
                AgentCases.Run();
                UdpCases.Run();
                Console.WriteLine("PROTOCOL TESTS PASS: " + Cases.Checks + " assertions.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
        }
    }
}
