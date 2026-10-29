using VagabondK.nF.MMS.Server;
using VagabondK.nF.MMS;
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using VagabondK.nF.IEC61850;
using VagabondK.nF.IEC61850.Client;
using VagabondK.nF.IEC61850.Server;
using VagabondK.nF.MMS.TCP;

/// <summary>구조체·동종 배열의 중첩 형식과 값 순서를 확인하고 로컬 TCP 및 외부 피어 왕복을 검증합니다.</summary>
internal static class CompositeTests
{
    private static int checks;
    private static void Check(bool value)
    {
        if (!value) throw new Exception("Composite assertion " + checks);
        checks++;
    }
    private static void Reject(Action action, Type expected)
    {
        try { action(); }
        catch (Exception error) { Check(error.GetType() == expected); return; }
        throw new Exception("Expected " + expected);
    }
    private static MmsValue Value(byte[] bytes)
    {
        return MmsValue.Decode(MmsEncoding.Single(bytes, 0, bytes.Length, bytes[0]));
    }
    private static MmsVariableAttributes Type(byte[] bytes)
    {
        return MmsVariableAttributes.Decode(MmsEncoding.Single(bytes, 0, bytes.Length, bytes[0]), false);
    }
    private static byte[] ArrayType(int count, byte[] element)
    {
        return MmsEncoding.Tlv(0xa1, MmsEncoding.Int(0x81, count), MmsEncoding.Tlv(0xa2, element));
    }
    private static byte[] StructureType(params byte[][] types)
    {
        var entries = new byte[types.Length][];
        for (int i = 0; i < entries.Length; i++)
            entries[i] = MmsEncoding.Tlv(0x30, MmsEncoding.Tlv(0xa1, types[i]));
        return MmsEncoding.Tlv(0xa2, MmsEncoding.Tlv(0xa1, entries));
    }
    internal static void Run()
    {
        byte[] scalar = MmsEncoding.Int(0x85, 123);
        byte[] value = MmsEncoding.Tlv(0xa2, MmsEncoding.Tlv(0x83, new byte[] { 1 }),
            MmsEncoding.Tlv(0xa1, scalar, MmsEncoding.Int(0x85, -123)));
        var decoded = Value(value);
        Check(decoded.Kind == MmsValueKind.Structure && decoded.Count == 2 && decoded.GetElement(0).Boolean);
        Check(decoded.GetElement(1).Kind == MmsValueKind.Array && decoded.GetElement(1).Count == 2);
        Check(decoded.GetElement(1).GetElement(0).Integer == 123 && decoded.GetElement(1).GetElement(1).Integer == -123);
        Array.Clear(value);
        Check(decoded.GetElement(1).GetElement(0).Integer == 123);
        Reject(() => { long ignored = decoded.Integer; }, typeof(InvalidOperationException));
        Reject(() => decoded.GetElement(-1), typeof(IndexOutOfRangeException));
        Reject(() => Value(scalar).GetElement(0), typeof(InvalidOperationException));
        Check(Value(MmsEncoding.Tlv(0xa1)).Count == 0 && Value(MmsEncoding.Tlv(0xa2)).Count == 0);
        byte[] type = StructureType(MmsEncoding.Tlv(0x83), ArrayType(3, MmsEncoding.Int(0x85, 32)));
        var definition = Type(type);
        Check(definition.Kind == MmsValueKind.Structure && definition.ComponentCount == 2 &&
            definition.GetComponentName(0) == null);
        Check(definition.GetComponent(1).ArrayLength == 3 &&
            definition.GetComponent(1).ElementType.Kind == MmsValueKind.Integer);
        Check(Type(ArrayType(0, MmsEncoding.Tlv(0x83))).ArrayLength == 0);
        Check(Type(StructureType()).ComponentCount == 0);
        var unpacked = Type(MmsEncoding.Tlv(0xa1, MmsEncoding.Tlv(0x80, new byte[] { 0 }),
            MmsEncoding.Int(0x81, 2), MmsEncoding.Tlv(0xa2, MmsEncoding.Tlv(0x83))));
        Check(unpacked.Kind == MmsValueKind.Array && unpacked.ArrayLength == 2);
        byte[] nested = scalar, nestedType = MmsEncoding.Int(0x85, 32);
        for (int i = 0; i < 8; i++) { nested = MmsEncoding.Tlv(0xa2, nested); nestedType = StructureType(nestedType); }
        Check(Value(nested).Kind == MmsValueKind.Structure && Type(nestedType).Kind == MmsValueKind.Structure);
        Reject(() => Value(MmsEncoding.Tlv(0xa2, nested)), typeof(MmsProtocolException));
        Reject(() => Type(StructureType(nestedType)), typeof(MmsProtocolException));
        byte[][] many = new byte[255][], manyTypes = new byte[255][];
        for (int i = 0; i < 255; i++) { many[i] = scalar; manyTypes[i] = MmsEncoding.Tlv(0x83); }
        Check(Value(MmsEncoding.Tlv(0xa1, many)).Count == 255);
        Check(Type(StructureType(manyTypes)).ComponentCount == 255);
        Reject(() => Value(MmsEncoding.Tlv(0xa2, MmsEncoding.Tlv(0xa1, many))), typeof(MmsProtocolException));
        Reject(() => Type(StructureType(StructureType(manyTypes))), typeof(MmsProtocolException));
        foreach (byte[] bad in new byte[][] {
            ArrayType(-1, MmsEncoding.Tlv(0x83)), ArrayType(256, MmsEncoding.Tlv(0x83)),
            MmsEncoding.Tlv(0xa1, MmsEncoding.Tlv(0x80, new byte[] { 1 }), MmsEncoding.Int(0x81, 1),
                MmsEncoding.Tlv(0xa2, MmsEncoding.Tlv(0x83))),
            MmsEncoding.Tlv(0xa2, MmsEncoding.Tlv(0xa1, MmsEncoding.Tlv(0x30))),
            MmsEncoding.Tlv(0xa2, MmsEncoding.Tlv(0xa1, MmsEncoding.Tlv(0x30,
                MmsEncoding.Tlv(0x80, new byte[] { 32 }), MmsEncoding.Tlv(0xa1, MmsEncoding.Tlv(0x83))))),
            MmsEncoding.Tlv(0xa1, MmsEncoding.Int(0x81, 3), MmsEncoding.Tlv(0xa2, MmsEncoding.Tlv(0x83), scalar)),
            MmsEncoding.Tlv(0xa2, MmsEncoding.Tlv(0xa1), MmsEncoding.Int(0x80, 1))
        }) Reject(() => Type(bad), typeof(MmsProtocolException));
        Reject(() => Value(new byte[] { 0xa2, 2, 0x83, 1 }), typeof(MmsProtocolException));
        Reject(() => Value(MmsEncoding.Tlv(0xa1, scalar, MmsEncoding.Tlv(0x83, new byte[] { 1 }))),
            typeof(MmsProtocolException));
        var child = new MmsServerVariable("D", "x", 1);
        Reject(() => new MmsServerVariable("D", "r", (MmsServerVariable[])null), typeof(ArgumentNullException));
        Reject(() => new MmsServerVariable("D", "r", new MmsServerVariable[0]), typeof(ArgumentOutOfRangeException));
        Reject(() => new MmsServerVariable("D", "r", new[] { child, child }), typeof(ArgumentException));
        Reject(() => new MmsServerVariable("D", "r", new[] { child, new MmsServerVariable("D", "y", true) }, true),
            typeof(ArgumentException));
        var list = new[] { child };
        var composite = new MmsServerVariable("D", "r", list);
        list[0] = null;
        child.SetInteger(-100);
        Check(Value(composite.EncodeValue()).GetElement(0).Integer == -100);
        Reject(() => composite.SetInteger(1), typeof(InvalidOperationException));
        var deep = child;
        for (int i = 0; i < 8; i++) deep = new MmsServerVariable("D", "x", new[] { deep });
        var deepest = deep;
        Reject(() => new MmsServerVariable("D", "x", new[] { deepest }), typeof(ArgumentException));
        var leaves = new MmsServerVariable[255];
        for (int i = 0; i < leaves.Length; i++) leaves[i] = child;
        var full = new MmsServerVariable("D", "full", leaves, true);
        Check(Value(full.EncodeValue()).Count == 255);
        Reject(() => new MmsServerVariable("D", "root", new[] { full }), typeof(ArgumentException));
        Loopback();
        Console.WriteLine("MMS composite bounded values/types/model: " + checks + " assertions PASS.");
    }
    private static void Loopback()
    {
        var children = new MmsServerVariable[80];
        for (int i = 0; i < children.Length; i++) children[i] = new MmsServerVariable("D", "e" + i, i);
        var large = new MmsServerVariable("D", "array", children, true);
        var structure = new MmsServerVariable("D", "structure", new[] {
            new MmsServerVariable("D", "flag", true), large
        });
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            var worker = Task.Run(() => {
                using (var socket = listener.AcceptSocket())
                {
                    var server = new Iec61850ServerSession(new MmsSocketChannel(socket), new[] { structure });
                    server.Associate();
                    Check(server.ProcessRequest());
                    Check(server.ProcessRequest());
                    children[0].SetInteger(int.MinValue);
                    Check(server.ProcessRequest());
                    Check(!server.ProcessRequest() && !server.Faulted);
                }
            });
            using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                socket.Connect(IPAddress.Loopback, port);
                var client = new Iec61850Client(new MmsSocketChannel(socket), maximumTpduSize: 128);
                client.Associate();
                var type = client.GetVariableAccessAttributes("D", "structure");
                Check(type.GetComponent(1).ArrayLength == 80 && type.GetComponentName(1) == "array");
                var first = client.Read("D", "structure").GetElement(1);
                Check(first.Count == 80 && first.GetElement(0).Integer == 0 && first.GetElement(79).Integer == 79);
                var second = client.Read("D", "structure").GetElement(1);
                Check(second.GetElement(0).Integer == int.MinValue && first.GetElement(0).Integer == 0);
            }
            worker.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
        }
        finally { listener.Stop(); }
    }
    internal static void NativeClient(Iec61850Client client)
    {
        var type = client.GetVariableAccessAttributes("NFTESTLD0", "GGIO1$ST$Status");
        Check(type.Kind == MmsValueKind.Structure && type.ComponentCount == 1 &&
            type.GetComponentName(0) == "stVal" && type.GetComponent(0).Kind == MmsValueKind.Boolean);
        var status = client.Read("NFTESTLD0", "GGIO1$ST$Status");
        Check(status.Kind == MmsValueKind.Structure && status.GetElement(0).Boolean);
        type = client.GetVariableAccessAttributes("NFTESTLD0", "GGIO1$MX$Samples$values");
        Check(type.Kind == MmsValueKind.Array && type.ArrayLength == 3 &&
            type.ElementType.Kind == MmsValueKind.Integer && type.ElementType.BitWidth == 32);
        var samples = client.Read("NFTESTLD0", "GGIO1$MX$Samples$values");
        Check(samples.Kind == MmsValueKind.Array && samples.Count == 3);
        for (int i = 0; i < 3; i++) Check(samples.GetElement(i).Integer == 100 + i);
        type = client.GetVariableAccessAttributes("NFTESTLD0", "GGIO1$MX$Samples");
        Check(type.Kind == MmsValueKind.Structure && type.GetComponentName(0) == "values" &&
            type.GetComponent(0).Kind == MmsValueKind.Array);
        var nested = client.Read("NFTESTLD0", "GGIO1$MX$Samples");
        Check(nested.Kind == MmsValueKind.Structure && nested.GetElement(0).GetElement(2).Integer == 102);
        Check(client.Read("NFTESTLD0", "GGIO1$ST$Status$stVal").Boolean && !client.Faulted);
    }
    internal static MmsServerVariable[] Model()
    {
        var samples = new MmsServerVariable("NFTESTLD0", "samples", new[] {
            new MmsServerVariable("NFTESTLD0", "e0", 100), new MmsServerVariable("NFTESTLD0", "e1", 101),
            new MmsServerVariable("NFTESTLD0", "e2", 102)
        }, true);
        return new[] { new MmsServerVariable("NFTESTLD0", "bundle", new[] {
            new MmsServerVariable("NFTESTLD0", "flag", true), samples
        }) };
    }
    internal static void Interop(string executable)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using (var native = Start(executable, "complexserver " + port))
        {
            try
            {
                Check(native.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult() == "READY");
                for (int connection = 0; connection < 2; connection++)
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    socket.Connect(IPAddress.Loopback, port);
                    var client = new Iec61850Client(new MmsSocketChannel(socket), maximumTpduSize: 128);
                    client.Associate();
                    NativeClient(client);
                }
            }
            finally
            {
                native.StandardInput.WriteLine("quit"); native.StandardInput.Flush();
                if (!native.WaitForExit(10000)) { native.Kill(true); throw new Exception("Composite peer shutdown timeout"); }
            }
            Check(native.ExitCode == 0);
        }
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            var worker = Task.Run(() => {
                using (var socket = listener.AcceptSocket())
                {
                    var server = new Iec61850ServerSession(new MmsSocketChannel(socket), Model());
                    server.Associate();
                    int count = 0;
                    while (server.ProcessRequest()) count++;
                    Check(count == 4 && !server.Faulted);
                }
            });
            using (var native = Start(executable, "complexverify " + port + " 127.0.0.1"))
            {
                if (!native.WaitForExit(15000)) { native.Kill(true); throw new Exception("Composite native Client timeout"); }
                if (native.ExitCode != 0) throw new Exception(native.StandardError.ReadToEnd());
                Check(native.StandardOutput.ReadToEnd().Contains("NATIVE COMPOSITE PASS"));
            }
            worker.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
        }
        finally { listener.Stop(); }
        Console.WriteLine("Original peer composite interop: structure/array/nested type and Read, both directions PASS.");
    }
    private static Process Start(string executable, string args)
    {
        return Process.Start(new ProcessStartInfo(executable, args) {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        });
    }
}
