using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Lextm.SharpSnmpLib.Security;
using VagabondK.nF.SNMP.UDP;
using VagabondK.nF.SNMP.UDPHost;

namespace VagabondK.nF.SNMP.Interop
{
    /// <summary>독립 SNMP 관리자(SharpSnmpLib)로 에이전트를 교차 검증하는 상호 운용 테스트 진입점입니다. 정상 요청/응답은 SharpSnmpLib가 인코딩·디코딩하고, 손상·초과 길이 데이터그램은 별도 바이트 조작으로 주입합니다.</summary>
    /// <remarks>
    /// 기본 실행은 UDPHost 샘플의 에이전트를 루프백 UDP에 띄워 v1/v2c 적합성, 잘못된 패킷 복구, 무작위 Set/Get(<c>--stress N</c>, <c>--seed S</c>)을 검사합니다.
    /// <c>--snmp-hardware &lt;보드 IPv4&gt; &lt;반복&gt;</c>은 UDPHost 샘플을 실행 중인 보드의 UDP 161을 대상으로 하며, 배포 후 재부팅·Wi-Fi 연결을 최대 90초 기다립니다.
    /// 보드 값이 바뀌므로 운영 장비에는 사용하지 마십시오.
    /// </remarks>
    internal static class Program
    {
        private const string E = DemoStore.Enterprise + ".";
        private const string Sys = "1.3.6.1.2.1.1.";
        private static readonly OctetString Public = new OctetString("public"), Private = new OctetString("private");
        private static IPEndPoint _target;
        private static int _timeout = 2000, _checks, _requestId = 1000;
        private static Random _random;

        private static int Main(string[] args)
        {
            try
            {
                int stress = 64, seed = 2611;
                string hardware = null;
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "--stress" && i + 1 < args.Length)
                        stress = int.Parse(args[++i]);
                    else if (args[i] == "--seed" && i + 1 < args.Length)
                        seed = int.Parse(args[++i]);
                    else if (args[i] == "--snmp-hardware" && i + 2 < args.Length)
                    {
                        hardware = args[++i];
                        stress = int.Parse(args[++i]);
                    }
                    else
                        throw new ArgumentException("Usage: [--stress N] [--seed S] | --snmp-hardware IP N");
                }
                _random = new Random(seed);
                if (hardware != null)
                {
                    _target = new IPEndPoint(IPAddress.Parse(hardware), 161);
                    WaitReady();
                    Conformance(true);
                    MalformedRecovery(true);
                    Stress(stress);
                    Console.WriteLine("SNMP MCU PASS: " + _checks + " checks, " + stress + " random Set/Get rounds against " + hardware + ".");
                    return 0;
                }
                RunLocal(stress);
                Console.WriteLine("PROTOCOL VERIFICATION PASS: " + _checks + " checks, seed " + seed + ", no mismatches in this run.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
        }

        // 보드는 배포 후 재부팅되며 Wi-Fi가 연결된 뒤에야 에이전트가 UDP 161을 바인딩합니다.
        private static void WaitReady()
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(90);
            while (true)
            {
                try
                {
                    Get(VersionCode.V2, "1.3.6.1.2.1.1.1.0");
                    return;
                }
                catch (Lextm.SharpSnmpLib.Messaging.TimeoutException) when (DateTime.UtcNow < deadline) { }
                catch (System.Net.Sockets.SocketException) when (DateTime.UtcNow < deadline)
                {
                    System.Threading.Thread.Sleep(1000);
                }
            }
        }

        private static void Check(bool condition, string what)
        {
            _checks++;
            if (!condition)
                throw new InvalidOperationException("Mismatch: " + what);
        }

