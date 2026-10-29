using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

/// <summary>PC용 정상 통신·손상 입력·전송 수명주기 시나리오를 반복하고 자원 증가를 JSONL로 기록합니다.</summary>
/// <remarks>시나리오는 순차 실행합니다. 측정값은 PC 프로세스 기준이며 MCU의 힙이나 통신 성능을 나타내지 않습니다.</remarks>
internal static class SoakRunner
{
    private static Scenario currentScenario;

    // 시나리오 전체 회차와 실제 요청/응답 한 건의 지연 시간을 구분해 측정합니다.
    internal static long StartExchange() { return Stopwatch.GetTimestamp(); }
    internal static void EndExchange(long started)
    {
        if (currentScenario == null) return;
        double elapsed = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
        currentScenario.Exchanges++;
        currentScenario.ExchangeMilliseconds += elapsed;
        currentScenario.MaximumExchangeMilliseconds = Math.Max(currentScenario.MaximumExchangeMilliseconds, elapsed);
    }

    internal sealed class Resources
    {
        public long ManagedBytes { get; set; }
        public long AllocatedBytes { get; set; }
        public long PrivateBytes { get; set; }
        public int Handles { get; set; }
        public int Threads { get; set; }
    }

    internal sealed class Scenario
    {
        public string Name { get; set; }
        public long Cycles { get; set; }
        public double TotalMilliseconds { get; set; }
        public double MaximumMilliseconds { get; set; }
        public double CyclesPerSecond { get { return Cycles * 1000 / Math.Max(0.001, TotalMilliseconds); } }
        public long Exchanges { get; set; }
        public double ExchangeMilliseconds { get; set; }
        public double MaximumExchangeMilliseconds { get; set; }
        public double ExchangesPerSecond { get { return Exchanges * 1000 / Math.Max(0.001, ExchangeMilliseconds); } }
        private readonly Action body;

        internal Scenario(string name, Action body) { Name = name; this.body = body; }

        internal void Execute()
        {
            long start = Stopwatch.GetTimestamp();
            currentScenario = this;
            try { body(); }
            finally { currentScenario = null; }
            double elapsed = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            Cycles++;
            TotalMilliseconds += elapsed;
            MaximumMilliseconds = Math.Max(MaximumMilliseconds, elapsed);
        }

        internal void Reset()
        {
            Cycles = Exchanges = 0;
            TotalMilliseconds = MaximumMilliseconds = ExchangeMilliseconds = MaximumExchangeMilliseconds = 0;
        }
    }

    /// <summary><c>--soak</c> 인수가 있으면 반복 시험을 실행하고, 없으면 일반 회귀 시험이 계속되도록 false를 반환합니다.</summary>
    internal static bool TryRun(string[] args, string protocol, Action normal, Action malformed, Action lifecycle)
    {
        if (args.Length == 0 || args[0] != "--soak") return false;
        if (args.Length != 7)
            throw new ArgumentException("--soak <seconds> <sampleSeconds> <jsonl> <maxManagedGrowthBytes> <maxHandleGrowth> <maxThreadGrowth>");
        int seconds = int.Parse(args[1], CultureInfo.InvariantCulture);
        int sampleSeconds = int.Parse(args[2], CultureInfo.InvariantCulture);
        long memoryLimit = long.Parse(args[4], CultureInfo.InvariantCulture);
        int handleLimit = int.Parse(args[5], CultureInfo.InvariantCulture);
        int threadLimit = int.Parse(args[6], CultureInfo.InvariantCulture);
        if (seconds < 1 || seconds > 86400 || sampleSeconds < 1 || sampleSeconds > seconds ||
            memoryLimit < 0 || handleLimit < 0 || threadLimit < 0)
            throw new ArgumentOutOfRangeException("soak limits");
        Run(protocol, seconds, sampleSeconds, args[3], memoryLimit, handleLimit, threadLimit,
            new[] { new Scenario("normal", normal), new Scenario("malformed", malformed),
                new Scenario("transport-cycle", lifecycle) });
        return true;
    }

    // 누적 할당량이 아니라 기준 표본 대비 GC 후 생존 메모리·핸들·스레드 증가량을 제한합니다.
    internal static void CheckGrowth(Resources baseline, Resources current, long memoryLimit, int handleLimit, int threadLimit)
    {
        if (current.ManagedBytes - baseline.ManagedBytes > memoryLimit)
            throw new InvalidOperationException("Post-GC managed heap growth exceeded " + memoryLimit + " bytes.");
        if (current.Handles - baseline.Handles > handleLimit)
            throw new InvalidOperationException("Process handle growth exceeded " + handleLimit + ".");
        if (current.Threads - baseline.Threads > threadLimit)
            throw new InvalidOperationException("Process thread growth exceeded " + threadLimit + ".");
    }

