using System;

namespace VagabondK.nF.SNMP
{
    /// <summary>잘못되었거나 지원하지 않는 SNMP 메시지 또는 BER 형식을 나타냅니다.</summary>
    public sealed class SnmpProtocolException : Exception
    {
        /// <summary>기본 SNMP 메시지 오류 설명으로 예외를 생성합니다.</summary>
        public SnmpProtocolException() : base("Malformed or unsupported SNMP message.") { }
    }

    // SNMPv1/v2c에 필요한 최소 BER 형식만 지원합니다. 길이는 최대 0xFFFF인 확정 길이 형식이고 태그는 단일 바이트입니다.
    internal static class Ber
    {
        internal const int Sequence = 0x30;

        internal static void Require(bool valid)
        {
            if (!valid)
                throw new SnmpProtocolException();
        }

        internal static int Length(byte[] b, ref int p, int end)
        {
            Require(p < end);
            int first = b[p++], length;
            if (first < 0x80)
                length = first;
            else
            {
                int count = first & 0x7f;
                Require(count >= 1 && count <= 2 && count <= end - p);
                length = 0;
                for (int i = 0; i < count; i++)
                    length = (length << 8) | b[p++];
            }
            Require(length <= end - p);
            return length;
        }

        internal static int Header(byte[] b, ref int p, int end, int tag)
        {
            Require(p < end && b[p] == tag);
            p++;
            return Length(b, ref p, end);
        }

        // 단일 바이트 태그의 헤더와 내용을 건너뛰고 헤더를 제외한 내용의 길이를 반환합니다.
        internal static int Skip(byte[] b, ref int p, int end)
        {
            Require(p < end && (b[p] & 0x1f) != 0x1f);
            p++;
            int length = Length(b, ref p, end);
            p += length;
            return length;
        }

        internal static int Integer(byte[] b, ref int p, int end)
        {
            int length = Header(b, ref p, end, 0x02);
            Require(length >= 1 && length <= 4);
            int value = (sbyte)b[p++];
            for (int i = 1; i < length; i++)
                value = (value << 8) | b[p++];
            return value;
        }

        // 부호 없는 애플리케이션 형식의 내용을 읽습니다. 음수이거나 허용 바이트 수를 초과하는 등 유효하지 않으면 false를 반환합니다.
        internal static bool Unsigned(byte[] b, int p, int length, int maximumBytes, out ulong value)
        {
            value = 0;
            if (length < 1 || length > maximumBytes + 1 || (b[p] & 0x80) != 0)
                return false;
            if (length == maximumBytes + 1 && b[p] != 0)
                return false;
            for (int i = 0; i < length; i++)
                value = (value << 8) | b[p + i];
            return true;
        }

        // 불필요한 패딩, 빈 내용, 길이 초과, 구성 요소의 32비트 범위 초과 등으로 OID가 유효하지 않으면 null을 반환합니다.
        internal static uint[] Oid(byte[] b, int p, int length)
        {
            if (length < 1 || length > 640)
                return null;
            int end = p + length, count = 0;
            var buffer = new uint[SnmpOid.MaximumLength];
            while (p < end)
            {
                if (b[p] == 0x80)
                    return null;
                ulong value = 0;
                int used = 0;
                while (true)
                {
                    if (p >= end || ++used > 5)
                        return null;
                    byte part = b[p++];
                    value = (value << 7) | (uint)(part & 0x7f);
                    if ((part & 0x80) == 0)
                        break;
                }
                if (value > uint.MaxValue)
                    return null;
                if (count == 0)
                {
                    uint first = value < 40 ? 0U : value < 80 ? 1U : 2U;
                    buffer[0] = first;
                    buffer[1] = (uint)value - first * 40;
                    count = 2;
                }
                else
                {
                    if (count == SnmpOid.MaximumLength)
                        return null;
                    buffer[count++] = (uint)value;
                }
            }
            var oid = new uint[count];
            Array.Copy(buffer, oid, count);
            return oid;
        }

