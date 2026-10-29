using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using MQTTnet;
using MQTTnet.Server;

/// <summary>Eclipse Tahu 정식 클라이언트를 별도 프로세스로 실행하는 실물 상호운용 시험입니다.</summary>
internal static class TahuChecks
{
    internal static async Task Run(string host, string board, string script)
    {
        script = Path.GetFullPath(script);
        if (!File.Exists(script)) throw new FileNotFoundException("Tahu peer script.", script);
        var start = new ProcessStartInfo("node") {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(host);
        start.ArgumentList.Add(board);
        using (var broker = new MqttFactory().CreateMqttServer(new MqttServerOptionsBuilder().WithDefaultEndpoint()
            .WithDefaultEndpointBoundIPAddress(IPAddress.Parse(host)).WithDefaultEndpointPort(18883).Build()))
        {
            await broker.StartAsync();
            try
            {
                using (var peer = Process.Start(start))
                {
                    var errors = peer.StandardError.ReadToEndAsync();
                    Task<string> output = null;
                    try
                    {
                        string ready = await peer.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
                        if (ready != "READY")
                            throw new InvalidOperationException("Tahu not ready: " + ready + "\n" +
                                await errors.WaitAsync(TimeSpan.FromSeconds(5)));
                        output = peer.StandardOutput.ReadToEndAsync();
                        Console.WriteLine("READY");
                        if (Console.ReadLine() != "go") throw new InvalidOperationException("Tahu runner handshake.");
                        await peer.StandardInput.WriteLineAsync("go");
                        await peer.StandardInput.FlushAsync();
                        await peer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(100));
                        string text = await output;
                        Console.Write(text);
                        string error = await errors;
                        if (peer.ExitCode != 0 || !text.Contains("TAHU HARDWARE PASS"))
                            throw new InvalidOperationException("Eclipse Tahu interop failed: " + error);
                    }
                    finally
                    {
                        if (!peer.HasExited) { peer.Kill(true); await peer.WaitForExitAsync(); }
                        if (peer.ExitCode != 0)
                        {
                            if (output != null) Console.Error.Write(await output);
                            Console.Error.Write(await errors);
                        }
                    }
                }
            }
            finally { await broker.StopAsync(); }
        }
    }
}
