using VagabondK.nF.MMS.Server;
using VagabondK.nF.MMS;
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Threading;
using VagabondK.nF.IEC61850;
using VagabondK.nF.IEC61850.Client;
using VagabondK.nF.IEC61850.Server;
using VagabondK.nF.MMS.TCP;

/// <summary>normal Direct/SBO 메모리 제어의 선택 수명·실행 상태와 로컬·외부 피어 상호 운용을 검증합니다.</summary>
internal static class ControlTests
{
    internal static MmsServerControl[] Model()
    {
        return new[] {
            new MmsServerControl("NFTESTLD0", "GGIO1$CO$Direct", MmsControlModel.DirectNormal) { Enabled = true },
            new MmsServerControl("NFTESTLD0", "GGIO1$CO$Selected", MmsControlModel.SboNormal) { Enabled = true }
        };
    }
    private static void Check(bool ok) { if (!ok) throw new Exception("Control state assertion"); }
    private static Process Start(string executable, string args)
    { return Process.Start(new ProcessStartInfo(executable, args) {
        UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true }); }
    private static int Port()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }
    internal static void Interop(string executable)
    {
        int port = Port();
        using (var peer = Start(executable, "controlserver " + port))
        {
            try
            {
                if (peer.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult() != "READY")
                    throw new Exception("Control peer not ready");
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    socket.Connect(IPAddress.Loopback, port);
                    var client = new Iec61850Client(new MmsSocketChannel(socket), maximumTpduSize: 128);
                    client.Associate();
                    ControlChecks.Run(client);
                }
            }
            finally
            {
                if (!peer.HasExited)
                {
                    peer.StandardInput.WriteLine("quit"); peer.StandardInput.Flush();
                    if (!peer.WaitForExit(10000)) { peer.Kill(true); throw new Exception("Control peer shutdown timeout"); }
                }
                if (peer.ExitCode != 0) throw new Exception(peer.StandardError.ReadToEnd());
            }
        }
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var controls = Model();
        int handlerCalls = 0;
        foreach (var control in controls)
        {
            control.ReportControlErrors = true;
            control.SetHandlers(request => {
                Check(request.OriginCategory == 3 && !request.Test &&
                    System.Text.Encoding.ASCII.GetString(request.GetOrigin()) == "native-memory");
                return -1;
            }, request => { handlerCalls++; return -1; });
            control.Enabled = true;
        }
        var worker = Task.Run(() => {
            using (var socket = listener.AcceptSocket())
            {
                var variables = ServerTests.Model(1);
                WriteTests.Enable(variables);
                var server = new Iec61850ServerSession(new MmsSocketChannel(socket), variables, maximumNamesPerPage: 1, controls: controls);
                server.Associate();
                int count = 0;
                while (server.ProcessRequest()) count++;
                Check(count == 96 && !server.Faulted);
            }
        });
        try
        {
            using (var peer = Start(executable, "controlverify " + port + " 127.0.0.1 1"))
            {
                if (!peer.WaitForExit(15000)) { peer.Kill(true); throw new Exception("Native control timeout"); }
                if (peer.ExitCode != 0) throw new Exception("Native control failed: " + peer.StandardError.ReadToEnd());
                Check(peer.StandardOutput.ReadToEnd().Contains("NATIVE CONTROL PASS"));
            }
            worker.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
            Check(controls[0].Value && controls[1].Value && handlerCalls == 2);
        }
        finally { listener.Stop(); }
        StressInterop(executable);
        Console.WriteLine("CONTROL original peer both directions: Boolean Direct/SBO select/operate/cancel, Test and readback PASS.");
    }
    private static void StressInterop(string executable)
    {
        var controls = Model();
        int validations = 0, executions = 0, failures = 0;
        foreach (var control in controls)
        {
            control.SetHandlers(request => { validations++; return -1; }, request => {
                Check(!request.Test);
                executions++;
                if (request.OriginCategory == 8) { failures++; return 3; }
                return -1;
            });
            control.ReportControlErrors = true;
            control.Enabled = true;
        }
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var worker = Task.Run(() => {
            using (var socket = listener.AcceptSocket())
            {
                var server = new Iec61850ServerSession(new MmsSocketChannel(socket), new MmsServerVariable[0], controls: controls);
                try
                {
                    server.Associate();
                    int count = 0;
                    while (server.ProcessRequest()) count++;
                    Check(count == 1294 && !server.Faulted);
                }
                finally { server.Close(); }
            }
        });
        try
        {
            using (var peer = Start(executable, "controlstress " + port + " 127.0.0.1 256"))
            {
                if (!peer.WaitForExit(60000)) { peer.Kill(true); throw new Exception("Native stress timeout"); }
                if (peer.ExitCode != 0) throw new Exception("Native stress failed: " + peer.StandardError.ReadToEnd());
                Check(peer.StandardOutput.ReadToEnd().Contains("NATIVE CONTROL STRESS PASS repetitions=256 operations=512"));
            }
            worker.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
            Check(validations == 516 && executions == 514 && failures == 2 && controls[0].Value && controls[1].Value);
        }
        finally { listener.Stop(); }
    }
    internal static void Run()
    {
        Types();
        var model = Model();
        var direct = model[0]; var sbo = model[1];
        var first = new object(); var other = new object();
        Check(Visible(sbo.Select(first, 0)).Length != 0);
        Check(Visible(sbo.Select(other, 1)).Length == 0);
        Check(sbo.Apply(other, 1, Command(true), true) == 3 && !sbo.Value);
        Check(sbo.Apply(first, 1000, Command(true), true) == 3 && !sbo.Value);
        Check(Visible(sbo.Select(other, 1000)).Length != 0);
        sbo.Release(first);
        Check(Visible(sbo.Select(first, 1001)).Length == 0);
        sbo.Release(other);
        Check(Visible(sbo.Select(first, 1001)).Length != 0);
        Check(sbo.Apply(first, 1002, Command(true), true) == -1 && sbo.Value);
        Check(sbo.Apply(first, 1003, Command(false), true) == 3 && sbo.Value);
        Check(Visible(sbo.Select(first, 1003)).Length != 0);
        Check(sbo.Apply(first, 1004, Command(false, cancel: true), false) == -1 && sbo.Value);
        Check(sbo.Apply(first, 1005, Command(false), true) == 3 && sbo.Value);
        direct.Enabled = false;
        Check(direct.Apply(first, 0, Command(true), true) == 3 && !direct.Value);
        direct.Enabled = true;
        Check(direct.Apply(first, 0, Command(true, test: true), true) == -1 && !direct.Value);
        Check(direct.Apply(first, 0, Command(true, check: true), true) == 3 && !direct.Value);
        Check(direct.Apply(first, 0, Command(true), true) == -1 && direct.Value);
        Check(Visible(sbo.Select(first, 1010)).Length != 0);
        sbo.Enabled = false; sbo.Enabled = true;
        Check(sbo.Apply(first, 1011, Command(false), true) == 3 && sbo.Value);
        foreach (byte[] malformed in new[] { MmsEncoding.Tlv(0xa2), MmsEncoding.Tlv(0xa2, MmsEncoding.Tlv(0x83)),
            MmsEncoding.Tlv(0xa2, MmsEncoding.Tlv(0x83, new byte[] { 1 })) })
        {
            try { direct.Apply(first, 0, Element(malformed), true); throw new Exception("Malformed control accepted"); }
            catch (MmsProtocolException) { }
            Check(direct.Value);
        }
        Sessions();
        ControlHandlerTests.Run();
        ControlDiagnosticTests.Run();
        Console.WriteLine("CONTROL ownership/expiry/cancel/one-shot/Test/default-deny/Check/malformed/no-mutation PASS.");
    }
    internal sealed class Connection : IDisposable
    {
        private readonly Socket socket;
        private readonly Task worker;
        internal readonly Iec61850Client Client;
        internal Connection(MmsServerControl[] controls)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            worker = Task.Run(() => {
                try
                {
                    using (var accepted = listener.AcceptSocket())
                    {
                        var server = new Iec61850ServerSession(new MmsSocketChannel(accepted), new MmsServerVariable[0], controls: controls);
                        try { server.Associate(); while (server.ProcessRequest()) { } }
                        finally { server.Close(); }
                    }
                }
                finally { listener.Stop(); }
            });
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Connect(IPAddress.Loopback, port);
            Client = new Iec61850Client(new MmsSocketChannel(socket));
            Client.Associate();
        }
        public void Dispose()
        {
            socket.Dispose();
            worker.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
        }
    }
    private static void Sessions()
    {
        var controls = Model();
        using (var second = new Connection(controls))
        {
            using (var first = new Connection(controls))
            {
                var a = new MmsControlClient(first.Client, "NFTESTLD0", "GGIO1$CO$Selected", new byte[0]);
                var b = new MmsControlClient(second.Client, "NFTESTLD0", "GGIO1$CO$Selected", new byte[0]);
                Check(a.Select() && !b.Select());
                int code;
                Check(!second.Client.TryWriteControl("NFTESTLD0", "GGIO1$CO$Selected$Oper", Command(true).Buffer, out code) && code == 3);
                Check(!controls[1].Value);
                Thread.Sleep(1100);
                Check(!a.Operate(true, 0, out code) && code == 3 && !controls[1].Value);
                Check(a.Select());
            }
            var next = new MmsControlClient(second.Client, "NFTESTLD0", "GGIO1$CO$Selected", new byte[0]);
            int result;
            Check(second.Client.Read("NFTESTLD0", "GGIO1$CF$Selected$sboTimeout").Unsigned == 1000);
            Check(second.Client.Read("NFTESTLD0", "GGIO1$CF$Selected$sboClass").Integer == 0);
            MmsValue ignored;
            Check(!second.Client.TryRead("NFTESTLD0", "GGIO1$CO$Selected", out ignored, out result) && result == 3);
            Check(next.Select() && next.Operate(true, 0, out result) && controls[1].Value);
            Check(next.Select() && next.Cancel(0, out result));
            Check(!second.Client.TryWriteControl("NFTESTLD0", "GGIO1$CO$Selected$Oper", Command(false).Buffer, out result) && result == 3);
            Check(controls[1].Value && !second.Client.Faulted);
        }
    }
    private static string Visible(byte[] encoded) { return MmsValue.Decode(Element(encoded)).VisibleString; }
    private static void Types()
    {
        var bits = MmsValue.Decode(Element(MmsEncoding.Tlv(0x84, new byte[] { 6, 0x80 })));
        Check(bits.Kind == MmsValueKind.BitString && bits.BitCount == 2 && bits.GetBytes()[0] == 0x80);
        var copy = bits.GetBytes(); copy[0] = 0;
        Check(bits.GetBytes()[0] == 0x80);
        Check(MmsValue.Decode(Element(MmsEncoding.Tlv(0x89, new byte[64]))).GetBytes().Length == 64);
        Check(Visible(MmsEncoding.Tlv(0x8a, System.Text.Encoding.ASCII.GetBytes(new string('A', 129)))).Length == 129);
        Check(MmsValue.Decode(Element(MmsEncoding.Tlv(0x91, new byte[8]))).GetBytes().Length == 8);
        foreach (byte[] bad in new[] {
            MmsEncoding.Tlv(0x84), MmsEncoding.Tlv(0x84, new byte[] { 1 }),
            MmsEncoding.Tlv(0x84, new byte[] { 6, 1 }), MmsEncoding.Tlv(0x84, new byte[6]),
            MmsEncoding.Tlv(0x89, new byte[65]), MmsEncoding.Tlv(0x8a, new byte[] { 0 }),
            MmsEncoding.Tlv(0x8a, new byte[130]), MmsEncoding.Tlv(0x91, new byte[7])
        })
        {
            try { MmsValue.Decode(Element(bad)); throw new Exception("Invalid control type accepted"); }
            catch (MmsProtocolException) { }
        }
        foreach (byte[] bad in new[] {
            MmsEncoding.Int(0x84, 33), MmsEncoding.Int(0x89, -65), MmsEncoding.Int(0x8a, -130), MmsEncoding.Tlv(0x91, new byte[] { 0 })
        })
        {
            try { MmsVariableAttributes.Decode(Element(bad), false); throw new Exception("Invalid control type declaration accepted"); }
            catch (MmsProtocolException) { }
        }
    }
    private static BerElement Element(byte[] bytes) { return MmsEncoding.Single(bytes, 0, bytes.Length, bytes[0]); }
    internal static BerElement Command(bool value, bool test = false, bool check = false, bool cancel = false)
    {
        return Element(MmsEncoding.Tlv(0xa2, MmsEncoding.Tlv(0x83, new byte[] { value ? (byte)1 : (byte)0 }),
            MmsEncoding.Tlv(0xa2, MmsEncoding.Int(0x85, 3), MmsEncoding.Tlv(0x89, new byte[] { 78, 70 })),
            MmsEncoding.Int(0x86, 1), MmsEncoding.Tlv(0x91, new byte[8]), MmsEncoding.Tlv(0x83, new byte[] { test ? (byte)1 : (byte)0 }),
            cancel ? new byte[0] : MmsEncoding.Tlv(0x84, new byte[] { 6, check ? (byte)128 : (byte)0 })));
    }
}
