using System;
using VagabondK.nF.BACnet;
using VagabondK.nF.SNMP;

namespace VagabondK.nF.Virtual.Tests
{
    /// <summary>BACnet·SNMP의 값 인코딩과 잘못된 쓰기 요청의 부작용 차단을 nanoCLR에서 검증합니다.</summary>
    internal static class DeviceVirtual
    {
        private const string E = "1.3.6.1.4.1.32473.1.";

        internal static void Bacnet()
        {
            var store = new BacnetStore();
            var device = new BacnetDevice(store, 1000, 7, "Device");
            byte[] iam = device.Process(Frame(new byte[] { 0x10, 8 }));
            Program.Check(iam != null && iam.Length == 20 && iam[6] == 0x10 && iam[8] == 0xc4, "BACnet Who-Is/I-Am");

            byte[] analog = device.Process(Confirmed(12, Id(2, 1, 0x19, 85)));
            Program.Check(analog[17] == 0x44 && analog[18] == 0xc0 && analog[19] == 0x20 && analog[20] == 0 && analog[21] == 0,
                "BACnet negative REAL present-value");
            byte[] binary = device.Process(Confirmed(12, Id(5, 2, 0x19, 85)));
            Program.Check(binary[17] == 0x91 && binary[18] == 1, "BACnet binary present-value");

            byte[] ack = device.Process(Confirmed(15, Id(2, 1, 0x19, 85, 0x3e, 0x44, 0xbf, 0x00, 0x00, 0x00, 0x3f)));
            Program.Check(ack[6] == 0x20 && ack[8] == 15 && store.WriteIndex == 0 && store.WriteBits == 0xBF000000, "BACnet write negative REAL");
            ack = device.Process(Confirmed(15, Id(5, 2, 0x19, 85, 0x3e, 0x91, 0, 0x3f)));
            Program.Check(ack[6] == 0x20 && store.WriteIndex == 1 && store.WriteBits == 0, "BACnet write binary inactive");

            byte[] error = device.Process(Confirmed(12, Id(2, 4, 0x19, 85)));
            Program.Check(error.Length == 13 && error[6] == 0x50, "BACnet unknown object error");
            Program.Throws(() => device.Process(Frame(new byte[] { 0x00, 5 })), "BACnet truncated request rejected");
            BacnetRecovery(device, store);
        }

        // 거절 응답뿐 아니라 저장값·쓰기 횟수가 보존되는지 확인한 뒤 정상 쓰기/읽기를 재시도합니다.
        private static void BacnetRecovery(BacnetDevice device, BacnetStore store)
        {
            byte[] good = Confirmed(15, Id(2, 1, 0x19, 85, 0x3e, 0x44, 0xbf, 0, 0, 0, 0x3f));
            for (int kind = 0; kind < 4; kind++)
            {
                byte[] bad = Program.Copy(good, kind == 0 ? 3 : kind == 1 ? good.Length - 1 : good.Length);
                if (kind == 1) bad[3] = (byte)bad.Length;
                else if (kind == 2) bad[0] = 0;
                else if (kind == 3) bad[3]--;
                int writes = store.Writes;
                uint bits = store.Read(0).Bits;
                if (kind == 1)
                {
                    byte[] rejection = device.Process(bad);
                    Program.Check(rejection != null && rejection[6] == 0x60,
                        "BACnet truncated write receives Reject");
                }
                else
                    Program.Rejects(() => device.Process(bad), typeof(BacnetProtocolException),
                        "BACnet malformed write rejected " + kind);
                Program.Check(store.Writes == writes && store.Read(0).Bits == bits,
                    "BACnet rejected write preserves value/count " + kind);
                byte[] ack = device.Process(good);
                Program.Check(ack != null && ack[6] == 0x20 && ack[8] == 15 &&
                    store.Writes == writes + 1 && store.WriteBits == 0xbf000000,
                    "BACnet normal write after rejection " + kind);
                byte[] read = device.Process(Confirmed(12, Id(2, 1, 0x19, 85)));
                Program.Check(read != null && read[17] == 0x44 && read[18] == 0xbf &&
                    read[19] == 0 && read[20] == 0 && read[21] == 0,
                    "BACnet normal read after rejection " + kind);
                Program.Check(device.Process(Frame(new byte[] { 0x10, 8 })) != null,
                    "BACnet discovery after rejection " + kind);
            }
        }

