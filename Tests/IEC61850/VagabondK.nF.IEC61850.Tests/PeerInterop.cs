using VagabondK.nF.MMS;
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using VagabondK.nF.IEC61850;
using VagabondK.nF.IEC61850.Client;
using VagabondK.nF.MMS.TCP;

/// <summary>호출자가 지정한 외부 피어 실행 파일을 구동하여 관리 코드와의 상호 운용을 검사하고 통신 데이터를 캡처합니다.</summary>
/// <remarks>신뢰하는 시험 실행 파일만 사용하고 관련 포트·프로세스 수명을 다른 시험과 분리하십시오.</remarks>
internal static class PeerInterop
{
    internal static void Run(string executable)
    {
        CompositeTests.Interop(executable);
        WriteTests.Interop(executable);
        using (var identity = Start(executable, "--identity"))
        {
            if (!identity.WaitForExit(5000)) { identity.Kill(true); throw new Exception("Peer identity timeout"); }
            if (identity.ExitCode != 0 || identity.StandardOutput.ReadToEnd().Trim() !=
                "libiec61850 v1.6.2 ORIGINAL f0f797bf955da763353f7081c892a1ff6b55e5a1")
                throw new Exception("Unexpected independent peer identity");
        }
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        using (var server = Start(executable, "server " + port))
        {
            try
            {
                string ready = server.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                if (ready != "READY") throw new Exception("Peer not ready: " + ready);
                using (var native = Start(executable, "client " + port))
                {
                    if (!native.WaitForExit(15000)) { native.Kill(true); throw new Exception("Native baseline timeout"); }
                    if (native.ExitCode != 0 || !native.StandardOutput.ReadToEnd().Contains("NATIVE CLIENT PASS"))
                        throw new Exception("Native baseline failed: " + native.StandardError.ReadToEnd());
                }
                var readStart = new ProcessStartInfo("dotnet") {
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
                };
                foreach (string arg in new string[] { typeof(Program).Assembly.Location, "--read", "127.0.0.1",
                    port.ToString(), "NFTESTLD0", "GGIO1$ST$Status$stVal" })
                    readStart.ArgumentList.Add(arg);
                using (var read = Process.Start(readStart))
                {
                    if (!read.WaitForExit(15000)) { read.Kill(true); throw new Exception("CLI Read timeout"); }
                    string result = read.StandardOutput.ReadToEnd();
                    if (read.ExitCode != 0 || !result.Contains("MMS Read type=Boolean") || !result.Contains("True"))
                        throw new Exception("CLI Read failed: " + read.StandardError.ReadToEnd());
                    Console.WriteLine("Native baseline and --read CLI PASS.");
                }
                for (int connection = 0; connection < 2; connection++)
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    socket.Connect(IPAddress.Loopback, port);
                    var client = new Iec61850Client(new ThrottledChannel(new MmsSocketChannel(socket)),
                        maximumTpduSize: connection == 0 ? 1024 : 128);
                    client.Associate();
                    Console.WriteLine("Association PASS; PDU=" + client.NegotiatedPduSize);
                    DiscoveryTests.NativeServer(client);
                    for (int round = 0; round < 10; round++)
                    {
                        MmsValue value;
                        int accessCode;
                        if (!(connection == 0 ? client.Read("NFTESTLD0", "GGIO1$ST$Status$stVal").Boolean :
                            client.TryRead("NFTESTLD0", "GGIO1$ST$Status$stVal", out value, out accessCode) &&
                            accessCode == -1 && value.Boolean))
                            throw new Exception("Boolean mismatch");
                        if (!(connection == 0 ? client.Read("NFTESTLD0", "GGIO1$ST$Count$stVal").Integer == -123456 :
                            client.TryRead("NFTESTLD0", "GGIO1$ST$Count$stVal", out value, out accessCode) &&
                            accessCode == -1 && value.Integer == -123456))
                            throw new Exception("Integer mismatch");
                        if (!(connection == 0 ? client.Read("NFTESTLD0", "GGIO1$MX$Analog$mag").FloatingPoint == 12.5 :
                            client.TryRead("NFTESTLD0", "GGIO1$MX$Analog$mag", out value, out accessCode) &&
                            accessCode == -1 && value.FloatingPoint == 12.5))
                            throw new Exception("Float mismatch");
                        bool rejected = false;
                        if (connection == 0)
                        {
                            try { client.Read("NFTESTLD0", "GGIO1$ST$Missing$stVal"); }
                            catch (MmsAccessException error) { rejected = error.ErrorCode == 10; }
                        }
                        else rejected = !client.TryRead("NFTESTLD0", "GGIO1$ST$Missing$stVal", out value, out accessCode) &&
                            value == null && accessCode == 10;
                        if (!rejected || client.Faulted || !client.Associated) throw new Exception("Read error faulted association");
                    }
                }
                Console.WriteLine("ORIGINAL libiec61850 interop PASS: Read/TryRead, 60 scalar reads, 20 access errors, TCP partial I/O, TPDU128 segmentation and two associations.");
            }
            finally
            {
                if (!server.HasExited)
                {
                    server.StandardInput.WriteLine("quit");
                    server.StandardInput.Flush();
                    if (!server.WaitForExit(10000)) { server.Kill(true); throw new Exception("Peer shutdown timeout"); }
                }
                if (server.ExitCode != 0) throw new Exception("Peer server failed: " + server.StandardError.ReadToEnd());
            }
        }
    }

    internal static void Capture(string executable, bool controls = false, bool enhanced = false, bool sboEnhanced = false,
        bool data = false)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var backendReservation = new TcpListener(IPAddress.Loopback, 0);
        backendReservation.Start();
        int backendPort = ((IPEndPoint)backendReservation.LocalEndpoint).Port;
        backendReservation.Stop();
        using (var server = Start(executable, (data ? "dataserver " : sboEnhanced ? "sboenhancedserver " : enhanced ? "enhancedserver " : controls ? "controlserver " : "server ") + backendPort))
        {
            try
            {
                string ready = server.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                if (ready != "READY") throw new Exception("Peer not ready: " + ready);
                using (var client = Start(executable, data ? "dataclient " + port + " 127.0.0.1" :
                    sboEnhanced ? "sboenhancedclient " + port + " 127.0.0.1" :
                    enhanced ? "enhancedclient " + port + " 127.0.0.1" :
                    controls ? "controlclient " + port + " 127.0.0.1 expire" : "client " + port))
                using (var front = listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult())
                using (var back = new TcpClient())
                {
                    back.Connect(IPAddress.Loopback, backendPort);
                    Task outgoing = Pump(front.GetStream(), back.GetStream(), "C>");
                    Task incoming = Pump(back.GetStream(), front.GetStream(), "S>");
                    if (!client.WaitForExit(15000)) throw new Exception("Peer client timeout");
                    if (client.ExitCode != 0) throw new Exception(client.StandardError.ReadToEnd());
                    front.Close();
                    back.Close();
                    try { Task.WhenAll(outgoing, incoming).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
                    catch (IOException) { /* Closing both test proxy sockets ends pending reads. */ }
                    Console.WriteLine(client.StandardOutput.ReadToEnd());
                }
            }
            finally
            {
                listener.Stop();
                if (!server.HasExited)
                {
                    server.StandardInput.WriteLine("quit");
                    server.StandardInput.Flush();
                    if (!server.WaitForExit(10000)) { server.Kill(true); throw new Exception("Peer shutdown timeout"); }
                }
                if (server.ExitCode != 0) throw new Exception("Peer server failed: " + server.StandardError.ReadToEnd());
            }
        }
    }

    private sealed class ThrottledChannel : IMmsChannel
    {
        private readonly IMmsChannel inner;
        internal ThrottledChannel(IMmsChannel inner) { this.inner = inner; }
        public long Milliseconds { get { return inner.Milliseconds; } }
        public int Read(byte[] b, int p, int n, int timeout) { return inner.Read(b, p, Math.Min(n, 3), timeout); }
        public int Write(byte[] b, int p, int n, int timeout) { return inner.Write(b, p, Math.Min(n, 5), timeout); }
    }

    private static Process Start(string executable, string args)
    {
        return Process.Start(new ProcessStartInfo(executable, args) {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true
        });
    }

    private static async Task Pump(NetworkStream source, NetworkStream destination, string direction)
    {
        var buffer = new byte[8192];
        while (true)
        {
            int n = await source.ReadAsync(buffer, 0, buffer.Length);
            if (n == 0) return;
            Console.WriteLine(direction + Convert.ToHexString(buffer, 0, n));
            await destination.WriteAsync(buffer, 0, n);
        }
    }
}
