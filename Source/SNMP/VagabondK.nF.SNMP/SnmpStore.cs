using System;
using System.Text;

namespace VagabondK.nF.SNMP
{
    /// <summary>지원하는 SNMP SMI 값 형식의 BER 전송 태그입니다.</summary>
    public enum SnmpType : byte
    {
        /// <summary>부호 있는 32비트 INTEGER이며 태그는 0x02입니다.</summary>
        Integer32 = 0x02,

        /// <summary>OCTET STRING이며 태그는 0x04입니다.</summary>
        OctetString = 0x04,

        /// <summary>OBJECT IDENTIFIER이며 태그는 0x06입니다.</summary>
        ObjectIdentifier = 0x06,

        /// <summary>4바이트 IPv4 주소이며 애플리케이션 태그는 0x40입니다.</summary>
        IpAddress = 0x40,

        /// <summary>부호 없는 32비트 Counter이며 애플리케이션 태그는 0x41입니다.</summary>
        Counter32 = 0x41,

        /// <summary>부호 없는 32비트 Gauge이며 애플리케이션 태그는 0x42입니다.</summary>
        Gauge32 = 0x42,

        /// <summary>1/100초 단위의 부호 없는 32비트 TimeTicks이며 애플리케이션 태그는 0x43입니다.</summary>
        TimeTicks = 0x43,

        /// <summary>부호 없는 64비트 Counter이며 애플리케이션 태그는 0x46입니다. SNMPv1에서는 노출하지 않습니다.</summary>
        Counter64 = 0x46
    }

    /// <summary>숫자 OID의 파싱, 표시 및 지원 범위 검증을 제공합니다.</summary>
    public static class SnmpOid
    {
        /// <summary>지원하는 OID 구성 요소(sub-identifier)의 최대 개수이며 128개입니다.</summary>
        public const int MaximumLength = 128;

        /// <summary>점으로 구분된 숫자 문자열을 OID로 변환합니다.</summary>
        /// <param name="text">선행/후행 점이나 공백이 없는 숫자 OID입니다. 예: 1.3.6.1.4.1.99999.1.0.</param>
        /// <returns>새로 생성한 OID 구성 요소 배열입니다.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="text"/>가 <see langword="null"/>입니다.</exception>
        /// <exception cref="ArgumentException">형식이나 길이가 잘못되었거나 구성 요소가 32비트 범위 또는 첫 두 값의 규칙을 벗어납니다.</exception>
        /// <remarks>각 부분은 최대 10자리입니다. OID 범위는 Validate와 같습니다.</remarks>
        public static uint[] Parse(string text)
        {
            if (text == null)
                throw new ArgumentNullException("text");
            int count = 1;
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '.')
                    count++;
            if (count > MaximumLength)
                throw new ArgumentException("Too many OID sub-identifiers.");
            var oid = new uint[count];
            int index = 0, digits = 0;
            ulong value = 0;
            for (int i = 0; i <= text.Length; i++)
            {
                if (i == text.Length || text[i] == '.')
                {
                    if (digits == 0)
                        throw new ArgumentException("Empty OID sub-identifier.");
                    oid[index++] = (uint)value;
                    value = 0;
                    digits = 0;
                    continue;
                }
                char c = text[i];
                if (c < '0' || c > '9')
                    throw new ArgumentException("OID text must contain digits and dots only.");
                value = value * 10 + (uint)(c - '0');
                if (++digits > 10 || value > uint.MaxValue)
                    throw new ArgumentException("OID sub-identifier exceeds 32 bits.");
            }
            Validate(oid);
            return oid;
        }

        /// <summary>유효한 OID를 점으로 구분된 숫자 문자열로 변환합니다.</summary>
        /// <param name="oid">표시할 OID입니다. 배열을 수정하지 않습니다.</param>
        /// <returns>각 구성 요소를 점으로 연결한 문자열입니다.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="oid"/>가 <see langword="null"/>입니다.</exception>
        /// <exception cref="ArgumentException">OID가 Validate의 지원 범위를 벗어납니다.</exception>
        public static string Format(uint[] oid)
        {
            Validate(oid);
            var text = new StringBuilder();
            for (int i = 0; i < oid.Length; i++)
            {
                if (i != 0)
                    text.Append('.');
                text.Append(oid[i].ToString());
            }
            return text.ToString();
        }

