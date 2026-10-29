using VagabondK.nF.SNMP.Mapping;

namespace VagabondK.nF.SNMP.Mapping.Sample
{
    /// <summary>SNMP로 노출할 장치 모델입니다. 생성기가 중첩 <c>SnmpStore</c> 클래스를 추가합니다.</summary>
    /// <remarks>OID는 문서용 enterprise 1.3.6.1.4.1.32473(RFC 5612) 아래에 있으므로 실제 제품에서는 자체 OID로 바꾸십시오.
    /// 지원하는 SMI 형식별 CLR 형식(int, uint, ulong, byte[], uint[])을 한 번씩 보여 줍니다.</remarks>
    [SnmpMap]
    public partial class DeviceData
    {
        public const string Enterprise = "1.3.6.1.4.1.32473.10";
        // 일부러 OID 순서와 다르게 선언했습니다. 생성기가 숫자 OID 순서로 정렬합니다.
        [SnmpCounter64(Enterprise + ".6.0", Writable = true)]
        public ulong Total { get; private set; } = 0x100000000UL;

        [SnmpInteger(Enterprise + ".1.0", Writable = true)]
        public int Setpoint { get; private set; } = -250;

        [SnmpOctetString(Enterprise + ".2.0", Writable = true, MaximumLength = 32)]
        public byte[] Label { get; private set; } = new byte[] { 112, 117, 109, 112 };

        [SnmpCounter32(Enterprise + ".3.0")]
        public uint BootCount { get { return 1; } }

        [SnmpGauge32(Enterprise + ".4.0", Writable = true)]
        public uint Level { get; private set; } = 50;

        [SnmpTimeTicks(Enterprise + ".5.0", Writable = true)]
        public uint Delay { get; private set; } = 1500;

        [SnmpIpAddress(Enterprise + ".7.0", Writable = true)]
        public byte[] Peer { get; private set; } = new byte[] { 192, 0, 2, 10 };

        [SnmpObjectIdentifier(Enterprise + ".8.0", Writable = true)]
        public uint[] Profile { get; private set; } = new uint[] { 1, 3, 6, 1, 4, 1, 32473, 10 };
    }
}
