using System;
using System.Text;

namespace VagabondK.nF.SNMP
{
    /// <summary>UDP 페이로드 단위로 SNMPv1/v2c 커뮤니티 요청을 처리하는 Agent입니다.</summary>
    /// <remarks>
    /// Get, GetNext, Set 및 v2c GetBulk와 Trap 생성을 지원합니다. SNMPv3, Inform, v1 Trap,
    /// proxy, 행 생성 및 community별 view는 지원하지 않습니다. Community는 암호화나 강한 인증을 제공하지 않습니다.
    /// 동일 인스턴스의 Process/CreateTrap 호출은 호출자가 직렬화해야 합니다.
    /// BER은 단일 바이트 태그와 최대 65535바이트의 확정 길이 형식(definite length)만 사용하며, 메시지 크기 제한은 별도로 적용됩니다.
    /// </remarks>
    public sealed class SnmpAgent
    {
        /// <summary>UDP/IP 헤더를 제외한 SNMP 메시지의 최대 길이이며 1472바이트입니다.</summary>
        public const int MaximumMessage = 1472;

        /// <summary>내장 system 그룹 외에 저장소가 제공할 수 있는 최대 객체 수이며 512개입니다.</summary>
        public const int MaximumObjects = 512;
        private const int MaximumResults = 256;
        private const int NoSuchObject = -1, NoSuchInstance = -2, EndOfMibView = -3;
        private static readonly uint[] SystemPrefix = { 1, 3, 6, 1, 2, 1, 1 };
        private static readonly uint[] UpTimeName = { 1, 3, 6, 1, 2, 1, 1, 3, 0 };
        private static readonly uint[] TrapOidName = { 1, 3, 6, 1, 6, 3, 1, 1, 4, 1, 0 };

        private readonly ISnmpValueStore _store;
        private readonly byte[] _read, _write;
        private readonly uint[][] _oids;
        private readonly int[] _sources;
        private readonly SnmpType[] _types;
        private readonly bool[] _writable;
        private readonly int[] _maximumLengths;
        private readonly int[] _entryOfStore;
        private readonly SnmpValue[] _system;
        private readonly long _start;
        private int _trapId;

        private sealed class Request
        {
            internal byte[] Bytes;
            internal int Version, CommunityStart, CommunityLength, RequestId, ListStart, ListLength;
        }

