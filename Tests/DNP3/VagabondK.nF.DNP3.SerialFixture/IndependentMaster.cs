using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

// 이 드라이버는 COM 포트를 열거나 DNP3를 디코딩하지 않습니다. 두 작업은 수정하지 않은 원본 네이티브 OpenDNP3 라이브러리가 담당합니다.
// 원본 Decoder 정체성을 확인한 네이티브 Master를 실행하고 출력된 측정값을 검증합니다.
// COM6 또는 지정한 IPv4:20000의 보드가 필요하며 이 C# 드라이버 자체는 DNP3 디코딩을 수행하지 않습니다.
internal sealed class IndependentMaster : IDisposable
{
    private readonly Process _process;
    private readonly BlockingCollection<string> _lines = new BlockingCollection<string>();
    private readonly Thread _reader;
    private readonly Task<string> _errors;
    private int _checks;
    private readonly string _target;
    private static readonly string[] Types = { "B", "D", "C", "F", "A", "O", "Q" };
    private static readonly int[] Static = { 1, 3, 20, 21, 30, 10, 40 };
    private static readonly int[] Events = { 2, 4, 22, 23, 32, 11, 42 };

    private IndependentMaster(string target)
    {
        _target = target;
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\.."));
        string executable = Path.Combine(root, @"bin\protocol-tools\master-build\Release\VagabondKDnp3Master.exe");
        RequireOriginal(executable);
        _process = Process.Start(new ProcessStartInfo(executable, target == "COM6" ? "COM6 full" : target + " 20000 full")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        });
        _errors = _process.StandardError.ReadToEndAsync();
        _reader = new Thread(() =>
        {
            try
            {
                string line;
                while ((line = _process.StandardOutput.ReadLine()) != null)
                    _lines.Add(line);
            }
            finally
            {
                _lines.CompleteAdding();
            }
        });
        _reader.Start();
    }

    private void Check(bool condition, string reason)
    {
        if (!condition)
            throw new InvalidOperationException(reason);
        _checks++;
    }

    private List<string> Until(string prefix)
    {
        var result = new List<string>();
        long deadline = Environment.TickCount64 + 20000;
        while (Environment.TickCount64 < deadline)
        {
            if (!_lines.TryTake(out string line, 100))
            {
                if (_lines.IsCompleted)
                    throw new IOException("OpenDNP3 exited before " + prefix);
                continue;
            }
            Console.WriteLine("OpenDNP3: " + line);
            result.Add(line);
            if (line.StartsWith(prefix, StringComparison.Ordinal))
                return result;
        }
        throw new TimeoutException("OpenDNP3 waiting for " + prefix);
    }

    private void Send(string command)
    {
        Console.WriteLine("REQUEST " + command);
        _process.StandardInput.WriteLine(command);
        _process.StandardInput.Flush();
    }

    private List<string> Scan(int group, int variation, string range = "")
    {
        Send(group + " " + variation + range);
        var lines = Until("DONE ");
        Check(lines[lines.Count - 1] == "DONE SUCCESS", "Independent scan failed.");
        Check(lines.Any(line => line.StartsWith("IIN ", StringComparison.Ordinal) && line.Split(' ')[2] == "0"),
            "Independent scan has no successful IIN.");
        return lines;
    }

    private void Command(string command)
    {
        Send(command);
        var lines = Until("CMD ");
        Check(lines.Contains("CMD SUCCESS") && lines.Contains("STATUS SUCCESS"), "Independent control failed.");
    }

    private void Enable(bool enabled)
    {
        Send(enabled ? "enable" : "disable");
        var lines = Until("DONE ");
        Check(lines[lines.Count - 1] == "DONE SUCCESS", "Independent unsolicited configuration failed.");
    }

    private static bool Supported(int type, bool events, int variation)
    {
        if (type == 4)
            return events ? variation <= 8 : variation == 1 || variation == 2 || variation == 5 || variation == 6;
        if (type == 6)
            return events ? variation <= 8 : variation <= 4;
        if (type == 2 || type == 3)
            return events ? variation == 1 || variation == 2 || variation == 5 || variation == 6 : variation <= 2;
        return events ? variation <= 2 : variation == 2;
    }

    private static double Value(int type, int index, int generation)
    {
        if (type == 4 || type == 6)
            return ((index & 1) == 0 ? -1 : 1) * (index + generation + 0.25);
        return type == 0 || type == 5 ? (index + generation) & 1 :
            type == 1 ? 1 + ((index + generation) & 1) : 1000 + index + generation;
    }

    private static string[][] Measurements(IEnumerable<string> lines, int type)
    {
        return lines.Where(line => line.StartsWith(Types[type] + " ", StringComparison.Ordinal)).Select(line => line.Split(' ')).ToArray();
    }

    private void Values(List<string> lines, int type, bool events, int variation, int generation)
    {
        string[][] values = Measurements(lines, type);
        Check(values.Length == 8 && values.Select(v => v[1]).Distinct().Count() == 8, "Independent count/index uniqueness.");
        foreach (string[] point in values)
        {
            int index = int.Parse(point[1], CultureInfo.InvariantCulture);
            Check(index >= 0 && index < 8, "Independent point range.");
            bool analog = type == 4 || type == 6;
            double expected = Value(type, index, generation);
            if (analog && (events ? variation <= 4 : variation <= 2))
                expected = (int)expected;
            Check(double.Parse(point[2], CultureInfo.InvariantCulture) == expected, "Independent exact value: " + string.Join(" ", point));
            int flags = int.Parse(point[3], CultureInfo.InvariantCulture);
            Check((flags & (type == 1 ? 63 : type == 0 || type == 5 ? 127 : 255)) == 1, "Independent flags.");
            bool timed = events && (analog ? variation == 3 || variation == 4 || variation == 7 || variation == 8 :
                type == 2 || type == 3 ? variation >= 5 : variation == 2);
            Check(long.Parse(point[4], CultureInfo.InvariantCulture) == (timed ? 1700000000000 + generation : 0),
                "Independent timestamp.");
        }
    }

    private void Test()
    {
        Until("READY");
        // 반이중 조회를 시작하기 전에 초기 빈 비요청 응답과 자동 재시작·시각 동기화 작업을 처리합니다.
        Thread.Sleep(5000);
        while (_lines.TryTake(out string line))
            Console.WriteLine("OpenDNP3 startup: " + line);
        Enable(false);
        int generation = 0, variations = 0;
        for (int e = 0; e < 2; e++)
            for (int type = 0; type < 7; type++)
                for (int variation = 1; variation <= 8; variation++)
                {
                    bool events = e != 0;
                    if (!Supported(type, events, variation))
                        continue;
                    if (events)
                    {
                        Command("relay 65500 3 0");
                        generation++;
                    }
                    Values(Scan(events ? Events[type] : Static[type], variation), type, events, variation, generation);
                    variations++;
                    if (events)
                        Scan(60, 2);
                }
        Check(variations == 45, "All independent variations exercised.");
        for (int variation = 1; variation <= 4; variation++)
        {
            double value = variation <= 2 ? -123 : 1.25;
            Command("ao " + variation + " 0 " + value.ToString(CultureInfo.InvariantCulture) + " 1");
            Readback(value, 4);
        }
        Scan(60, 2);
        foreach (double value in new double[] { -2147483648.0, -123, -1, 0, 2147483647.0 })
        {
            Command("ao 1 0 " + value.ToString(CultureInfo.InvariantCulture) + " 1");
            Readback(value, 1);
            var values = Measurements(Scan(42, 1), 6);
            Check(values.Length == 1 && double.Parse(values[0][2], CultureInfo.InvariantCulture) == value, "Independent signed32 event.");
        }
        Scan(60, 2);
        Enable(true);
        Command("relay 65500 3 1");
        generation++;
        var unsolicited = Until("END UNS FINAL");
        for (int type = 0; type < 7; type++)
            Values(unsolicited, type, true,
            type == 4 || type == 6 ? 7 : type == 2 || type == 3 ? 5 : 2, generation);
        Enable(false);
        for (int i = 0; i < 100; i++)
            Values(Scan(30, 6), 4, false, 6, generation);
        for (int scenario = 0; scenario < 4; scenario++)
        {
            Command("relay 65500 3 0");
            if (scenario == 2)
                continue;
            for (int type = 0; type < 7; type++)
            {
                int stat = type == 4 ? 6 : type == 6 ? 4 : type == 2 || type == 3 ? 1 : 2;
                int ev = type == 4 || type == 6 ? 8 : type == 2 || type == 3 ? 5 : 2;
                BoundaryValues(Scan(Static[type], stat), type, false, scenario);
                BoundaryValues(Scan(Events[type], ev), type, true, scenario);
            }
        }
        Console.WriteLine("INDEPENDENT PHYSICAL MASTER PASS: unmodified OpenDNP3 3.1.2 " + _target + " -> MCU; " +
            "45 variations, 56 points, selected controls, signed32 boundaries, unsolicited, 100 reads; " +
            "48-bit time/quality/counter boundaries and same-time event ordering; checks=" + _checks);
    }

    private void BoundaryValues(List<string> lines, int type, bool events, int scenario)
    {
        string[][] points = Measurements(lines, type);
        int generations = events && scenario == 3 ? 2 : 1;
        Check(points.Length == 8 * generations, "Boundary measurement count.");
        var seen = new int[8];
        foreach (string[] point in points)
        {
            int index = int.Parse(point[1], CultureInfo.InvariantCulture);
            Check(index >= 0 && index < 8, "Boundary index range.");
            int occurrence = seen[index]++;
            Check(occurrence < generations, "Boundary duplicate.");
            int generation = 32 + (generations == 2 ? 2 + occurrence : scenario);
            var expected = VagabondK.nF.DNP3.SerialHost.SerialValidation.BoundaryValue(type, index, generation);
            double number = double.Parse(point[2], CultureInfo.InvariantCulture);
            Check(number == (type == 4 || type == 6 ? expected.AsAnalog() : expected.Raw), "Boundary value.");
            int flags = int.Parse(point[3], CultureInfo.InvariantCulture);
            Check((flags & (type == 1 ? 63 : type == 0 || type == 5 ? 127 : 255)) == expected.Flags, "Boundary quality.");
            Check(long.Parse(point[4], CultureInfo.InvariantCulture) == (events ? expected.Timestamp : 0),
                "Boundary timestamp/order.");
        }
    }

    private void Readback(double expected, int variation)
    {
        var values = Measurements(Scan(40, variation, " 0 0"), 6);
        Check(values.Length == 1 && double.Parse(values[0][2], CultureInfo.InvariantCulture) == expected, "Independent control readback.");
    }

    public void Dispose()
    {
        bool clean = true;
        if (!_process.HasExited)
        {
            Send("quit");
            clean = _process.WaitForExit(5000);
        }
        if (!clean)
        {
            _process.Kill(true);
            _process.WaitForExit();
        }
        _reader.Join();
        string errors = _errors.GetAwaiter().GetResult();
        int exit = _process.ExitCode;
        _process.Dispose();
        _lines.Dispose();
        if (!clean || exit != 0)
            throw new IOException("OpenDNP3 failed: " + errors);
    }

    internal static int Run(string target)
    {
        try
        {
            using var test = new IndependentMaster(target);
            test.Test();
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("INDEPENDENT PHYSICAL MASTER FAIL: " + error);
            return 1;
        }
    }

    internal static void RequireOriginal(string executable)
    {
        using var identity = Process.Start(new ProcessStartInfo(executable, "--decoder-identity")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        });
        var output = identity.StandardOutput.ReadToEndAsync();
        if (!identity.WaitForExit(5000))
        {
            identity.Kill(true);
            identity.WaitForExit();
            throw new TimeoutException("Independent decoder identity timed out.");
        }
        if (identity.ExitCode != 0 || output.GetAwaiter().GetResult().Trim() != "DECODER ORIGINAL")
            throw new InvalidOperationException("Physical independent tests require the unmodified OpenDNP3 decoder.");
    }
}
