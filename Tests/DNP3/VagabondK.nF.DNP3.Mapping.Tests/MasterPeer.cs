using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

// 원본 Decoder 정체성을 확인한 네이티브 Outstation을 로컬에서 실행해 생성 Master 매핑의 수신 결과를 검증합니다.
internal static class MasterPeer
{
    internal static void Run()
    {
        int port;
        using (var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            port = ((IPEndPoint)listener.LocalEndPoint).Port;
        }
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\.."));
        string executable = Path.Combine(root, @"bin\protocol-tools\master-build\Release\VagabondKDnp3Outstation.exe");
        using (var identity = Process.Start(new ProcessStartInfo(executable, "--decoder-identity")
            { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true }))
        {
            var text = identity.StandardOutput.ReadToEndAsync();
            if (!identity.WaitForExit(5000)) { identity.Kill(true); throw new TimeoutException("Outstation identity."); }
            Program.Check(identity.ExitCode == 0 && text.GetAwaiter().GetResult().Trim() == "DECODER ORIGINAL");
        }
        using var process = Process.Start(new ProcessStartInfo(executable, port + " physical")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        });
        Task<string> errors = process.StandardError.ReadToEndAsync();
        try
        {
            while (true)
            {
                var line = process.StandardOutput.ReadLineAsync();
                if (!line.Wait(10000)) throw new TimeoutException("Independent Outstation readiness.");
                if (line.Result == null) throw new IOException("Independent Outstation exited.");
                if (line.Result == "READY") break;
            }
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            var validation = new MasterValidation();
            validation.Run("127.0.0.1", port, 8);
            Program.Check(validation.Checks > 6000 && validation.Events == 1344 && validation.Unsolicited == 448);
            Console.WriteLine("OpenDNP3 ORIGINAL Master mapping PASS: seven mapped types, all 56 records, duplicate index events, unsolicited, confirmations and two connections; checks=" + validation.Checks);
            process.StandardInput.WriteLine("quit");
            if (!process.WaitForExit(5000)) throw new TimeoutException("Independent Outstation shutdown.");
            Program.Check(process.ExitCode == 0, errors.GetAwaiter().GetResult());
            output.GetAwaiter().GetResult();
        }
        finally
        {
            if (!process.HasExited) { process.Kill(true); process.WaitForExit(); }
        }
    }
}
