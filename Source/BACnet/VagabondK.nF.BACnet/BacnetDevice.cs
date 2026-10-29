using System;
using System.Text;

namespace VagabondK.nF.BACnet
{
    /// <summary>잘못되었거나 지원하지 않는 BACnet/IP 프레임을 나타냅니다.</summary>
    public sealed class BacnetProtocolException : Exception
    {
        /// <summary>기본 BACnet/IP 프레임 오류 메시지로 예외를 생성합니다.</summary>
        public BacnetProtocolException() : base("Malformed or unsupported BACnet/IP frame.") { }
    }

    /// <summary>단일 LAN에서 분할되지 않은 BACnet/IP 요청을 처리하는 평가용 장치입니다.</summary>
    /// <remarks>
    /// Who-Is, ReadProperty, WriteProperty의 제한된 부분집합을 지원합니다.
    /// 라우팅, segmentation, priority array 및 commandability는 지원하지 않습니다.
    /// 동일 인스턴스에 대한 Process 호출은 호출자가 직렬화해야 합니다.
    /// </remarks>
    public sealed class BacnetDevice
    {
        /// <summary>BVLC와 NPDU를 포함한 요청 데이터그램의 최대 길이이며 512바이트입니다.</summary>
        public const int MaximumDatagram = 512;
        private readonly IBacnetValueStore _store;
        private readonly uint _device;
        private readonly ushort _vendor;
        private readonly string _name;
        private readonly uint[] _objects;
        private readonly bool[] _writable;

        /// <summary>저장소의 객체 정의를 읽어 BACnet/IP 장치를 구성합니다.</summary>
        /// <param name="store">수명 동안 유지할 0~64개 객체의 저장소입니다. 소유권은 이전되지 않습니다.</param>
        /// <param name="deviceInstance">0~4194302 범위의 Device 인스턴스 번호입니다.</param>
        /// <param name="vendorId">I-Am 및 Device 속성에 노출할 Vendor ID입니다.</param>
        /// <param name="deviceName">ASCII 32~126으로 구성된 1~64자 Device 이름입니다.</param>
        /// <exception cref="ArgumentNullException"><paramref name="store"/>가 <see langword="null"/>입니다.</exception>
        /// <exception cref="ArgumentException">인스턴스 번호, 객체 수 또는 이름이 유효하지 않거나 객체 정의가 null 또는 중복입니다.</exception>
        public BacnetDevice(IBacnetValueStore store, uint deviceInstance, ushort vendorId, string deviceName)
        {
            if (store == null)
                throw new ArgumentNullException("store");
            if (deviceInstance > 4194302 || store.Count < 0 || store.Count > 64)
                throw new ArgumentException("A valid device instance and at most 64 objects are required.");
            if (deviceName == null || deviceName.Length < 1 || deviceName.Length > 64)
                throw new ArgumentException("Device name length must be 1..64.");
            for (int i = 0; i < deviceName.Length; i++)
                if (deviceName[i] < 32 || deviceName[i] > 126)
                    throw new ArgumentException("Use an ASCII device name.");
            _store = store;
            _device = (8U << 22) | deviceInstance;
            _vendor = vendorId;
            _name = deviceName;
            _objects = new uint[store.Count];
            _writable = new bool[store.Count];
            for (int i = 0; i < _objects.Length; i++)
            {
                BacnetObject point = store.GetObject(i);
                if (point == null)
                    throw new ArgumentException("Null BACnet object.");
                uint id = ((point.Binary ? 5U : 2U) << 22) | point.Instance;
                for (int j = 0; j < i; j++)
                    if (_objects[j] == id)
                        throw new ArgumentException("Duplicate BACnet object identifier.");
                _objects[i] = id;
                _writable[i] = point.Writable;
            }
        }

        private static uint U32(byte[] b, int p)
        {
            return ((uint)b[p] << 24) | ((uint)b[p + 1] << 16) | ((uint)b[p + 2] << 8) | b[p + 3];
        }

