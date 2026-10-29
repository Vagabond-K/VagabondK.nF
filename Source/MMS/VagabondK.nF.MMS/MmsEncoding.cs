using System;

namespace VagabondK.nF.MMS
{
    internal sealed class BerElement
    {
        internal readonly byte Tag;
        internal readonly byte[] Buffer;
        internal readonly int Offset, Length;
        internal BerElement(byte[] buffer, int offset, int length, byte tag)
        {
            Buffer = buffer;
            Offset = offset;
            Length = length;
            Tag = tag;
        }
        internal BerCursor Children() { return new BerCursor(Buffer, Offset, Length); }
        internal long Integer() { return BerEncoding.ReadInteger(Buffer, Offset, Length); }
        internal void Expect(byte tag)
        {
            if (Tag != tag) throw new MmsProtocolException("Unexpected BER tag.");
        }
        internal bool Matches(byte[] bytes)
        {
            if (Length != bytes.Length) return false;
            for (int i = 0; i < Length; i++) if (Buffer[Offset + i] != bytes[i]) return false;
            return true;
        }
        internal byte[] Copy()
        {
            byte[] bytes = new byte[Length];
            Array.Copy(Buffer, Offset, bytes, 0, Length);
            return bytes;
        }
    }

    internal sealed class BerCursor
    {
        private readonly byte[] buffer;
        private int position;
        private readonly int end;
        internal BerCursor(byte[] buffer, int offset, int count)
        {
            BufferRange.Check(buffer, offset, count);
            this.buffer = buffer;
            position = offset;
            end = offset + count;
        }
        internal bool More { get { return position < end; } }
        internal BerElement Read()
        {
            byte tag;
            int start, count;
            BerEncoding.Read(buffer, ref position, end, out tag, out start, out count);
            return new BerElement(buffer, start, count, tag);
        }
        internal BerElement Read(byte tag)
        {
            BerElement value = Read();
            value.Expect(tag);
            return value;
        }
        internal void End()
        {
            if (More) throw new MmsProtocolException("Unexpected trailing BER fields.");
        }
    }

    internal static class MmsEncoding
    {
        internal static readonly byte[] AcseSyntax = { 0x52, 1, 0, 1 };
        internal static readonly byte[] MmsSyntax = { 0x28, 0xca, 0x22, 2, 1 };
        internal static readonly byte[] BerSyntax = { 0x51, 1 };
        internal static readonly byte[] ApplicationContext = { 0x28, 0xca, 0x22, 2, 3 };

