using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using VagabondK.nF.MMS;
using VagabondK.nF.MMS.Client;
using VagabondK.nF.MMS.Mapping;
using VagabondK.nF.MMS.Server;
using VagabondK.nF.MMS.TCP;
using VagabondK.nF.IEC61850;
using VagabondK.nF.IEC61850.Client;
using VagabondK.nF.IEC61850.Mapping;
using VagabondK.nF.IEC61850.Server;

// 독립 libiec61850 peer(peer.c map* 모드)와 실제 생성 바인딩을 맞대는 상호운용 시험 모델입니다.
// 이름·초기값·명령 번호(적용 완료 시 Command=1000+명령)는 peer.c와 고정 계약이며 보드와 PC가 같은 소스를 공유합니다. 격리 시험망 전용입니다.
internal delegate void InteropApply(object report);
// 엔진 수신 콜백은 MMS 작업 기한 안에서 실행됩니다. NaN 검증 예외의 nanoCLR 디버그 출력 지연이
// 3초 기한을 소모한 fixture 문제를 피하도록 독립 스냅샷을 고정 8슬롯에 보관하고 적용은 작업 종료 후 수행합니다.
// 엔진·펌웨어·통신 기한을 바꾸는 대책이 아니며 큐가 넘치면 이 시험은 실패합니다.
internal sealed class InteropPendingReports
{
    private readonly object[] items = new object[8];
    private int count;
    internal int Count { get { return count; } }
    internal void Add(object report)
    {
        if (count == items.Length) throw new InvalidOperationException("Too many pending reports.");
        items[count++] = report;
    }
    internal void Drain(InteropApply apply)
    {
        for (int n = 0; n < count; n++) { object report = items[n]; items[n] = null; apply(report); }
        count = 0;
    }
}

// 실제 MmsSocketChannel을 감싸 각 읽기·쓰기의 시작 시각·소요 시간·결과만 InteropStats에 기록하는 시험 전용 채널입니다.
internal sealed class InteropTraceChannel : IMmsTimeoutChannel
{
    private readonly MmsSocketChannel inner;
    internal InteropTraceChannel(Socket socket) { inner = new MmsSocketChannel(socket); }
    public long Milliseconds { get { return inner.Milliseconds; } }
    public int Read(byte[] buffer, int offset, int count, int timeoutMilliseconds)
    {
        long started = Environment.TickCount64; int n = -1;
        try { n = inner.Read(buffer, offset, count, timeoutMilliseconds); return n; }
        finally { InteropStats.Op(1, count, timeoutMilliseconds, started, Environment.TickCount64 - started, n); }
    }
    public int Write(byte[] buffer, int offset, int count, int timeoutMilliseconds)
    {
        long started = Environment.TickCount64; int n = -1;
        try { n = inner.Write(buffer, offset, count, timeoutMilliseconds); return n; }
        finally { InteropStats.Op(2, count, timeoutMilliseconds, started, Environment.TickCount64 - started, n); }
    }
    public bool TryRead(byte[] buffer, int offset, int count, int timeoutMilliseconds, out int transferred)
    {
        long started = Environment.TickCount64; long result = -1; transferred = 0;
        try { bool ok = inner.TryRead(buffer, offset, count, timeoutMilliseconds, out transferred); result = ok ? transferred : -2; return ok; }
        finally { InteropStats.Op(1, count, timeoutMilliseconds, started, Environment.TickCount64 - started, result); }
    }
    public bool TryWrite(byte[] buffer, int offset, int count, int timeoutMilliseconds, out int transferred)
    {
        long started = Environment.TickCount64; long result = -1; transferred = 0;
        try { bool ok = inner.TryWrite(buffer, offset, count, timeoutMilliseconds, out transferred); result = ok ? transferred : -2; return ok; }
        finally { InteropStats.Op(2, count, timeoutMilliseconds, started, Environment.TickCount64 - started, result); }
    }
}

internal static class InteropNames
{
    internal const string Domain = "NFTESTLD0";
    internal const int Port = 18104, PeerPort = 18105;
    internal const long InitialTime = 1700000000125L, ChangedTime = 1700000001000L;
    internal const IecQuality InitialQuality = IecQuality.Invalid | IecQuality.Test | IecQuality.OldData;
}

internal delegate bool InteropCondition();

