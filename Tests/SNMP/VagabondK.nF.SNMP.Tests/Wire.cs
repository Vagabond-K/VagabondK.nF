using System;
using System.Collections.Generic;
using System.Text;

namespace VagabondK.nF.SNMP.Tests
{
    // 라이브러리 인코더와 독립적으로 작성한 테스트용 BER 인코더/파서입니다.
    internal static class Wire
    {
        // true이면 짧은 내용에도 비최소 0x81 길이를 사용합니다(BER로는 유효하지만 DER는 아님).
        internal static bool LongForm;

        internal static byte[] Tlv(int tag, params byte[][] parts)
        {
            var content = new List<byte>();
            foreach (byte[] part in parts)
                content.AddRange(part);
            var result = new List<byte> { (byte)tag };
            int n = content.Count;
            if (n < 128 && !LongForm)
                result.Add((byte)n);
            else if (n < 256)
            {
                result.Add(0x81);
                result.Add((byte)n);
            }
            else
            {
                result.Add(0x82);
                result.Add((byte)(n >> 8));
                result.Add((byte)n);
            }
            result.AddRange(content);
            return result.ToArray();
        }

        internal static byte[] Int(long value, int tag = 2)
        {
            var bytes = new List<byte>();
            do
            {
                bytes.Insert(0, (byte)value);
                value >>= 8;
            }
            while (!((value == 0 && (bytes[0] & 0x80) == 0) || (value == -1 && (bytes[0] & 0x80) != 0)));
            return Tlv(tag, bytes.ToArray());
        }

        internal static byte[] UInt(ulong value, int tag)
        {
            var bytes = new List<byte>();
            do
            {
                bytes.Insert(0, (byte)value);
                value >>= 8;
            } while (value != 0);
            if ((bytes[0] & 0x80) != 0)
                bytes.Insert(0, 0);
            return Tlv(tag, bytes.ToArray());
        }

        internal static byte[] OidContent(string text)
        {
            string[] parts = text.Split('.');
            var arcs = new List<ulong>();
            arcs.Add(ulong.Parse(parts[0]) * 40 + ulong.Parse(parts[1]));
            for (int i = 2; i < parts.Length; i++)
                arcs.Add(ulong.Parse(parts[i]));
            var bytes = new List<byte>();
            foreach (ulong arc in arcs)
            {
                var chunk = new List<byte>();
                ulong v = arc;
                do
                {
                    chunk.Insert(0, (byte)((v & 0x7f) | (chunk.Count == 0 ? 0UL : 0x80UL)));
                    v >>= 7;
                } while (v != 0);
                bytes.AddRange(chunk);
            }
            return bytes.ToArray();
        }

        internal static byte[] Oid(string text)
        {
            return Tlv(6, OidContent(text));
        }

        internal static byte[] Null()
        {
            return new byte[] { 5, 0 };
        }

        internal static byte[] Text(string text)
        {
            return Tlv(4, Encoding.ASCII.GetBytes(text));
        }

        internal static byte[] Bind(string oid, byte[] value = null)
        {
            return Tlv(0x30, Oid(oid), value ?? Null());
        }

        internal static byte[] Message(int version, string community, int pdu, int id, int a, int b, params byte[][] binds)
        {
            return Tlv(0x30, Int(version), Text(community),
                Tlv(pdu, Int(id), Int(a), Int(b), Tlv(0x30, binds)));
        }

        internal sealed class Binding
        {
            public string Oid;
            public int Tag;
            public byte[] Content;

            public long Signed
            {
                get
                {
                    long v = (sbyte)Content[0];
                    for (int i = 1; i < Content.Length; i++)
                        v = (v << 8) | Content[i];
                    return v;
                }
            }

            public ulong Unsigned
            {
                get
                {
                    ulong v = 0;
                    foreach (byte x in Content)
                        v = (v << 8) | x;
                    return v;
                }
            }

            public string Ascii
            {
                get
                {
                    return Encoding.ASCII.GetString(Content);
                }
            }
        }

        internal sealed class Reply
        {
            public int Version, Pdu, Id, Status, Index;
            public string Community;
            public List<Binding> Bindings = new List<Binding>();
        }

        private static byte[] Read(byte[] b, ref int p, out int tag)
        {
            tag = b[p++];
            int n = b[p++];
            if (n == 0x81)
                n = b[p++];
            else if (n == 0x82)
            {
                n = (b[p] << 8) | b[p + 1];
                p += 2;
            }
            else
                Cases.Check(n < 0x80);
            var content = new byte[n];
            Array.Copy(b, p, content, 0, n);
            p += n;
            return content;
        }

        internal static string OidText(byte[] c)
        {
            var arcs = new List<ulong>();
            ulong v = 0;
            foreach (byte x in c)
            {
                v = (v << 7) | (uint)(x & 0x7f);
                if ((x & 0x80) == 0)
                {
                    arcs.Add(v);
                    v = 0;
                }
            }
            ulong first = arcs[0] < 40 ? 0UL : arcs[0] < 80 ? 1UL : 2UL;
            var text = new StringBuilder(first + "." + (arcs[0] - first * 40));
            for (int i = 1; i < arcs.Count; i++)
                text.Append('.').Append(arcs[i]);
            return text.ToString();
        }

        internal static Reply Parse(byte[] message)
        {
            Cases.Check(message != null);
            int p = 0, tag;
            byte[] outer = Read(message, ref p, out tag);
            Cases.Check(tag == 0x30 && p == message.Length);
            var reply = new Reply();
            p = 0;
            reply.Version = (int)new Binding { Content = Read(outer, ref p, out tag) }.Signed;
            reply.Community = Encoding.ASCII.GetString(Read(outer, ref p, out tag));
            byte[] pdu = Read(outer, ref p, out reply.Pdu);
            Cases.Check(p == outer.Length);
            p = 0;
            reply.Id = (int)new Binding { Content = Read(pdu, ref p, out tag) }.Signed;
            reply.Status = (int)new Binding { Content = Read(pdu, ref p, out tag) }.Signed;
            reply.Index = (int)new Binding { Content = Read(pdu, ref p, out tag) }.Signed;
            byte[] list = Read(pdu, ref p, out tag);
            Cases.Check(tag == 0x30 && p == pdu.Length);
            p = 0;
            while (p < list.Length)
            {
                byte[] bind = Read(list, ref p, out tag);
                int q = 0;
                var item = new Binding { Oid = OidText(Read(bind, ref q, out tag)) };
                Cases.Check(tag == 6);
                item.Content = Read(bind, ref q, out item.Tag);
                Cases.Check(q == bind.Length);
                reply.Bindings.Add(item);
            }
            return reply;
        }
    }
}