        /// <summary>OID가 이 구현의 BER 인코딩 범위에 속하는지 검증합니다.</summary>
        /// <param name="oid">2~128개의 uint 구성 요소로 이루어진 OID입니다.</param>
        /// <exception cref="ArgumentNullException"><paramref name="oid"/>가 <see langword="null"/>입니다.</exception>
        /// <exception cref="ArgumentException">길이나 첫 두 구성 요소의 값이 유효하지 않습니다.</exception>
        /// <remarks>첫 구성 요소는 0~2입니다. 첫 값이 0 또는 1이면 둘째 값은 0~39, 첫 값이 2이면 둘째 값은 uint.MaxValue - 80 이하여야 합니다. 배열은 수정하지 않습니다.</remarks>
        public static void Validate(uint[] oid)
        {
            if (oid == null)
                throw new ArgumentNullException("oid");
            if (!IsValid(oid))
                throw new ArgumentException("An OID needs 2..128 sub-identifiers with a valid encodable first arc.");
        }

        internal static bool IsValid(uint[] oid)
        {
            if (oid.Length < 2 || oid.Length > MaximumLength || oid[0] > 2)
                return false;
            if (oid[0] < 2)
                return oid[1] < 40;
            return oid[1] <= uint.MaxValue - 80;
        }

        internal static uint[] Copy(uint[] oid)
        {
            Validate(oid);
            var copy = new uint[oid.Length];
            Array.Copy(oid, copy, oid.Length);
            return copy;
        }

        internal static int Compare(uint[] left, uint[] right)
        {
            int count = left.Length < right.Length ? left.Length : right.Length;
            for (int i = 0; i < count; i++)
                if (left[i] != right[i])
                    return left[i] < right[i] ? -1 : 1;
            return left.Length == right.Length ? 0 : left.Length < right.Length ? -1 : 1;
        }