        /// <summary>저장소 정의와 읽기 전용 system group을 사용하여 Agent를 구성합니다.</summary>
        /// <param name="store">0~512개 객체를 제공하며 Agent 사용 동안 유지할 저장소입니다. 소유권은 이전되지 않습니다.</param>
        /// <param name="system">내장 MIB-II system group의 설정입니다.</param>
        /// <param name="readCommunity">읽기 community입니다. 공백 없는 ASCII 33~126의 1~32자 문자열입니다.</param>
        /// <param name="writeCommunity">동일한 문자 제한의 읽기/쓰기 community입니다. null이면 Set을 비활성화합니다.</param>
        /// <exception cref="ArgumentNullException">store, system 또는 readCommunity가 <see langword="null"/>입니다.</exception>
        /// <exception cref="ArgumentException">community, 객체 수 또는 정의가 유효하지 않거나 OID가 중복/접두 관계이거나 예약된 system group과 겹칩니다.</exception>
        /// <remarks>두 community가 같으면 읽기/쓰기를 허용합니다. 객체 정의는 생성 시 읽으며 이후 변경하지 않아야 합니다.</remarks>
        public SnmpAgent(ISnmpValueStore store, SnmpSystem system, string readCommunity, string writeCommunity = null)
        {
            if (store == null)
                throw new ArgumentNullException("store");
            if (system == null)
                throw new ArgumentNullException("system");
            _store = store;
            _read = Community(readCommunity, "readCommunity");
            _write = writeCommunity == null ? null : Community(writeCommunity, "writeCommunity");
            int count = store.Count;
            if (count < 0 || count > MaximumObjects)
                throw new ArgumentException("A store may expose 0..512 objects.");
            int total = count + 7;
            _oids = new uint[total][];
            _sources = new int[total];
            _types = new SnmpType[total];
            _writable = new bool[total];
            _maximumLengths = new int[total];
            _entryOfStore = new int[count];
            _system = new SnmpValue[] {
                SnmpValue.Text(system.Description), SnmpValue.Wrap(system.ObjectId), null,
                SnmpValue.Text(system.Contact), SnmpValue.Text(system.Name), SnmpValue.Text(system.Location),
                SnmpValue.Integer32(system.Services)
            };
            for (int i = 0; i < 7; i++)
            {
                _oids[i] = new uint[] { 1, 3, 6, 1, 2, 1, 1, (uint)i + 1, 0 };
                _sources[i] = -1 - i;
                _types[i] = i == 2 ? SnmpType.TimeTicks : _system[i].Type;
            }
            for (int i = 0; i < count; i++)
            {
                SnmpObject point = store.GetObject(i);
                if (point == null)
                    throw new ArgumentException("Null SNMP object.");
                if (SnmpOid.StartsWith(point.RawOid, SystemPrefix, SystemPrefix.Length) ||
                    SnmpOid.StartsWith(SystemPrefix, point.RawOid, point.RawOid.Length))
                    throw new ArgumentException("The MIB-II system group is reserved by the agent.");
                int e = 7 + i;
                _oids[e] = point.RawOid;
                _sources[e] = i;
                _types[e] = point.Type;
                _writable[e] = point.Writable;
                _maximumLengths[e] = point.MaximumLength;
            }
            for (int i = 1; i < total; i++)
            {
                uint[] oid = _oids[i];
                int source = _sources[i];
                SnmpType type = _types[i];
                bool writable = _writable[i];
                int maximum = _maximumLengths[i];
                int j = i - 1;
                while (j >= 0 && SnmpOid.Compare(_oids[j], oid) > 0)
                {
                    _oids[j + 1] = _oids[j];
                    _sources[j + 1] = _sources[j];
                    _types[j + 1] = _types[j];
                    _writable[j + 1] = _writable[j];
                    _maximumLengths[j + 1] = _maximumLengths[j];
                    j--;
                }
                _oids[j + 1] = oid;
                _sources[j + 1] = source;
                _types[j + 1] = type;
                _writable[j + 1] = writable;
                _maximumLengths[j + 1] = maximum;
            }
            for (int i = 0; i < total; i++)
            {
                if (i > 0 && SnmpOid.StartsWith(_oids[i], _oids[i - 1], _oids[i - 1].Length))
                    throw new ArgumentException("SNMP object OIDs must be unique leaves (no OID may prefix another).");
                if (_sources[i] >= 0)
                    _entryOfStore[_sources[i]] = i;
            }
            _start = Environment.TickCount64;
        }

        private static byte[] Community(string text, string parameter)
        {
            if (text == null)
                throw new ArgumentNullException(parameter);
            if (text.Length < 1 || text.Length > 32)
                throw new ArgumentException("Community length must be 1..32.");
            for (int i = 0; i < text.Length; i++)
                if (text[i] < 33 || text[i] > 126)
                    throw new ArgumentException("Use printable ASCII communities without spaces.");
            return Encoding.UTF8.GetBytes(text);
        }

        // 요청 값의 SMI 형식을 검사합니다. 다른 형식은 ASN.1 해석 오류로 처리하여 메시지를 폐기합니다.
        private static bool ValidValue(byte[] b, int p, int stop)
        {
            int tag = b[p++];
            int length = Ber.Length(b, ref p, stop);
            switch (tag)
            {
                case 0x02:
                    return length >= 1 && length <= 4;
                case 0x04:
                case 0x44:
                    return true;
                case 0x05:
                case 0x80:
                case 0x81:
                case 0x82:
                    return length == 0;
                case 0x06:
                    return Ber.Oid(b, p, length) != null;
                case 0x40:
                    return length == 4;
                case 0x41:
                case 0x42:
                case 0x43:
                    return length >= 1 && length <= 5;
                case 0x46:
                    return length >= 1 && length <= 9;
                default:
                    return false;
            }
        }