        private static byte[] Frame(byte[] apdu)
        {
            var frame = new byte[apdu.Length + 6];
            frame[0] = 0x81;
            frame[1] = 10;
            frame[2] = (byte)(frame.Length >> 8);
            frame[3] = (byte)frame.Length;
            frame[4] = 1;
            frame[5] = 4;
            Array.Copy(apdu, 0, frame, 6, apdu.Length);
            return frame;
        }

        private static byte[] Confirmed(byte service, byte[] body)
        {
            var apdu = new byte[4 + body.Length];
            apdu[1] = 5;
            apdu[2] = 7;
            apdu[3] = service;
            Array.Copy(body, 0, apdu, 4, body.Length);
            return Frame(apdu);
        }

        private static byte[] Id(uint type, uint instance, params byte[] tail)
        {
            uint id = (type << 22) | instance;
            var body = new byte[5 + tail.Length];
            body[0] = 0x0c;
            body[1] = (byte)(id >> 24);
            body[2] = (byte)(id >> 16);
            body[3] = (byte)(id >> 8);
            body[4] = (byte)id;
            Array.Copy(tail, 0, body, 5, tail.Length);
            return body;
        }

        private sealed class BacnetStore : IBacnetValueStore
        {
            private readonly BacnetObject[] _objects = { new BacnetObject(1, false, true), new BacnetObject(2, true, true) };
            private readonly uint[] _bits = { 0xC0200000, 1 };
            internal int WriteIndex = -1;
            internal uint WriteBits;
            internal int Writes;

            public int Count { get { return _objects.Length; } }

            public BacnetObject GetObject(int index)
            {
                return _objects[index];
            }

            public BacnetSample Read(int index)
            {
                return new BacnetSample(_bits[index], true);
            }

            public void QueueWrite(int index, uint bits)
            {
                WriteIndex = index;
                WriteBits = bits;
                _bits[index] = bits;
                Writes++;
            }
        }

        // Integer32/Counter32/Counter64와 큰 OID 구성 요소를 사용해 정수 폭에 따른 인코딩 오류를 드러냅니다.
        internal static void Snmp()
        {
            var store = new SnmpStore();
            var agent = new SnmpAgent(store, new SnmpSystem("VagabondK SNMP", "node"), "public", "private");

            byte[][] values = Bindings(agent.Process(Message("public", 0xa0, 11, Bind(E + "1.0", Null()), Bind(E + "2.0", Null()),
                Bind(E + "3.0", Null()), Bind(E + "4.0", Null()))), 4);
            Program.Check(values != null, "SNMP GET response shape");
            if (values == null)
                return;
            Program.Check(values[0][0] == 2 && Signed(values[0]) == int.MinValue, "SNMP Integer32 minimum");
            Program.Check(values[1][0] == 0x41 && Unsigned(values[1]) == uint.MaxValue, "SNMP Counter32 maximum");
            Program.Check(values[2][0] == 0x46 && Unsigned(values[2]) == ulong.MaxValue, "SNMP Counter64 maximum");
            Program.Check(values[3][0] == 6 && values[3].Length == 8 && values[3][1] == 0x2b && values[3][2] == 0x8f && values[3][5] == 0xff &&
                values[3][6] == 0x7f && values[3][7] == 0,
                "SNMP OID with 32-bit arc");

            values = Bindings(agent.Process(Message("public", 0xa1, 12, Bind(E + "1", Null()))), 1);
            Program.Check(values != null && Signed(values[0]) == int.MinValue, "SNMP GetNext into instance");

            values = Bindings(agent.Process(Message("private", 0xa3, 13, Bind(E + "1.0", Integer(-2, 2)),
                Bind(E + "3.0", Unsigned64(0x8000000000000001UL, 0x46)))), 2);
            Program.Check(values != null && store.Writes == 2 && store.Integer == -2 && store.Big == 0x8000000000000001UL, "SNMP SET negative and Counter64");

            Program.Check(agent.Process(Message("wrong", 0xa0, 14, Bind(E + "1.0", Null()))) == null, "SNMP wrong community dropped");
            SnmpRecovery(agent, store);
        }

