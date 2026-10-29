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

/// <summary>명시적 스칼라 쓰기의 성공·검증 거절·접근 오류와 잘못된 통신 입력을 관리 코드 및 외부 피어로 검증합니다.</summary>
internal static class WriteTests
{
    internal static void Enable(MmsServerVariable[] model)
    {
        model[0].EnableWrite();
        model[1].EnableWrite(v => v.Integer < -1000000 || v.Integer > 1000000 ? 11 : -1);
        model[2].EnableWrite();
    }
    private static void Check(bool ok) { if (!ok) throw new Exception("MMS Write assertion"); }
    private static BerElement Element(byte[] encoded)
    { return MmsEncoding.Single(encoded, 0, encoded.Length, encoded[0]); }
    internal static void Run()
    {
        var variable = new MmsServerVariable("D", "i", 12);
        Check(!variable.Writable && variable.ApplyWrite(Element(MmsEncoding.Int(0x85, 13))) == 3);
        variable.EnableWrite(v => v.Integer < 0 ? 11 : -1);
        Check(variable.ApplyWrite(Element(MmsEncoding.Tlv(0x83, new byte[] { 1 }))) == 7);
        Check(variable.ApplyWrite(Element(MmsEncoding.Int(0x85, long.MaxValue))) == 11);
        Check(variable.ApplyWrite(Element(MmsEncoding.Int(0x85, -1))) == 11);
        Check(MmsValue.Decode(Element(variable.EncodeValue())).Integer == 12);
        Check(variable.ApplyWrite(Element(MmsEncoding.Int(0x85, 42))) == -1);
        Check(MmsValue.Decode(Element(variable.EncodeValue())).Integer == 42);
        variable.DisableWrite();
        Check(variable.ApplyWrite(Element(MmsEncoding.Int(0x85, 14))) == 3);
        variable.SetInteger(99);
        Check(MmsValue.Decode(Element(variable.EncodeValue())).Integer == 99);
        variable.EnableWrite(v => 12);
        try { variable.ApplyWrite(Element(MmsEncoding.Int(0x85, 14))); throw new Exception("Invalid validator accepted"); }
        catch (InvalidOperationException) { }
        Check(MmsValue.Decode(Element(variable.EncodeValue())).Integer == 99);
        variable.EnableWrite(v => { throw new Exception("Wrong type reached validator"); });
        Check(variable.ApplyWrite(Element(MmsEncoding.Tlv(0x83, new byte[] { 0 }))) == 7);
        variable.EnableWrite();
        foreach (int boundary in new[] { int.MinValue, int.MaxValue })
        {
            Check(variable.ApplyWrite(Element(MmsEncoding.Int(0x85, boundary))) == -1);
            Check(MmsValue.Decode(Element(variable.EncodeValue())).Integer == boundary);
        }
        var composite = new MmsServerVariable("D", "composite", new[] { variable });
        try { composite.EnableWrite(); throw new Exception("Composite writes enabled"); }
        catch (InvalidOperationException) { }
        Check(!composite.Writable);
        Managed();
        Console.WriteLine("MMS Write model: default deny/type/range/validator/no mutation/local setter PASS.");
    }
    private static void Managed()
    {
        var b = new MmsServerVariable("D", "b", true);
        var i = new MmsServerVariable("D", "i", 1);
        var f = new MmsServerVariable("D", "f", 1.0f);
        var ro = new MmsServerVariable("D", "ro", 5);
        var composite = new MmsServerVariable("D", "c", new[] { ro });
        b.EnableWrite(); i.EnableWrite(v => v.Integer < -10 || v.Integer > 10 ? 11 : -1);
        f.EnableWrite(v => double.IsNaN(v.FloatingPoint) || double.IsInfinity(v.FloatingPoint) ? 11 : -1);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var worker = Task.Run(() => {
                using (var socket = listener.AcceptSocket())
                {
                    var server = new Iec61850ServerSession(new MmsSocketChannel(socket), new[] { b, i, f, ro, composite });
                    server.Associate();
                    while (server.ProcessRequest()) { }
                    Check(!server.Faulted);
                }
            });
            using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                socket.Connect(IPAddress.Loopback, port);
                var client = new Iec61850Client(new MmsSocketChannel(socket), maximumTpduSize: 128);
                client.Associate();
                int code;
                client.Write("D", "b", false);
                Check(!client.Read("D", "b").Boolean);
                client.Write("D", "i", -10);
                Check(!client.TryWrite("D", "i", 11, out code) && code == 11);
                Check(client.Read("D", "i").Integer == -10);
                Check(!client.TryWrite("D", "i", true, out code) && code == 7);
                Check(!client.TryWrite("D", "ro", 42, out code) && code == 3);
                Check(client.Read("D", "ro").Integer == 5);
                Check(!client.TryWrite("D", "c", true, out code) && code == 3);
                Check(!client.TryWrite("D", "missing", 1, out code) && code == 10);
                client.Write("D", "f", -0.0f);
                Check(BitConverter.DoubleToInt64Bits(client.Read("D", "f").FloatingPoint) < 0);
                Check(!client.TryWrite("D", "f", float.NaN, out code) && code == 11);
                Check(!client.TryWrite("D", "f", float.PositiveInfinity, out code) && code == 11);
                Check(!client.Faulted && client.Associated);
                Check(!client.GetVariableAccessAttributes("D", "i").Deletable);
            }
            worker.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
        }
        finally { listener.Stop(); }
    }
    internal static void Native(Iec61850Client client)
    {
        const string d = "NFTESTLD0", b = "GGIO1$SP$Writable$flag", i = "GGIO1$SP$Writable$count", f = "GGIO1$SP$Writable$real";
        int code;
        if (!client.SupportsWrite) throw new Exception("Native peer does not advertise Write");
        if (!client.TryWrite(d, b, false, out code)) throw new Exception("Native Boolean Write rejected: " + code);
        Check(code == -1);
        Check(!client.Read(d, b).Boolean);
        client.Write(d, i, 42);
        Check(client.Read(d, i).Integer == 42);
        client.Write(d, f, 9.25f);
        Check(client.Read(d, f).FloatingPoint == 9.25);
        Check(!client.TryWrite(d, "missing", true, out code) && code == 10);
        Check(!client.TryWrite(d, i, true, out code) && code == 7);
        Check(!client.TryWrite(d, i, 1000001, out code) && code == 11);
        Check(client.Read(d, i).Integer == 42 && !client.Faulted && client.Associated);
        client.Write(d, b, true); client.Write(d, i, -123456); client.Write(d, f, 12.5f);
    }
    internal static void Interop(string executable)
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        using (var process = Process.Start(new ProcessStartInfo(executable, "writeserver " + port) {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        }))
        {
            try
            {
                Check(process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult() == "READY");
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    socket.Connect(IPAddress.Loopback, port);
                    var client = new Iec61850Client(new MmsSocketChannel(socket), maximumTpduSize: 128);
                    client.Associate();
                    Native(client);
                }
            }
            finally
            {
                process.StandardInput.WriteLine("quit"); process.StandardInput.Flush();
                if (!process.WaitForExit(10000)) { process.Kill(true); throw new Exception("Write peer shutdown timed out"); }
            }
            Check(process.ExitCode == 0);
        }
        Console.WriteLine("MMS Write -> original peer: three scalar types/readback/type/range/missing/recovery PASS.");
    }
}