        internal static uint[] ReadOid(byte[] b, ref int p, int end)
        {
            int length = Header(b, ref p, end, 0x06);
            uint[] oid = Oid(b, p, length);
            Require(oid != null);
            p += length;
            return oid;
        }

        internal static int Tlv(int contentLength)
        {
            return 1 + (contentLength < 0x80 ? 1 : contentLength < 0x100 ? 2 : 3) + contentLength;
        }

        internal static int SignedLength(long value)
        {
            int count = 1;
            while (count < 8 && (value < -(1L << (8 * count - 1)) || value >= (1L << (8 * count - 1))))
                count++;
            return count;
        }

        internal static int UnsignedLength(ulong value)
        {
            int count = 1;
            while (count < 9 && (value >> (8 * count - 1)) != 0)
                count++;
            return count;
        }

        private static int Base128Length(ulong value)
        {
            int count = 1;
            while (count < 10 && (value >> (7 * count)) != 0)
                count++;
            return count;
        }

        internal static int OidLength(uint[] oid)
        {
            int length = Base128Length((ulong)oid[0] * 40 + oid[1]);
            for (int i = 2; i < oid.Length; i++)
                length += Base128Length(oid[i]);
            return length;
        }

        internal static void Put(byte[] b, ref int p, int tag, int length)
        {
            b[p++] = (byte)tag;
            if (length < 0x80)
                b[p++] = (byte)length;
            else if (length < 0x100)
            {
                b[p++] = 0x81;
                b[p++] = (byte)length;
            }
            else
            {
                b[p++] = 0x82;
                b[p++] = (byte)(length >> 8);
                b[p++] = (byte)length;
            }
        }

        internal static void PutSigned(byte[] b, ref int p, long value)
        {
            int length = SignedLength(value);
            Put(b, ref p, 0x02, length);
            for (int shift = (length - 1) * 8; shift >= 0; shift -= 8)
                b[p++] = (byte)(value >> shift);
        }

        internal static void PutUnsigned(byte[] b, ref int p, int tag, ulong value)
        {
            int length = UnsignedLength(value);
            Put(b, ref p, tag, length);
            for (int i = length - 1; i >= 0; i--)
                b[p++] = i >= 8 ? (byte)0 : (byte)(value >> (8 * i));
        }

        private static void PutBase128(byte[] b, ref int p, ulong value)
        {
            for (int i = Base128Length(value) - 1; i >= 0; i--)
                b[p++] = (byte)(((value >> (7 * i)) & 0x7f) | (i == 0 ? 0UL : 0x80UL));
        }

        internal static void PutOid(byte[] b, ref int p, uint[] oid)
        {
            Put(b, ref p, 0x06, OidLength(oid));
            PutBase128(b, ref p, (ulong)oid[0] * 40 + oid[1]);
            for (int i = 2; i < oid.Length; i++)
                PutBase128(b, ref p, oid[i]);
        }

        internal static int ValueLength(SnmpValue value)
        {
            switch (value.Type)
            {
                case SnmpType.Integer32:
                    return SignedLength((long)value.Raw);
                case SnmpType.OctetString:
                case SnmpType.IpAddress:
                    return value.RawBytes.Length;
                case SnmpType.ObjectIdentifier:
                    return OidLength(value.RawOid);
                default:
                    return UnsignedLength(value.Raw);
            }
        }

        internal static void PutValue(byte[] b, ref int p, SnmpValue value)
        {
            switch (value.Type)
            {
                case SnmpType.Integer32:
                    PutSigned(b, ref p, (long)value.Raw);
                    break;
                case SnmpType.OctetString:
                case SnmpType.IpAddress:
                    byte[] bytes = value.RawBytes;
                    Put(b, ref p, (int)value.Type, bytes.Length);
                    Array.Copy(bytes, 0, b, p, bytes.Length);
                    p += bytes.Length;
                    break;
                case SnmpType.ObjectIdentifier:
                    PutOid(b, ref p, value.RawOid);
                    break;
                default:
                    PutUnsigned(b, ref p, (int)value.Type, value.Raw);
                    break;
            }
        }
    }
}
