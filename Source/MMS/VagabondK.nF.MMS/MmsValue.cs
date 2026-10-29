using System;
using VagabondK.nF.Internal;

namespace VagabondK.nF.MMS
{
    /// <summary>이 라이브러리의 클라이언트가 지원 범위 안에서 반환할 수 있는 MMS Data 형식입니다.</summary>
    public enum MmsValueKind
    {
        /// <summary>순서가 있는 동종 원소 배열입니다.</summary>
        Array = 1,
        /// <summary>순서가 있는 구조체 성분입니다.</summary>
        Structure = 2,
        /// <summary>BOOLEAN입니다.</summary>
        Boolean = 3,
        /// <summary>최대 32비트 BitString입니다.</summary>
        BitString = 4,
        /// <summary>Int64 범위의 INTEGER입니다.</summary>
        Integer = 5,
        /// <summary>UInt64 범위의 Unsigned입니다.</summary>
        Unsigned = 6,
        /// <summary>IEEE 754 binary32 또는 binary64입니다.</summary>
        FloatingPoint = 7,
        /// <summary>최대 64바이트의 OctetString입니다.</summary>
        OctetString = 9,
        /// <summary>선택 응답 등에 사용하는 출력 가능한 ASCII 문자열입니다.</summary>
        VisibleString = 10,
        /// <summary>8바이트 MMS UTC time입니다.</summary>
        UtcTime = 17
    }

    /// <summary>한 MMS Read 결과를 보관합니다. 반환 후 연결 버퍼를 참조하지 않습니다.</summary>
    public sealed class MmsValue
    {
        /// <summary>값의 형식입니다.</summary>
        public MmsValueKind Kind { get; private set; }
        private bool boolean;
        private long integer;
        private ulong unsigned;
        private double floatingPoint;
        private MmsValue[] elements;
        private int floatingWidth;
        private string text;
        private byte[] bytes;
        /// <summary>BitString의 유효 비트 수입니다. 다른 형식은 0입니다.</summary>
        public int BitCount { get; private set; }
        /// <summary>OctetString·BitString·UtcTime의 독립된 바이트 복사본을 반환합니다. BitString은 unused-bits 접두사를 제외합니다.</summary>
        /// <returns>수신 버퍼와 독립된 복사본입니다.</returns>
        /// <exception cref="InvalidOperationException">OctetString·BitString·UtcTime이 아닌 형식입니다.</exception>
        public byte[] GetBytes()
        {
            if (bytes == null) throw new InvalidOperationException("Not a byte-oriented MMS value.");
            var copy = new byte[bytes.Length]; Array.Copy(bytes, copy, copy.Length); return copy;
        }
        /// <summary>OctetString·BitString·UtcTime의 한 바이트를 복사본 할당 없이 읽습니다. 내부 배열은 노출하지 않습니다.</summary>
        /// <param name="index">0부터 시작하는 바이트 인덱스입니다. BitString은 unused-bits 접두사를 제외합니다.</param>
        /// <returns>독립된 스냅샷에 저장된 바이트입니다.</returns>
        /// <exception cref="InvalidOperationException">바이트 기반 형식이 아닙니다.</exception>
        /// <exception cref="ArgumentOutOfRangeException">유효한 바이트 범위를 벗어났습니다.</exception>
        public byte GetByte(int index)
        {
            if (bytes == null) throw new InvalidOperationException("Not a byte-oriented MMS value.");
            if (index < 0 || index >= bytes.Length) throw new ArgumentOutOfRangeException("index");
            return bytes[index];
        }
        /// <summary>VisibleString 값입니다. 형식이 다르면 예외가 발생합니다.</summary>
        public string VisibleString { get { Require(MmsValueKind.VisibleString); return text; } }