        private static void SnmpRecovery(SnmpAgent agent, SnmpStore store)
        {
            store.Stored = true;
            byte[] good = Message("private", 0xa3, 21, Bind(E + "1.0", Integer(-123, 2)),
                Bind(E + "3.0", Unsigned64(0x8000000000000002UL, 0x46)));
            for (int kind = 0; kind < 7; kind++)
            {
                byte[] bad = Program.Copy(good, kind == 0 ? 0 : kind == 1 ? good.Length - 1 :
                    kind == 6 ? good.Length + 1 : good.Length);
                if (kind == 2) bad[0] = 0x31;
                else if (kind == 3) bad[1] = 0x80;
                else if (kind == 4) bad[1]++;
                else if (kind == 5) bad[2] = 5;
                int writes = store.Writes, value = store.Integer;
                ulong big = store.Big;
                Program.Rejects(() => agent.Process(bad), typeof(SnmpProtocolException),
                    "SNMP malformed two-value SET rejected " + kind);
                Program.Check(store.Writes == writes && store.Integer == value && store.Big == big,
                    "SNMP rejected SET preserves both values/count " + kind);
                byte[][] reply = Bindings(agent.Process(good), 2);
                Program.Check(reply != null && store.Writes == writes + 2 && store.Integer == -123 &&
                    store.Big == 0x8000000000000002UL, "SNMP normal SET after rejection " + kind);
                reply = Bindings(agent.Process(Message("public", 0xa0, 22,
                    Bind(E + "1.0", Null()), Bind(E + "3.0", Null()))), 2);
                Program.Check(reply != null && Signed(reply[0]) == -123 &&
                    Unsigned(reply[1]) == 0x8000000000000002UL, "SNMP normal GET after rejection " + kind);
            }
        }

        private sealed class SnmpStore : ISnmpValueStore
        {
            private readonly SnmpObject[] _objects = {
                new SnmpObject(SnmpOid.Parse(E + "1.0"), SnmpType.Integer32, true),
                new SnmpObject(SnmpOid.Parse(E + "2.0"), SnmpType.Counter32),
                new SnmpObject(SnmpOid.Parse(E + "3.0"), SnmpType.Counter64, true),
                new SnmpObject(SnmpOid.Parse(E + "4.0"), SnmpType.ObjectIdentifier)
            };
            internal int Writes;
            internal int Integer;
            internal ulong Big;
            internal bool Stored;

            public int Count { get { return _objects.Length; } }

            public SnmpObject GetObject(int index)
            {
                return _objects[index];
            }

            public SnmpValue Read(int index)
            {
                if (index == 0)
                    return SnmpValue.Integer32(Stored ? Integer : int.MinValue);
                if (index == 1)
                    return SnmpValue.Counter32(uint.MaxValue);
                if (index == 2)
                    return SnmpValue.Counter64(Stored ? Big : ulong.MaxValue);
                return SnmpValue.ObjectIdentifier(SnmpOid.Parse("1.3.4294967295.0"));
            }

            public bool AcceptsWrite(int index, SnmpValue value)
            {
                return true;
            }

            public void QueueWrite(int index, SnmpValue value)
            {
                Writes++;
                if (index == 0)
                    Integer = value.Integer;
                else
                    Big = value.Unsigned64;
            }
        }

        private static byte[] Tlv(int tag, params byte[][] parts)
        {
            int length = 0;
            for (int i = 0; i < parts.Length; i++)
                length += parts[i].Length;
            int header = length < 128 ? 2 : length < 256 ? 3 : 4;
            var result = new byte[header + length];
            result[0] = (byte)tag;
            if (header == 2)
                result[1] = (byte)length;
            else if (header == 3)
            {
                result[1] = 0x81;
                result[2] = (byte)length;
            }
            else
            {
                result[1] = 0x82;
                result[2] = (byte)(length >> 8);
                result[3] = (byte)length;
            }
            int p = header;
            for (int i = 0; i < parts.Length; i++)
            {
                Array.Copy(parts[i], 0, result, p, parts[i].Length);
                p += parts[i].Length;
            }
            return result;
        }

