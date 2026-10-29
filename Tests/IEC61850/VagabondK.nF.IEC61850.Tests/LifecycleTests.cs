using VagabondK.nF.MMS.Server;
using VagabondK.nF.MMS;
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using VagabondK.nF.IEC61850;
using VagabondK.nF.IEC61850.Client;
using VagabondK.nF.IEC61850.Server;
using VagabondK.nF.MMS.TCP;

/// <summary>Association부터 정상 종료·EOF·재연결까지의 수명과 반복 세션에서 상태가 누적되지 않는지 확인합니다.</summary>
internal static class LifecycleTests
{
    private static void Check(bool ok) { if (!ok) throw new Exception("MMS lifecycle assertion."); }
    private static byte[] Wrap(byte[] data) { return MmsEncoding.Join(new byte[] { 1, 0, 1, 0 }, MmsEncoding.Pdv(3, data)); }
    internal static void Run()
    {
        for (int mode = 0; mode < 6; mode++)
        {
            var channel = MmsClientTests.Ready(); var client = new Iec61850Client(channel); client.Associate();
            channel.Segments(mode % 2 == 0 ?
                Wrap(MmsEncoding.Tlv(0xa4, MmsEncoding.Int(0x80, 1), MmsEncoding.Int(0x81, 1))) :
                Wrap(MmsEncoding.Tlv(0xa2, MmsEncoding.Int(0x80, 1), MmsEncoding.Tlv(0xa2, MmsEncoding.Tlv(0xa0, MmsEncoding.Int(0x87, 2))))), 3);
            bool caught = false;
            try
            {
                if (mode < 2) client.Read("D", "I");
                else if (mode < 4) client.GetNameList();
                else { int error; client.TryWrite("D", "I", true, out error); }
            }
            catch (MmsServiceException error)
            { Check(mode % 2 == 0 ? error is MmsRejectException && ((MmsRejectException)error).Reason == 1 : error.ErrorClass == 7 && error.ErrorCode == 2); caught = true; }
            Check(caught && client.Associated && !client.Faulted);
            channel.Segments(MmsClientTests.ServiceResponse(2, MmsEncoding.Tlv(0xa4, MmsEncoding.Tlv(0xa1, MmsEncoding.Int(0x85, 42)))), 3);
            Check(client.Read("D", "I").Integer == 42);
            channel.Segments(Wrap(MmsEncoding.Tlv(0x8c)), 3); channel.Segments(MmsEncoding.Release(true), 3);
            client.Release(); Check(!client.Associated && !client.Faulted);
            bool reused = false; try { client.Associate(); } catch (InvalidOperationException) { reused = true; } Check(reused);
        }
        byte[] release = MmsEncoding.Release(false);
        for (int cut = 0; cut <= release.Length; cut++)
        {
            byte[] association = MmsEncoding.Association(4096);
            var channel = ServerTests.ReadyTranscript(association, association.Length);
            var server = new Iec61850ServerSession(channel, ServerTests.Model(0)); server.Associate(); channel.Output.Clear();
            if (cut == release.Length)
            {
                channel.AppendRead(MmsEncoding.Tlv(0xa0, MmsEncoding.Int(2, 1), MmsEncoding.Tlv(0xa7)));
                Check(server.ProcessRequest() && !server.Faulted);
                byte[] tsdu = channel.LastTsdu();
                var data = MmsEncoding.UnwrapPdv(MmsEncoding.Single(tsdu, 4, tsdu.Length - 4, 0x61), 3);
                var rejected = MmsEncoding.Single(data.Buffer, data.Offset, data.Length, 0xa4).Children();
                Check(rejected.Read(0x80).Integer() == 1 && rejected.Read(0x81).Integer() == 1); rejected.End();
                channel.Output.Clear(); channel.AppendRead(MmsEncoding.Tlv(0x8b)); channel.Segments(release);
                Check(!server.ProcessRequest() && !server.Faulted && !server.Associated);
            }
            else
            {
                channel.AppendRead(MmsEncoding.Tlv(0x8b));
                if (cut > 0) { byte[] truncated = new byte[cut]; Array.Copy(release, truncated, cut); channel.Segments(truncated); }
                bool failed = false;
                try { server.ProcessRequest(); } catch (MmsProtocolException) { failed = true; } catch (IOException) { failed = true; }
                Check(failed && server.Faulted && !server.Associated);
            }
        }
        for (int reason = 0; reason <= 9; reason++)
        {
            var channel = MmsClientTests.Ready(); var client = new Iec61850Client(channel); client.Associate();
            channel.Segments(Wrap(MmsEncoding.Tlv(0xa4, MmsEncoding.Int(0x80, 1), MmsEncoding.Int(0x81, reason))), 3);
            bool caught = false;
            try { client.Read("D", "I"); } catch (MmsRejectException error) { Check(error.Reason == reason); caught = true; }
            Check(caught && client.Associated && !client.Faulted);
        }
        for (int mode = 0; mode < 5; mode++)
        {
            var channel = MmsClientTests.Ready(); var client = new Iec61850Client(channel); client.Associate();
            byte[] invalid = mode == 0 ? MmsEncoding.Tlv(0x8c, new byte[] { 0 }) :
                mode == 1 ? MmsEncoding.Tlv(0xa4, MmsEncoding.Int(0x80, 2), MmsEncoding.Int(0x81, 1)) :
                mode == 2 ? MmsEncoding.Tlv(0xa4, MmsEncoding.Int(0x80, 1), MmsEncoding.Int(0x81, 10)) :
                mode == 3 ? MmsEncoding.Tlv(0xa4, MmsEncoding.Int(0x81, 1)) : MmsEncoding.Tlv(0x8c);
            channel.Segments(Wrap(invalid), 3);
            if (mode == 4) channel.Segments(MmsEncoding.SessionParameter(10, MmsEncoding.SessionParameter(0xc1,
                MmsEncoding.Pdv(3, MmsEncoding.Tlv(0x63)))), 3);
            bool failed = false;
            try { if (mode == 0 || mode == 4) client.Release(); else client.Read("D", "I"); }
            catch (MmsProtocolException) { failed = true; }
            Check(failed && client.Faulted);
        }
        {
            byte[] association = MmsEncoding.Association(4096);
            var channel = ServerTests.ReadyTranscript(association, association.Length);
            var variables = new[] { new MmsServerVariable("D", "L$ST$Value$stVal", 0) };
            var set = new MmsServerDataSet("D", "L$Values", variables);
            var report = new MmsServerReportControl("D", "L$RP$R", "LIFECYCLE", set);
            var selected = new MmsServerControl("D", "L$CO$Selected", MmsControlModel.SboNormal);
            var pending = new MmsServerControl("D", "L$CO$Pending", MmsControlModel.DirectEnhanced);
            selected.Enabled = true; pending.SetHandlers(r => -1, r => -1); pending.Enabled = true;
            var server = new Iec61850ServerSession(channel, variables, controls: new[] { selected, pending },
                dataSets: new[] { set }, reports: new[] { report });
            server.Associate();
            Check(selected.Select(server, 0).Length > 2);
            Check(pending.Apply(server, 0, ControlTests.Command(true), true) == -1 && pending.HasPendingOperation);
            byte[] boolean = { 0x83, 1, 1 };
            Check(report.Write(server, "Resv", MmsEncoding.Single(boolean, 0, 3, 0x83), 0) == -1);
            channel.AppendRead(MmsEncoding.Tlv(0x8b)); channel.Segments(MmsEncoding.Release(false));
            Check(!server.ProcessRequest() && !pending.HasPendingOperation);
            Check(selected.Select(new object(), 0).Length > 2);
            Check(report.Write(new object(), "Resv", MmsEncoding.Single(boolean, 0, 3, 0x83), 0) == -1);
        }
        Loop(null);
        Console.WriteLine("LIFECYCLE: Conclude/ACSE Release, truncation/context/invoke/reject bounds, single Read service-error recovery PASS.");
    }
    internal static void SoakNormal() { Loop(null, 100); }
    private static void Loop(string peer, int iterations = 1)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var worker = Task.Run(() => {
            using (var socket = listener.AcceptSocket())
            {
                socket.NoDelay = true;
                var server = new Iec61850ServerSession(new MmsSocketChannel(socket), ServerTests.Model(0), timeoutMilliseconds: 5000);
                try { server.Associate(); while (server.ProcessRequest()) { } Check(!server.Faulted && !server.Associated); }
                finally { server.Close(); }
            }
        });
        try
        {
            if (peer == null)
            {
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    socket.Connect(IPAddress.Loopback, port);
                    socket.NoDelay = true;
                    var client = new Iec61850Client(new MmsSocketChannel(socket), maximumTpduSize: 128);
                    client.Associate();
                    for (int i = 0; i < iterations; i++)
                    {
                        long started = SoakRunner.StartExchange();
                        Check(!client.Read("NFTESTLD0", "GGIO1$ST$Status$stVal").Boolean);
                        SoakRunner.EndExchange(started);
                    }
                    client.Release();
                    Check(!client.Associated && !client.Faulted);
                }
            }
            else
            {
                using (var process = Process.Start(new ProcessStartInfo(peer, "releaseclient " + port + " 127.0.0.1") {
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true }))
                {
                    try
                    {
                        if (!process.WaitForExit(15000)) throw new TimeoutException("Native release.");
                        Check(process.ExitCode == 0 && process.StandardOutput.ReadToEnd().Contains("NATIVE RELEASE PASS"));
                    }
                    finally { if (!process.HasExited) { process.Kill(true); process.WaitForExit(); } }
                }
            }
            worker.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }
        finally { listener.Stop(); }
    }
    internal static void Interop(string peer)
    {
        Loop(peer);
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        using (var process = Process.Start(new ProcessStartInfo(peer, "server " + port) {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true }))
        {
            try
            {
                Check(process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult() == "READY");
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    socket.Connect(IPAddress.Loopback, port);
                    var client = new Iec61850Client(new MmsSocketChannel(socket), timeoutMilliseconds: 5000, maximumTpduSize: 128);
                    client.Associate(); client.Release(); Check(!client.Faulted && !client.Associated);
                }
                process.StandardInput.WriteLine("quit");
                Check(process.WaitForExit(10000) && process.ExitCode == 0);
            }
            finally { if (!process.HasExited) { process.Kill(true); process.WaitForExit(); } }
        }
        Console.WriteLine("NATIVE LIFECYCLE: original libiec61850 public API bidirectional release PASS.");
    }
}
