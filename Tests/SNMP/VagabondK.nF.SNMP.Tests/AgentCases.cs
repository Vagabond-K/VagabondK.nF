using System;
using System.Collections.Generic;


namespace VagabondK.nF.SNMP.Tests
{
    /// <summary>테스트용 BER 메시지로 <see cref="SnmpAgent.Process"/>의 Get/GetNext/GetBulk/Set, 커뮤니티 권한, 오류 상태와 잘못된 메시지 처리를 검사합니다.</summary>
    internal static class AgentCases
    {
        private const string Sys = "1.3.6.1.2.1.1.";
        private const string E = Store.E;
        private static readonly Random Random = new Random(1611);

        internal static SnmpAgent Agent(Store store, string write = "private")
        {
            return new SnmpAgent(store, new SnmpSystem("VagabondK SNMP", "node", "ops", "lab"), "public", write);
        }

        private static Wire.Reply Ask(SnmpAgent agent, byte[] message)
        {
            return Wire.Parse(agent.Process(message));
        }

        internal static void Run()
        {
            KnownVector();
            OidText();
            Construction();
            ByteLength();
            GetCases();
            NextCases();
            BulkCases();
            SetCases();
            Dropped();
            Malformed();
            TrapCases();
        }

        private static void KnownVector()
        {
            byte[] request = Hex("30 26 02 01 00 04 06 70 75 62 6c 69 63 a0 19 02 01 01 02 01 00 02 01 00 30 0e 30 0c 06 08 2b 06 01 02 01 01 01 00 05 00");
            byte[] reply = Agent(new Store()).Process(request);
            byte[] expected = Hex("30 34 02 01 00 04 06 70 75 62 6c 69 63 a2 27 02 01 01 02 01 00 02 01 00 30 1c 30 1a 06 08 2b 06 01 02 01 01 01 00 04 0e" +
                " 56 61 67 61 62 6f 6e 64 4b 20 53 4e 4d 50");
            Cases.Check(Same(reply, expected));
            Wire.LongForm = true;
            byte[] longForm = Wire.Message(0, "public", 0xa0, 1, 0, 0, Wire.Bind(Sys + "1.0"));
            Wire.LongForm = false;
            Cases.Check(Same(Agent(new Store()).Process(longForm), expected));
        }

        private static void OidText()
        {
            uint[] oid = SnmpOid.Parse("1.3.6.1.4.1.4294967295.0");
            Cases.Check(oid.Length == 8 && oid[6] == uint.MaxValue);
            Cases.Check(SnmpOid.Format(oid) == "1.3.6.1.4.1.4294967295.0");
            foreach (string bad in new[] { "", "1", "3.1", "1.40", "1..2", "1.2.", ".1.2", "1.4294967296", "1.a" })
                Cases.Throws<ArgumentException>(() => SnmpOid.Parse(bad));
            Cases.Throws<ArgumentNullException>(() => SnmpOid.Parse(null));
            Cases.Check(SnmpOid.Format(SnmpOid.Parse("2.999.1")) == "2.999.1");
        }

        private static void Construction()
        {
            var store = new Store();
            Cases.Throws<ArgumentNullException>(() => new SnmpAgent(null, new SnmpSystem("d", "n"), "public"));
            Cases.Throws<ArgumentException>(() => new SnmpAgent(store, new SnmpSystem("d", "n"), "with space"));
            Cases.Throws<ArgumentException>(() => new SnmpAgent(store, new SnmpSystem("d", "n"), new string('a', 33)));
            Cases.Throws<ArgumentException>(() => new SnmpAgent(store, new SnmpSystem("d", "n"), "public", ""));
            Cases.Throws<ArgumentException>(() => new SnmpSystem("bad\u0001", "n"));
            Cases.Throws<ArgumentException>(() => new SnmpSystem(new string('a', 256), "n"));
            Cases.Throws<ArgumentException>(() => Agent(new Store(new[] {
                new SnmpObject(SnmpOid.Parse(Sys + "9.0"), SnmpType.Integer32) }, new[] { SnmpValue.Integer32(0) })));
            Cases.Throws<ArgumentException>(() => Agent(new Store(new[] {
                new SnmpObject(SnmpOid.Parse("1.3.6.1.2"), SnmpType.Integer32) }, new[] { SnmpValue.Integer32(0) })));
            Cases.Throws<ArgumentException>(() => Agent(new Store(new[] {
                new SnmpObject(SnmpOid.Parse(E + "1"), SnmpType.Integer32),
                new SnmpObject(SnmpOid.Parse(E + "1.0"), SnmpType.Integer32) },
                new[] { SnmpValue.Integer32(0), SnmpValue.Integer32(0) })));
            Cases.Throws<ArgumentException>(() => Agent(new Store(new[] {
                new SnmpObject(SnmpOid.Parse(E + "1.0"), SnmpType.Integer32),
                new SnmpObject(SnmpOid.Parse(E + "1.0"), SnmpType.Integer32) },
                new[] { SnmpValue.Integer32(0), SnmpValue.Integer32(0) })));
            Cases.Throws<ArgumentException>(() => SnmpValue.OctetString(new byte[1025]));
            Cases.Throws<ArgumentException>(() => SnmpValue.IpAddress(new byte[3]));
            Cases.Check(Agent(new Store(new SnmpObject[0], new SnmpValue[0])) != null);
        }