        internal static byte[] Join(params byte[][] parts)
        {
            int count = 0;
            for (int i = 0; i < parts.Length; i++) count = checked(count + parts[i].Length);
            byte[] output = new byte[count];
            int p = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                Array.Copy(parts[i], 0, output, p, parts[i].Length);
                p += parts[i].Length;
            }
            return output;
        }
        internal static byte[] Tlv(byte tag, params byte[][] parts)
        {
            byte[] content = Join(parts);
            byte[] header = new byte[6];
            int size = BerEncoding.WriteHeader(header, 0, tag, content.Length);
            byte[] result = new byte[size + content.Length];
            Array.Copy(header, result, size);
            Array.Copy(content, 0, result, size, content.Length);
            return result;
        }
        internal static byte[] Int(byte tag, long value)
        {
            byte[] buffer = new byte[10];
            int count = BerEncoding.WriteInteger(buffer, 0, tag, value);
            byte[] result = new byte[count];
            Array.Copy(buffer, result, count);
            return result;
        }
        internal static byte[] Identifier(string text)
        {
            if (text == null) throw new ArgumentNullException("identifier");
            if (text.Length < 1 || text.Length > 64) throw new ArgumentOutOfRangeException("identifier");
            byte[] result = new byte[text.Length];
            for (int i = 0; i < result.Length; i++)
            {
                if (text[i] < 33 || text[i] > 126) throw new ArgumentException("MMS identifier must be printable ASCII without spaces.");
                result[i] = (byte)text[i];
            }
            return Tlv(0x1a, result);
        }
        internal static string ReadIdentifier(BerElement element)
        {
            if (element.Length < 1 || element.Length > 64) throw new MmsProtocolException("Invalid discovery identifier size.");
            char[] chars = new char[element.Length];
            for (int i = 0; i < chars.Length; i++)
            {
                byte b = element.Buffer[element.Offset + i];
                if (b < 33 || b > 126) throw new MmsProtocolException("Invalid discovery identifier.");
                chars[i] = (char)b;
            }
            return new string(chars);
        }
        private static byte[] Context(int id, byte[] syntax)
        {
            return Tlv(0x30, Int(2, id), Tlv(6, syntax), Tlv(0x30, Tlv(6, BerSyntax)));
        }
        internal static byte[] Pdv(int id, byte[] value)
        {
            return Tlv(0x61, Tlv(0x30, Int(2, id), Tlv(0xa0, value)));
        }
        internal static byte[] Initiate(int maximumPdu, int nesting = 8, bool namedVariableLists = false)
        {
            // Parameter-CBB는 str1/str2/vnam, 서비스는 GetNameList·Read·Write·GetVariableAccessAttributes입니다.
            return Tlv(0xa8, Int(0x80, maximumPdu), Int(0x81, 1), Int(0x82, 1), Int(0x83, nesting),
                Tlv(0xa4, Int(0x80, 1), Tlv(0x81, new byte[] { 5, 0xf1, 0 }),
                    Tlv(0x82, new byte[] { 3, 0x4e, namedVariableLists ? (byte)8 : (byte)0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })));
        }
        internal static byte[] Association(int maximumPdu)
        {
            byte[] external = Tlv(0x28, Int(2, 3), Tlv(0xa0, Initiate(maximumPdu)));
            byte[] aarq = Tlv(0x60, Tlv(0xa1, Tlv(6, ApplicationContext)), Tlv(0xbe, external));
            byte[] presentation = Tlv(0x31, Tlv(0xa0, Int(0x80, 1)),
                Tlv(0xa2, Tlv(0x81, new byte[] { 0, 0, 0, 1 }), Tlv(0x82, new byte[] { 0, 0, 0, 1 }),
                    Tlv(0xa4, Context(1, AcseSyntax), Context(3, MmsSyntax)), Pdv(1, aarq)));
            byte[] parameters = Join(new byte[] { 5, 6, 0x13, 1, 0, 0x16, 1, 2,
                0x14, 2, 0, 2, 0x33, 2, 0, 1, 0x34, 2, 0, 1 }, SessionParameter(0xc1, presentation));
            return SessionParameter(0x0d, parameters);
        }
        internal static byte[] SessionParameter(byte tag, byte[] value)
        {
            if (value.Length > 65535) throw new ArgumentOutOfRangeException("value");
            byte[] header = value.Length < 255 ? new byte[] { tag, (byte)value.Length } :
                new byte[] { tag, 255, (byte)(value.Length >> 8), (byte)value.Length };
            return Join(header, value);
        }
        internal static byte[] Release(bool response)
        {
            return SessionParameter(response ? (byte)10 : (byte)9,
                SessionParameter(0xc1, Pdv(1, response ? Tlv(0x63) : Tlv(0x62, Int(0x80, 0)))));
        }
        internal static void ValidateRelease(byte[] buffer, int length, bool response)
        {
            // 이 제한형 프로파일에는 사용자 정보나 세션 transport-disconnect 매개변수가 없습니다.
            if (length < 4 || buffer[0] != (response ? 10 : 9) || buffer[1] != length - 2 ||
                buffer[2] != 0xc1 || buffer[3] != length - 4)
                throw new MmsProtocolException("Invalid release Session envelope.");
            BerElement user = UnwrapPdv(Single(buffer, 4, length - 4, 0x61), 1);
            var fields = Single(user.Buffer, user.Offset, user.Length, response ? (byte)0x63 : (byte)0x62).Children();
            if (fields.More)
            {
                long reason = fields.Read(0x80).Integer();
                if (reason != 0) throw new MmsProtocolException("Unsupported ACSE release reason.");
            }
            fields.End();
        }
        internal static BerElement Single(byte[] buffer, int offset, int count, byte tag)
        {
            var cursor = new BerCursor(buffer, offset, count);
            BerElement value = cursor.Read(tag);
            cursor.End();
            return value;
        }
        internal static BerElement UnwrapPdv(BerElement element, int context)
        {
            element.Expect(0x61);
            var pdvs = element.Children();
            var pdv = pdvs.Read(0x30).Children();
            pdvs.End();
            if (pdv.Read(2).Integer() != context) throw new MmsProtocolException("Unexpected presentation context.");
            BerElement data = pdv.Read(0xa0);
            pdv.End();
            return data;
        }
        internal static byte[] ReadRequest(int invoke, byte[] domain, byte[] item)
        {
            byte[] name = Tlv(0xa1, domain, item);
            byte[] specification = Tlv(0xa1, Tlv(0xa0, Tlv(0x30, Tlv(0xa0, name))));
            return Tlv(0xa0, Int(2, invoke), Tlv(0xa4, specification));
        }
    }
}