internal static class InteropStats
{
    internal static int Checks, Applied, Rejected, Runs;
    // 실패 위치 진단: 현재 단계, 단계 시작 이후 보고 수신 시각, 최근 채널 동작 8개(종류·요청 바이트·허용 ms·시작/소요 ms·결과).
    // 숫자 배열만 고정 크기로 유지하므로 반복 중 할당이 늘지 않으며 문자열은 실패 시에만 만듭니다.
    private static string step = "idle";
    private static int detail, reports, opCount;
    private static long stepAt, reportAt = -1;
    private static readonly long[] ops = new long[8 * 6];
    internal static void Mark(string name, int value)
    { step = name; detail = value; stepAt = Environment.TickCount64; reports = 0; reportAt = -1; opCount = 0; }
    internal static void Reported() { reports++; reportAt = Environment.TickCount64 - stepAt; }
    internal static void Op(long kind, long count, long timeout, long started, long elapsed, long result)
    {
        int p = (opCount++ % 8) * 6;
        ops[p] = kind; ops[p + 1] = count; ops[p + 2] = timeout; ops[p + 3] = started - stepAt; ops[p + 4] = elapsed; ops[p + 5] = result;
    }
    internal static string Describe()
    {
        string text = " | run=" + Runs + " step=" + step + (detail != 0 ? "(" + detail + ")" : "") + " +" + (Environment.TickCount64 - stepAt) +
            "ms reports=" + reports + " lastReport=+" + reportAt + "ms ops=" + opCount + ":";
        int first = opCount > 8 ? opCount - 8 : 0;
        for (int n = first; n < opCount; n++)
        {
            int p = (n % 8) * 6;
            text += " " + (ops[p] == 1 ? "R" : "W") + ops[p + 1] + "/" + ops[p + 2] + "@+" + ops[p + 3] + " took" + ops[p + 4] + "->" +
                (ops[p + 5] == -1 ? "exception" : ops[p + 5] == -2 ? "notready" : ops[p + 5].ToString());
        }
        return text;
    }
    internal static void Ensure(bool value, string step)
    { Checks++; if (!value) throw new InvalidOperationException("Interop assertion " + Checks + ": " + step); }
    internal static void Until(InteropCondition condition, int milliseconds, string step)
    {
        long deadline = Environment.TickCount64 + milliseconds;
        while (!condition())
        {
            if (Environment.TickCount64 >= deadline) Ensure(false, step);
            Thread.Sleep(20);
        }
        Checks++;
    }
}

[Iec61850ServerMap]
internal partial class InteropIecLocal
{
    [Iec61850Point("NFTESTLD0", "GGIO1$ST$Status$stVal")] public bool Status = true;
    [Iec61850Point("NFTESTLD0", "GGIO1$ST$Status$q")] public IecQuality Quality = InteropNames.InitialQuality;
    [Iec61850Point("NFTESTLD0", "GGIO1$ST$Status$t", TimeQuality = nameof(TimeQuality))] public long Time = InteropNames.InitialTime;
    public byte TimeQuality = 0x3f;
    [Iec61850Point("NFTESTLD0", "GGIO1$ST$Pos$stVal")] public IecDoublePoint Pos = IecDoublePoint.On;
    [Iec61850Point("NFTESTLD0", "GGIO1$MX$Level$mag")] public float Level = 12.5f;
    [Iec61850Point("NFTESTLD0", "GGIO1$SP$Target$setVal", Writable = true, Validate = nameof(CheckTarget))]
    public int Target { get { return target; } private set { Writes++; target = value; if (value == 999) Thread.Sleep(5000); } }
    [Iec61850Point("NFTESTLD0", "GGIO1$SP$Gain$setVal", Writable = true)] public float Gain = 1.0f;
    [Iec61850Point("NFTESTLD0", "GGIO1$SP$Command$setVal", Writable = true, Validate = nameof(CheckCommand))]
    public int Command { get { return command; } private set { command = 1000 + value; Script(value); } }
    [Iec61850Point("NFTESTLD0", "GGIO1$ST$Writes$stVal")] public int Writes { get; private set; }
    private int target, command;
    private int CheckTarget(MmsValue value) { return value.Integer >= 0 && value.Integer <= 1000 ? -1 : 11; }
    private int CheckCommand(MmsValue value) { return value.Integer == 0 || value.Integer == 1 ? -1 : 11; }
    private void Script(int value)
    {
        if (value == 0)
        {
            Status = true; Quality = InteropNames.InitialQuality; Time = InteropNames.InitialTime; TimeQuality = 0x3f;
            Pos = IecDoublePoint.On; Level = 12.5f; target = 0; Gain = 1.0f;
        }
        else
        {
            Status = false; Quality = IecQuality.Good; Time = InteropNames.ChangedTime; TimeQuality = 0x0a;
            Pos = IecDoublePoint.Off; Level = 20.5f;
        }
    }
}