        private static void ByteLength()
        {
            foreach (int length in new[] { 0, 1, 32, 512, 1024 })
            {
                var input = new byte[length];
                var value = SnmpValue.OctetString(input);
                Cases.Check(value.ByteLength == length && value.GetBytes().Length == length);
                if (length > 0)
                {
                    input[0] = 99;
                    byte[] copy = value.GetBytes();
                    Cases.Check(copy[0] == 0);
                    copy[0] = 1;
                    Cases.Check(value.GetBytes()[0] == 0 && value.ByteLength == length);
                }
            }
            Cases.Check(SnmpValue.IpAddress(new byte[] { 192, 0, 2, 1 }).ByteLength == 4);
            var others = new[] {
                SnmpValue.Integer32(1), SnmpValue.Counter32(1), SnmpValue.Gauge32(1),
                SnmpValue.TimeTicks(1), SnmpValue.Counter64(1), SnmpValue.ObjectIdentifier(new uint[] { 1, 3 })
            };
            foreach (var value in others)
                Cases.Throws<InvalidOperationException>(() => { int length = value.ByteLength; });
            var octets = SnmpValue.OctetString(new byte[512]);
            int total = 0;
            for (int i = 0; i < 100; i++) total += octets.ByteLength;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++) total += octets.ByteLength;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Cases.Check(total == 10100 * 512 && allocated == 0);
        }