        internal static bool StartsWith(uint[] oid, uint[] prefix, int prefixLength)
        {
            if (oid.Length < prefixLength)
                return false;
            for (int i = 0; i < prefixLength; i++)
                if (oid[i] != prefix[i])
                    return false;
            return true;
        }
    }

    /// <summary>변경 불가능한 SNMP 값입니다. 입력 배열을 복사해 보관하며, 공개 API에서 배열을 반환할 때도 복사본을 제공합니다.</summary>
    /// <remarks>저장된 형식과 맞지 않는 getter는 InvalidOperationException을 발생시킵니다.</remarks>
    public sealed class SnmpValue
    {
        /// <summary>OCTET STRING의 최대 크기이며 1024바이트입니다.</summary>
        public const int MaximumOctets = 1024;
        private readonly ulong _number;
        private readonly byte[] _bytes;
        private readonly uint[] _oid;

        private SnmpValue(SnmpType type, ulong number, byte[] bytes, uint[] oid)
        {
            Type = type;
            _number = number;
            _bytes = bytes;
            _oid = oid;
        }

        /// <summary>저장된 SMI 값 형식을 가져옵니다.</summary>
        public SnmpType Type { get; private set; }

        /// <summary>Integer32 값을 생성합니다.</summary>
        /// <param name="value">부호 있는 32비트 값입니다.</param>
        /// <returns>지정한 값을 담은 변경 불가능한 값입니다.</returns>
        public static SnmpValue Integer32(int value)
        {
            return new SnmpValue(SnmpType.Integer32, unchecked((ulong)(long)value), null, null);
        }

        /// <summary>Counter32 값을 생성합니다. 값을 증가시키거나 최댓값 초과 시 0으로 되돌리는 처리는 수행하지 않습니다.</summary>
        /// <param name="value">부호 없는 32비트 Counter 값입니다.</param>
        /// <returns>지정한 Counter32 값입니다.</returns>
        public static SnmpValue Counter32(uint value)
        {
            return new SnmpValue(SnmpType.Counter32, value, null, null);
        }

        /// <summary>Gauge32 값을 생성합니다.</summary>
        /// <param name="value">부호 없는 32비트 Gauge 값입니다.</param>
        /// <returns>지정한 Gauge32 값입니다.</returns>
        public static SnmpValue Gauge32(uint value)
        {
            return new SnmpValue(SnmpType.Gauge32, value, null, null);
        }

        /// <summary>TimeTicks 값을 생성합니다.</summary>
        /// <param name="value">1/100초 단위의 부호 없는 32비트 값입니다.</param>
        /// <returns>지정한 TimeTicks 값입니다.</returns>
        public static SnmpValue TimeTicks(uint value)
        {
            return new SnmpValue(SnmpType.TimeTicks, value, null, null);
        }

        /// <summary>Counter64 값을 생성합니다. 값을 증가시키거나 최댓값 초과 시 0으로 되돌리는 처리는 수행하지 않습니다.</summary>
        /// <param name="value">부호 없는 64비트 Counter 값입니다.</param>
        /// <returns>지정한 Counter64 값입니다.</returns>
        public static SnmpValue Counter64(ulong value)
        {
            return new SnmpValue(SnmpType.Counter64, value, null, null);
        }

        /// <summary>바이트 배열을 복사하여 OCTET STRING 값을 생성합니다.</summary>
        /// <param name="value">0~1024바이트의 데이터입니다.</param>
        /// <returns>입력 배열과 독립적인 OCTET STRING 값입니다.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="value"/>가 <see langword="null"/>입니다.</exception>
        /// <exception cref="ArgumentException">데이터가 1024바이트를 초과합니다.</exception>
        public static SnmpValue OctetString(byte[] value)
        {
            if (value == null)
                throw new ArgumentNullException("value");
            if (value.Length > MaximumOctets)
                throw new ArgumentException("OCTET STRING values are limited to 1024 bytes.");
            var copy = new byte[value.Length];
            Array.Copy(value, copy, value.Length);
            return new SnmpValue(SnmpType.OctetString, 0, copy, null);
        }

        /// <summary>문자열을 UTF-8 OCTET STRING으로 변환합니다.</summary>
        /// <param name="value">인코딩할 문자열입니다. 빈 문자열을 허용합니다.</param>
        /// <returns>UTF-8로 인코딩한 바이트를 담은 OCTET STRING 값입니다.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="value"/>가 <see langword="null"/>입니다.</exception>
        /// <exception cref="ArgumentException">UTF-8 인코딩 결과가 1024바이트를 초과합니다.</exception>
        public static SnmpValue Text(string value)
        {
            if (value == null)
                throw new ArgumentNullException("value");
            return OctetString(Encoding.UTF8.GetBytes(value));
        }

        /// <summary>IPv4 주소의 바이트 배열을 복사하여 IpAddress 값을 생성합니다.</summary>
        /// <param name="address">네트워크 바이트 순서로 저장된 길이가 정확히 4바이트인 IPv4 주소입니다.</param>
        /// <returns>입력 배열과 독립적인 IpAddress 값입니다.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="address"/>가 <see langword="null"/>입니다.</exception>
        /// <exception cref="ArgumentException">배열 길이가 4가 아닙니다.</exception>
        public static SnmpValue IpAddress(byte[] address)
        {
            if (address == null)
                throw new ArgumentNullException("address");
            if (address.Length != 4)
                throw new ArgumentException("IpAddress values are four IPv4 octets.");
            var copy = new byte[4];
            Array.Copy(address, copy, 4);
            return new SnmpValue(SnmpType.IpAddress, 0, copy, null);
        }

        /// <summary>OID를 검증하고 복사하여 OBJECT IDENTIFIER 값을 생성합니다.</summary>
        /// <param name="oid">SnmpOid.Validate의 범위에 속하는 OID입니다.</param>
        /// <returns>입력 배열과 독립적인 OBJECT IDENTIFIER 값입니다.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="oid"/>가 <see langword="null"/>입니다.</exception>
        /// <exception cref="ArgumentException">OID가 유효하지 않습니다.</exception>
        public static SnmpValue ObjectIdentifier(uint[] oid)
        {
            return new SnmpValue(SnmpType.ObjectIdentifier, 0, null, SnmpOid.Copy(oid));
        }

        internal static SnmpValue Wrap(SnmpType type, byte[] bytes)
        {
            return new SnmpValue(type, 0, bytes, null);
        }

        internal static SnmpValue Wrap(uint[] oid)
        {
            return new SnmpValue(SnmpType.ObjectIdentifier, 0, null, oid);
        }

        internal static SnmpValue Wrap(SnmpType type, ulong number)
        {
            return new SnmpValue(type, number, null, null);
        }

        internal ulong Raw
        {
            get
            {
                return _number;
            }
        }

        internal byte[] RawBytes
        {
            get
            {
                return _bytes;
            }
        }

        internal uint[] RawOid
        {
            get
            {
                return _oid;
            }
        }

        /// <summary>Integer32 값을 가져옵니다.</summary>
        /// <exception cref="InvalidOperationException">형식이 Integer32가 아닙니다.</exception>
        public int Integer
        {
            get
            {
                Expect(Type == SnmpType.Integer32);
                return (int)(long)_number;
            }
        }

        /// <summary>Counter32, Gauge32 또는 TimeTicks 값을 가져옵니다. TimeTicks의 단위는 1/100초입니다.</summary>
        /// <exception cref="InvalidOperationException">지원하는 부호 없는 32비트 형식이 아닙니다.</exception>
        public uint Unsigned
        {
            get
            {
                Expect(Type == SnmpType.Counter32 || Type == SnmpType.Gauge32 || Type == SnmpType.TimeTicks);
                return (uint)_number;
            }
        }

        /// <summary>Counter64 값을 가져옵니다.</summary>
        /// <exception cref="InvalidOperationException">형식이 Counter64가 아닙니다.</exception>
        public ulong Unsigned64
        {
            get
            {
                Expect(Type == SnmpType.Counter64);
                return _number;
            }
        }

        /// <summary>OCTET STRING 또는 IpAddress의 바이트 수를 배열 복사 없이 가져옵니다.</summary>
        /// <exception cref="InvalidOperationException">형식이 OctetString 또는 IpAddress가 아닙니다.</exception>
        public int ByteLength
        {
            get
            {
                Expect(_bytes != null);
                return _bytes.Length;
            }
        }

        /// <summary>OCTET STRING 또는 IpAddress의 바이트를 복사합니다.</summary>
        /// <returns>저장된 값과 독립적인 새 바이트 배열입니다.</returns>
        /// <exception cref="InvalidOperationException">형식이 OctetString 또는 IpAddress가 아닙니다.</exception>
        public byte[] GetBytes()
        {
            Expect(_bytes != null);
            var copy = new byte[_bytes.Length];
            Array.Copy(_bytes, copy, _bytes.Length);
            return copy;
        }

        /// <summary>OBJECT IDENTIFIER의 구성 요소 배열을 복사합니다.</summary>
        /// <returns>저장된 OID와 독립적인 새 배열입니다.</returns>
        /// <exception cref="InvalidOperationException">형식이 ObjectIdentifier가 아닙니다.</exception>
        public uint[] GetObjectIdentifier()
        {
            Expect(_oid != null);
            var copy = new uint[_oid.Length];
            Array.Copy(_oid, copy, _oid.Length);
            return copy;
        }

        private static void Expect(bool valid)
        {
            if (!valid)
                throw new InvalidOperationException("The SNMP value has a different type.");
        }
    }

    /// <summary>scalar의 .0이나 테이블 행의 특정 열에 해당하는 단일 말단 인스턴스의 변경 불가능한 정의입니다.</summary>
    public sealed class SnmpObject
    {
        private readonly uint[] _oid;

        /// <summary>OID를 복사하고 객체의 형식과 쓰기 제한을 구성합니다.</summary>
        /// <param name="oid">인스턴스까지 포함한 유효한 전체 OID입니다.</param>
        /// <param name="type">지원하는 SNMP 값 형식입니다.</param>
        /// <param name="writable">Set을 허용할지 여부입니다.</param>
        /// <param name="maximumLength">Set에서 허용할 OCTET STRING의 최대 바이트 수이며 0~1024입니다. 다른 형식에서는 무시합니다.</param>
        /// <exception cref="ArgumentNullException"><paramref name="oid"/>가 <see langword="null"/>입니다.</exception>
        /// <exception cref="ArgumentException">OID가 유효하지 않습니다.</exception>
        /// <exception cref="ArgumentOutOfRangeException">형식 또는 최대 길이가 허용 범위를 벗어납니다.</exception>
        public SnmpObject(uint[] oid, SnmpType type, bool writable = false, int maximumLength = 255)
        {
            _oid = SnmpOid.Copy(oid);
            if (type != SnmpType.Integer32 && type != SnmpType.OctetString && type != SnmpType.ObjectIdentifier &&
                type != SnmpType.IpAddress && type != SnmpType.Counter32 && type != SnmpType.Gauge32 &&
                type != SnmpType.TimeTicks && type != SnmpType.Counter64)
                throw new ArgumentOutOfRangeException("type");
            if (maximumLength < 0 || maximumLength > SnmpValue.MaximumOctets)
                throw new ArgumentOutOfRangeException("maximumLength");
            Type = type;
            Writable = writable;
            MaximumLength = maximumLength;
        }

        /// <summary>객체가 반환하고 Set에서 받을 SNMP 값 형식을 가져옵니다.</summary>
        public SnmpType Type { get; private set; }

        /// <summary>이 객체의 Set을 허용하는지 가져옵니다.</summary>
        public bool Writable { get; private set; }

        /// <summary>Set에서 허용할 OCTET STRING의 최대 바이트 수를 가져옵니다. 다른 형식에서는 무시합니다.</summary>
        public int MaximumLength { get; private set; }

        /// <summary>이 말단 인스턴스의 전체 OID를 복사합니다.</summary>
        /// <returns>내부 정의와 독립적인 새 OID 배열입니다.</returns>
        public uint[] GetOid()
        {
            return SnmpOid.Copy(_oid);
        }

        internal uint[] RawOid
        {
            get
            {
                return _oid;
            }
        }
    }

    /// <summary>SNMP 객체 정의, 값 읽기 및 Set 검증과 쓰기를 제공하는 저장소 계약입니다.</summary>
    /// <remarks>
    /// 사용 중 객체 수와 정의는 변경하지 않아야 합니다. Agent는 Set의 모든 변수 바인딩을 검증한 후
    /// QueueWrite를 바인딩 순서대로 호출하지만, 여러 호출의 원자적 반영이나 실패 시 변경 되돌리기를 제공하지 않습니다.
    /// </remarks>
    public interface ISnmpValueStore
    {
        /// <summary>저장소 객체 수를 가져옵니다. Agent는 0~512개를 허용합니다.</summary>
        int Count { get; }

        /// <summary>지정한 객체의 변경 불가능한 정의를 가져옵니다.</summary>
        /// <param name="index">0 이상 Count 미만인 저장소 인덱스입니다.</param>
        /// <returns><see langword="null"/>이 아닌 객체 정의입니다.</returns>
        SnmpObject GetObject(int index);

        /// <summary>지정한 객체의 현재 값을 읽습니다.</summary>
        /// <param name="index">0 이상 Count 미만인 저장소 인덱스입니다.</param>
        /// <returns>정의된 Type과 일치하는 null이 아닌 값입니다.</returns>
        /// <remarks>Agent는 여러 객체를 읽는 요청 전체가 같은 시점의 일관된 스냅샷임을 보장하지 않습니다.</remarks>
        SnmpValue Read(int index);

        /// <summary>상태를 변경하지 않고 객체가 Set 값을 수락할 수 있는지 검사합니다.</summary>
        /// <param name="index">검증할 저장소 객체의 인덱스입니다.</param>
        /// <param name="value">Agent가 형식 및 길이를 검증한 값입니다.</param>
        /// <returns>수락 가능하면 <see langword="true"/>, wrongValue로 거부하려면 <see langword="false"/>입니다.</returns>
        bool AcceptsWrite(int index, SnmpValue value);

        /// <summary>Set의 모든 변수 바인딩 검증이 성공한 후 쓰기 요청을 전달받습니다.</summary>
        /// <param name="index">쓰기 대상 저장소 객체의 인덱스입니다.</param>
        /// <param name="value">검증된 변경 불가능한 값입니다.</param>
        /// <remarks>반영 시점은 구현이 정합니다. 성공 응답은 호출 완료를 뜻하며 실제 장치 반영이나 전체 Set의 원자성을 보장하지 않습니다.</remarks>
        void QueueWrite(int index, SnmpValue value);
    }

    /// <summary>읽기 전용 MIB-II system group(1.3.6.1.2.1.1)의 설정입니다. sysUpTime은 Agent가 생성합니다.</summary>
    public sealed class SnmpSystem
    {
        /// <summary>system group의 변경 불가능한 설정을 생성합니다.</summary>
        /// <param name="description">sysDescr입니다. 0~255자의 출력 가능한 ASCII 문자열입니다.</param>
        /// <param name="name">sysName입니다. 0~255자의 출력 가능한 ASCII 문자열입니다.</param>
        /// <param name="contact">sysContact입니다. 0~255자의 출력 가능한 ASCII 문자열입니다.</param>
        /// <param name="location">sysLocation입니다. 0~255자의 출력 가능한 ASCII 문자열입니다.</param>
        /// <param name="objectId">복사할 sysObjectID입니다. null이면 0.0을 사용합니다.</param>
        /// <param name="services">sysServices의 비트 마스크이며 범위는 0~127입니다. 계층 L에 해당하는 비트 값은 2^(L-1)이며 기본값 72는 계층 4와 7을 나타냅니다.</param>
        /// <exception cref="ArgumentNullException">문자열 인수가 <see langword="null"/>입니다.</exception>
        /// <exception cref="ArgumentException">문자열이 ASCII 32~126 또는 길이 제한을 벗어나거나 OID가 유효하지 않습니다.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="services"/>가 0~127 범위를 벗어납니다.</exception>
        public SnmpSystem(string description, string name, string contact = "", string location = "",
            uint[] objectId = null, int services = 72)
        {
            Description = Display(description, "description");
            Name = Display(name, "name");
            Contact = Display(contact, "contact");
            Location = Display(location, "location");
            ObjectId = objectId == null ? new uint[] { 0, 0 } : SnmpOid.Copy(objectId);
            if (services < 0 || services > 127)
                throw new ArgumentOutOfRangeException("services");
            Services = services;
        }

        /// <summary>sysDescr로 제공할 설명을 가져옵니다.</summary>
        public string Description { get; private set; }

        /// <summary>sysName으로 제공할 이름을 가져옵니다.</summary>
        public string Name { get; private set; }

        /// <summary>sysContact로 제공할 연락처를 가져옵니다.</summary>
        public string Contact { get; private set; }

        /// <summary>sysLocation으로 제공할 위치를 가져옵니다.</summary>
        public string Location { get; private set; }

        internal uint[] ObjectId { get; private set; }

        /// <summary>sysServices로 제공할 OSI 계층 비트 마스크(0~127)를 가져옵니다.</summary>
        public int Services { get; private set; }

        private static string Display(string text, string parameter)
        {
            if (text == null)
                throw new ArgumentNullException(parameter);
            if (text.Length > 255)
                throw new ArgumentException("DisplayString values are limited to 255 characters.");
            for (int i = 0; i < text.Length; i++)
                if (text[i] < 32 || text[i] > 126)
                    throw new ArgumentException("Use printable ASCII system text.");
            return text;
        }
    }
}