        private static byte[] Integer(long value, int tag)
        {
            var bytes = new byte[9];
            int start = 9;
            do
            {
                bytes[--start] = (byte)value;
                value >>= 8;
            }
            while (!((value == 0 && (bytes[start] & 0x80) == 0) || (value == -1 && (bytes[start] & 0x80) != 0)));
            var content = new byte[9 - start];
            Array.Copy(bytes, start, content, 0, content.Length);
            return Tlv(tag, content);
        }

        private static byte[] Unsigned64(ulong value, int tag)
        {
            var bytes = new byte[9];
            int start = 9;
            do
            {
                bytes[--start] = (byte)value;
                value >>= 8;
            }
            while (value != 0);
            if ((bytes[start] & 0x80) != 0)
                start--;
            var content = new byte[9 - start];
            Array.Copy(bytes, start, content, 0, content.Length);
            return Tlv(tag, content);
        }

        private static byte[] Oid(string text)
        {
            uint[] arcs = SnmpOid.Parse(text);
            var bytes = new byte[arcs.Length * 5];
            int p = 0;
            for (int i = 1; i < arcs.Length; i++)
            {
                uint arc = i == 1 ? arcs[0] * 40 + arcs[1] : arcs[i];
                int groups = 1;
                for (uint v = arc >> 7; v != 0; v >>= 7)
                    groups++;
                for (int g = groups - 1; g >= 0; g--)
                    bytes[p++] = (byte)(((arc >> (7 * g)) & 0x7f) | (g == 0 ? 0u : 0x80u));
            }
            var content = new byte[p];
            Array.Copy(bytes, content, p);
            return Tlv(6, content);
        }

        private static byte[] Null()
        {
            return new byte[] { 5, 0 };
        }

        private static byte[] Bind(string oid, byte[] value)
        {
            return Tlv(0x30, Oid(oid), value);
        }

        private static byte[] Message(string community, int pdu, int id, params byte[][] binds)
        {
            var text = new byte[community.Length];
            for (int i = 0; i < text.Length; i++)
                text[i] = (byte)community[i];
            return Tlv(0x30, Integer(1, 2), Tlv(4, text), Tlv(pdu, Integer(id, 2), Integer(0, 2), Integer(0, 2), Tlv(0x30, binds)));
        }

        // 각 바인딩 값을 [태그, 내용...]으로 반환하며 성공한 GetResponse가 아니면 null을 반환합니다.
        private static byte[][] Bindings(byte[] reply, int count)
        {
            if (reply == null)
                return null;
            int p = Enter(reply, 0);
            p = Skip(reply, p);
            p = Skip(reply, p);
            if (reply[p] != 0xa2)
                return null;
            p = Enter(reply, p);
            p = Skip(reply, p);
            if (reply[p + 2] != 0)
                return null;
            p = Skip(reply, p);
            p = Skip(reply, p);
            p = Enter(reply, p);
            var values = new byte[count][];
            for (int i = 0; i < count; i++)
            {
                int bind = Enter(reply, p);
                int value = Skip(reply, bind);
                int end = Skip(reply, value);
                int content = Enter(reply, value);
                values[i] = new byte[1 + end - content];
                values[i][0] = reply[value];
                Array.Copy(reply, content, values[i], 1, end - content);
                p = end;
            }
            return values;
        }

        private static int Enter(byte[] b, int p)
        {
            byte first = b[p + 1];
            return p + (first < 0x80 ? 2 : 2 + (first & 0x7f));
        }

        private static int Skip(byte[] b, int p)
        {
            byte first = b[p + 1];
            int length = first;
            if (first == 0x81)
                length = b[p + 2];
            else if (first == 0x82)
                length = (b[p + 2] << 8) | b[p + 3];
            return Enter(b, p) + length;
        }

        private static long Signed(byte[] value)
        {
            long v = (sbyte)value[1];
            for (int i = 2; i < value.Length; i++)
                v = (v << 8) | value[i];
            return v;
        }

        private static ulong Unsigned(byte[] value)
        {
            ulong v = 0;
            for (int i = 1; i < value.Length; i++)
                v = (v << 8) | value[i];
            return v;
        }
    }
}