        private static void GetCases()
        {
            var agent = Agent(new Store());
            for (int version = 0; version <= 1; version++)
            {
                var r = Ask(agent, Wire.Message(version, "public", 0xa0, 77, 0, 0,
                    Wire.Bind(Sys + "1.0"), Wire.Bind(Sys + "3.0"), Wire.Bind(Sys + "5.0"), Wire.Bind(Sys + "7.0"),
                    Wire.Bind(E + "1.0"), Wire.Bind(E + "2.0"), Wire.Bind(E + "3.0"), Wire.Bind(E + "4.0"),
                    Wire.Bind(E + "5.0"), Wire.Bind(E + "7.0"), Wire.Bind(E + "8.0"), Wire.Bind(Sys + "2.0")));
                Cases.Check(r.Version == version && r.Community == "public" && r.Pdu == 0xa2 && r.Id == 77 && r.Status == 0);
                Cases.Check(r.Bindings.Count == 12 && r.Bindings[0].Ascii == "VagabondK SNMP" && r.Bindings[1].Tag == 0x43);
                Cases.Check(r.Bindings[2].Ascii == "node" && r.Bindings[3].Signed == 72);
                Cases.Check(r.Bindings[4].Tag == 2 && r.Bindings[4].Signed == -5);
                Cases.Check(r.Bindings[5].Tag == 4 && r.Bindings[5].Ascii == "hello");
                Cases.Check(r.Bindings[6].Tag == 0x41 && r.Bindings[6].Unsigned == 4000000000 && r.Bindings[6].Content.Length == 5);
                Cases.Check(r.Bindings[7].Tag == 0x42 && r.Bindings[7].Unsigned == 7);
                Cases.Check(r.Bindings[8].Tag == 0x43 && r.Bindings[8].Unsigned == 100);
                Cases.Check(r.Bindings[9].Tag == 0x40 && Same(r.Bindings[9].Content, new byte[] { 192, 168, 0, 1 }));
                Cases.Check(r.Bindings[10].Tag == 6 && Wire.OidText(r.Bindings[10].Content) == "1.3.6");
                Cases.Check(r.Bindings[11].Tag == 6 && Wire.OidText(r.Bindings[11].Content) == "0.0");
            }

            var v2 = Ask(agent, Wire.Message(1, "public", 0xa0, 5, 0, 0,
                Wire.Bind(E + "6.0"), Wire.Bind(E + "1.1"), Wire.Bind("1.3.6.1.4.1.99.0"), Wire.Bind(E + "1"), Wire.Bind(E + "1.0.0")));
            Cases.Check(v2.Status == 0 && v2.Bindings[0].Tag == 0x46 && v2.Bindings[0].Unsigned == ulong.MaxValue);
            Cases.Check(v2.Bindings[1].Tag == 0x81 && v2.Bindings[1].Content.Length == 0);
            Cases.Check(v2.Bindings[2].Tag == 0x80 && v2.Bindings[3].Tag == 0x81 && v2.Bindings[4].Tag == 0x81);

            byte[] request = Wire.Message(0, "public", 0xa0, 6, 0, 0, Wire.Bind(E + "1.0"), Wire.Bind(E + "6.0"));
            var v1 = Ask(agent, request);
            Cases.Check(v1.Status == 2 && v1.Index == 2 && v1.Bindings.Count == 2 && v1.Bindings[1].Tag == 5);
            v1 = Ask(agent, Wire.Message(0, "public", 0xa0, 6, 0, 0, Wire.Bind(E + "1.0", Wire.Int(9))));
            Cases.Check(v1.Status == 0 && v1.Bindings[0].Signed == -5);
            v1 = Ask(agent, Wire.Message(0, "public", 0xa0, 6, 0, 0));
            Cases.Check(v1.Status == 0 && v1.Bindings.Count == 0);
        }

        private static string[] Walk(SnmpAgent agent, int version)
        {
            var names = new List<string>();
            string oid = "1.3";
            for (int guard = 0; guard < 100; guard++)
            {
                var r = Ask(agent, Wire.Message(version, "public", 0xa1, guard, 0, 0, Wire.Bind(oid)));
                if (version == 0 && r.Status == 2)
                {
                    Cases.Check(r.Index == 1 && r.Bindings[0].Oid == oid);
                    return names.ToArray();
                }
                Cases.Check(r.Status == 0 && r.Bindings.Count == 1);
                if (r.Bindings[0].Tag == 0x82)
                {
                    Cases.Check(version == 1 && r.Bindings[0].Oid == oid);
                    return names.ToArray();
                }
                oid = r.Bindings[0].Oid;
                names.Add(oid);
            }
            throw new InvalidOperationException("Walk did not finish.");
        }

        private static void NextCases()
        {
            var agent = Agent(new Store());
            string[] v2 = Walk(agent, 1), v1 = Walk(agent, 0);
            Cases.Check(v2.Length == 16 && v1.Length == 15);
            Cases.Check(v2[0] == Sys + "1.0" && v2[6] == Sys + "7.0" && v2[7] == "1.3.6.1.2.1.2.1.0" && v2[8] == E + "1.0" && v2[15] == E + "8.0");
            Cases.Check(Array.IndexOf(v1, E + "6.0") < 0 && Array.IndexOf(v2, E + "6.0") == 13);
            for (int i = 1; i < v2.Length; i++)
                Cases.Check(Compare(v2[i - 1], v2[i]) < 0);
            var r = Ask(agent, Wire.Message(1, "public", 0xa1, 1, 0, 0, Wire.Bind(E + "1"), Wire.Bind(E + "5.0"), Wire.Bind("0.0")));
            Cases.Check(r.Bindings[0].Oid == E + "1.0" && r.Bindings[1].Oid == E + "6.0" && r.Bindings[2].Oid == Sys + "1.0");
            r = Ask(agent, Wire.Message(0, "public", 0xa1, 1, 0, 0, Wire.Bind(E + "5.0")));
            Cases.Check(r.Bindings[0].Oid == E + "7.0");
        }