        private static bool Same(byte[] bytes, int start, int length, byte[] expected)
        {
            if (expected == null || length != expected.Length)
                return false;
            int difference = 0;
            for (int i = 0; i < length; i++)
                difference |= bytes[start + i] ^ expected[i];
            return difference == 0;
        }

        /// <summary>Agent 생성 이후 경과 시간을 1/100초 단위로 가져옵니다. 부호 없는 32비트 범위를 초과하면 0부터 다시 증가합니다.</summary>
        public uint UpTime
        {
            get
            {
                return (uint)(((ulong)(Environment.TickCount64 - _start) / 10UL) & 0xffffffffUL);
            }
        }

        /// <summary>하나의 완전한 SNMP 메시지를 처리하여 응답 페이로드를 생성합니다.</summary>
        /// <param name="request">2~1472바이트의 BER 메시지입니다. 배열을 수정하지 않습니다.</param>
        /// <returns>새 응답 배열입니다. 알 수 없는 버전·커뮤니티이거나 응답·알림 PDU여서 메시지를 폐기하면 <see langword="null"/>입니다.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="request"/>가 <see langword="null"/>입니다.</exception>
        /// <exception cref="SnmpProtocolException">메시지 길이, BER 또는 요청 PDU 형식이 잘못되었거나 지원하지 않습니다.</exception>
        /// <exception cref="InvalidOperationException">저장소가 null 또는 정의와 다른 형식의 값을 반환했습니다.</exception>
        /// <remarks>
        /// 호출을 직렬화해야 합니다. 프로토콜 예외를 받은 호출자는 해당 메시지를 폐기하고 다음 요청을 처리할 수 있습니다.
        /// Set은 모든 변수 바인딩을 검증한 후 QueueWrite를 순서대로 호출하며, 원자적 반영이나 변경 되돌리기를 제공하지 않습니다.
        /// GetBulk는 반복 결과 수를 제한하고 메시지 크기에 맞춰 결과를 줄일 수 있습니다.
        /// </remarks>
        public byte[] Process(byte[] request)
        {
            if (request == null)
                throw new ArgumentNullException("request");
            int end = request.Length, p = 0;
            Ber.Require(end >= 2 && end <= MaximumMessage);
            Ber.Require(Ber.Header(request, ref p, end, Ber.Sequence) == end - p);
            var message = new Request();
            message.Bytes = request;
            message.Version = Ber.Integer(request, ref p, end);
            if (message.Version != 0 && message.Version != 1)
                return null;
            message.CommunityLength = Ber.Header(request, ref p, end, 0x04);
            message.CommunityStart = p;
            p += message.CommunityLength;
            bool canWrite = Same(request, message.CommunityStart, message.CommunityLength, _write);
            if (!canWrite && !Same(request, message.CommunityStart, message.CommunityLength, _read))
                return null;
            Ber.Require(p < end);
            int pdu = request[p];
            if (pdu == 0xa2 || pdu == 0xa4 || pdu == 0xa6 || pdu == 0xa7 || pdu == 0xa8)
                return null;
            Ber.Require(pdu == 0xa0 || pdu == 0xa1 || pdu == 0xa3 || (pdu == 0xa5 && message.Version == 1));
            p++;
            Ber.Require(Ber.Length(request, ref p, end) == end - p);
            message.RequestId = Ber.Integer(request, ref p, end);
            int first = Ber.Integer(request, ref p, end), second = Ber.Integer(request, ref p, end);
            message.ListLength = Ber.Header(request, ref p, end, Ber.Sequence);
            Ber.Require(message.ListLength == end - p);
            message.ListStart = p;
            int count = 0;
            for (int q = p; q < end; count++)
            {
                int length = Ber.Header(request, ref q, end, Ber.Sequence);
                q += length;
            }
            var names = new uint[count][];
            var valueAt = new int[count];
            for (int i = 0; i < count; i++)
            {
                int stop = Ber.Header(request, ref p, end, Ber.Sequence) + p;
                names[i] = Ber.ReadOid(request, ref p, stop);
                valueAt[i] = p;
                Ber.Require(p < stop && ValidValue(request, p, stop));
                Ber.Skip(request, ref p, stop);
                Ber.Require(p == stop);
            }
            if (pdu == 0xa0)
                return Get(message, names);
            if (pdu == 0xa1)
                return GetNext(message, names);
            if (pdu == 0xa5)
                return GetBulk(message, names, first, second);
            return Set(message, names, valueAt, canWrite);
        }