        private static void Put32(byte[] b, int p, uint value)
        {
            b[p] = (byte)(value >> 24);
            b[p + 1] = (byte)(value >> 16);
            b[p + 2] = (byte)(value >> 8);
            b[p + 3] = (byte)value;
        }

        private static void Require(bool valid)
        {
            if (!valid)
                throw new BacnetProtocolException();
        }

        private static uint Unsigned(byte[] bytes, ref int offset, int end, int tag)
        {
            Require(offset < end);
            int header = bytes[offset++], length = header & 7;
            Require((header >> 4) == tag && (header & 8) != 0 && length >= 1 && length <= 4 && length <= end - offset);
            uint value = 0;
            for (int i = 0; i < length; i++)
                value = (value << 8) | bytes[offset++];
            return value;
        }

        private static int UInt(byte[] bytes, int p, int tag, uint value)
        {
            int width = value <= 255 ? 1 : value <= 65535 ? 2 : value <= 16777215 ? 3 : 4;
            bytes[p++] = (byte)((tag << 4) | width);
            for (int shift = (width - 1) * 8; shift >= 0; shift -= 8)
                bytes[p++] = (byte)(value >> shift);
            return p;
        }

        private static int Object(byte[] bytes, int p, uint id)
        {
            bytes[p++] = 0xc4;
            Put32(bytes, p, id);
            return p + 4;
        }

        private static int Text(byte[] bytes, int p, string text)
        {
            byte[] encoded = Encoding.UTF8.GetBytes(text);
            bytes[p++] = 0x75;
            bytes[p++] = (byte)(encoded.Length + 1);
            bytes[p++] = 0;
            Array.Copy(encoded, 0, bytes, p, encoded.Length);
            return p + encoded.Length;
        }

        private static byte[] Finish(byte[] bytes, int count)
        {
            bytes[0] = 0x81;
            bytes[1] = 0x0a;
            bytes[2] = (byte)(count >> 8);
            bytes[3] = (byte)count;
            bytes[4] = 1;
            bytes[5] = 0;
            var result = new byte[count];
            Array.Copy(bytes, result, count);
            return result;
        }

        private byte[] IAm()
        {
            var reply = new byte[32];
            reply[6] = 0x10;
            reply[7] = 0;
            int p = Object(reply, 8, _device);
            p = UInt(reply, p, 2, 480);
            p = UInt(reply, p, 9, 3);
            p = UInt(reply, p, 2, _vendor);
            return Finish(reply, p);
        }

        private static byte[] Error(byte invoke, byte service, int errorClass, int code)
        {
            var reply = new byte[16];
            reply[6] = 0x50;
            reply[7] = invoke;
            reply[8] = service;
            int p = UInt(reply, 9, 9, (uint)errorClass);
            p = UInt(reply, p, 9, (uint)code);
            return Finish(reply, p);
        }

        private static byte[] Reject(byte invoke, byte reason, bool abort)
        {
            var reply = new byte[9];
            reply[6] = abort ? (byte)0x71 : (byte)0x60;
            reply[7] = invoke;
            reply[8] = reason;
            return Finish(reply, 9);
        }

        private int Find(uint id)
        {
            for (int i = 0; i < _objects.Length; i++)
                if (_objects[i] == id)
                    return i;
            return -1;
        }