        private static void BulkCases()
        {
            var agent = Agent(new Store());
            var r = Ask(agent, Wire.Message(1, "public", 0xa5, 9, 1, 3, Wire.Bind("1.3"), Wire.Bind(E + "6.0"), Wire.Bind(E + "7.0")));
            Cases.Check(r.Status == 0 && r.Bindings.Count == 7);
            Cases.Check(r.Bindings[0].Oid == Sys + "1.0" && r.Bindings[1].Oid == E + "7.0" && r.Bindings[2].Oid == E + "8.0");
            Cases.Check(r.Bindings[3].Oid == E + "8.0" && r.Bindings[4].Tag == 0x82 && r.Bindings[5].Tag == 0x82 && r.Bindings[6].Tag == 0x82);
            r = Ask(agent, Wire.Message(1, "public", 0xa5, 9, 5, -4, Wire.Bind("1.3"), Wire.Bind(E + "1.0")));
            Cases.Check(r.Bindings.Count == 2 && r.Bindings[1].Oid == E + "2.0");
            r = Ask(agent, Wire.Message(1, "public", 0xa5, 9, -1, 1000, Wire.Bind("1.3")));
            Cases.Check(r.Bindings.Count == 17 && r.Bindings[16].Tag == 0x82);
            Cases.Throws<SnmpProtocolException>(() => agent.Process(Wire.Message(0, "public", 0xa5, 1, 0, 1, Wire.Bind("1.3"))));

            var objects = new SnmpObject[40];
            var values = new SnmpValue[40];
            for (int i = 0; i < 40; i++)
            {
                objects[i] = new SnmpObject(SnmpOid.Parse(E + "9." + i), SnmpType.OctetString, false, 1024);
                values[i] = SnmpValue.OctetString(new byte[200]);
            }
            var big = Agent(new Store(objects, values));
            byte[] request = Wire.Message(1, "public", 0xa5, 2, 0, 100, Wire.Bind(E + "9"));
            byte[] reply = big.Process(request);
            r = Wire.Parse(reply);
            Cases.Check(reply.Length <= SnmpAgent.MaximumMessage && r.Status == 0 && r.Bindings.Count >= 5 && r.Bindings.Count < 40);
            for (int i = 0; i < r.Bindings.Count; i++)
                Cases.Check(r.Bindings[i].Oid == E + "9." + i);
            values[0] = SnmpValue.OctetString(new byte[1024]);
            values[1] = values[0];
            var v2 = Ask(big, Wire.Message(1, "public", 0xa0, 3, 0, 0, Wire.Bind(E + "9.0"), Wire.Bind(E + "9.1")));
            Cases.Check(v2.Status == 1 && v2.Index == 0 && v2.Bindings.Count == 0);
            var v1 = Ask(big, Wire.Message(0, "public", 0xa0, 3, 0, 0, Wire.Bind(E + "9.0"), Wire.Bind(E + "9.1")));
            Cases.Check(v1.Status == 1 && v1.Bindings.Count == 2 && v1.Bindings[0].Tag == 5);
        }

