using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using VagabondK.nF.DNP3;
using VagabondK.nF.DNP3.TCP;

// Go/Python 독립 피어 실행 파일과 스크립트를 인수로 받아 Analog 계열의 Static/Event·실수·시각을 비교합니다.
// 외부 피어 도구가 미리 준비되어야 하며 알려진 제한 보고 모드는 완전한 상호운용 성공과 구분합니다.
internal static class Program
{
    private static readonly ulong[] BoundaryBits = {
        0, 0x8000000000000000, 1, 0x8000000000000001,
        0x000fffffffffffff, 0x800fffffffffffff, 0x0010000000000000, 0x8010000000000000,
        0x36a0000000000000, 0xb6a0000000000000, 0x380fffffc0000000, 0xb80fffffc0000000,
        0x3810000000000000, 0xb810000000000000, 0x3ff4000000000000, 0xbff4000000000000,
        0x47efffffe0000000, 0xc7efffffe0000000, 0x3ff0000000000001, 0xbff0000000000001
    };
    private const int Count = 84;

    private static double Number(int i)
    {
        if (i < BoundaryBits.Length)
            return BitConverter.Int64BitsToDouble(unchecked((long)BoundaryBits[i]));
        uint bits = unchecked((uint)(i - 20) * 2654435761U) & 0xfeffffffU;
        return BitConverter.Int32BitsToSingle(unchecked((int)bits));
    }

    private static long Time(int i) => new long[] { 0, 1, 65535, 65536, 4294967295L,
        4294967296L, 281474976710654L, 281474976710655L }[i & 7];

    private static byte Flags(int i) => (byte)(1 | ((i * 37) & 254));

    private static Dnp3PointType Type(int group) => group == 30 || group == 32 ?
        Dnp3PointType.Analog : Dnp3PointType.AnalogOutputStatus;

    private static bool Event(int group) => group == 32 || group == 42;

    private static bool Narrow(int group, int variation) => group == 30 ? variation == 5 :
        group == 40 ? variation == 3 : variation == 5 || variation == 7;

    private static bool Timed(int group, int variation) => Event(group) && variation >= 7;

    private static IEnumerable<(int Group, int Variation)> Matrix()
    {
        foreach (int group in new[] { 30, 40, 32, 42 })
            foreach (int variation in group == 30 ? new[] { 5, 6 } :
                group == 40 ? new[] { 3, 4 } : new[] { 5, 6, 7, 8 })
                yield return (group, variation);
    }

    private static void Require(bool value, string reason)
    {
        if (!value)
            throw new InvalidOperationException(reason);
    }

    private sealed class Handler : IDnp3MasterHandler
    {
        internal readonly List<Dnp3Measurement> Values = new();

        public void OnMeasurements(Dnp3Measurement[] values, Dnp3ResponseInfo response)
        {
            Require(!response.Unsolicited, "Unexpected unsolicited numeric data.");
            Values.AddRange(values);
        }
    }

    private static void Compare(Handler handler, int group, int variation)
    {
        Require(handler.Values.Count == Count, "Numeric measurement count.");
        var seen = new bool[Count];
        foreach (var point in handler.Values)
        {
            int i = point.Index;
            Require(i < Count && !seen[i] && point.Type == Type(group) && point.IsEvent == Event(group),
                "Numeric type/index/event uniqueness.");
            seen[i] = true;
            double expected = Narrow(group, variation) ? (double)(float)Number(i) : Number(i);
            Require(point.Value.Raw == unchecked((ulong)BitConverter.DoubleToInt64Bits(expected)),
                $"Exact IEEE754 mismatch g{group}v{variation} index={i}: {point.Value.Raw:x16}");
            Require(point.Value.Flags == Flags(i), "Numeric flags mismatch.");
            long expectedTime = Timed(group, variation) ? Math.Max(1, Time(i)) : 0;
            Require(point.Value.Timestamp == expectedTime,
                $"Numeric timestamp mismatch g{group}v{variation} index={i}: got {point.Value.Timestamp}, want {expectedTime}.");
        }
    }

    private static Process Start(string executable, IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new IOException("Peer process did not start.");
    }

    private static void Finish(Process process, Task<string> errors)
    {
        Require(process.WaitForExit(10000), "Peer did not finish.");
        string stderr = errors.GetAwaiter().GetResult();
        Require(process.ExitCode == 0, "Independent peer failed: " + stderr);
        if (!string.IsNullOrWhiteSpace(stderr))
            Console.WriteLine(stderr.Trim());
    }

