using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using VagabondK.nF.SNMP;
using VagabondK.nF.SNMP.Mapping.Sample;
using VagabondK.nF.SNMP.UDP;

/// <summary>정상 요청/응답은 SharpSnmpLib로 교차 검증하고, 외부 구현이 지원하지 않는 OID 경계와 비정상 데이터그램은 별도 BER/바이트 주입으로 검사합니다.</summary>
internal static class HardwarePeer
{
    internal static void RunLocal()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var store = new DeviceData.SnmpStore(new DeviceData(), new object());
        var session = new SnmpUdpSession(socket,
            new SnmpAgent(store, new SnmpSystem("SNMP static mapping evaluation", "mapping-node"), "public", "private"));
        int stop = 0;
        var server = Task.Run(() => {
            while (Volatile.Read(ref stop) == 0) session.ProcessNext(20);
        });
        try { Run((IPEndPoint)socket.LocalEndPoint, 16); }
        finally { Volatile.Write(ref stop, 1); server.GetAwaiter().GetResult(); }
    }

    internal static void Run(string address, int iterations)
    {
        Run(new IPEndPoint(IPAddress.Parse(address), 1161), iterations);
    }

    internal static void Robustness(string address)
    {
        var target = new IPEndPoint(IPAddress.Parse(address), 1161);
        var read = new OctetString("public");
        var write = new OctetString("private");
        int id = 100000;
        Variable Bind(int index, ISnmpData data = null)
        {
            var oid = new ObjectIdentifier(DeviceData.Enterprise + "." + index + ".0");
            return data == null ? new Variable(oid) : new Variable(oid, data);
        }
        ISnmpPdu Get() => new GetRequestMessage(id++, VersionCode.V2, read,
            Enumerable.Range(1, 8).Select(i => Bind(i)).ToArray()).GetResponse(3000, target).Pdu();
        using var udp = new UdpClient();
        udp.Connect(address, 1162);
        udp.Client.ReceiveTimeout = 3000;
        uint Command(byte slot)
        {
            udp.Send(new byte[] { 83, 78, 82, 49, slot });
            IPEndPoint peer = new IPEndPoint(IPAddress.Any, 0);
            byte[] response = udp.Receive(ref peer);
            Program.Check(response.Length == 12 && response.Take(4).SequenceEqual(new byte[] { 83, 78, 82, 49 }) &&
                response[4] == slot && response[5] == 0x60 && response[6] == 0x0d, "Board ownership/concurrent snapshot checks completed.");
            return (uint)response[8] << 24 | (uint)response[9] << 16 | (uint)response[10] << 8 | response[11];
        }
        Command(0);
        var normalized = new SetRequestMessage(id++, VersionCode.V2, write,
            new[] { Bind(8, new ObjectIdentifier("2.39.42")) }).GetResponse(3000, target).Pdu();
        Program.Check(normalized.ErrorStatus.ToInt32() == 0, "Normalize raw OID boundary for external decoder.");
        var expected = Get().Variables.Select(v => v.Data).ToArray();
        for (byte slot = 1; slot <= 3; slot++)
        {
            Command(slot);
            int changed = slot == 1 ? 0 : slot == 2 ? 3 : 7;
            expected[changed] = slot == 1 ? new Integer32(-111) :
                slot == 2 ? new Gauge32(54321) : new ObjectIdentifier("2.39.77");
            Program.Check(Get().Variables.Select(v => v.Data).SequenceEqual(expected),
                "First/middle/last local property changes isolated in external GET.");
        }
        uint initial = Command(4), minimum = initial;
        for (int round = 0; round < 64; round++)
        {
            var rejected = new SetRequestMessage(id++, VersionCode.V2, write, new[] {
                Bind(1, new Integer32(round)), Bind(4, new Gauge32((uint)round)),
                Bind(6, new Counter64((ulong)round)), Bind(8, new Integer32(round))
            }).GetResponse(3000, target).Pdu();
            Program.Check(rejected.ErrorStatus.ToInt32() == 7 && rejected.ErrorIndex.ToInt32() == 4,
                "Late wrongType rejects complete SET.");
            Program.Check(Get().Variables.Select(v => v.Data).SequenceEqual(expected),
                "Every property preserved after late SET failure.");
            using (var malformed = new UdpClient())
            {
                malformed.Send(new byte[] { 0x30, 0x82, 0xff, 0xff }, target);
                malformed.Send(new byte[4096], target);
            }
            Program.Check(Get().Variables.Select(v => v.Data).SequenceEqual(expected), "Malformed datagram recovery preserves model.");
            minimum = Math.Min(minimum, Command(4));
        }
        Program.Check((long)initial - minimum <= 256, "Bounded mixed-fault retained heap growth.");
        Console.WriteLine("SNMP ROBUST PASS local-model/isolation/late-multi-SET/array-ownership/concurrent-snapshots/malformed-recovery; cycles=64 heapGrowth=" +
            ((long)initial - minimum));
    }

    private static void Run(IPEndPoint target, int iterations)
    {
        int id = 1;
        var read = new OctetString("public");
        var write = new OctetString("private");
        string e = DeviceData.Enterprise + ".";
        Variable Bind(int index, ISnmpData value = null)
        {
            var oid = new ObjectIdentifier(e + index + ".0");
            return value == null ? new Variable(oid) : new Variable(oid, value);
        }
        ISnmpPdu Ask(ISnmpMessage request)
        {
            var response = request.GetResponse(2000, target);
            Program.Check(response is ResponseMessage && response.RequestId() == request.RequestId() &&
                response.Version == request.Version, "SharpSnmpLib response identity.");
            return response.Pdu();
        }
        ISnmpPdu Get(params Variable[] binds) => Ask(new GetRequestMessage(id++, VersionCode.V2, read, binds));
        ISnmpPdu Set(params Variable[] binds) => Ask(new SetRequestMessage(id++, VersionCode.V2, write, binds));
        DateTime deadline = DateTime.UtcNow.AddSeconds(90);
        while (true)
        {
            try { Get(Bind(1)); break; }
            catch (Lextm.SharpSnmpLib.Messaging.TimeoutException) when (DateTime.UtcNow < deadline)
            { Console.WriteLine("Waiting for MCU network: SharpSnmpLib timeout."); }
            catch (SocketException error) when ((error.SocketErrorCode == SocketError.TimedOut ||
                error.SocketErrorCode == SocketError.ConnectionReset) && DateTime.UtcNow < deadline)
            { Console.WriteLine("Waiting for MCU network: " + error.SocketErrorCode); Thread.Sleep(1000); }
        }
        var all = Enumerable.Range(1, 8).Select(i => Bind(i)).ToArray();
        var initial = Get(all);
        Program.Check(initial.ErrorStatus.ToInt32() == 0 && initial.Variables.Count == 8 &&
            initial.Variables[0].Data.Equals(new Integer32(-250)) &&
            initial.Variables[2].Data.Equals(new Counter32(1)) &&
            initial.Variables[5].Data.Equals(new Counter64(0x100000000UL)), "Initial typed model.");
        var next = Ask(new GetNextRequestMessage(id++, VersionCode.V2, read, new[] { Bind(1) }));
        Program.Check(next.ErrorStatus.ToInt32() == 0 && next.Variables[0].Id == Bind(2).Id, "GETNEXT order.");
        var bulk = Ask(new GetBulkRequestMessage(id++, VersionCode.V2, read, 0, 8,
            new[] { new Variable(new ObjectIdentifier(DeviceData.Enterprise)) }));
        Program.Check(bulk.ErrorStatus.ToInt32() == 0 && bulk.Variables.Count == 8 &&
            bulk.Variables.Select(v => v.Id).SequenceEqual(all.Select(v => v.Id)), "GETBULK order.");
        var walk = new List<Variable>();
        Messenger.Walk(VersionCode.V2, target, read, new ObjectIdentifier(DeviceData.Enterprise), walk, 2000, WalkMode.WithinSubtree);
        Program.Check(walk.Select(v => v.Id).SequenceEqual(all.Select(v => v.Id)), "SharpSnmpLib WALK.");
        var v1 = Ask(new GetRequestMessage(id++, VersionCode.V1, read, new[] { Bind(6) }));
        Program.Check(v1.ErrorStatus.ToInt32() == 2 && v1.ErrorIndex.ToInt32() == 1, "v1 Counter64 hidden.");
        v1 = Ask(new GetRequestMessage(id++, VersionCode.V1, read, new[] { Bind(1) }));
        Program.Check(v1.ErrorStatus.ToInt32() == 0 && v1.Variables[0].Data.Equals(new Integer32(-250)), "v1 signed read.");
        for (int round = 0; round < iterations; round++)
        {
            int signed = round % 2 == 0 ? int.MinValue + round : int.MaxValue - round;
            uint unsigned = uint.MaxValue - (uint)round;
            ulong wide = ulong.MaxValue - (ulong)round;
            var expected = new[] {
                Bind(1, new Integer32(signed)), Bind(2, new OctetString(new byte[] { (byte)round, 0, 255 })),
                Bind(3, new Counter32(1)), Bind(4, new Gauge32(unsigned)), Bind(5, new TimeTicks(unsigned)),
                Bind(6, new Counter64(wide)), Bind(7, new IP(new byte[] { 198, 51, 100, (byte)round })),
                Bind(8, new ObjectIdentifier("2.39." + (uint.MaxValue - (uint)round)))
            };
            var writable = expected.Where((_, i) => i != 2).ToArray();
            var result = Set(writable);
            Program.Check(result.ErrorStatus.ToInt32() == 0 && result.Variables.Count == 7 &&
                result.Variables.Select(v => v.Data).SequenceEqual(writable.Select(v => v.Data)), "All writable SMI types SET/echo.");
            result = Get(all);
            Program.Check(result.ErrorStatus.ToInt32() == 0 && result.Variables.Count == 8 &&
                result.Variables.Select(v => v.Data).SequenceEqual(expected.Select(v => v.Data)), "Model GET after SET.");
            result = Set(Bind(1, new Integer32(0)), Bind(3, new Counter32(2)));
            Program.Check(result.ErrorStatus.ToInt32() == 17 && result.ErrorIndex.ToInt32() == 2,
                "Readonly rejects complete SET before mutation.");
            Program.Check(Get(Bind(1)).Variables[0].Data.Equals(new Integer32(signed)), "Rejected SET keeps first property.");
        }
        var wrongType = Set(Bind(1, new Gauge32(1)));
        Program.Check(wrongType.ErrorStatus.ToInt32() == 7 && wrongType.ErrorIndex.ToInt32() == 1, "wrongType");
        var wrongLength = Set(Bind(2, new OctetString(new byte[33])));
        Program.Check(wrongLength.ErrorStatus.ToInt32() == 8 && wrongLength.ErrorIndex.ToInt32() == 1, "wrongLength");
        var denied = Ask(new SetRequestMessage(id++, VersionCode.V2, read, new[] { Bind(1, new Integer32(0)) }));
        Program.Check(denied.ErrorStatus.ToInt32() == 6, "Read community cannot SET.");
        Program.Check(Get(Bind(9)).Variables[0].Data is NoSuchObject, "Missing scalar.");
        RawOidBoundary(target);
        Console.WriteLine("SharpSnmpLib 12.5.7 mapping PASS: v1/v2c GET/GETNEXT/GETBULK/WALK, eight SMI types, SET atomicity; rounds=" + iterations);
    }

    private static void RawOidBoundary(IPEndPoint target)
    {
        // SharpSnmpLib 12.5.7은 첫 arc가 2여도 두 번째 arc를 40 미만으로 제한하므로 이 경계는 자체 BER로 검사합니다.
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.ReceiveTimeout = 2000;
        socket.Connect(target);
        string oid = DeviceData.Enterprise + ".8.0";
        string value = "2.100.4294967295";
        var request = VagabondK.nF.SNMP.Tests.Wire.Message(1, "private", 0xa3, 900001, 0, 0,
            VagabondK.nF.SNMP.Tests.Wire.Bind(oid, VagabondK.nF.SNMP.Tests.Wire.Oid(value)));
        Program.Check(socket.Send(request) == request.Length, "Raw OID boundary send.");
        var buffer = new byte[4096];
        int length = socket.Receive(buffer);
        var response = VagabondK.nF.SNMP.Tests.Wire.Parse(buffer.Take(length).ToArray());
        Program.Check(response.Id == 900001 && response.Status == 0 &&
            VagabondK.nF.SNMP.Tests.Wire.OidText(response.Bindings[0].Content) == value, "Raw large second OID arc SET.");
        request = VagabondK.nF.SNMP.Tests.Wire.Message(1, "public", 0xa0, 900002, 0, 0,
            VagabondK.nF.SNMP.Tests.Wire.Bind(oid));
        Program.Check(socket.Send(request) == request.Length, "Raw OID boundary GET send.");
        length = socket.Receive(buffer);
        response = VagabondK.nF.SNMP.Tests.Wire.Parse(buffer.Take(length).ToArray());
        Program.Check(response.Id == 900002 && response.Status == 0 &&
            VagabondK.nF.SNMP.Tests.Wire.OidText(response.Bindings[0].Content) == value, "Raw large second OID arc readback.");
    }
}