    private static Resources Capture()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using (var process = Process.GetCurrentProcess())
        {
            process.Refresh();
            return new Resources {
                ManagedBytes = GC.GetTotalMemory(false),
                AllocatedBytes = GC.GetTotalAllocatedBytes(),
                PrivateBytes = process.PrivateMemorySize64,
                Handles = process.HandleCount,
                Threads = process.Threads.Count
            };
        }
    }

    internal static void Run(string protocol, int seconds, int sampleSeconds, string path,
        long memoryLimit, int handleLimit, int threadLimit, Scenario[] scenarios)
    {
        using (var log = new StreamWriter(path, false))
        {
            log.AutoFlush = true;
            string active = "warmup";
            var clock = new Stopwatch();
            try
            {
                Emit(log, new { Event = "start", Protocol = protocol, Runtime = Environment.Version.ToString(),
                    OS = Environment.OSVersion.ToString(), Seconds = seconds, SampleSeconds = sampleSeconds,
                    MaxManagedGrowthBytes = memoryLimit, MaxHandleGrowth = handleLimit, MaxThreadGrowth = threadLimit });
                // 최초 초기화 비용을 누수로 오인하지 않도록 세 차례 예열한 뒤 기준 표본과 계수를 초기화합니다.
                for (int round = 0; round < 3; round++)
                {
                    foreach (var scenario in scenarios)
                    {
                        active = scenario.Name;
                        scenario.Execute();
                    }
                    Emit(log, new { Event = "warmup", Round = round, Resources = Capture(), Scenarios = scenarios });
                }
                foreach (var scenario in scenarios) scenario.Reset();
                Resources baseline = Capture();
                Emit(log, new { Event = "baseline", Resources = baseline });
                clock.Start();
                long nextSample = sampleSeconds * 1000L;
                int samples = 0;
                while (clock.Elapsed.TotalSeconds < seconds)
                {
                    foreach (var scenario in scenarios)
                    {
                        active = scenario.Name;
                        scenario.Execute();
                    }
                    if (clock.ElapsedMilliseconds >= nextSample)
                    {
                        // 주기 표본 사이에 시험이 끝나도 종료 시점의 자원 증가를 반드시 검사합니다.
                        Sample(log, clock, baseline, scenarios, memoryLimit, handleLimit, threadLimit);
                        samples++;
                        nextSample = clock.ElapsedMilliseconds + sampleSeconds * 1000L;
                    }
                }
                Sample(log, clock, baseline, scenarios, memoryLimit, handleLimit, threadLimit);
                Emit(log, new { Event = "pass", Protocol = protocol, ElapsedSeconds = clock.Elapsed.TotalSeconds,
                    Samples = samples + 1, Scenarios = scenarios });
                Console.WriteLine("SOAK PASS " + protocol + ": " + clock.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) +
                    " s; samples=" + (samples + 1) + "; " + path);
            }
            catch (Exception error)
            {
                Emit(log, new { Event = "fail", Protocol = protocol, Scenario = active,
                    ElapsedSeconds = clock.Elapsed.TotalSeconds, Error = error.ToString(), Scenarios = scenarios });
                throw;
            }
        }
    }

    private static void Sample(StreamWriter log, Stopwatch clock, Resources baseline, Scenario[] scenarios,
        long memoryLimit, int handleLimit, int threadLimit)
    {
        Resources current = Capture();
        Emit(log, new { Event = "sample", ElapsedSeconds = clock.Elapsed.TotalSeconds, Resources = current,
            ManagedGrowthBytes = current.ManagedBytes - baseline.ManagedBytes,
            HandleGrowth = current.Handles - baseline.Handles, ThreadGrowth = current.Threads - baseline.Threads,
            AllocatedBytes = current.AllocatedBytes - baseline.AllocatedBytes, Scenarios = scenarios });
        CheckGrowth(baseline, current, memoryLimit, handleLimit, threadLimit);
    }

    private static void Emit(StreamWriter log, object record)
    {
        log.WriteLine(JsonSerializer.Serialize(record));
    }
}