        private byte[] Get(Request message, uint[][] names)
        {
            int count = names.Length;
            var refs = new int[count];
            var values = new SnmpValue[count];
            for (int i = 0; i < count; i++)
            {
                int e = Find(names[i]);
                if (e < 0 || (message.Version == 0 && _types[e] == SnmpType.Counter64))
                {
                    if (message.Version == 0)
                        return Echo(message, SnmpErrorStatus.NoSuchName, i + 1);
                    refs[i] = Missing(names[i]);
                }
                else
                {
                    refs[i] = e;
                    values[i] = Value(e);
                }
            }
            return Respond(message, names, refs, values, count, false);
        }

        private byte[] GetNext(Request message, uint[][] names)
        {
            int count = names.Length;
            var refs = new int[count];
            var values = new SnmpValue[count];
            var results = new uint[count][];
            for (int i = 0; i < count; i++)
            {
                int e = Next(names[i], message.Version);
                if (e < 0)
                {
                    if (message.Version == 0)
                        return Echo(message, SnmpErrorStatus.NoSuchName, i + 1);
                    results[i] = names[i];
                    refs[i] = EndOfMibView;
                }
                else
                {
                    results[i] = _oids[e];
                    refs[i] = e;
                    values[i] = Value(e);
                }
            }
            return Respond(message, results, refs, values, count, false);
        }

        private byte[] GetBulk(Request message, uint[][] names, int nonRepeaters, int maxRepetitions)
        {
            int count = names.Length;
            int fixedCount = nonRepeaters < 0 ? 0 : nonRepeaters > count ? count : nonRepeaters;
            int repeaters = count - fixedCount;
            int rows = maxRepetitions < 0 || repeaters == 0 ? 0 : maxRepetitions;
            if (repeaters != 0 && rows > (MaximumResults - fixedCount) / repeaters)
                rows = (MaximumResults - fixedCount) / repeaters;
            int total = fixedCount + rows * repeaters, produced = 0;
            var results = new uint[total][];
            var refs = new int[total];
            var values = new SnmpValue[total];
            for (int i = 0; i < fixedCount; i++, produced++)
                Bulk(names[i], results, refs, values, produced);
            var cursor = new uint[repeaters][];
            Array.Copy(names, fixedCount, cursor, 0, repeaters);
            for (int row = 0; row < rows; row++)
            {
                bool ended = true;
                for (int j = 0; j < repeaters; j++, produced++)
                {
                    Bulk(cursor[j], results, refs, values, produced);
                    cursor[j] = results[produced];
                    if (refs[produced] != EndOfMibView)
                        ended = false;
                }
                if (ended)
                    break;
            }
            return Respond(message, results, refs, values, produced, true);
        }

        private void Bulk(uint[] name, uint[][] results, int[] refs, SnmpValue[] values, int index)
        {
            int e = Next(name, 1);
            if (e < 0)
            {
                results[index] = name;
                refs[index] = EndOfMibView;
            }
            else
            {
                results[index] = _oids[e];
                refs[index] = e;
                values[index] = Value(e);
            }
        }