internal sealed class InteropIecServer
{
    private readonly object sync = new object();
    private readonly InteropIecLocal model = new InteropIecLocal();
    private readonly InteropIecLocal.Iec61850ServerBinding binding;
    private readonly MmsServerDataSet dataSet;
    private readonly MmsServerReportControl report;
    internal int Sessions, Requests, Reports;
    internal int Writes { get { lock (sync) return model.Writes; } }
    internal InteropIecServer()
    {
        binding = new InteropIecLocal.Iec61850ServerBinding(model, sync);
        string[] order = { "GGIO1$ST$Writes$stVal", "GGIO1$MX$Level$mag", "GGIO1$ST$Status$t", "GGIO1$ST$Status$stVal",
            "GGIO1$ST$Status$q", "GGIO1$ST$Pos$stVal", "GGIO1$SP$Target$setVal" };
        var members = new MmsServerVariable[order.Length];
        for (int i = 0; i < order.Length; i++)
            for (int n = 0; n < binding.Count; n++) if (binding.GetVariable(n).Item == order[i]) members[i] = binding.GetVariable(n);
        for (int i = 0; i < members.Length; i++) if (members[i] == null) throw new InvalidOperationException("Missing DataSet member.");
        dataSet = new MmsServerDataSet(InteropNames.Domain, "GGIO1$Events", members);
        report = new MmsServerReportControl(InteropNames.Domain, "GGIO1$RP$Events", "NFMAPREPORT", dataSet, minimumIntervalMilliseconds: 1);
    }
    internal void Serve(Socket socket)
    {
        lock (sync)
        {
            binding.Publish();
            var session = binding.CreateSession(new MmsSocketChannel(socket), new[] { dataSet }, new[] { report });
            try
            {
                session.Associate();
                long deadline = Environment.TickCount64 + 60000;
                while (Environment.TickCount64 < deadline)
                {
                    // 최소 송신 간격에 걸린 변경 보고도 다음 요청 없이 송신되도록 수신 대기 중에도 보고를 처리합니다.
                    if (socket.Poll(20000, SelectMode.SelectRead))
                    {
                        if (!session.ProcessRequest()) return;
                        Requests++;
                    }
                    binding.Publish();
                    Reports += session.ProcessReports();
                }
                throw new TimeoutException("Interop IEC session exceeded its bounded duration.");
            }
            finally { session.Close(); Sessions++; }
        }
    }
}

[MmsServerMap(Domain = "NFTESTLD0")]
internal partial class InteropMmsLocal
{
    [MmsPoint("GGIO1$ST$Status$stVal")] public bool Status = true;
    [MmsPoint("GGIO1$MX$Level$mag")] public float Level = 12.5f;
    [MmsPoint("GGIO1$SP$Target$setVal", Writable = true, Validate = nameof(CheckTarget))]
    public int Target { get { return target; } private set { Writes++; target = value; if (value == 999) Thread.Sleep(5000); } }
    [MmsPoint("GGIO1$SP$Gain$setVal", Writable = true)] public float Gain = 1.0f;
    [MmsPoint("GGIO1$SP$Command$setVal", Writable = true, Validate = nameof(CheckCommand))]
    public int Command { get { return command; } private set { command = 1000 + value; Script(value); } }
    [MmsPoint("GGIO1$ST$Writes$stVal")] public int Writes { get; private set; }
    public bool ReportPending;
    private int target, command;
    private int CheckTarget(MmsValue value) { return value.Integer >= 0 && value.Integer <= 1000 ? -1 : 11; }
    private int CheckCommand(MmsValue value) { return value.Integer == 0 || value.Integer == 1 ? -1 : 11; }
    private void Script(int value)
    {
        if (value == 0) { Status = true; Level = 12.5f; target = 0; Gain = 1.0f; }
        else { Status = false; Level = 20.5f; ReportPending = true; }
    }
}