        /// <summary>하나의 BACnet/IP 데이터그램을 처리하고 전송할 응답을 생성합니다.</summary>
        /// <param name="request">BVLC와 NPDU를 포함한 8~512바이트의 전체 요청입니다. 배열을 수정하지 않습니다.</param>
        /// <returns>새 응답 배열입니다. Who-Is의 조회 범위에 이 장치가 포함되지 않으면 <see langword="null"/>입니다.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="request"/>가 <see langword="null"/>입니다.</exception>
        /// <exception cref="BacnetProtocolException">외부 프레임, BVLC, NPDU 또는 Who-Is 형식이 잘못되었거나 지원하지 않습니다.</exception>
        /// <exception cref="InvalidOperationException">저장소가 Binary Value에 0/1 이외의 값을 반환했습니다.</exception>
        /// <remarks>
        /// 호출을 직렬화해야 합니다. 확인형 서비스 내부의 형식 오류는 Reject 응답으로 변환될 수 있습니다.
        /// 쓰기 성공 ACK는 QueueWrite 반환 후 생성되며 실제 장치 반영 완료를 보장하지 않습니다.
        /// </remarks>
        public byte[] Process(byte[] request)
        {
            if (request == null)
                throw new ArgumentNullException("request");
            int end = request.Length;
            Require(end >= 8 && end <= MaximumDatagram && request[0] == 0x81 &&
                (request[1] == 0x0a || request[1] == 0x0b) && ((request[2] << 8) | request[3]) == end &&
                request[4] == 1 && (request[5] == 0 || request[5] == 4));
            if (request[6] == 0x10 && request[7] == 8)
            {
                if (end == 8)
                    return IAm();
                int p = 8;
                uint low = Unsigned(request, ref p, end, 0), high = Unsigned(request, ref p, end, 1);
                Require(p == end && low <= high && high <= 4194303);
                uint instance = _device & 0x3fffff;
                return instance >= low && instance <= high ? IAm() : null;
            }
            Require(end >= 10 && (request[6] & 0xf0) == 0);
            byte invoke = request[8], service = request[9];
            if ((request[6] & 0x0c) != 0)
                return Reject(invoke, 4, true); // 메시지 분할은 지원하지 않습니다.
            if ((request[6] & 1) != 0)
                return Reject(invoke, 4, false);
            int maxCode = request[7] & 15;
            if (maxCode > 5 || (request[7] & 0x80) != 0)
                return Reject(invoke, 2, false);
            if (service != 12 && service != 15)
                return Reject(invoke, 9, false);
            try
            {
                int p = 10;
                Require(p + 5 <= end && request[p++] == 0x0c);
                uint id = U32(request, p);
                p += 4;
                uint property = Unsigned(request, ref p, end, 1);
                int array = -1;
                if (p < end && (request[p] >> 4) == 2)
                {
                    uint value = Unsigned(request, ref p, end, 2);
                    if (value > int.MaxValue)
                        return Error(invoke, service, 2, 42);
                    array = (int)value;
                }
                int index = Find(id);
                if (id != _device && index < 0)
                    return Error(invoke, service, 1, 31);
                if (array >= 0 && property != 76)
                    return Error(invoke, service, 2, 50);
                if (service == 15)
                {
                    if (id == _device || property != 85 || !_writable[index])
                        return Error(invoke, service, 2, 40);
                    Require(p < end && request[p++] == 0x3e);
                    bool binary = (_objects[index] >> 22) == 5;
                    uint bits;
                    if (!binary)
                    {
                        if (p + 5 > end || request[p++] != 0x44)
                            return Error(invoke, service, 2, 9);
                        bits = U32(request, p);
                        p += 4;
                        if ((bits & 0x7f800000) == 0x7f800000)
                            return Error(invoke, service, 2, 37);
                    }
                    else
                    {
                        if (p + 2 > end || request[p++] != 0x91)
                            return Error(invoke, service, 2, 9);
                        int bit = request[p++];
                        if (bit > 1)
                            return Error(invoke, service, 2, 37);
                        bits = (uint)bit;
                    }
                    Require(p < end && request[p++] == 0x3f);
                    if (p != end)
                        return Error(invoke, service, 2, 40); // 우선순위 배열과 명령 우선순위 제어는 지원하지 않습니다.
                    _store.QueueWrite(index, bits);
                    var ack = new byte[9];
                    ack[6] = 0x20;
                    ack[7] = invoke;
                    ack[8] = service;
                    return Finish(ack, ack.Length);
                }
                Require(p == end);
                var reply = new byte[MaximumDatagram];
                reply[6] = 0x30;
                reply[7] = invoke;
                reply[8] = service;
                p = 9;
                reply[p++] = 0x0c;
                Put32(reply, p, id);
                p += 4;
                p = UInt(reply, p, 1, property); // 컨텍스트 태그를 나타내는 비트는 아래에서 추가합니다.
                reply[14] |= 8;
                if (array >= 0)
                {
                    int begin = p;
                    p = UInt(reply, p, 2, (uint)array);
                    reply[begin] |= 8;
                }
                reply[p++] = 0x3e;
                int result = Property(reply, p, id, index, property, array);
                if (result < 0)
                    return Error(invoke, service, 2, -result);
                p = result;
                reply[p++] = 0x3f;
                int maximum = maxCode == 0 ? 50 : maxCode == 1 ? 128 : maxCode == 2 ? 206 : maxCode == 3 ? 480 : 1476;
                if (p - 6 > maximum)
                    return Reject(invoke, 4, true);
                return Finish(reply, p);
            }
            catch (BacnetProtocolException)
            {
                return Reject(invoke, 4, false);
            }
        }