        private static void SetCases()
        {
            var store = new Store();
            var agent = Agent(store);
            var r = Ask(agent, Wire.Message(1, "private", 0xa3, 4, 0, 0,
                Wire.Bind(E + "1.0", Wire.Int(-2147483648)), Wire.Bind(E + "2.0", Wire.Text("0123456789abcdef")),
                Wire.Bind(E + "4.0", Wire.UInt(uint.MaxValue, 0x42)), Wire.Bind(E + "5.0", Wire.UInt(5, 0x43)),
                Wire.Bind(E + "6.0", Wire.UInt(ulong.MaxValue - 1, 0x46)), Wire.Bind(E + "7.0", Wire.Tlv(0x40, new byte[] { 10, 0, 0, 2 })),
                Wire.Bind(E + "8.0", Wire.Oid("1.3.6.1.4.1"))));
            Cases.Check(r.Pdu == 0xa2 && r.Status == 0 && r.Bindings.Count == 7 && store.Writes == 7);
            Cases.Check(r.Bindings[0].Signed == int.MinValue && r.Bindings[4].Unsigned == ulong.MaxValue - 1);
            Cases.Check(store.Values[0].Integer == int.MinValue && store.Values[1].GetBytes().Length == 16);
            Cases.Check(store.Values[3].Unsigned == uint.MaxValue && store.Values[4].Unsigned == 5 && store.Values[5].Unsigned64 == ulong.MaxValue - 1);
            Cases.Check(store.Values[6].GetBytes()[3] == 2 && SnmpOid.Format(store.Values[7].GetObjectIdentifier()) == "1.3.6.1.4.1");
            Cases.Check(Ask(agent, Wire.Message(1, "public", 0xa0, 1, 0, 0, Wire.Bind(E + "1.0"))).Bindings[0].Signed == int.MinValue);

            SetError(agent, store, 1, "public", Wire.Bind(E + "1.0", Wire.Int(1)), 6, 1);
            SetError(agent, store, 0, "public", Wire.Bind(E + "1.0", Wire.Int(1)), 2, 1);
            SetError(agent, store, 1, "private", Wire.Bind(E + "1.0", Wire.Text("x")), 7, 2);
            SetError(agent, store, 1, "private", Wire.Bind(E + "2.0", Wire.Text("0123456789abcdefX")), 8, 2);
            SetError(agent, store, 1, "private", Wire.Bind(E + "1.0", Wire.Tlv(2, new byte[] { 0, 1 })), 9, 2);
            SetError(agent, store, 1, "private", Wire.Bind(E + "1.0", Wire.Int(666)), 10, 2);
            SetError(agent, store, 1, "private", Wire.Bind(E + "1.0", Wire.Tlv(2, new byte[] { 0xff, 0x80 })), 9, 2);
            SetError(agent, store, 1, "private", Wire.Bind(E + "4.0", Wire.Tlv(0x42, new byte[] { 0, 1 })), 9, 2);
            SetError(agent, store, 1, "private", Wire.Bind(E + "5.0", Wire.Tlv(0x43, new byte[] { 0, 0x7f, 0, 0, 0 })), 9, 2);
            SetError(agent, store, 1, "private", Wire.Bind(E + "6.0", Wire.Tlv(0x46, new byte[] { 0, 0x7f })), 9, 2);
            SetError(agent, store, 1, "private", Wire.Bind(E + "8.0", Wire.Int(1)), 7, 2);
            SetError(agent, store, 1, "private", Wire.Bind(E + "7.0", Wire.Text("abcd")), 7, 2);
            var minimal = Ask(agent, Wire.Message(1, "private", 0xa3, 5, 0, 0,
                Wire.Bind(E + "1.0", Wire.Tlv(2, new byte[] { 0xff, 0x7f })), Wire.Bind(E + "4.0", Wire.Tlv(0x42, new byte[] { 0, 0x80 })),
                Wire.Bind(E + "6.0", Wire.Tlv(0x46, new byte[] { 0, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff }))));
            Cases.Check(minimal.Status == 0 && store.Values[0].Integer == -129 && store.Values[3].Unsigned == 128 &&
                store.Values[5].Unsigned64 == ulong.MaxValue);
            SetError(agent, store, 1, "private", Wire.Bind(E + "6.0", Wire.Tlv(0x46, new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 0 })), 10, 2);
            SetError(agent, store, 1, "private", Wire.Bind(E + "4.0", Wire.UInt(1UL << 32, 0x42)), 10, 2);
            SetError(agent, store, 1, "private", Wire.Bind(E + "3.0", Wire.UInt(1, 0x41)), 17, 2);
            SetError(agent, store, 1, "private", Wire.Bind(Sys + "5.0", Wire.Text("x")), 17, 2);
            SetError(agent, store, 1, "private", Wire.Bind(E + "99.0", Wire.Int(1)), 11, 2);
            SetError(agent, store, 1, "private", Wire.Bind(E + "1.0", Wire.Null()), 7, 2);
            SetError(agent, store, 0, "private", Wire.Bind(E + "1.0", Wire.Text("x")), 3, 2);
            SetError(agent, store, 0, "private", Wire.Bind(E + "1.0", Wire.Int(666)), 3, 2);
            SetError(agent, store, 0, "private", Wire.Bind(E + "3.0", Wire.UInt(1, 0x41)), 2, 2);
            SetError(agent, store, 0, "private", Wire.Bind(E + "99.0", Wire.Int(1)), 2, 2);
            SetError(agent, store, 0, "private", Wire.Bind(E + "6.0", Wire.UInt(1, 0x46)), 2, 2);