    private static void Stop(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(true);
            process.WaitForExit();
        }
    }

    private static int RunPeerMaster(string executable, string pythonScript, string pythonSource, int group, int variation)
    {
        var points = new Dnp3PointDefinition[Count];
        for (ushort i = 0; i < Count; i++)
            points[i] = new Dnp3PointDefinition(Type(group), i, Dnp3Value.Analog(0, 0),
                staticVariation: (byte)(group == 30 || group == 32 ? 6 : 4), eventVariation: 8);
        var db = new Dnp3Database(points, 128);
        for (ushort i = 0; i < Count; i++)
            Require(db.Update(Type(group), i, Dnp3Value.Analog(Number(i), Flags(i), Time(i))), "Numeric queue overflow.");
        var station = new Dnp3Station(db, 1, 2, fragmentSize: 256);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var args = new List<string>();
        if (pythonScript != null)
        {
            args.Add(pythonScript);
            args.Add(pythonSource);
        }
        args.AddRange(new[] { "master", port.ToString(), group.ToString(), variation.ToString() });
        using var peer = Start(executable, args);
        var errors = peer.StandardError.ReadToEndAsync();
        var output = peer.StandardOutput.ReadToEndAsync();
        try
        {
            using var socket = listener.AcceptSocketAsync().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
            var session = new Dnp3TcpSession(socket, station);
            long deadline = Environment.TickCount64 + 30000;
            while (!peer.HasExited)
            {
                Require(Environment.TickCount64 < deadline, "Peer master deadline.");
                try
                {
                    session.RunOnce(10);
                }
                catch (IOException) when (socket.Poll(1000, SelectMode.SelectRead) && socket.Available == 0)
                {
                    break;
                }
                catch (SocketException error) when (error.SocketErrorCode == SocketError.ConnectionReset ||
                    error.SocketErrorCode == SocketError.ConnectionAborted)
                {
                    break;
                }
            }
            Finish(peer, errors);
            string text = output.GetAwaiter().GetResult();
            Require(text.Contains($"NUMERIC/QUALITY PASS {Count}"), "Peer did not confirm all numeric/quality comparisons: " + text);
            Require(session.Decoder.CrcErrors == 0, "Numeric CRC error.");
            Console.WriteLine(text.Trim());
            string limit = text.Split('\n').FirstOrDefault(line => line.StartsWith("TIME LIMIT "));
            return limit == null ? 0 : int.Parse(limit.Split(' ')[2].TrimEnd(':'));
        }
        finally
        {
            listener.Stop();
            Stop(peer);
        }
    }

    private static void RunGoStation(string executable, int group, int variation)
    {
        using var peer = Start(executable, new[] { "station", "0", group.ToString(), variation.ToString() });
        var errors = peer.StandardError.ReadToEndAsync();
        try
        {
            string ready = peer.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
            Require(ready != null && ready.StartsWith("READY "), "Go station not ready: " + ready);
            int port = int.Parse(ready.Substring(6));
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Connect(IPAddress.Loopback, port);
            var handler = new Handler();
            var master = new Dnp3Master(2, 1, handler);
            var session = new Dnp3TcpSession(socket, master);
            session.Send(master.ReadGroup((byte)group, (byte)variation, Environment.TickCount64));
            long deadline = Environment.TickCount64 + 30000;
            while (master.Busy)
            {
                Require(Environment.TickCount64 < deadline, "VagabondK.nF master deadline.");
                session.RunOnce(10);
            }
            Require(master.Result == Dnp3MasterResult.Success && master.Iin2 == 0, "VagabondK.nF numeric read failed.");
            Compare(handler, group, variation);
            Require(session.Decoder.CrcErrors == 0, "Numeric CRC error.");
            peer.StandardInput.WriteLine("quit");
            peer.StandardInput.Flush();
            Finish(peer, errors);
            Console.WriteLine($"VagabondK.nF master <- Go station g{group}v{variation}: NUMERIC/QUALITY PASS {Count}; timestamps exact.");
        }
        finally
        {
            Stop(peer);
        }
    }

    private static int Main(string[] args)
    {
        try
        {
            Require(args.Length == 4 || args.Length == 5 && args[4] == "--report-known-limits",
                "Usage: ExtraPeers <go-peer.exe> <python.exe> <python-peer.py> <dnp3py-src> [--report-known-limits].");
            int timeLimits = 0;
            foreach (var (group, variation) in Matrix())
            {
                RunPeerMaster(args[0], null, null, group, variation);
                timeLimits += RunPeerMaster(args[1], args[2], args[3], group, variation);
                RunGoStation(args[0], group, variation);
            }
            Console.WriteLine("EXTRA INDEPENDENT NUMERIC TCP PASS: unmodified otfabric/go-dnp3 v1.4.1 both roles, dnp3py v0.6.0 master; " +
                "12 variations x 84 points x 3 paths = 3024 exact numeric/quality comparisons. " +
                "Includes signed zero, float32/64 subnormals and normal boundaries; PC loopback, NOT MCU/UART validation.");
            Console.WriteLine($"TIMESTAMPS: {3024 - timeLimits} exact matches; {timeLimits} independent Python callback limitations NOT passed.");
            Console.WriteLine("Go station API treats time=0 as automatic stamping: its transmit vectors use 1 instead; " +
                "literal zero is checked only in the Go/Python master receive directions.");
            if (timeLimits != 0)
            {
                Console.WriteLine("VERIFICATION INCOMPLETE: timestamp limitations remain; no unconditional PASS.");
                return args.Length == 5 ? 0 : 1;
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("EXTRA PEER FAIL: " + error);
            return 1;
        }
    }
}
