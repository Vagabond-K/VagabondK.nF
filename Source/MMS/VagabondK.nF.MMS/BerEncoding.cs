using System;

namespace VagabondK.nF.MMS
{
    // 초기 지원 범위는 단일 옥텟 식별자와 확정 길이 형식으로 제한합니다.
    internal static class BerEncoding
    {
        internal static void Read(byte[] buffer, ref int position, int end,
            out byte tag, out int contentOffset, out int contentLength)
        {
            BufferRange.Check(buffer, position, end - position);
            int p = position;
            if (end - p < 2) throw new MmsProtocolException("Truncated BER header.");
            tag = buffer[p++];
            if (tag == 0 || (tag & 31) == 31)
                throw new MmsProtocolException("BER EOC or high-tag identifier is unsupported.");
            int first = buffer[p++];
            long length = first;
            if (first >= 128)
            {
                int count = first & 127;
                if (count == 0) throw new MmsProtocolException("Indefinite BER length is unsupported.");
                if (count > 4 || count > end - p)
                    throw new MmsProtocolException("BER length is truncated or exceeds supported width.");
                length = 0;
                for (int i = 0; i < count; i++) length = (length << 8) | buffer[p++];
            }
            if (length > end - p)
                throw new MmsProtocolException("BER content exceeds its enclosing boundary.");
            contentOffset = p;
            contentLength = (int)length;
            position = p + contentLength;
        }

        internal static int WriteHeader(byte[] buffer, int offset, byte tag, int length)
        {
            if (tag == 0 || (tag & 31) == 31) throw new ArgumentOutOfRangeException("tag");
            if (length < 0) throw new ArgumentOutOfRangeException("length");
            int count = length < 128 ? 0 : length <= 255 ? 1 : length <= 65535 ? 2 : length <= 16777215 ? 3 : 4;
            int size = 2 + count;
            BufferRange.Check(buffer, offset, size);
            buffer[offset] = tag;
            buffer[offset + 1] = (byte)(count == 0 ? length : 128 | count);
            for (int i = 0; i < count; i++)
                buffer[offset + 2 + i] = (byte)(length >> (8 * (count - i - 1)));
            return size;
        }

        internal static long ReadInteger(byte[] buffer, int offset, int count)
        {
            BufferRange.Check(buffer, offset, count);
            if (count < 1 || count > 8) throw new MmsProtocolException("BER INTEGER exceeds Int64.");
            if (count > 1 && ((buffer[offset] == 0 && (buffer[offset + 1] & 128) == 0) ||
                (buffer[offset] == 255 && (buffer[offset + 1] & 128) != 0)))
                throw new MmsProtocolException("Redundant BER INTEGER sign octet.");
            long value = (sbyte)buffer[offset];
            for (int i = 1; i < count; i++) value = (value << 8) | buffer[offset + i];
            return value;
        }

        internal static int WriteInteger(byte[] buffer, int offset, byte tag, long value)
        {
            int count = 8;
            while (count > 1)
            {
                int first = (int)((value >> ((count - 1) * 8)) & 255);
                int second = (int)((value >> ((count - 2) * 8)) & 255);
                if ((first == 0 && (second & 128) == 0) || (first == 255 && (second & 128) != 0))
                    count--;
                else break;
            }
            BufferRange.Check(buffer, offset, count + 2);
            int header = WriteHeader(buffer, offset, tag, count);
            for (int i = 0; i < count; i++)
                buffer[offset + header + i] = (byte)(value >> (8 * (count - 1 - i)));
            return header + count;
        }

        internal static bool ReadBoolean(byte[] buffer, int offset, int count)
        {
            BufferRange.Check(buffer, offset, count);
            if (count != 1) throw new MmsProtocolException("BER BOOLEAN must contain one octet.");
            return buffer[offset] != 0;
        }
    }
}
