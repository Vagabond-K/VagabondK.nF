using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

// 독립 Outstation과 오류 주입 프록시를 함께 실행하여 장시간 매핑과 재접속 경로를 검사하고 종료 시 자식 프로세스를 정리합니다.
internal static class MasterSoakPeer
{
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
    internal static void Run()
    {
        int port = FreePort(), upstream = FreePort();
        while (upstream == port) upstream = FreePort();
        string executable = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            @"..\..\..\..\..\..\bin\protocol-tools\master-build\Release\VagabondKDnp3Outstation.exe"));
        Process peer = null;
        Task<string> output = null, errors = null;
        void Start()
        {
            peer = Process.Start(new ProcessStartInfo(executable, upstream + " soak")
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true
            });
            errors = peer.StandardError.ReadToEndAsync();
            while (true)
            {
                var line = peer.StandardOutput.ReadLineAsync();
                if (!line.Wait(10000) || line.Result == null) throw new IOException("Soak peer readiness.");
                if (line.Result == "READY") break;
            }
            output = peer.StandardOutput.ReadToEndAsync();
        }
        void Stop()
        {
            if (peer == null) return;
            try
            {
                peer.StandardInput.WriteLine("quit");
                if (!peer.WaitForExit(5000)) throw new TimeoutException("Soak peer shutdown.");
                Program.Check(peer.ExitCode == 0, errors.GetAwaiter().GetResult());
                output.GetAwaiter().GetResult();
            }
            finally
            {
                if (!peer.HasExited) { peer.Kill(true); peer.WaitForExit(); }
                peer.Dispose();
                peer = null;
            }
        }
        using var proxy = new MasterSoakProxy("127.0.0.1", port, upstream);
        try
        {
            Start();
            var validation = new MasterValidation();
            for (int i = 0; i < 3; i++) validation.Run("127.0.0.1", port, 8);
            Program.Check(validation.Generation == 72 && validation.Events == 4032, "Persistent model across generation 32 boundary.");
            validation.OpenFault("127.0.0.1", port);
            Program.Check(proxy.ResetConnections() == 1, "Exactly one live TCP connection reset.");
            Program.Check(validation.AwaitFault() is 1 or 2, "Reset faults existing session.");
            validation.Run("127.0.0.1", port, 8);
            Program.Check(validation.Generation == 96, "Same peer generation preserved after TCP reset.");
            validation.OpenFault("127.0.0.1", port);
            Stop();
            Program.Check(validation.AwaitFault() is 1 or 2, "Peer shutdown faults existing session.");
            Start();
            validation.PeerRestarted();
            validation.Run("127.0.0.1", port, 8);
            Program.Check(validation.Generation == 24 && validation.Events == 6720 && validation.Unsolicited == 2240,
                "Explicit peer restart resync without resetting lifetime totals/model.");
            proxy.Healthy();
            Stop();
            Console.WriteLine("OpenDNP3 ORIGINAL Master soak preparation PASS: persistent model, generation 72, TCP reset, peer restart and resumed receipt.");
        }
        finally { Stop(); }
    }
}