        private byte[] Set(Request message, uint[][] names, int[] valueAt, bool canWrite)
        {
            int count = names.Length;
            bool v1 = message.Version == 0;
            if (!canWrite)
                return Echo(message, v1 ? SnmpErrorStatus.NoSuchName : SnmpErrorStatus.NoAccess, count == 0 ? 0 : 1);
            var entries = new int[count];
            var values = new SnmpValue[count];
            for (int i = 0; i < count; i++)
            {
                int e = Find(names[i]);
                if (e < 0 || (v1 && _types[e] == SnmpType.Counter64))
                    return Echo(message, v1 ? SnmpErrorStatus.NoSuchName : SnmpErrorStatus.NoCreation, i + 1);
                if (_sources[e] < 0 || !_writable[e])
                    return Echo(message, v1 ? SnmpErrorStatus.NoSuchName : SnmpErrorStatus.NotWritable, i + 1);
                SnmpErrorStatus status = Decode(message.Bytes, valueAt[i], e, out values[i]);
                if (status == SnmpErrorStatus.NoError && !_store.AcceptsWrite(_sources[e], values[i]))
                    status = SnmpErrorStatus.WrongValue;
                if (status != SnmpErrorStatus.NoError)
                    return Echo(message, v1 ? SnmpErrorStatus.BadValue : status, i + 1);
                entries[i] = e;
            }
            for (int i = 0; i < count; i++)
                _store.QueueWrite(_sources[entries[i]], values[i]);
            return Echo(message, SnmpErrorStatus.NoError, 0);
        }

        // Set 실행 전 ValidValue에서 정수·부호 없는 값의 길이와 OID 구문 등 SMI 형식을 이미 검증했습니다.
        private SnmpErrorStatus Decode(byte[] b, int p, int e, out SnmpValue value)
        {
            value = null;
            int tag = b[p++];
            int length = Ber.Length(b, ref p, b.Length);
            SnmpType type = _types[e];
            if (tag != (int)type)
                return SnmpErrorStatus.WrongType;
            ulong number;
            if (type != SnmpType.OctetString && type != SnmpType.IpAddress && type != SnmpType.ObjectIdentifier &&
                length > 1 && ((b[p] == 0 && b[p + 1] < 0x80) || (type == SnmpType.Integer32 && b[p] == 0xff && b[p + 1] >= 0x80)))
                return SnmpErrorStatus.WrongEncoding;
            switch (type)
            {
                case SnmpType.Integer32:
                    int signed = (sbyte)b[p];
                    for (int i = 1; i < length; i++)
                        signed = (signed << 8) | b[p + i];
                    value = SnmpValue.Integer32(signed);
                    return SnmpErrorStatus.NoError;
                case SnmpType.OctetString:
                case SnmpType.IpAddress:
                    if (type == SnmpType.IpAddress ? length != 4 : length > _maximumLengths[e])
                        return SnmpErrorStatus.WrongLength;
                    var bytes = new byte[length];
                    Array.Copy(b, p, bytes, 0, length);
                    value = SnmpValue.Wrap(type, bytes);
                    return SnmpErrorStatus.NoError;
                case SnmpType.ObjectIdentifier:
                    value = SnmpValue.Wrap(Ber.Oid(b, p, length));
                    return SnmpErrorStatus.NoError;
                default:
                    if (!Ber.Unsigned(b, p, length, type == SnmpType.Counter64 ? 8 : 4, out number))
                        return SnmpErrorStatus.WrongValue;
                    value = SnmpValue.Wrap(type, number);
                    return SnmpErrorStatus.NoError;
            }
        }