        private static void RunLocal(int stress)
        {
            Check(new DemoStore().Count == 8, "default demo MIB remains eight scalars");
            var store = new DemoStore(true);
            Check(store.Count == 9 && !store.GetObject(8).Writable,
                "diagnostic MIB adds one read-only scalar");
            var agent = UDPHost.Program.CreateAgent(store);
            using (var server = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                server.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                _target = (IPEndPoint)server.LocalEndPoint;
                var session = new SnmpUdpSession(server, agent);
                bool stop = false;
                int rejected = 0;
                var thread = new Thread(() =>
                {
                    while (!Volatile.Read(ref stop))
                    {
                        lock (agent)
                        {
                            try
                            {
                                session.ProcessNext(20);
                            }
                            catch (SnmpProtocolException)
                            {
                                rejected++;
                                store.RejectedDatagrams++;
                            }
                        }
                    }
                });
                thread.Start();
                try
                {
                    Conformance(false);
                    MalformedRecovery();
                    lock (agent)
                        Trap(session, agent);
                    lock (agent)
                        InProcess(agent, stress * 64);
                    Stress(stress);
                    using (var raw = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                    {
                        raw.SendTo(new byte[] { 0x30, 0x02, 0x02, 0x00 }, _target);
                        Thread.Sleep(200);
                    }
                    Check(Get(VersionCode.V2, Sys + "5.0")[0].Data.ToString() == "vagabondk-nf", "service recovers after malformed datagram");
                }
                finally
                {
                    Volatile.Write(ref stop, true);
                    thread.Join();
                }
                Check(rejected >= 1, "malformed datagram reported");
            }
        }

        private static void Conformance(bool hardware)
        {
            Reset();
            foreach (VersionCode version in new[] { VersionCode.V1, VersionCode.V2 })
            {
                IList<Variable> system = Get(version, Sys + "1.0", Sys + "2.0", Sys + "3.0", Sys + "4.0", Sys + "5.0", Sys + "6.0", Sys + "7.0");
                Check(system[0].Data.ToString() == UDPHost.Program.Description, "sysDescr");
                Check(system[1].Data.Equals(new ObjectIdentifier("1.3.6.1.4.1.32473.2")), "sysObjectID");
                Check(system[2].Data is TimeTicks, "sysUpTime type");
                Check(system[3].Data.ToString() == "operator@example.com" && system[4].Data.ToString() == "vagabondk-nf", "sysContact/sysName");
                Check(system[5].Data.ToString() == "lab" && ((Integer32)system[6].Data).ToInt32() == 72, "sysLocation/sysServices");
            }

            var v1 = new List<Variable>();
            Messenger.Walk(VersionCode.V1, _target, Public, new ObjectIdentifier("1.3.6.1"), v1, _timeout, WalkMode.WithinSubtree);
            Check(v1.Count == 15, "v1 diagnostic walk count skips Counter64: " + v1.Count);
            var walk = new List<Variable>();
            Messenger.Walk(VersionCode.V2, _target, Public, new ObjectIdentifier("1.3.6.1"), walk, _timeout, WalkMode.WithinSubtree);
            var bulk = new List<Variable>();
            Messenger.BulkWalk(VersionCode.V2, _target, Public, OctetString.Empty, new ObjectIdentifier("1.3.6.1"), bulk, _timeout, 4,
                WalkMode.WithinSubtree, null, null);
            Check(walk.Count == 16 && bulk.Count == 16, "v2c diagnostic walk/bulk count " + walk.Count + "/" + bulk.Count);
            for (int i = 0; i < 16; i++)
            {
                Check(walk[i].Id == bulk[i].Id, "walk order");
                if (walk[i].Id.ToString() != Sys + "3.0")
                    Check(walk[i].Data.Equals(bulk[i].Data), "walk data");
            }
            Check(walk[7].Id.ToString() == E + "1.0" && walk[15].Id.ToString() == E + "9.0", "enterprise objects sorted");
            Check(walk[12].Data is Counter64 && ((Counter64)walk[12].Data).ToUInt64() == 0x100000000UL, "Counter64");

            ISnmpPdu pdu = Exchange(new GetBulkRequestMessage(NextId(), VersionCode.V2, Public, 1, 3, Vars(Sys + "7.0", E + "6.0", E + "7.0")));
            Check(pdu.ErrorStatus.ToInt32() == 0 && pdu.Variables.Count == 7, "GetBulk shape");
            Check(pdu.Variables[0].Id.ToString() == E + "1.0" && pdu.Variables[1].Id.ToString() == E + "7.0", "GetBulk first row");
            Check(pdu.Variables[3].Id.ToString() == E + "8.0" && pdu.Variables[4].Id.ToString() == E + "9.0" &&
                pdu.Variables[6].Data is EndOfMibView, "GetBulk diagnostic end");

            pdu = Exchange(new GetRequestMessage(NextId(), VersionCode.V2, Public, Vars(E + "99.0", E + "1.1", E + "1.0")));
            Check(pdu.Variables[0].Data is NoSuchObject && pdu.Variables[1].Data is NoSuchInstance && pdu.Variables[2].Data is Integer32, "v2c exceptions");
            pdu = Exchange(new GetNextRequestMessage(NextId(), VersionCode.V2, Public, Vars(E + "9.0")));
            Check(pdu.Variables[0].Data is EndOfMibView, "endOfMibView");
            pdu = Exchange(new GetRequestMessage(NextId(), VersionCode.V1, Public, Vars(E + "1.0", E + "6.0")));
            Check(pdu.ErrorStatus.ToInt32() == (int)ErrorCode.NoSuchName && pdu.ErrorIndex.ToInt32() == 2, "v1 Counter64 hidden");
            pdu = Exchange(new GetNextRequestMessage(NextId(), VersionCode.V1, Public, Vars(E + "9.0")));
            Check(pdu.ErrorStatus.ToInt32() == (int)ErrorCode.NoSuchName && pdu.ErrorIndex.ToInt32() == 1, "v1 end of MIB");
            var many = new List<Variable>();
            for (int i = 0; i < 60; i++)
                many.Add(new Variable(new ObjectIdentifier(Sys + "1.0")));
            pdu = Exchange(new GetRequestMessage(NextId(), VersionCode.V2, Public, many));
            Check(pdu.ErrorStatus.ToInt32() == (int)ErrorCode.TooBig && pdu.Variables.Count == 0, "v2c tooBig");

            SetAll();
            uint before = Accepted();
            SetError(VersionCode.V2, Public, ErrorCode.NoAccess, 1, new Variable(new ObjectIdentifier(E + "1.0"), new Integer32(1)));
            SetError(VersionCode.V2, Private, ErrorCode.WrongType, 2, Good(), new Variable(new ObjectIdentifier(E + "1.0"), new OctetString("1")));
            SetError(VersionCode.V2, Private, ErrorCode.WrongLength, 2, Good(), new Variable(new ObjectIdentifier(E + "2.0"), new OctetString(new string('x', 33))));
            SetError(VersionCode.V2, Private, ErrorCode.WrongType, 2, Good(), new Variable(new ObjectIdentifier(E + "7.0"), new OctetString(new byte[4])));
            SetError(VersionCode.V2, Private, ErrorCode.WrongValue, 2, Good(), new Variable(new ObjectIdentifier(E + "1.0"), new Integer32(100001)));
            SetError(VersionCode.V2, Private, ErrorCode.WrongValue, 2, Good(), new Variable(new ObjectIdentifier(E + "4.0"), new Gauge32(101)));
            SetError(VersionCode.V2, Private, ErrorCode.NotWritable, 2, Good(), new Variable(new ObjectIdentifier(E + "3.0"), new Counter32(1)));
            SetError(VersionCode.V2, Private, ErrorCode.NotWritable, 2, Good(), new Variable(new ObjectIdentifier(Sys + "5.0"), new OctetString("x")));
            SetError(VersionCode.V2, Private, ErrorCode.NoCreation, 2, Good(), new Variable(new ObjectIdentifier(E + "99.0"), new Integer32(1)));
            SetError(VersionCode.V1, Public, ErrorCode.NoSuchName, 1, new Variable(new ObjectIdentifier(E + "1.0"), new Integer32(1)));
            SetError(VersionCode.V1, Private, ErrorCode.BadValue, 2, Good(), new Variable(new ObjectIdentifier(E + "1.0"), new OctetString("1")));
            SetError(VersionCode.V1, Private, ErrorCode.BadValue, 2, Good(), new Variable(new ObjectIdentifier(E + "1.0"), new Integer32(-100001)));
            SetError(VersionCode.V1, Private, ErrorCode.NoSuchName, 2, Good(), new Variable(new ObjectIdentifier(E + "3.0"), new Counter32(1)));
            SetError(VersionCode.V1, Private, ErrorCode.NoSuchName, 2, Good(), new Variable(new ObjectIdentifier(E + "6.0"), new Counter64(1)));
            Check(Accepted() == before, "failed Sets are atomic");
            Check(Get(VersionCode.V2, E + "1.0")[0].Data.Equals(new Integer32(-77)), "value unchanged after failed Set");

            Silent(new GetRequestMessage(NextId(), VersionCode.V2, new OctetString("wrong"), Vars(Sys + "1.0")), "wrong community");
            Silent(new GetRequestMessage(NextId(), VersionCode.V2, new OctetString("Public"), Vars(Sys + "1.0")), "community case");
            Check(Get(VersionCode.V2, Sys + "1.0")[0].Data.ToString() == UDPHost.Program.Description, "agent still serving");
            Reset();
        }

        private static Variable Good()
        {
            return new Variable(new ObjectIdentifier(E + "1.0"), new Integer32(31337));
        }

        private static void SetAll()
        {
            uint before = Accepted();
            var values = new List<Variable> {
                new Variable(new ObjectIdentifier(E + "1.0"), new Integer32(-77)),
                new Variable(new ObjectIdentifier(E + "2.0"), new OctetString(new byte[] { 0, 0xff, 0x41, 0x80 })),
                new Variable(new ObjectIdentifier(E + "4.0"), new Gauge32(100)),
                new Variable(new ObjectIdentifier(E + "5.0"), new TimeTicks(uint.MaxValue)),
                new Variable(new ObjectIdentifier(E + "6.0"), new Counter64(ulong.MaxValue)),
                new Variable(new ObjectIdentifier(E + "7.0"), new IP(new byte[] { 10, 1, 2, 3 })),
                new Variable(new ObjectIdentifier(E + "8.0"), new ObjectIdentifier("1.3.6.1.4.1.4294967295.0"))
            };
            ISnmpPdu pdu = Exchange(new SetRequestMessage(NextId(), VersionCode.V2, Private, values));
            Check(pdu.ErrorStatus.ToInt32() == 0 && pdu.Variables.Count == 7, "Set all types");
            IList<Variable> read = Get(VersionCode.V2, E + "1.0", E + "2.0", E + "4.0", E + "5.0", E + "6.0", E + "7.0", E + "8.0");
            for (int i = 0; i < values.Count; i++)
            {
                Check(pdu.Variables[i].Data.Equals(values[i].Data), "Set echo " + i);
                Check(read[i].Data.Equals(values[i].Data), "Get after Set " + i);
            }
            Check(Accepted() == before + 7, "accepted write counter");
        }

        private static void Reset()
        {
            ISnmpPdu pdu = Exchange(new SetRequestMessage(NextId(), VersionCode.V2, Private, new List<Variable> {
                new Variable(new ObjectIdentifier(E + "1.0"), new Integer32(250)),
                new Variable(new ObjectIdentifier(E + "2.0"), new OctetString("pump-1")),
                new Variable(new ObjectIdentifier(E + "4.0"), new Gauge32(50)),
                new Variable(new ObjectIdentifier(E + "5.0"), new TimeTicks(1500)),
                new Variable(new ObjectIdentifier(E + "6.0"), new Counter64(0x100000000UL)),
                new Variable(new ObjectIdentifier(E + "7.0"), new IP(new byte[] { 192, 0, 2, 10 })),
                new Variable(new ObjectIdentifier(E + "8.0"), new ObjectIdentifier("1.3.6.1.4.1.32473.2.1")) }));
            Check(pdu.ErrorStatus.ToInt32() == 0, "reset demo values");
        }

        private static uint Accepted()
        {
            return ((Counter32)Get(VersionCode.V2, E + "3.0")[0].Data).ToUInt32();
        }

        private static void MalformedRecovery(bool hardware = false)
        {
            Reset();
            byte[] valid = new SetRequestMessage(NextId(), VersionCode.V2, Private, new List<Variable> {
                new Variable(new ObjectIdentifier(E + "1.0"), new Integer32(31337)),
                new Variable(new ObjectIdentifier(E + "2.0"), new OctetString("must-not-apply"))
            }).ToBytes();
            Check(valid.Length < 128 && valid[1] == valid.Length - 2, "short BER fixture length");
            var cases = new List<KeyValuePair<string, byte[]>>();
            foreach (int length in new[] { 0, 1, 2, valid.Length / 2, valid.Length - 1 })
            {
                var cut = new byte[length];
                Array.Copy(valid, cut, length);
                cases.Add(new KeyValuePair<string, byte[]>("truncated-" + length, cut));
            }
            foreach (string kind in new[] { "sequence-tag", "indefinite-length", "excess-length", "version-tag" })
            {
                var bad = (byte[])valid.Clone();
                if (kind == "sequence-tag") bad[0] = 0x31;
                else if (kind == "indefinite-length") bad[1] = 0x80;
                else if (kind == "excess-length") bad[1]++;
                else bad[2] = 0x05;
                cases.Add(new KeyValuePair<string, byte[]>(kind, bad));
            }
            var trailing = new byte[valid.Length + 1];
            Array.Copy(valid, trailing, valid.Length);
            cases.Add(new KeyValuePair<string, byte[]>("trailing-byte", trailing));
            cases.Add(new KeyValuePair<string, byte[]>("oversized-1473", new byte[1473]));
            cases.Add(new KeyValuePair<string, byte[]>("oversized-4000", new byte[4000]));
            int previousTimeout = _timeout;
            _timeout = Math.Max(_timeout, 10000);
            int observed = 0, transportOnly = 0;
            try
            {
                using (var raw = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    raw.Bind(new IPEndPoint(IPAddress.Any, 0));
                    for (int i = 0; i < cases.Count; i++)
                    {
                        var bad = cases[i];
                        uint accepted = Accepted();
                        uint rejected = ((Counter32)Get(VersionCode.V2, E + "9.0")[0].Data).ToUInt32();
                        IList<Variable> before = Get(VersionCode.V2, E + "1.0", E + "2.0");
                        Check(raw.SendTo(bad.Value, _target) == bad.Value.Length, "complete fault datagram send");
                        // UDP 전송 성공만이 아니라 장치 쪽에서 실제로 거부했는지 기다립니다.
                        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
                        uint after;
                        do
                        {
                            after = ((Counter32)Get(VersionCode.V2, E + "9.0")[0].Data).ToUInt32();
                            if (after != rejected) break;
                            Thread.Sleep(20);
                        } while (DateTime.UtcNow < deadline);
                        bool oversizedHardware = hardware && bad.Value.Length > VagabondK.nF.SNMP.SnmpAgent.MaximumMessage;
                        if (oversizedHardware)
                            Check(after == rejected || after == rejected + 1,
                                "at most one oversized rejection: " + bad.Key + "; before=" + rejected + "; after=" + after);
                        else
                            Check(after == rejected + 1,
                                "exact malformed rejection: " + bad.Key + "; before=" + rejected + "; after=" + after);
                        Check(!raw.Poll(100000, SelectMode.SelectRead), "malformed request must not receive a response");
                        Check(Accepted() == accepted, "malformed SET must not queue any write: " + bad.Key);
                        IList<Variable> unchanged = Get(VersionCode.V2, E + "1.0", E + "2.0");
                        Check(unchanged[0].Data.Equals(before[0].Data) && unchanged[1].Data.Equals(before[1].Data),
                            "malformed SET must preserve both values: " + bad.Key);
                        int value = 500 + i;
                        var good = new SetRequestMessage(NextId(), VersionCode.V2, Private, new List<Variable> {
                            new Variable(new ObjectIdentifier(E + "1.0"), new Integer32(value))
                        });
                        Check(Exchange(good).ErrorStatus.ToInt32() == 0, "normal SET after rejection");
                        Check(((Integer32)Get(VersionCode.V2, E + "1.0")[0].Data).ToInt32() == value &&
                            Accepted() == accepted + 1, "normal GET and exactly one SET after rejection");
                        if (after == rejected)
                        {
                            transportOnly++;
                            Console.WriteLine("SNMP OVERSIZED TRANSPORT ONLY: " + bad.Key +
                                "; application rejection NOT OBSERVED (fragmented UDP may be dropped before the socket)" +
                                "; no response or writes; values unchanged; subsequent GET/SET PASS.");
                        }
                        else
                        {
                            observed++;
                            Console.WriteLine("SNMP MALFORMED RECOVERY PASS: " + bad.Key +
                                "; rejectedDelta=1; writesDelta=0; values unchanged; subsequent GET/SET PASS.");
                        }
                    }
                }
            }
            finally { _timeout = previousTimeout; }
            Reset();
            Console.WriteLine("SNMP MALFORMED RECOVERY COMPLETE: " + cases.Count + " UDP cases; " +
                observed + " application rejections verified; " + transportOnly +
                " transport-only probes.");
        }

        private static void SetError(VersionCode version, OctetString community, ErrorCode status, int index, params Variable[] binds)
        {
            ISnmpPdu pdu = Exchange(new SetRequestMessage(NextId(), version, community, binds));
            Check(pdu.ErrorStatus.ToInt32() == (int)status && pdu.ErrorIndex.ToInt32() == index,
                "Set " + status + " got " + pdu.ErrorStatus + "/" + pdu.ErrorIndex);
            Check(pdu.Variables.Count == binds.Length, "Set error echoes varbinds");
            for (int i = 0; i < binds.Length; i++)
                Check(pdu.Variables[i].Id == binds[i].Id && pdu.Variables[i].Data.Equals(binds[i].Data), "echo " + i);
        }

        private static void Silent(ISnmpMessage request, string what)
        {
            bool timedOut = false;
            try
            {
                request.GetResponse(500, _target);
            }
            catch (Lextm.SharpSnmpLib.Messaging.TimeoutException)
            {
                timedOut = true;
            }
            Check(timedOut, what + " must be dropped");
        }

        private static void Trap(SnmpUdpSession session, SnmpAgent agent)
        {
            using (var receiver = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                receiver.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                receiver.ReceiveTimeout = 2000;
                for (int round = 0; round < 2; round++)
                {
                    session.SendTrap(receiver.LocalEndPoint, "traps", SnmpOid.Parse(E + "100"), new[] { DemoStore.Setpoint, DemoStore.Total, DemoStore.Peer });
                    var buffer = new byte[2048];
                    int count = receiver.Receive(buffer);
                    var bytes = new byte[count];
                    Array.Copy(buffer, bytes, count);
                    var trap = MessageFactory.ParseMessages(bytes, new UserRegistry())[0] as TrapV2Message;
                    Check(trap != null && trap.Version == VersionCode.V2 && trap.Community().ToString() == "traps", "TrapV2 parsed");
                    Check(trap.Enterprise.ToString() == E + "100", "snmpTrapOID");
                    IList<Variable> list = trap.Variables();
                    Check(list.Count == 3 && list[0].Id.ToString() == E + "1.0" && list[1].Data is Counter64 && list[2].Data is IP, "trap objects " + list.Count);
                }
            }
        }

        private static readonly string[] Known = {
            "1.3", Sys + "1.0", Sys + "3.0", Sys + "7.0", Sys + "8", E + "1.0", E + "1", E + "1.1", E + "2.0", E + "3.0",
            E + "4.0", E + "5.0", E + "6.0", E + "7.0", E + "8.0", E + "9.0", "1.3.6.1.6.3.1.1.4.1.0", "2.39"
        };

        // 프로세스 안에서 특정 모델에 의존하지 않는 견고성 검사를 수행합니다. 모든 응답은 요청 ID가 일치하는 올바른 SharpSnmpLib Response여야 합니다.
        private static void InProcess(SnmpAgent agent, int rounds)
        {
            int replies = 0, rejected = 0;
            for (int round = 0; round < rounds; round++)
            {
                VersionCode version = _random.Next(2) == 0 ? VersionCode.V1 : VersionCode.V2;
                OctetString community = _random.Next(4) == 0 ? Public : Private;
                var binds = new List<Variable>();
                int count = _random.Next(6);
                for (int i = 0; i < count; i++)
                {
                    string name = Known[_random.Next(Known.Length)];
                    if (_random.Next(4) == 0)
                        name += "." + (uint)_random.Next();
                    binds.Add(new Variable(new ObjectIdentifier(name), RandomData()));
                }
                int id = _random.Next(int.MinValue, int.MaxValue);
                ISnmpMessage request;
                switch (_random.Next(version == VersionCode.V2 ? 4 : 3))
                {
                    case 0:
                        request = new GetRequestMessage(id, version, community, binds);
                        break;
                    case 1:
                        request = new GetNextRequestMessage(id, version, community, binds);
                        break;
                    case 2:
                        request = new SetRequestMessage(id, version, community, binds);
                        break;
                    default:
                        request = new GetBulkRequestMessage(id, version, community, _random.Next(0, binds.Count + 1), _random.Next(1, 40), binds);
                        break;
                }
                byte[] bytes = (byte[])request.ToBytes().Clone();
                if (_random.Next(3) == 0)
                    for (int i = 1 + _random.Next(3); i > 0; i--)
                        bytes[_random.Next(bytes.Length)] = (byte)_random.Next(256);
                byte[] reply;
                try
                {
                    reply = agent.Process(bytes);
                }
                catch (SnmpProtocolException)
                {
                    rejected++;
                    continue;
                }
                if (reply == null)
                    continue;
                replies++;
                Check(reply.Length <= SnmpAgent.MaximumMessage, "reply size");
                var response = MessageFactory.ParseMessages(reply, new UserRegistry())[0] as ResponseMessage;
                Check(response != null, "reply is a Response PDU");
                if (Equals(bytes, request.ToBytes()))
                {
                    Check(response.RequestId() == id && response.Version == version, "request id/version echoed");
                    int status = response.Pdu().ErrorStatus.ToInt32();
                    if (request is GetRequestMessage && status == 0)
                        Check(response.Pdu().Variables.Count == binds.Count, "Get count");
                    if (request is SetRequestMessage && community == Public)
                        Check(status == (version == VersionCode.V1 ? 2 : 6) || binds.Count == 0, "read-only Set");
                }
            }
            Check(replies > rounds / 3 && rejected > 0, "fuzz coverage " + replies + "/" + rejected);
        }

        private static bool Equals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i])
                    return false;
            return true;
        }

        private static ISnmpData RandomData()
        {
            switch (_random.Next(9))
            {
                case 0:
                    return new Integer32(_random.Next(int.MinValue, int.MaxValue));
                case 1:
                    {
                        var b = new byte[_random.Next(40)];
                        _random.NextBytes(b);
                        return new OctetString(b);
                    }
                case 2:
                    return new Counter32((uint)_random.Next());
                case 3:
                    return new Gauge32((uint)_random.Next(120));
                case 4:
                    return new TimeTicks((uint)_random.Next());
                case 5:
                    return new Counter64((ulong)_random.NextInt64());
                case 6:
                    {
                        var b = new byte[4];
                        _random.NextBytes(b);
                        return new IP(b);
                    }
                case 7:
                    return new ObjectIdentifier("1.3.6.1." + (uint)_random.Next());
                default:
                    return new Null();
            }
        }

        // 예상 데모 모델에 유효한 값을 무작위로 쓰고 Get과 GetNext로 다시 읽어 확인합니다.
        private static void Stress(int rounds)
        {
            for (int round = 0; round < rounds; round++)
            {
                int item = new[] { 1, 2, 4, 5, 6, 7, 8 }[_random.Next(7)];
                ISnmpData data;
                switch (item)
                {
                    case 1:
                        data = new Integer32(_random.Next(-100000, 100001));
                        break;
                    case 2:
                        {
                            var b = new byte[_random.Next(33)];
                            _random.NextBytes(b);
                            data = new OctetString(b);
                            break;
                        }
                    case 4:
                        data = new Gauge32((uint)_random.Next(101));
                        break;
                    case 5:
                        data = new TimeTicks((uint)_random.NextInt64(0, 1L << 32));
                        break;
                    case 6:
                        data = new Counter64((ulong)_random.NextInt64() * 2UL + (ulong)_random.Next(2));
                        break;
                    case 7:
                        {
                            var b = new byte[4];
                            _random.NextBytes(b);
                            data = new IP(b);
                            break;
                        }
                    default:
                        data = new ObjectIdentifier("1.3." + (uint)_random.Next() + "." + (uint)_random.Next());
                        break;
                }
                uint before = Accepted();
                var bind = new Variable(new ObjectIdentifier(E + item + ".0"), data);
                ISnmpPdu pdu = Exchange(new SetRequestMessage(NextId(), VersionCode.V2, Private, new List<Variable> { bind }));
                Check(pdu.ErrorStatus.ToInt32() == 0 && pdu.Variables[0].Data.Equals(data), "random Set " + item);
                Check(Get(VersionCode.V2, E + item + ".0")[0].Data.Equals(data), "random Get " + item);
                pdu = Exchange(new GetNextRequestMessage(NextId(), VersionCode.V2, Public, Vars(E + (item - 1) + ".0")));
                if (item != 4)
                    Check(pdu.Variables[0].Data.Equals(data), "random GetNext " + item);
                Check(Accepted() == before + 1, "random write counter");
            }
            Reset();
        }

        private static List<Variable> Vars(params string[] names)
        {
            var list = new List<Variable>();
            foreach (string name in names)
                list.Add(new Variable(new ObjectIdentifier(name)));
            return list;
        }

        private static IList<Variable> Get(VersionCode version, params string[] names)
        {
            ISnmpPdu pdu = Exchange(new GetRequestMessage(NextId(), version, Public, Vars(names)));
            Check(pdu.ErrorStatus.ToInt32() == 0 && pdu.Variables.Count == names.Length, "Get " + names[0] + " status " + pdu.ErrorStatus);
            for (int i = 0; i < names.Length; i++)
                Check(pdu.Variables[i].Id.ToString() == names[i], "Get order");
            return pdu.Variables;
        }

        private static int NextId()
        {
            return Interlocked.Increment(ref _requestId);
        }

        private static ISnmpPdu Exchange(ISnmpMessage request)
        {
            ISnmpMessage response = request.GetResponse(_timeout, _target);
            Check(response is ResponseMessage && response.RequestId() == request.RequestId() && response.Version == request.Version, "response header");
            return response.Pdu();
        }
    }
}