        /// <summary>복합 값의 원소 수입니다. 스칼라 값에서는 예외가 발생합니다.</summary>
        public int Count { get { RequireComposite(); return elements.Length; } }
        /// <summary>배열 원소 또는 구조체 성분을 선언 순서로 반환합니다.</summary>
        /// <param name="index">0부터 시작하는 원소 인덱스입니다.</param>
        /// <returns>연결 버퍼를 참조하지 않는 자식 값입니다.</returns>
        /// <exception cref="InvalidOperationException">값이 배열이나 구조체가 아닙니다.</exception>
        /// <exception cref="IndexOutOfRangeException">인덱스가 원소 범위를 벗어납니다.</exception>
        public MmsValue GetElement(int index) { RequireComposite(); return elements[index]; }
        private void RequireComposite()
        {
            if (Kind != MmsValueKind.Array && Kind != MmsValueKind.Structure)
                throw new InvalidOperationException("MMS value is not composite.");
        }

        /// <summary>BOOLEAN 값입니다. 형식이 다르면 InvalidOperationException입니다.</summary>
        public bool Boolean { get { Require(MmsValueKind.Boolean); return boolean; } }
        /// <summary>부호 있는 정수입니다. 형식이 다르면 InvalidOperationException입니다.</summary>
        public long Integer { get { Require(MmsValueKind.Integer); return integer; } }
        /// <summary>부호 없는 정수입니다. 형식이 다르면 InvalidOperationException입니다.</summary>
        public ulong Unsigned { get { Require(MmsValueKind.Unsigned); return unsigned; } }
        /// <summary>부동소수점 값입니다. 형식이 다르면 InvalidOperationException입니다.</summary>
        public double FloatingPoint { get { Require(MmsValueKind.FloatingPoint); return floatingPoint; } }