        /// <summary>sysUpTime.0, snmpTrapOID.0 및 선택한 저장소 객체를 포함하는 SNMPv2c Trap을 생성합니다.</summary>
        /// <param name="community">공백 없는 ASCII 33~126의 1~32자 Trap community입니다. 설정된 읽기/쓰기 community와 달라도 됩니다.</param>
        /// <param name="trapOid">snmpTrapOID.0의 값으로 사용할 유효한 OID입니다. 내부에서 복사합니다.</param>
        /// <param name="objectIndexes">추가할 저장소 객체의 인덱스 목록(최대 256개)입니다. null이면 추가 객체가 없습니다. 목록 순서대로 현재 값을 읽습니다.</param>
        /// <returns>1472바이트 이하의 새 Trap BER 메시지입니다. 이 메서드는 전송하지 않습니다.</returns>
        /// <exception cref="ArgumentNullException">community 또는 trapOid가 <see langword="null"/>입니다.</exception>
        /// <exception cref="ArgumentException">community/OID가 유효하지 않거나 객체 수 또는 인코딩된 메시지가 제한을 초과합니다.</exception>
        /// <exception cref="ArgumentOutOfRangeException">객체 인덱스가 저장소 범위를 벗어납니다.</exception>
        /// <exception cref="InvalidOperationException">저장소가 null 또는 정의와 다른 형식의 값을 반환했습니다.</exception>
        /// <remarks>Process 및 다른 CreateTrap 호출과 직렬화해야 합니다. 여러 객체의 값을 같은 시점에 읽은 스냅샷은 보장하지 않습니다.</remarks>
        public byte[] CreateTrap(string community, uint[] trapOid, int[] objectIndexes)
        {
            byte[] name = Community(community, "community");
            uint[] trap = SnmpOid.Copy(trapOid);
            int extra = objectIndexes == null ? 0 : objectIndexes.Length;
            if (extra > MaximumResults)
                throw new ArgumentException("Too many trap objects.");
            int count = 2 + extra;
            var names = new uint[count][];
            var refs = new int[count];
            var values = new SnmpValue[count];
            names[0] = UpTimeName;
            values[0] = SnmpValue.TimeTicks(UpTime);
            names[1] = TrapOidName;
            values[1] = SnmpValue.Wrap(trap);
            for (int i = 0; i < extra; i++)
            {
                int index = objectIndexes[i];
                if (index < 0 || index >= _entryOfStore.Length)
                    throw new ArgumentOutOfRangeException("objectIndexes");
                int e = _entryOfStore[index];
                names[i + 2] = _oids[e];
                values[i + 2] = Value(e);
            }
            var message = new Request();
            message.Bytes = name;
            message.Version = 1;
            message.CommunityStart = 0;
            message.CommunityLength = name.Length;
            message.RequestId = ++_trapId & 0x7fffffff;
            byte[] result = Encode(message, 0xa7, SnmpErrorStatus.NoError, 0, names, refs, values, count, false);
            if (result == null)
                throw new ArgumentException("The trap exceeds the maximum SNMP message size.");
            return result;
        }

        private int Find(uint[] name)
        {
            int low = 0, high = _oids.Length - 1;
            while (low <= high)
            {
                int middle = (low + high) >> 1;
                int order = SnmpOid.Compare(_oids[middle], name);
                if (order == 0)
                    return middle;
                if (order < 0)
                    low = middle + 1;
                else
                    high = middle - 1;
            }
            return -1;
        }

        private int Next(uint[] name, int version)
        {
            int low = 0, high = _oids.Length;
            while (low < high)
            {
                int middle = (low + high) >> 1;
                if (SnmpOid.Compare(_oids[middle], name) <= 0)
                    low = middle + 1;
                else
                    high = middle;
            }
            while (version == 0 && low < _oids.Length && _types[low] == SnmpType.Counter64)
                low++;
            return low < _oids.Length ? low : -1;
        }

        // 요청 OID가 알려진 객체 형식(말단 OID에서 마지막 구성 요소를 뺀 부분)에 속하면 noSuchInstance를 반환합니다.
        private int Missing(uint[] name)
        {
            for (int e = 0; e < _oids.Length; e++)
                if (SnmpOid.StartsWith(name, _oids[e], _oids[e].Length - 1))
                    return NoSuchInstance;
            return NoSuchObject;
        }

        private SnmpValue Value(int e)
        {
            int source = _sources[e];
            if (source < 0)
                return source == -3 ? SnmpValue.TimeTicks(UpTime) : _system[-1 - source];
            SnmpValue value = _store.Read(source);
            if (value == null || value.Type != _types[e])
                throw new InvalidOperationException("The store returned a value that differs from its SNMP object type.");
            return value;
        }

