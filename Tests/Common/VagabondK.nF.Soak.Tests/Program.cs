using System;
using System.IO;
using System.Text.Json;

/// <summary>프로토콜 구현과 독립적으로 반복 시험 실행기의 경계값·계측·실패 기록을 검증합니다.</summary>
internal static class Program
{
    private static int checks;
    private static void Check(bool ok) { checks++; if (!ok) throw new Exception("Soak runner assertion " + checks); }
    private static void Reject<T>(Action body) where T : Exception
    {
        try { body(); }
        catch (T) { checks++; return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private static void Main()
    {
        // 허용량과 정확히 같은 증가는 통과하고, 메모리·핸들·스레드 중 하나라도 초과하면 실패해야 합니다.
        var baseline = new SoakRunner.Resources { ManagedBytes = 100, Handles = 20, Threads = 5 };
        var boundary = new SoakRunner.Resources { ManagedBytes = 200, Handles = 22, Threads = 6 };
        SoakRunner.CheckGrowth(baseline, boundary, 100, 2, 1);
        Reject<InvalidOperationException>(() => SoakRunner.CheckGrowth(baseline, boundary, 99, 2, 1));
        Reject<InvalidOperationException>(() => SoakRunner.CheckGrowth(baseline, boundary, 100, 1, 1));
        Reject<InvalidOperationException>(() => SoakRunner.CheckGrowth(baseline, boundary, 100, 2, 0));
        SoakRunner.CheckGrowth(boundary, baseline, 0, 0, 0);
        Action empty = () => { };
        Check(!SoakRunner.TryRun(new string[0], "test", empty, empty, empty));
        Reject<ArgumentException>(() => SoakRunner.TryRun(new[] { "--soak" }, "test", empty, empty, empty));
        Reject<ArgumentOutOfRangeException>(() => SoakRunner.TryRun(
            new[] { "--soak", "1", "2", "unused", "0", "0", "0" }, "test", empty, empty, empty));
        Reject<ArgumentOutOfRangeException>(() => SoakRunner.TryRun(
            new[] { "--soak", "0", "1", "unused", "0", "0", "0" }, "test", empty, empty, empty));
        Reject<ArgumentOutOfRangeException>(() => SoakRunner.TryRun(
            new[] { "--soak", "1", "1", "unused", "-1", "0", "0" }, "test", empty, empty, empty));
        // 시나리오 회차와 통신 건수는 서로 다른 계수이며 Reset 이후 이전 측정이 남지 않아야 합니다.
        var scenario = new SoakRunner.Scenario("normal", empty);
        scenario.Execute();
        Check(scenario.Cycles == 1 && scenario.MaximumMilliseconds >= 0 && scenario.CyclesPerSecond > 0);
        scenario.Reset();
        Check(scenario.Cycles == 0 && scenario.TotalMilliseconds == 0 && scenario.MaximumMilliseconds == 0);
        var exchange = new SoakRunner.Scenario("exchange", () => {
            long started = SoakRunner.StartExchange();
            SoakRunner.EndExchange(started);
        });
        exchange.Execute();
        Check(exchange.Exchanges == 1 && exchange.ExchangeMilliseconds >= 0 &&
            exchange.MaximumExchangeMilliseconds == exchange.ExchangeMilliseconds && exchange.ExchangesPerSecond > 0);
        SoakRunner.EndExchange(SoakRunner.StartExchange());
        Check(exchange.Exchanges == 1);
        exchange.Reset();
        Check(exchange.Exchanges == 0 && exchange.ExchangeMilliseconds == 0 && exchange.MaximumExchangeMilliseconds == 0);
        string path = Path.GetTempFileName();
        try
        {
            SoakRunner.Run("test", 1, 1, path, 8 * 1024 * 1024, 32, 8, new[] { scenario });
            string last = null;
            int samples = 0;
            foreach (string line in File.ReadLines(path))
                using (JsonDocument record = JsonDocument.Parse(line))
                {
                    last = record.RootElement.GetProperty("Event").GetString();
                    if (last == "sample") samples++;
                }
            Check(last == "pass" && samples >= 1 && scenario.Cycles > 0);
            // 예열 세 회 이후 첫 측정 회차에 실패를 주입해 마지막 JSONL 레코드까지 확인합니다.
            int calls = 0;
            Reject<ApplicationException>(() => SoakRunner.Run("test", 1, 1, path, 0, 0, 0,
                new[] { new SoakRunner.Scenario("injected", () => {
                    if (++calls == 4) throw new ApplicationException("Injected failure.");
                }) }));
            string[] lines = File.ReadAllLines(path);
            using (JsonDocument record = JsonDocument.Parse(lines[lines.Length - 1]))
            {
                Check(record.RootElement.GetProperty("Event").GetString() == "fail");
                Check(record.RootElement.GetProperty("Scenario").GetString() == "injected");
                Check(record.RootElement.GetProperty("Error").GetString().Contains("Injected failure."));
            }
            calls = 0;
            // GC 이후에도 살아 있는 2 MiB를 의도적으로 유지해 1 MiB 증가 제한의 검출을 검증합니다.
            byte[] retained = null;
            Reject<InvalidOperationException>(() => SoakRunner.Run("test", 1, 1, path, 1024 * 1024, 32, 8,
                new[] { new SoakRunner.Scenario("retained", () => {
                    if (++calls == 4) retained = new byte[2 * 1024 * 1024];
                }) }));
            lines = File.ReadAllLines(path);
            using (JsonDocument record = JsonDocument.Parse(lines[lines.Length - 1]))
            {
                Check(record.RootElement.GetProperty("Event").GetString() == "fail");
                Check(record.RootElement.GetProperty("Error").GetString().Contains("Post-GC"));
            }
            GC.KeepAlive(retained);
        }
        finally { File.Delete(path); }
        Console.WriteLine("SOAK RUNNER PASS: " + checks + " checks.");
    }
}
