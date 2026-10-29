using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VagabondK.nF.DNP3;
using VagabondK.nF.DNP3.Mapping.Sample;
using VagabondK.nF.DNP3.TCP;

// 로컬 TCP 왕복과 외부 보드의 샘플/규모별 검증을 연결하는 peer입니다. 외부 모드는 지정한 테스트 주소만 사용합니다.
internal static class Peer
{
    internal static void Loopback()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        int port = ((IPEndPoint)listener.LocalEndPoint).Port;
        var data = new DeviceData();
        var mapped = new DeviceData.Dnp3Binding(data, new object(), 32);
        var station = new Dnp3Station(mapped.Database, 1, 2, mapped);
        var worker = Task.Run(() =>
        {
            using var socket = listener.Accept();
            var session = new Dnp3TcpSession(socket, station);
            try
            {
                while (true)
                {
                    if (!mapped.Publish()) throw new InvalidOperationException("Loopback overflow.");
                    session.RunOnce();
                }
            }
            catch (IOException error)
            {
                if (error.Message != "DNP3 TCP peer closed.") throw;
            }
        });
        try { Sample("127.0.0.1", 16, port); }
        finally
        {
            listener.Close();
            if (!worker.Wait(15000)) throw new TimeoutException("Loopback worker did not finish.");
        }
    }

    internal static void Sample(string ip, int rounds, int port = 20000)
    {
        if (rounds < 1 || rounds > 10000) throw new ArgumentOutOfRangeException(nameof(rounds));
        using var master = new NativeMaster(ip, port);
        string[] types = { "B", "D", "C", "F", "A", "O", "Q" };
        int[] groups = { 1, 3, 20, 21, 30, 10, 40 };
        int[] variations = { 2, 2, 1, 1, 6, 2, 4 };
        double[] values = { 1, 2, 1000, 999, 250, 1, 12.5 };
        for (int i = 0; i < types.Length; i++)
            Measurements(master.Scan(groups[i], variations[i]), types[i], 1, values[i], false, 0);
        master.Command("relay 9 3 0", "NOT_SUPPORTED");
        master.Command("relay 0 1 0", "NOT_SUPPORTED");
        master.Command("ao 4 0 1001 0", "OUT_OF_RANGE");
        for (int i = 0; i < rounds; i++)
        {
            bool enabled = (i & 1) != 0;
            double value = (i % 200) - 100.25;
            master.Command("relay 0 " + (enabled ? "3" : "4") + " " + (i & 1));
            master.Command("ao 4 0 " + value.ToString("R", CultureInfo.InvariantCulture) + " " + (i & 1));
            Measurements(master.Scan(10, 2), "O", 1, enabled ? 1 : 0, false, 0);
            Measurements(master.Scan(40, 4), "Q", 1, value, false, 0);
            var events = master.Scan(60, 2);
            Program.Check(events.Count(IsMeasurement) == 2, "Only output changes generate events.");
            Measurements(events, "O", 1, enabled ? 1 : 0, true, 1700000000001L + i * 2);
            Measurements(events, "Q", 1, value, true, 1700000000002L + i * 2);
            Program.Check(!master.Scan(60, 2).Any(IsMeasurement), "Confirmed events drained.");
        }
        Console.WriteLine("OpenDNP3 ORIGINAL mapping PASS: seven Point types, quality, timestamps, rejection, selected/direct control and event confirmation; rounds=" + rounds);
    }

    private static bool IsMeasurement(string s) { return s.Length > 2 && "BDCFAOQ".Contains(s[0]) && s[1] == ' '; }
    private static void Measurements(List<string> lines, string type, int count, double value, bool timed, long timestamp)
    {
        var points = lines.Where(s => s.StartsWith(type + " ", StringComparison.Ordinal)).Select(s => s.Split(' ')).ToArray();
        Program.Check(points.Length == count, type + " point count: expected " + count + ", got " + points.Length);
        Program.Check(points.Select(p => int.Parse(p[1], CultureInfo.InvariantCulture)).SequenceEqual(Enumerable.Range(0, count)), "Exact index ordering.");
        foreach (var point in points)
        {
            int index = int.Parse(point[1], CultureInfo.InvariantCulture);
            Program.Check(index >= 0 && index < count);
            Program.Check(double.Parse(point[2], CultureInfo.InvariantCulture) == value, "Independent exact value: " + string.Join(" ", point));
            int mask = type == "D" ? 63 : type == "B" || type == "O" ? 127 : 255;
            Program.Check((int.Parse(point[3], CultureInfo.InvariantCulture) & mask) == 1, "Online flags.");
            Program.Check(long.Parse(point[4], CultureInfo.InvariantCulture) == (timed ? timestamp : 0), "Timestamp.");
        }
    }

    internal static void Scaling(string ip, int count, int capacity, int rounds, string output)
    {
        if ((count != 8 && count != 32 && count != 128) || (capacity != 16 && capacity != 128) || rounds < 10 || rounds > 1000)
            throw new ArgumentException("Scaling count=8|32|128 capacity=16|128 rounds=10..1000.");
        using var diagnostic = new Diagnostic(ip);
        uint[] initial = diagnostic.Query(0);
        Program.Check(initial[0] == count && initial[1] == capacity && initial[7] == 100 &&
            initial[10] == 0 && initial[11] == 0x600D && initial[23] == 1000, "MCU identity/benchmark/debugger.");
        using var master = new NativeMaster(ip, 20000);
        Measurements(master.Scan(30, 6), "A", count, 0, false, 0);
        var reads = new List<double>();
        var commands = new List<double>();
        var direct = new List<double>();
        var selected = new List<double>();
        var heaps = new List<uint>();
        int generation = 0;
        void Cycle(bool measure)
        {
            double value = ++generation + 0.25;
            var watch = Stopwatch.StartNew();
            master.Command("ao 4 0 " + value.ToString("R", CultureInfo.InvariantCulture) + " " + (generation & 1));
            if (measure)
            {
                double elapsed = watch.Elapsed.TotalMilliseconds;
                commands.Add(elapsed);
                ((generation & 1) == 0 ? direct : selected).Add(elapsed);
            }
            watch.Restart();
            Measurements(master.Scan(30, 6), "A", count, value, false, 0);
            if (measure) reads.Add(watch.Elapsed.TotalMilliseconds);
            uint[] pending = diagnostic.Query(0);
            Program.Check(pending[12] == Math.Min(count, capacity) && pending[13] == (count > capacity ? 1U : 0U), "Event count/overflow.");
            Measurements(master.Scan(32, 8), "A", Math.Min(count, capacity), value, true, generation);
            long deadline = Environment.TickCount64 + 5000;
            while (diagnostic.Query(0)[12] != 0)
            {
                if (Environment.TickCount64 > deadline) throw new TimeoutException("Event confirmation drain.");
                Thread.Sleep(10);
            }
            diagnostic.Query(2);
            Program.Check(diagnostic.Query(0)[13] == 0, "Clear overflow only after drain.");
        }
        for (int i = 0; i < 3; i++) Cycle(false);
        diagnostic.Collect();
        heaps.Add(diagnostic.Collect());
        for (int i = 0; i < rounds; i++)
        {
            Cycle(true);
            if ((i + 1) % 10 == 0) heaps.Add(diagnostic.Collect());
        }
        var final = diagnostic.Query(0);
        Program.Check(final[14] == (count > capacity ? rounds + 3 : 0), "Exactly one publication result per command.");
        var expectedValues = Enumerable.Repeat(generation + 0.25, count).ToArray();
        var expectedFlags = Enumerable.Repeat(1, count).ToArray();
        int[] changedSlots = { 0, count / 2, count - 1, count / 2, count - 1, 0, 0 };
        for (int step = 0; step < changedSlots.Length; step++)
        {
            int changed = changedSlots[step];
            diagnostic.Query((byte)(step + 3));
            if (step < 3) expectedValues[changed] = -700 - changed;
            if (step == 3) expectedFlags[changed] = 4;
            if (step == 5) expectedValues[0] = -699.5;
            if (step == 6) expectedValues[0] = -699.25;
            var actual = master.Scan(30, 6).Where(s => s.StartsWith("A ", StringComparison.Ordinal))
                .Select(s => s.Split(' ')).ToArray();
            Program.Check(actual.Length == count, "Exact static point count after selective update.");
            for (int i = 0; i < count; i++)
                Program.Check(int.Parse(actual[i][1], CultureInfo.InvariantCulture) == i &&
                    double.Parse(actual[i][2], CultureInfo.InvariantCulture) == expectedValues[i] &&
                    int.Parse(actual[i][3], CultureInfo.InvariantCulture) == expectedFlags[i],
                    "Selective local value/flags change preserves every other static point.");
            var events = master.Scan(32, 8).Where(s => s.StartsWith("A ", StringComparison.Ordinal))
                .Select(s => s.Split(' ')).ToArray();
            bool generatesEvent = step != 4 && step != 5;
            Program.Check(events.Length == (generatesEvent ? 1 : 0), "Timestamp-only and exact deadband do not create an event.");
            if (generatesEvent)
                Program.Check(int.Parse(events[0][1], CultureInfo.InvariantCulture) == changed &&
                    double.Parse(events[0][2], CultureInfo.InvariantCulture) == expectedValues[changed] &&
                    int.Parse(events[0][3], CultureInfo.InvariantCulture) == expectedFlags[changed] &&
                    long.Parse(events[0][4], CultureInfo.InvariantCulture) == generation,
                    "Independent event index/value/quality/time.");
            long drainDeadline = Environment.TickCount64 + 5000;
            while (diagnostic.Query(0)[12] != 0)
            {
                if (Environment.TickCount64 > drainDeadline) throw new TimeoutException("Selective event confirmation.");
                Thread.Sleep(10);
            }
        }
        Console.WriteLine("DNP3 ROBUST PASS first/middle/last selective local updates, flags-only, time-only, exact/exceeded deadband, confirmation.");
        uint growth = heaps.Max() - heaps[0];
        var result = new
        {
            Points = count, EventCapacity = capacity, Rounds = rounds,
            ModelBytes = (long)initial[2] - initial[3],
            BindingDatabaseBytes = (long)initial[3] - initial[4],
            PublishSweeps = initial[7], PublishMilliseconds = initial[6],
            ProfileIterations = initial[23],
            SelectiveFirstMilliseconds = initial[16], SelectiveLastMilliseconds = initial[17],
            LockMilliseconds = initial[18], ModelReadMilliseconds = initial[19],
            ValueConstructionMilliseconds = initial[20], DatabaseUpdateMilliseconds = initial[21], LoopMilliseconds = initial[22],
            PublishPostGcGrowth = (long)initial[4] - initial[5],
            GcSamples = heaps, MaximumRetainedGrowthBytes = growth,
            OverflowObserved = count > capacity, PublishFailures = final[14],
            Read = Stats(reads), Control = Stats(commands), DirectControl = Stats(direct), SelectedControl = Stats(selected)
        };
        File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(result));
        Program.Check(growth == 0, "Retained heap grew after warmup.");
    }
    private static object Stats(List<double> values)
    {
        values.Sort();
        return new { Requests = values.Count, Min = values[0], Median = values[values.Count / 2], P95 = values[(int)Math.Ceiling(values.Count * .95) - 1], Max = values[values.Count - 1] };
    }

    private sealed class Diagnostic : IDisposable
    {
        private readonly UdpClient socket = new UdpClient();
        internal Diagnostic(string ip) { socket.Connect(ip, 20001); socket.Client.ReceiveTimeout = 5000; }
        internal uint[] Query(byte operation)
        {
            byte[] request = { 0x44, 0x4e, 0x4d, 0x31, operation };
            socket.Send(request, request.Length);
            IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            byte[] reply = socket.Receive(ref remote);
            Program.Check(reply.Length == 100 && reply.Take(4).SequenceEqual(request.Take(4)), "Diagnostic packet.");
            var values = new uint[24];
            for (int i = 0; i < values.Length; i++)
            {
                int p = 4 + i * 4;
                values[i] = (uint)reply[p] << 24 | (uint)reply[p + 1] << 16 | (uint)reply[p + 2] << 8 | reply[p + 3];
            }
            return values;
        }
        internal uint Collect()
        {
            uint counter = Query(1)[9];
            long deadline = Environment.TickCount64 + 5000;
            while (true)
            {
                var reply = Query(0);
                if (reply[9] > counter) return reply[8];
                if (Environment.TickCount64 > deadline) throw new TimeoutException("MCU GC.");
                Thread.Sleep(10);
            }
        }
        public void Dispose() { socket.Dispose(); }
    }

    private sealed class NativeMaster : IDisposable
    {
        private readonly Process process;
        private readonly BlockingCollection<string> lines = new BlockingCollection<string>();
        private readonly Task reader;
        private readonly Task<string> errors;
        private readonly Queue<string> history = new Queue<string>();
        private void Remember(string line)
        {
            history.Enqueue(line);
            while (history.Count > 100) history.Dequeue();
        }
        internal NativeMaster(string address, int port)
        {
            string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\.."));
            string executable = Path.Combine(root, @"bin\protocol-tools\master-build\Release\VagabondKDnp3Master.exe");
            using (var identity = Process.Start(new ProcessStartInfo(executable, "--decoder-identity") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true }))
            {
                var output = identity.StandardOutput.ReadToEndAsync();
                if (!identity.WaitForExit(5000)) { identity.Kill(true); throw new TimeoutException("Decoder identity."); }
                Program.Check(identity.ExitCode == 0 && output.GetAwaiter().GetResult().Trim() == "DECODER ORIGINAL", "Unmodified independent decoder required.");
            }
            process = Process.Start(new ProcessStartInfo(executable, address + " " + port + " full")
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            });
            errors = process.StandardError.ReadToEndAsync();
            reader = Task.Run(() =>
            {
                try { string line; while ((line = process.StandardOutput.ReadLine()) != null) lines.Add(line); }
                finally { lines.CompleteAdding(); }
            });
            try
            {
                var startup = Until("READY");
                if (!startup.Contains("CONNECTED")) Until("CONNECTED");
            }
            catch { Dispose(); throw; }
        }
        private List<string> Until(string prefix)
        {
            var result = new List<string>();
            long deadline = Environment.TickCount64 + 20000;
            while (Environment.TickCount64 < deadline)
            {
                if (!lines.TryTake(out string line, 100))
                {
                    if (lines.IsCompleted) throw new IOException("OpenDNP3 exited: " + string.Join("\n", result));
                    continue;
                }
                result.Add(line);
                Remember(line);
                if (line.StartsWith(prefix, StringComparison.Ordinal)) return result;
            }
            throw new TimeoutException("OpenDNP3 waiting for " + prefix + ": " + string.Join("\n", result));
        }
        private void Send(string command) { Remember("REQUEST " + command); process.StandardInput.WriteLine(command); process.StandardInput.Flush(); }
        internal List<string> Scan(int group, int variation)
        {
            Send(group + " " + variation);
            var result = Until("DONE ");
            Program.Check(result.Last() == "DONE SUCCESS", "Independent scan failed: " + string.Join("\n", history));
            Program.Check(result.Any(s => s.StartsWith("IIN ", StringComparison.Ordinal)), "IIN missing.");
            return result;
        }
        internal void Command(string command, string status = "SUCCESS")
        {
            Send(command);
            var result = Until("CMD ");
            Program.Check(result.Contains("STATUS " + status), "Control result " + command + ": " + string.Join("\n", result));
            if (status == "SUCCESS") Program.Check(result.Last() == "CMD SUCCESS");
        }
        public void Dispose()
        {
            bool clean = true;
            if (!process.HasExited)
            {
                Send("quit");
                clean = process.WaitForExit(5000);
                if (!clean) { process.Kill(true); process.WaitForExit(); }
            }
            reader.GetAwaiter().GetResult();
            string error = errors.GetAwaiter().GetResult();
            int exit = process.ExitCode;
            process.Dispose();
            lines.Dispose();
            if (!clean || exit != 0) throw new IOException("OpenDNP3 failed: " + error);
        }
    }
}