internal sealed class InteropMmsServer
{
    private readonly object sync = new object();
    private readonly InteropMmsLocal model = new InteropMmsLocal();
    private readonly InteropMmsLocal.MmsServerBinding binding;
    private readonly MmsServerNamedVariableList list;
    internal int Sessions, Requests, Reports;
    internal int Writes { get { lock (sync) return model.Writes; } }
    internal InteropMmsServer()
    {
        binding = new InteropMmsLocal.MmsServerBinding(model, sync);
        string[] order = { "GGIO1$ST$Writes$stVal", "GGIO1$MX$Level$mag", "GGIO1$ST$Status$stVal" };
        var members = new MmsServerVariable[order.Length];
        for (int i = 0; i < order.Length; i++)
            for (int n = 0; n < binding.Count; n++) if (binding.GetVariable(n).Item == order[i]) members[i] = binding.GetVariable(n);
        for (int i = 0; i < members.Length; i++) if (members[i] == null) throw new InvalidOperationException("Missing list member.");
        list = new MmsServerNamedVariableList(InteropNames.Domain, "GGIO1$Events", members);
    }
    internal void Serve(Socket socket)
    {
        lock (sync)
        {
            binding.Publish();
            var session = binding.CreateSession(new MmsSocketChannel(socket), new[] { list });
            try
            {
                session.Associate();
                long deadline = Environment.TickCount64 + 60000;
                while (Environment.TickCount64 < deadline)
                {
                    if (!session.ProcessRequest()) return;
                    Requests++;
                    binding.Publish();
                    if (model.ReportPending) { model.ReportPending = false; session.SendInformationReport(list); Reports++; }
                }
                throw new TimeoutException("Interop MMS session exceeded its bounded duration.");
            }
            finally { session.Close(); Sessions++; }
        }
    }
}

[Iec61850ClientMap]
internal partial class InteropIecRemote
{
    [Iec61850Point("NFTESTLD0", "GGIO1$ST$Status$stVal")] public bool Status { get; private set; }
    [Iec61850Point("NFTESTLD0", "GGIO1$ST$Status$q")] public IecQuality Quality { get; private set; }
    [Iec61850Point("NFTESTLD0", "GGIO1$ST$Status$t", TimeQuality = nameof(TimeQuality))] public long Time { get; private set; }
    public byte TimeQuality { get; private set; }
    [Iec61850Point("NFTESTLD0", "GGIO1$ST$Pos$stVal")] public IecDoublePoint Pos { get; private set; }
    [Iec61850Point("NFTESTLD0", "GGIO1$MX$Level$mag")] public float Level { get; private set; }
    [Iec61850Point("NFTESTLD0", "GGIO1$SP$Target$setVal", Writable = true)] public int Target { get; set; }
    [Iec61850Point("NFTESTLD0", "GGIO1$SP$Gain$setVal", Writable = true)] public float Gain { get; set; }
    [Iec61850Point("NFTESTLD0", "GGIO1$SP$Command$setVal", Writable = true)] public int Command { get; set; }
    [Iec61850Point("NFTESTLD0", "GGIO1$ST$Writes$stVal")] public int Writes { get; private set; }
}

[MmsClientMap(Domain = "NFTESTLD0")]
internal partial class InteropMmsRemote
{
    [MmsPoint("GGIO1$ST$Status$stVal")] public bool Status { get; private set; }
    [MmsPoint("GGIO1$MX$Level$mag")] public float Level { get; private set; }
    [MmsPoint("GGIO1$SP$Target$setVal", Writable = true)] public int Target { get; set; }
    [MmsPoint("GGIO1$SP$Gain$setVal", Writable = true)] public float Gain { get; set; }
    [MmsPoint("GGIO1$SP$Command$setVal", Writable = true)] public int Command { get; set; }
    [MmsPoint("GGIO1$ST$Writes$stVal")] public int Writes { get; private set; }
}

// 독립 native 서버에 생성 Client 바인딩을 연결합니다. 원격 측 효과는 native 서버가 별도로 확인할 수 없으므로 재조회로 판정합니다.
internal static class InteropClients
{
    private static Socket Connect(IPAddress address, int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try { socket.Connect(new IPEndPoint(address, port)); return socket; }
        catch { socket.Close(); throw; }
    }
    private static int Index(string item, MmsVariableReference[] references)
    {
        for (int n = 0; n < references.Length; n++) if (references[n].Item == item) return n;
        throw new InvalidOperationException("Unmapped interop point " + item);
    }
    private static MmsVariableReference[] References(InteropIecRemote.Iec61850ClientBinding binding)
    { var r = new MmsVariableReference[binding.Count]; for (int n = 0; n < r.Length; n++) r[n] = binding.GetReference(n); return r; }
    private static MmsVariableReference[] References(InteropMmsRemote.MmsClientBinding binding)
    { var r = new MmsVariableReference[binding.Count]; for (int n = 0; n < r.Length; n++) r[n] = binding.GetReference(n); return r; }