        private int Property(byte[] b, int p, uint id, int index, uint property, int array)
        {
            if (property == 75)
                return Object(b, p, id);
            if (property == 79)
                return UInt(b, p, 9, id >> 22);
            if (property == 77)
                return Text(b, p, id == _device ? _name : "point-" + (id >> 22) + "-" + (id & 0x3fffff));
            if (id == _device)
            {
                if (property == 76)
                {
                    if (array == 0)
                        return UInt(b, p, 2, (uint)(_objects.Length + 1));
                    if (array >= 0)
                    {
                        if (array > _objects.Length + 1)
                            return -42;
                        return Object(b, p, array == 1 ? _device : _objects[array - 2]);
                    }
                    p = Object(b, p, _device);
                    for (int i = 0; i < _objects.Length; i++)
                        p = Object(b, p, _objects[i]);
                    return p;
                }
                if (property == 120)
                    return UInt(b, p, 2, _vendor);
                if (property == 121)
                    return Text(b, p, "VagabondK.nF evaluation");
                if (property == 70)
                    return Text(b, p, "VagabondK.nF BACnet");
                if (property == 44 || property == 12)
                    return Text(b, p, "0.1.0-preview.1");
                if (property == 98)
                    return UInt(b, p, 2, 1);
                if (property == 139)
                    return UInt(b, p, 2, 14);
                if (property == 62)
                    return UInt(b, p, 2, 480);
                if (property == 107)
                    return UInt(b, p, 9, 3);
                if (property == 112)
                    return UInt(b, p, 9, 0);
                if (property == 11)
                    return UInt(b, p, 2, 3000);
                if (property == 73)
                    return UInt(b, p, 2, 0);
                if (property == 155)
                    return UInt(b, p, 2, 1);
                if (property == 97)
                {
                    b[p++] = 0x85;
                    b[p++] = 6;
                    b[p++] = 5;
                    b[p++] = 0;
                    b[p++] = 0x09;
                    b[p++] = 0;
                    b[p++] = 0x20;
                    b[p++] = 0x20;
                    return p;
                }
                if (property == 96)
                {
                    b[p++] = 0x83;
                    b[p++] = 7;
                    b[p++] = 0x24;
                    b[p++] = 0x80;
                    return p;
                }
                return -32;
            }
            if (property == 81)
            {
                b[p++] = 0x10;
                return p;
            } // 애플리케이션 태그 BOOLEAN의 false 값입니다.
            BacnetSample sample = _store.Read(index);
            if ((id >> 22) == 5 && sample.Bits > 1)
                throw new InvalidOperationException("Binary BACnet store returned a non-binary value.");
            if (property == 111)
            {
                b[p++] = 0x82;
                b[p++] = 4;
                b[p++] = sample.Good ? (byte)0 : (byte)0x40;
                return p;
            }
            if (property == 103)
                return UInt(b, p, 9, sample.Good ? 0U : 7U);
            if (property == 117 && (id >> 22) == 2)
                return UInt(b, p, 9, 95); // 단위 없음(no-units)입니다.
            if (property == 85)
            {
                if ((id >> 22) == 5)
                    return UInt(b, p, 9, sample.Bits);
                b[p++] = 0x44;
                Put32(b, p, sample.Bits);
                return p + 4;
            }
            return -32;
        }
    }
}