        private MmsValue() { }
        /// <summary>로컬 Boolean 값의 독립된 스냅샷을 생성합니다.</summary>
        /// <param name="value">원본 값입니다.</param>
        /// <returns>Boolean 스냅샷입니다.</returns>
        public static MmsValue FromBoolean(bool value)
        { return new MmsValue { Kind = MmsValueKind.Boolean, boolean = value }; }
        /// <summary>로컬 Int32 값의 독립된 스냅샷을 생성합니다.</summary>
        /// <param name="value">원본 값입니다.</param>
        /// <returns>Integer 스냅샷입니다.</returns>
        public static MmsValue FromInteger(int value)
        { return new MmsValue { Kind = MmsValueKind.Integer, integer = value }; }
        /// <summary>로컬 Float32 값의 독립된 스냅샷을 생성합니다.</summary>
        /// <param name="value">원본 값입니다.</param>
        /// <returns>Float32 스냅샷입니다.</returns>
        public static MmsValue FromFloatingPoint(float value)
        { return new MmsValue { Kind = MmsValueKind.FloatingPoint, floatingPoint = value, floatingWidth = 32 }; }
        private void Require(MmsValueKind kind)
        {
            if (Kind != kind) throw new InvalidOperationException("MMS value type mismatch.");
        }
        internal static MmsValue Decode(BerElement element)
        {
            int remaining = 256;
            return Decode(element, 0, ref remaining);
        }
        internal static MmsValue Decode(BerElement element, int depth, ref int remaining)
        {
            if (depth > 8 || --remaining < 0)
                throw new MmsProtocolException("MMS value exceeds depth 8 or total node limit 256.");
            byte[] b = element.Buffer;
            int p = element.Offset, n = element.Length;
            var result = new MmsValue();
            if (element.Tag == 0xa1 || element.Tag == 0xa2)
            {
                result.Kind = element.Tag == 0xa1 ? MmsValueKind.Array : MmsValueKind.Structure;
                var fields = element.Children();
                int count = 0;
                while (fields.More)
                {
                    fields.Read();
                    if (++count > remaining) throw new MmsProtocolException("Composite value exceeds node budget.");
                }
                result.elements = new MmsValue[count];
                fields = element.Children();
                for (int i = 0; i < count; i++)
                {
                    result.elements[i] = Decode(fields.Read(), depth + 1, ref remaining);
                    if (element.Tag == 0xa1 && i > 0 && !SameShape(result.elements[0], result.elements[i]))
                        throw new MmsProtocolException("Array element shapes differ.");
                }
            }
            else if (element.Tag == 0x83)
            {
                result.Kind = MmsValueKind.Boolean;
                result.boolean = BerEncoding.ReadBoolean(b, p, n);
            }
            else if (element.Tag == 0x85)
            {
                result.Kind = MmsValueKind.Integer;
                result.integer = element.Integer();
            }
            else if (element.Tag == 0x86)
            {
                if (n < 1 || n > 9 || (b[p] & 128) != 0 || (n == 9 && b[p] != 0))
                    throw new MmsProtocolException("Unsupported MMS unsigned width.");
                if (n > 1 && b[p] == 0 && (b[p + 1] & 128) == 0)
                    throw new MmsProtocolException("Redundant unsigned sign octet.");
                result.Kind = MmsValueKind.Unsigned;
                for (int i = 0; i < n; i++) result.unsigned = (result.unsigned << 8) | b[p + i];
            }
            else if (element.Tag == 0x87)
            {
                result.Kind = MmsValueKind.FloatingPoint;
                if (n == 5 && b[p] == 8)
                {
                    result.floatingWidth = 32;
                    uint bits = 0;
                    for (int i = 1; i < 5; i++) bits = (bits << 8) | b[p + i];
                    result.floatingPoint = RealBits.Decode(bits);
                }
                else if (n == 9 && b[p] == 11)
                {
                    result.floatingWidth = 64;
                    ulong bits = 0;
                    for (int i = 1; i < 9; i++) bits = (bits << 8) | b[p + i];
                    result.floatingPoint = BitConverter.Int64BitsToDouble(unchecked((long)bits));
                }
                else throw new MmsProtocolException("Unsupported MMS floating point format.");
            }
            else if (element.Tag == 0x84 || element.Tag == 0x89 || element.Tag == 0x91)
            {
                result.Kind = (MmsValueKind)(element.Tag & 31);
                if (element.Tag == 0x84)
                {
                    if (n < 1 || n > 5 || b[p] > 7 || (n == 1 && b[p] != 0) ||
                        (n > 1 && (b[p + n - 1] & ((1 << b[p]) - 1)) != 0))
                        throw new MmsProtocolException("Invalid bounded BitString.");
                    result.BitCount = (n - 1) * 8 - b[p]; p++; n--;
                }
                else if ((element.Tag == 0x89 && n > 64) || (element.Tag == 0x91 && n != 8))
                    throw new MmsProtocolException("Invalid bounded byte string or UTC time.");
                result.bytes = new byte[n]; Array.Copy(b, p, result.bytes, 0, n);
            }
            else if (element.Tag == 0x8a)
            {
                if (n > 129) throw new MmsProtocolException("VisibleString exceeds 129 bytes.");
                char[] chars = new char[n];
                for (int i = 0; i < n; i++)
                {
                    if (b[p + i] < 32 || b[p + i] > 126) throw new MmsProtocolException("Non-ASCII VisibleString.");
                    chars[i] = (char)b[p + i];
                }
                result.Kind = MmsValueKind.VisibleString;
                result.text = new string(chars);
            }
            else throw new MmsProtocolException("MMS Data type is outside the supported profile.");
            return result;
        }
        private static bool SameShape(MmsValue a, MmsValue b)
        {
            if (a.Kind != b.Kind || a.floatingWidth != b.floatingWidth || a.BitCount != b.BitCount) return false;
            if (a.elements == null) return true;
            if (a.elements.Length != b.elements.Length) return false;
            for (int i = 0; i < a.elements.Length; i++)
                if (!SameShape(a.elements[i], b.elements[i])) return false;
            return true;
        }
    }

    /// <summary>서버가 한 변수 Read/Write에 반환한 DataAccessError입니다. 연결 자체의 오류는 아닙니다.</summary>
    public sealed class MmsAccessException : Exception
    {
        /// <summary>MMS DataAccessError 코드(0~11)입니다.</summary>
        public int ErrorCode { get; private set; }
        internal MmsAccessException(int code) : base("MMS DataAccessError: " + code) { ErrorCode = code; }
    }
}
