namespace VagabondK.nF.SNMP.UDPHost
{
    /// <summary>평가용 고정 MIB를 메모리에 보관하는 <see cref="ISnmpValueStore"/> 예제 구현입니다.</summary>
    /// <remarks>
    /// 문서용 enterprise 1.3.6.1.4.1.32473(RFC 5612) 아래의 OID를 쓰므로 제품에서는 자체 OID로 바꾸십시오.
    /// diagnostics가 참이면 거부된 데이터그램 수(.9.0)를 추가로 노출합니다. Agent/세션 호출자는 호출을 직렬화해야 합니다.
    /// </remarks>
    public sealed class DemoStore : ISnmpValueStore
    {
        public const string Enterprise = "1.3.6.1.4.1.32473.1";
        public const int Setpoint = 0, Label = 1, AcceptedWrites = 2, Level = 3, Delay = 4, Total = 5, Peer = 6, Profile = 7;
        private readonly SnmpObject[] _objects;
        private readonly SnmpValue[] _values;
        private uint _accepted;
        private readonly bool _diagnostics;
        public uint RejectedDatagrams { get; set; }

        public DemoStore(bool diagnostics = false)
        {
            _diagnostics = diagnostics;
            _objects = new SnmpObject[] {
                new SnmpObject(Name(1), SnmpType.Integer32, true),
                new SnmpObject(Name(2), SnmpType.OctetString, true, 32),
                new SnmpObject(Name(3), SnmpType.Counter32),
                new SnmpObject(Name(4), SnmpType.Gauge32, true),
                new SnmpObject(Name(5), SnmpType.TimeTicks, true),
                new SnmpObject(Name(6), SnmpType.Counter64, true),
                new SnmpObject(Name(7), SnmpType.IpAddress, true),
                new SnmpObject(Name(8), SnmpType.ObjectIdentifier, true),
                new SnmpObject(Name(9), SnmpType.Counter32)
            };
            _values = new SnmpValue[] {
                SnmpValue.Integer32(250), SnmpValue.Text("pump-1"), null, SnmpValue.Gauge32(50),
                SnmpValue.TimeTicks(1500), SnmpValue.Counter64(0x100000000UL), SnmpValue.IpAddress(new byte[] { 192, 0, 2, 10 }),
                SnmpValue.ObjectIdentifier(SnmpOid.Parse("1.3.6.1.4.1.32473.2.1"))
            };
        }

        private static uint[] Name(int item)
        {
            return SnmpOid.Parse(Enterprise + "." + item + ".0");
        }

        public int Count
        {
            get
            {
                return _diagnostics ? _objects.Length : _objects.Length - 1;
            }
        }

        public SnmpObject GetObject(int index)
        {
            return _objects[index];
        }

        public SnmpValue Read(int index)
        {
            return index == 8 ? SnmpValue.Counter32(RejectedDatagrams) :
                index == AcceptedWrites ? SnmpValue.Counter32(_accepted) : _values[index];
        }

        // 업무 범위 검사는 저장소에서 합니다. 형식·길이·인코딩은 Agent가 이미 검사했습니다.
        public bool AcceptsWrite(int index, SnmpValue value)
        {
            if (index == Setpoint)
                return value.Integer >= -100000 && value.Integer <= 100000;
            if (index == Level)
                return value.Unsigned <= 100;
            return true;
        }

        public void QueueWrite(int index, SnmpValue value)
        {
            _values[index] = value;
            _accepted++;
        }
    }
}