    internal static void RunIec(IPAddress address, int port)
    {
        object sync = new object();
        var model = new InteropIecRemote();
        int baseWrites;
        InteropStats.Runs++; InteropStats.Mark("IEC connect", 0);
        using (Socket socket = Connect(address, port))
        {
            var client = new Iec61850Client(new InteropTraceChannel(socket), timeoutMilliseconds: 3000);
            var binding = new InteropIecRemote.Iec61850ClientBinding(model, sync, client);
            var refs = References(binding);
            int target = Index("GGIO1$SP$Target$setVal", refs), gain = Index("GGIO1$SP$Gain$setVal", refs),
                command = Index("GGIO1$SP$Command$setVal", refs);
            InteropStats.Mark("IEC associate", 0); client.Associate();
            InteropStats.Mark("IEC reset", 0); model.Command = 0; binding.Write(command);
            InteropStats.Until(() => { binding.ReadAll(); return model.Command == 1000 && model.Status && model.Level == 12.5f && model.Target == 0 && model.Gain == 1.0f; }, 3000, "IEC reset");
            InteropStats.Ensure(model.Quality == InteropNames.InitialQuality && model.Time == InteropNames.InitialTime &&
                model.TimeQuality == 0x3f && model.Pos == IecDoublePoint.On, "IEC value-quality-time initial");
            baseWrites = model.Writes;
            model.Target = 5; binding.Write(target);
            InteropStats.Until(() => { binding.ReadAll(); return model.Writes == baseWrites + 1; }, 3000, "IEC accepted write");
            InteropStats.Ensure(model.Target == 5, "IEC write readback");
            Rejections(client, () => binding.Write(target), () => binding.Write(gain), v => model.Target = v, v => model.Gain = v);
            binding.ReadAll();
            InteropStats.Ensure(model.Target == 5 && model.Gain == 1.0f && model.Writes == baseWrites + 1 && !client.Faulted, "IEC rejection preserved");
            model.Gain = 2.5f; binding.Write(gain); binding.ReadAll();
            InteropStats.Ensure(model.Gain == 2.5f, "IEC gain readback");

            var set = client.GetDataSetDirectory(InteropNames.Domain, "GGIO1$Events");
            InteropStats.Ensure(set.Count == 8, "IEC native DataSet includes unmapped entry");
            int applied = 0, rejected = 0;
            var pending = new InteropPendingReports();
            InteropApply apply = o =>
            {
                try { binding.ApplyReport(set, (MmsReport)o); applied++; }
                catch (ArgumentException) { rejected++; }
                catch (InvalidOperationException) { rejected++; }
            };
            var rcb = new MmsReportClient(client, InteropNames.Domain, "GGIO1$RP$Events", set, r => { InteropStats.Reported(); pending.Add(r); });
            InteropStats.Mark("IEC GI", 0); rcb.Reserve(true); rcb.Enable(); rcb.GeneralInterrogation();
            ReceiveUntil(client, pending, apply, () => applied >= 1, "IEC GI report");
            InteropStats.Ensure(model.Status && model.Level == 12.5f && model.Target == 5 && model.Writes == baseWrites + 1 &&
                model.Quality == InteropNames.InitialQuality && model.Pos == IecDoublePoint.On, "IEC GI applied");
            lock (sync) model.Target = 77;
            InteropStats.Mark("IEC change report", 1); model.Command = 1; binding.Write(command);
            ReceiveUntil(client, pending, apply, () => !model.Status && model.Level == 20.5f && model.Pos == IecDoublePoint.Off &&
                model.Quality == IecQuality.Good && model.Time == InteropNames.ChangedTime && model.TimeQuality == 0x0a, "IEC change report");
            InteropStats.Ensure(model.Target == 77 && model.Gain == 2.5f && rejected == 0, "IEC not-included entries preserved");
            int before = applied;
            InteropStats.Mark("IEC invalid report", 2); model.Command = 2; binding.Write(command);
            ReceiveUntil(client, pending, apply, () => rejected >= 1, "IEC invalid report");
            InteropStats.Ensure(!model.Status && model.Level == 20.5f && applied == before, "IEC invalid report atomic");
            InteropStats.Mark("IEC partial report", 3); model.Command = 3; binding.Write(command);
            ReceiveUntil(client, pending, apply, () => model.Level == 21.5f, "IEC partial report");
            InteropStats.Ensure(!model.Status && model.Target == 77 && rejected == 1, "IEC partial report keeps old values");
            rcb.Disable(); rcb.Reserve(false);
            binding.ReadAll();
            InteropStats.Ensure(model.Status && model.Level == 21.5f && model.Target == 5, "IEC ReadAll after reports");
            InteropStats.Applied += applied; InteropStats.Rejected += rejected;
            InteropStats.Mark("IEC response loss", 999); LoseResponse(client, () => binding.Write(target), v => model.Target = v);
        }
        Thread.Sleep(4500);
        using (Socket socket = Connect(address, port))
        {
            InteropStats.Mark("IEC reconnect", 0);
            var client = new Iec61850Client(new InteropTraceChannel(socket), timeoutMilliseconds: 3000);
            var binding = new InteropIecRemote.Iec61850ClientBinding(model, sync, client);
            client.Associate();
            InteropStats.Until(() => { binding.ReadAll(); return model.Writes == baseWrites + 2; }, 3000, "IEC single side effect");
            InteropStats.Ensure(model.Target == 999, "IEC lost-response value");
            Thread.Sleep(1000); binding.ReadAll();
            InteropStats.Ensure(model.Target == 999 && model.Writes == baseWrites + 2, "IEC no write replay");
            client.Release();
        }
    }