        private byte[] Respond(Request message, uint[][] names, int[] refs, SnmpValue[] values, int count, bool truncate)
        {
            byte[] result = Encode(message, 0xa2, SnmpErrorStatus.NoError, 0, names, refs, values, count, truncate);
            if (result != null)
                return result;
            if (message.Version == 0)
                return Echo(message, SnmpErrorStatus.TooBig, 0);
            return Encode(message, 0xa2, SnmpErrorStatus.TooBig, 0, null, null, null, 0, false);
        }

        // 요청의 변수 바인딩을 변경 없이 복사한 응답입니다.
        private byte[] Echo(Request message, SnmpErrorStatus status, int index)
        {
            byte[] result = Encode(message, 0xa2, status, index, null, null, null, -1, false);
            return result != null ? result : Encode(message, 0xa2, SnmpErrorStatus.TooBig, 0, null, null, null, 0, false);
        }

        private static int Binding(uint[] name, int reference, SnmpValue value)
        {
            int content = Ber.Tlv(Ber.OidLength(name)) + (reference < 0 ? 2 : Ber.Tlv(Ber.ValueLength(value)));
            return Ber.Tlv(content);
        }

        private static int Length(Request message, SnmpErrorStatus status, int index, int bindings)
        {
            int pdu = Ber.Tlv(Ber.SignedLength(message.RequestId)) + Ber.Tlv(Ber.SignedLength((int)status)) +
                Ber.Tlv(Ber.SignedLength(index)) + Ber.Tlv(bindings);
            return 3 + Ber.Tlv(message.CommunityLength) + Ber.Tlv(pdu);
        }

        // count가 -1이면 원본 요청 목록을 복사합니다. 메시지가 MaximumMessage를 초과하면 null을 반환합니다.
        private static byte[] Encode(Request message, int tag, SnmpErrorStatus status, int index, uint[][] names, int[] refs,
            SnmpValue[] values, int count, bool truncate)
        {
            int bindings = 0, included = 0;
            if (count < 0)
                bindings = message.ListLength;
            else
                for (; included < count; included++)
                {
                    int size = Binding(names[included], refs[included], values[included]);
                    if (Ber.Tlv(Length(message, status, index, bindings + size)) > MaximumMessage)
                    {
                        if (truncate && included > 0)
                            break;
                        return null;
                    }
                    bindings += size;
                }
            int body = Length(message, status, index, bindings);
            int total = Ber.Tlv(body);
            if (total > MaximumMessage)
                return null;
            var b = new byte[total];
            int p = 0;
            Ber.Put(b, ref p, Ber.Sequence, body);
            Ber.PutSigned(b, ref p, message.Version);
            Ber.Put(b, ref p, 0x04, message.CommunityLength);
            Array.Copy(message.Bytes, message.CommunityStart, b, p, message.CommunityLength);
            p += message.CommunityLength;
            Ber.Put(b, ref p, tag, Ber.Tlv(Ber.SignedLength(message.RequestId)) + Ber.Tlv(Ber.SignedLength((int)status)) +
                Ber.Tlv(Ber.SignedLength(index)) + Ber.Tlv(bindings));
            Ber.PutSigned(b, ref p, message.RequestId);
            Ber.PutSigned(b, ref p, (int)status);
            Ber.PutSigned(b, ref p, index);
            Ber.Put(b, ref p, Ber.Sequence, bindings);
            if (count < 0)
            {
                Array.Copy(message.Bytes, message.ListStart, b, p, message.ListLength);
                p += message.ListLength;
            }
            else
                for (int i = 0; i < included; i++)
                {
                    int reference = refs[i];
                    SnmpValue value = values[i];
                    Ber.Put(b, ref p, Ber.Sequence, Ber.Tlv(Ber.OidLength(names[i])) +
                        (reference < 0 ? 2 : Ber.Tlv(Ber.ValueLength(value))));
                    Ber.PutOid(b, ref p, names[i]);
                    if (reference < 0)
                    {
                        b[p++] = (byte)(0x7f - reference);
                        b[p++] = 0;
                    }
                    else
                        Ber.PutValue(b, ref p, value);
                }
            if (p != total)
                throw new InvalidOperationException("SNMP encoder length mismatch.");
            return b;
        }
    }
}