            var readOnly = Agent(new Store(), null);
            Cases.Check(readOnly.Process(Wire.Message(1, "private", 0xa0, 1, 0, 0, Wire.Bind(E + "1.0"))) == null);
            Cases.Check(Ask(readOnly, Wire.Message(1, "public", 0xa3, 1, 0, 0, Wire.Bind(E + "1.0", Wire.Int(1)))).Status == 6);
            var same = Agent(new Store(), "public");
            same = new SnmpAgent(new Store(), new SnmpSystem("d", "n"), "public", "public");
            Cases.Check(Ask(same, Wire.Message(1, "public", 0xa3, 1, 0, 0, Wire.Bind(E + "1.0", Wire.Int(1)))).Status == 0);
            Cases.Check(Ask(agent, Wire.Message(1, "private", 0xa0, 1, 0, 0, Wire.Bind(E + "1.0"))).Status == 0);
        }

        private static void SetError(SnmpAgent agent, Store store, int version, string community, byte[] bad, int status, int index)
        {
            int before = store.Writes;
            int value = store.Values[0].Integer;
            byte[] good = Wire.Bind(E + "1.0", Wire.Int(value + 1));
            byte[] request = index == 1 ? Wire.Message(version, community, 0xa3, 3, 0, 0, bad, good)
                : Wire.Message(version, community, 0xa3, 3, 0, 0, good, bad);
            var r = Ask(agent, request);
            if (r.Status != status || r.Index != index)
                Console.Error.WriteLine("Set v" + version + " status " + r.Status + "/" + r.Index + " expected " + status + "/" + index);
            Cases.Check(r.Pdu == 0xa2 && r.Id == 3 && r.Status == status && r.Index == index && r.Bindings.Count == 2);
            Cases.Check(store.Writes == before && store.Values[0].Integer == value);
            byte[] list = ListOf(request), echoed = ListOf(agent.Process(request));
            Cases.Check(Same(list, echoed));
        }

        private static byte[] ListOf(byte[] message)
        {
            // 짧은 길이 형식 테스트 벡터에서는 varbind 목록이 메시지의 끝부분입니다.
            int p = message.Length;
            var r = Wire.Parse(message);
            int size = 2;
            foreach (var bind in r.Bindings)
                size += 2 + 2 + Wire.OidContent(bind.Oid).Length + 2 + bind.Content.Length;
            var result = new byte[size];
            Array.Copy(message, p - size, result, 0, size);
            return result;
        }

        private static void Dropped()
        {
            var agent = Agent(new Store());
            byte[] get = Wire.Bind(Sys + "1.0");
            Cases.Check(agent.Process(Wire.Message(1, "Public", 0xa0, 1, 0, 0, get)) == null);
            Cases.Check(agent.Process(Wire.Message(1, "publi", 0xa0, 1, 0, 0, get)) == null);
            Cases.Check(agent.Process(Wire.Message(3, "public", 0xa0, 1, 0, 0, get)) == null);
            Cases.Check(agent.Process(Wire.Message(2, "public", 0xa0, 1, 0, 0, get)) == null);
            foreach (int pdu in new[] { 0xa2, 0xa6, 0xa7, 0xa8 })
                Cases.Check(agent.Process(Wire.Message(1, "public", pdu, 1, 0, 0, get)) == null);
            Cases.Check(agent.Process(Wire.Tlv(0x30, Wire.Int(0), Wire.Text("public"),
                Wire.Tlv(0xa4, Wire.Oid("1.3.6.1.4.1"), Wire.Tlv(0x40, new byte[4]), Wire.Int(0), Wire.Int(0), Wire.Tlv(0x43, new byte[] { 0 }), Wire.Tlv(0x30)))) == null);
            Cases.Check(agent.Process(Wire.Message(1, "private", 0xa0, 1, 0, 0, get)) != null);
        }

        internal static void Malformed()
        {
            var agent = Agent(new Store());
            Cases.Throws<ArgumentNullException>(() => agent.Process(null));
            byte[] valid = Wire.Message(1, "private", 0xa3, int.MaxValue, 0, 0,
                Wire.Bind(E + "2.0", Wire.Text("abc")), Wire.Bind(E + "6.0", Wire.UInt(1, 0x46)), Wire.Bind(E + "8.0", Wire.Oid("1.3.6.1.4.1.4294967295")));
            Cases.Check(Wire.Parse(agent.Process(valid)).Id == int.MaxValue);
            for (int length = 0; length < valid.Length; length++)
            {
                var cut = new byte[length];
                Array.Copy(valid, cut, length);
                Rejects(agent, cut);
            }
            var trailing = new byte[valid.Length + 1];
            Array.Copy(valid, trailing, valid.Length);
            Rejects(agent, trailing);
            Rejects(agent, new byte[SnmpAgent.MaximumMessage + 1]);
            Rejects(agent, Hex("30 80 02 01 00 04 06 70 75 62 6c 69 63 a0 0b 02 01 01 02 01 00 02 01 00 30 00 00 00"));
            Rejects(agent, Wire.Message(1, "public", 0xa0, 1, 0, 0, Wire.Tlv(0x30, Wire.Tlv(6, new byte[] { 0x2b, 0x80, 1 }), Wire.Null())));
            Rejects(agent, Wire.Message(1, "public", 0xa0, 1, 0, 0, Wire.Tlv(0x30, Wire.Tlv(6, new byte[] { 0x2b, 0x90, 0x80, 0x80, 0x80, 0 }), Wire.Null())));
            Rejects(agent, Wire.Message(1, "public", 0xa0, 1, 0, 0, Wire.Tlv(0x30, Wire.Tlv(6), Wire.Null())));
            Rejects(agent, Wire.Message(1, "public", 0xa0, 1, 0, 0, Wire.Tlv(0x30, Wire.Tlv(6, new byte[] { 0x2b, 0x86 }), Wire.Null())));
            Rejects(agent, Wire.Message(1, "public", 0xa0, 1, 0, 0, Wire.Tlv(0x30, Wire.Oid(Sys + "1.0"))));
            Rejects(agent, Wire.Message(1, "public", 0xa0, 1, 0, 0, Wire.Tlv(0x31, Wire.Oid(Sys + "1.0"), Wire.Null())));
            Rejects(agent, Wire.Tlv(0x30, Wire.Int(1), Wire.Text("public"), Wire.Tlv(0xa0, Wire.Int(1), Wire.Int(0), Wire.Int(0), Wire.Tlv(0x30), Wire.Null())));
            Rejects(agent, Wire.Tlv(0x30, Wire.Int(1), Wire.Text("public"), Wire.Tlv(0xa0, Wire.Tlv(2, new byte[5]), Wire.Int(0), Wire.Int(0), Wire.Tlv(0x30))));
            Rejects(agent, Wire.Tlv(0x30, Wire.Int(1), Wire.Text("public"), Wire.Tlv(0xaf, Wire.Int(1), Wire.Int(0), Wire.Int(0), Wire.Tlv(0x30))));
            Rejects(agent, Hex("30 84 00 00 00 05 02 01 01 04 00"));
            foreach (byte[] value in new[] { Wire.Tlv(0xc3, new byte[] { 1 }), Wire.Tlv(0x41), Wire.Tlv(0x41, new byte[6]), Wire.Tlv(0x40, new byte[3]),
                Wire.Tlv(5, new byte[] { 0 }), Wire.Tlv(0x80, new byte[] { 0 }), Wire.Tlv(2), Wire.Tlv(2, new byte[5]), Wire.Tlv(0x46, new byte[10]),
                Wire.Tlv(6, new byte[] { 0x2b, 0x80, 1 }), Wire.Tlv(0x45, new byte[1]) })
            {
                Rejects(agent, Wire.Message(1, "public", 0xa0, 1, 0, 0, Wire.Bind(E + "1.0", value)));
                Rejects(agent, Wire.Message(1, "private", 0xa3, 1, 0, 0, Wire.Bind(E + "1.0", value)));
            }
            Cases.Check(Ask(agent, Wire.Message(1, "public", 0xa0, 1, 0, 0, Wire.Bind(E + "1.0", Wire.Tlv(0x44, new byte[] { 9 })))).Status == 0);

            var binds = new byte[129][];
            for (int i = 0; i < binds.Length; i++)
                binds[i] = Wire.Bind("1.3");
            byte[] many = Wire.Message(1, "public", 0xa0, 1, 0, 0, binds);
            if (many.Length <= SnmpAgent.MaximumMessage)
            {
                byte[] reply = null;
                try
                {
                    reply = agent.Process(many);
                }
                catch (SnmpProtocolException) { }
                Cases.Check(reply == null || Wire.Parse(reply).Status == 1 || Wire.Parse(reply).Bindings.Count == 129);
            }

            var fuzzStore = new Store();
            var fuzz = Agent(fuzzStore);
            for (int round = 0; round < 20000; round++)
            {
                byte[] mutated = (byte[])valid.Clone();
                int edits = 1 + Random.Next(4);
                for (int i = 0; i < edits; i++)
                    mutated[Random.Next(mutated.Length)] = (byte)Random.Next(256);
                byte[] reply = null;
                try
                {
                    reply = fuzz.Process(mutated);
                }
                catch (SnmpProtocolException)
                {
                    continue;
                }
                if (reply == null)
                    continue;
                Cases.Check(reply.Length <= SnmpAgent.MaximumMessage);
                Wire.Parse(reply);
            }
        }

        private static void Rejects(SnmpAgent agent, byte[] message)
        {
            byte[] reply = null;
            bool rejected = false;
            try
            {
                reply = agent.Process(message);
            }
            catch (SnmpProtocolException)
            {
                rejected = true;
            }
            Cases.Check(rejected || reply == null);
        }

        private static void TrapCases()
        {
            var store = new Store();
            var agent = Agent(store);
            byte[] trap = agent.CreateTrap("traps", SnmpOid.Parse(E + "100"), new[] { 0, 1, 5 });
            var r = Wire.Parse(trap);
            Cases.Check(r.Version == 1 && r.Community == "traps" && r.Pdu == 0xa7 && r.Status == 0 && r.Index == 0);
            Cases.Check(r.Bindings.Count == 5 && r.Bindings[0].Oid == Sys + "3.0" && r.Bindings[0].Tag == 0x43);
            Cases.Check(r.Bindings[1].Oid == "1.3.6.1.6.3.1.1.4.1.0" && Wire.OidText(r.Bindings[1].Content) == E + "100");
            Cases.Check(r.Bindings[2].Oid == E + "1.0" && r.Bindings[3].Ascii == "hello" && r.Bindings[4].Tag == 0x46);
            int id = r.Id;
            Cases.Check(Wire.Parse(agent.CreateTrap("traps", SnmpOid.Parse(E + "100"), null)).Id == id + 1);
            Cases.Throws<ArgumentOutOfRangeException>(() => agent.CreateTrap("traps", SnmpOid.Parse(E + "100"), new[] { 9 }));
            Cases.Throws<ArgumentException>(() => agent.CreateTrap("bad community", SnmpOid.Parse(E + "100"), null));
            Cases.Throws<ArgumentNullException>(() => agent.CreateTrap("traps", null, null));
            var objects = new SnmpObject[10];
            var values = new SnmpValue[10];
            var indexes = new int[10];
            for (int i = 0; i < 10; i++)
            {
                objects[i] = new SnmpObject(SnmpOid.Parse(E + "9." + i), SnmpType.OctetString, false, 1024);
                values[i] = SnmpValue.OctetString(new byte[200]);
                indexes[i] = i;
            }
            var big = Agent(new Store(objects, values));
            Cases.Throws<ArgumentException>(() => big.CreateTrap("traps", SnmpOid.Parse(E + "100"), indexes));
        }

        internal static int Compare(string a, string b)
        {
            uint[] x = SnmpOid.Parse(a), y = SnmpOid.Parse(b);
            for (int i = 0; i < x.Length && i < y.Length; i++)
                if (x[i] != y[i])
                    return x[i] < y[i] ? -1 : 1;
            return x.Length.CompareTo(y.Length);
        }

        internal static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i])
                    return false;
            return true;
        }

        internal static byte[] Hex(string text)
        {
            string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var bytes = new byte[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                bytes[i] = Convert.ToByte(parts[i], 16);
            return bytes;
        }
    }
}