    internal static void RunMms(IPAddress address, int port)
    {
        object sync = new object();
        var model = new InteropMmsRemote();
        int baseWrites;
        InteropStats.Runs++; InteropStats.Mark("MMS connect", 0);
        using (Socket socket = Connect(address, port))
        {
            var client = new MmsClient(new InteropTraceChannel(socket), timeoutMilliseconds: 3000);
            var binding = new InteropMmsRemote.MmsClientBinding(model, sync, client);
            var refs = References(binding);
            int target = Index("GGIO1$SP$Target$setVal", refs), gain = Index("GGIO1$SP$Gain$setVal", refs),
                command = Index("GGIO1$SP$Command$setVal", refs);
            int applied = 0, nan = 0, mismatch = 0, invalid = 0;
            var pending = new InteropPendingReports();
            InteropApply apply = o =>
            {
                try { binding.ApplyInformationReport((MmsInformationReport)o); applied++; }
                catch (ArgumentOutOfRangeException) { nan++; }
                catch (ArgumentException) { mismatch++; }
                catch (InvalidOperationException) { invalid++; }
            };
            client.InformationReportReceived = r => { InteropStats.Reported(); pending.Add(r); };
            InteropStats.Mark("MMS associate", 0); client.Associate();
            InteropStats.Mark("MMS reset", 0); model.Command = 0; binding.Write(command);
            InteropStats.Until(() => { binding.ReadAll(); return model.Command == 1000 && model.Status && model.Level == 12.5f && model.Target == 0 && model.Gain == 1.0f; }, 3000, "MMS reset");
            baseWrites = model.Writes;
            InteropStats.Mark("MMS accepted write", 5); model.Target = 5; binding.Write(target);
            InteropStats.Until(() => { binding.ReadAll(); return model.Writes == baseWrites + 1; }, 3000, "MMS accepted write");
            InteropStats.Ensure(model.Target == 5, "MMS write readback");
            InteropStats.Mark("MMS rejections", 0);
            Rejections(client, () => binding.Write(target), () => binding.Write(gain), v => model.Target = v, v => model.Gain = v);
            binding.ReadAll();
            InteropStats.Ensure(model.Target == 5 && model.Gain == 1.0f && model.Writes == baseWrites + 1 && !client.Faulted, "MMS rejection preserved");
            InteropStats.Mark("MMS gain write", 0); model.Gain = 2.5f; binding.Write(gain); binding.ReadAll();
            InteropStats.Ensure(model.Gain == 2.5f, "MMS gain readback");
            Synthetic(binding, command, model, pending, apply, 101);
            InteropStats.Ensure(applied == 1 && !model.Status && model.Level == 30.5f && model.Writes == baseWrites + 1, "MMS report skips VMD/unmapped and applies shuffled entries");
            Synthetic(binding, command, model, pending, apply, 102);
            InteropStats.Ensure(nan == 1 && !model.Status && model.Level == 30.5f, "MMS NaN report atomic");
            Synthetic(binding, command, model, pending, apply, 103);
            InteropStats.Ensure(invalid == 1 && model.Level == 30.5f, "MMS access-error report atomic");
            Synthetic(binding, command, model, pending, apply, 104);
            InteropStats.Ensure(invalid == 2 && model.Level == 30.5f && !model.Status, "MMS type-mismatch report atomic");
            Synthetic(binding, command, model, pending, apply, 105);
            InteropStats.Ensure(mismatch == 1 && model.Level == 30.5f, "MMS VMD named-list report rejected");
            Synthetic(binding, command, model, pending, apply, 106);
            InteropStats.Ensure(applied == 2 && model.Level == 32.5f && !model.Status && model.Target == 5 && !client.Faulted, "MMS partial report");
            InteropStats.Mark("MMS ReadAll after reports", 0); binding.ReadAll();
            InteropStats.Ensure(model.Status && model.Level == 12.5f && model.Target == 5, "MMS ReadAll after synthetic reports");
            InteropStats.Applied += applied; InteropStats.Rejected += nan + mismatch + invalid;
            InteropStats.Mark("MMS response loss", 999); LoseResponse(client, () => binding.Write(target), v => model.Target = v);
        }
        Thread.Sleep(4500);
        using (Socket socket = Connect(address, port))
        {
            InteropStats.Mark("MMS reconnect", 0);
            var client = new MmsClient(new InteropTraceChannel(socket), timeoutMilliseconds: 3000);
            var binding = new InteropMmsRemote.MmsClientBinding(model, sync, client);
            client.Associate();
            InteropStats.Until(() => { binding.ReadAll(); return model.Writes == baseWrites + 2; }, 3000, "MMS single side effect");
            InteropStats.Ensure(model.Target == 999, "MMS lost-response value");
            Thread.Sleep(1000); binding.ReadAll();
            InteropStats.Ensure(model.Target == 999 && model.Writes == baseWrites + 2, "MMS no write replay");
            client.Release();
        }
    }

    private delegate void Action0();
    private delegate void SetInt(int value);
    private delegate void SetFloat(float value);
    private static void Rejections(MmsClient client, Action0 writeTarget, Action0 writeGain, SetInt setTarget, SetFloat setGain)
    {
        int code = 0;
        setTarget(1001);
        try { writeTarget(); } catch (MmsAccessException error) { code = error.ErrorCode; }
        InteropStats.Ensure(code == 11 && !client.Faulted, "remote validator rejection");
        bool local = false;
        setGain(float.NaN);
        try { writeGain(); } catch (ArgumentOutOfRangeException) { local = true; }
        InteropStats.Ensure(local && !client.Faulted, "local NaN guard before network");
    }
    private static void LoseResponse(MmsClient client, Action0 writeTarget, SetInt setTarget)
    {
        bool lost = false;
        setTarget(999);
        try { writeTarget(); } catch (Exception) { lost = true; }
        InteropStats.Ensure(lost && client.Faulted, "response loss faults without retry");
    }
    // 네이티브 서버는 쓰기 응답 전에 리포트 1개를 보내므로 쓰기 대기 중 정확히 1개가 수신되어야 합니다. 적용은 쓰기가 끝난 뒤 합니다.
    private static void Synthetic(InteropMmsRemote.MmsClientBinding binding, int command, InteropMmsRemote model, InteropPendingReports pending, InteropApply apply, int value)
    {
        InteropStats.Mark("MMS synthetic report command", value); model.Command = value; binding.Write(command);
        InteropStats.Ensure(pending.Count == 1, "MMS report delivered while waiting for write response");
        pending.Drain(apply);
    }
    private static void ReceiveUntil(Iec61850Client client, InteropPendingReports pending, InteropApply apply, InteropCondition condition, string step)
    {
        long deadline = Environment.TickCount64 + 5000;
        while (true)
        {
            pending.Drain(apply);
            if (condition()) break;
            if (Environment.TickCount64 >= deadline) InteropStats.Ensure(false, step);
            client.ReceiveReport();
        }
        InteropStats.Checks++;
    }
}
