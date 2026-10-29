using VagabondK.nF.MMS.Server;
using VagabondK.nF.MMS;
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using VagabondK.nF.IEC61850;
using VagabondK.nF.IEC61850.Client;
using VagabondK.nF.IEC61850.Server;
using VagabondK.nF.MMS.TCP;

/// <summary>상태·품질·UTC의 갱신과 고정 DataSet 읽기, URCB 예약·변경·GI·주기 보고의 왕복 흐름을 검증합니다.</summary>
internal static class DataServiceTests
{
    internal static MmsServerVariable[] Model()
    {
        const string domain = DataChecks.Domain;
        return new MmsServerVariable[] {
            new MmsServerVariable(domain, "GGIO1$ST$Status$stVal", true),
            new MmsServerVariable(domain, "GGIO1$ST$Count$stVal", -123456),
            new MmsServerVariable(domain, "GGIO1$MX$Analog$mag", 12.5f),
            Iec61850ServerData.CreateQuality(domain, "GGIO1$ST$Status$q", IecQuality.Invalid | IecQuality.Test | IecQuality.OldData),
            Iec61850ServerData.CreateTimestamp(domain, "GGIO1$ST$Status$t", 1700000000125, 0x3f)
        };
    }
    private static void Check(bool ok) { if (!ok) throw new Exception("Data service assertion failed."); }
    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    internal static MmsServerDataSet Set(MmsServerVariable[] model)
    { return new MmsServerDataSet(DataChecks.Domain, DataChecks.SetName, new[] { model[0], model[1], model[2] }); }
    internal static void Run()
    {
        Quality();
        Updates();
        ReportState();
        Loop(null);
        Console.WriteLine("DATA: multi-read/DataSet directory/read/names/URCB/quality/UTC/state/bounds PASS.");
    }
    private static void Quality()
    {
        for (int i = 0; i <= 8191; i++)
        {
            var variable = Iec61850ServerData.CreateQuality("D", "q", (IecQuality)i);
            Check(Iec61850Data.ReadQuality(MmsValue.Decode(MmsEncoding.Single(variable.Encoded, 0, 5, 0x84))) == (IecQuality)i);
        }
        var q = Iec61850ServerData.CreateQuality("D", "q", IecQuality.Invalid);
        Check(q.Encoded[3] == 0x40 && q.Encoded[4] == 0);
        Reject<ArgumentOutOfRangeException>(() => q.SetQuality((IecQuality)8192));
        Reject<ArgumentException>(() => Iec61850Data.ReadQuality(MmsValue.Decode(MmsEncoding.Single(new byte[] { 0x83, 1, 1 }, 0, 3, 0x83))));
        var t = Iec61850ServerData.CreateTimestamp("D", "t");
        foreach (long ms in new[] { 0L, 1L, 125L, 999L, 1700000000125L, uint.MaxValue * 1000L + 999 })
        {
            t.SetTimestamp(ms, 0xff);
            var value = MmsValue.Decode(MmsEncoding.Single(t.Encoded, 0, 10, 0x91));
            long result = Iec61850Data.ReadTimestampMilliseconds(value);
            Check(result <= ms && result >= ms - 1 && Iec61850Data.ReadTimeQuality(value) == 255);
        }
        Reject<ArgumentOutOfRangeException>(() => t.SetTimestamp(-1));
        Reject<ArgumentOutOfRangeException>(() => t.SetTimestamp((uint.MaxValue + 1L) * 1000));
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) { q.SetQuality(IecQuality.Good); t.SetTimestamp(i); }
        Check(GC.GetAllocatedBytesForCurrentThread() == before);
    }
    private static void Updates()
    {
        var pos = Iec61850ServerData.CreateDoublePoint("D", "pos");
        var q = Iec61850ServerData.CreateQuality("D", "q");
        var t = Iec61850ServerData.CreateTimestamp("D", "t", 1000, 0x0a);
        var plain = new MmsServerVariable("D", "x", 1);
        long version = pos.ChangeVersion;
        Check(pos.Encoded[3] == 0);
        pos.UpdateDoublePoint(IecDoublePoint.Intermediate); Check(pos.ChangeVersion == version);
        foreach (IecDoublePoint value in new[] { IecDoublePoint.Off, IecDoublePoint.On, IecDoublePoint.Bad, IecDoublePoint.Intermediate })
        {
            pos.UpdateDoublePoint(value);
            Check(pos.ChangeVersion == ++version && pos.Encoded[3] == (byte)((int)value << 6));
            pos.UpdateDoublePoint(value); Check(pos.ChangeVersion == version);
        }
        Reject<ArgumentOutOfRangeException>(() => pos.UpdateDoublePoint((IecDoublePoint)4));
        Reject<ArgumentOutOfRangeException>(() => pos.UpdateDoublePoint((IecDoublePoint)(-1)));
        Check(pos.ChangeVersion == version);

        version = q.ChangeVersion;
        q.UpdateQuality(IecQuality.Good); Check(q.ChangeVersion == version);
        for (int bit = 0; bit < 13; bit++)
        {
            q.UpdateQuality((IecQuality)(1 << bit));
            Check(q.ChangeVersion == ++version && Iec61850Data.ReadQuality(MmsValue.Decode(MmsEncoding.Single(q.Encoded, 0, 5, 0x84))) == (IecQuality)(1 << bit));
            q.UpdateQuality((IecQuality)(1 << bit)); Check(q.ChangeVersion == version);
        }
        q.UpdateQuality((IecQuality)8191); Check(q.ChangeVersion == ++version);
        q.UpdateQuality((IecQuality)8191); Check(q.ChangeVersion == version);
        Reject<ArgumentOutOfRangeException>(() => q.UpdateQuality((IecQuality)8192));
        Reject<ArgumentOutOfRangeException>(() => q.UpdateQuality((IecQuality)(-1)));

        version = t.ChangeVersion;
        t.UpdateTimestamp(1000, 0x0a); Check(t.ChangeVersion == version);
        t.UpdateTimestamp(1001, 0x0a); Check(t.ChangeVersion == ++version);
        t.UpdateTimestamp(1001, 0x0a); Check(t.ChangeVersion == version);
        t.UpdateTimestamp(1001, 0x0b); Check(t.ChangeVersion == ++version && t.Encoded[9] == 0x0b);
        t.UpdateTimestamp(1001 + 256000L, 0x0b); Check(t.ChangeVersion == ++version);
        t.UpdateTimestamp(1001 + (1L << 32) / 1000 * 1000, 0x0b); Check(t.ChangeVersion == ++version);
        t.UpdateTimestamp(uint.MaxValue * 1000L + 999, 0x0b); Check(t.ChangeVersion == ++version);
        t.UpdateTimestamp(uint.MaxValue * 1000L + 999, 0x0b); Check(t.ChangeVersion == version);
        Reject<ArgumentOutOfRangeException>(() => t.UpdateTimestamp(-1));
        Reject<ArgumentOutOfRangeException>(() => t.UpdateTimestamp((uint.MaxValue + 1L) * 1000));
        Check(t.ChangeVersion == version);
        for (long ms = 0; ms < 3000; ms += 7)
        {
            t.UpdateTimestamp(ms, 0x0b);
            long result = Iec61850Data.ReadTimestampMilliseconds(MmsValue.Decode(MmsEncoding.Single(t.Encoded, 0, 10, 0x91)));
            Check(result <= ms && result >= ms - 1);
        }

        Reject<InvalidOperationException>(() => plain.UpdateDoublePoint(IecDoublePoint.On));
        Reject<InvalidOperationException>(() => plain.UpdateQuality(IecQuality.Good));
        Reject<InvalidOperationException>(() => plain.UpdateTimestamp(0));
        Reject<InvalidOperationException>(() => q.UpdateDoublePoint(IecDoublePoint.On));
        Reject<InvalidOperationException>(() => pos.UpdateQuality(IecQuality.Good));
        Reject<InvalidOperationException>(() => pos.UpdateTimestamp(0));
        Reject<InvalidOperationException>(() => t.UpdateQuality(IecQuality.Good));
        Reject<InvalidOperationException>(() => q.SetDoublePoint(IecDoublePoint.On));
        Reject<InvalidOperationException>(() => pos.SetQuality(IecQuality.Good));
        Reject<InvalidOperationException>(() => pos.SetTimestamp(0));
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) { pos.UpdateDoublePoint(IecDoublePoint.On); q.UpdateQuality(IecQuality.Good); t.UpdateTimestamp(i); }
        Check(GC.GetAllocatedBytesForCurrentThread() == before);
    }
    private static void ReportState()
    {
        var model = Model(); var set = Set(model);
        var rcb = new MmsServerReportControl(DataChecks.Domain, DataChecks.RcbName, "NFREPORT", set);
        object first = new object(), second = new object();
        BerElement Bool(bool flag) { byte[] b = { 0x83, 1, flag ? (byte)1 : (byte)0 }; return MmsEncoding.Single(b, 0, b.Length, 0x83); }
        Check(rcb.Build(first, 0) == null);
        Check(rcb.Write(first, "Resv", Bool(true), 0) == -1);
        Check(rcb.Write(second, "RptEna", Bool(true), 0) == 3);
        Check(rcb.Write(first, "RptEna", Bool(true), 0) == -1);
        Check(rcb.Write(first, "Resv", Bool(false), 0) == 3);
        Check(rcb.Write(first, "GI", Bool(true), 0) == -1 && rcb.Build(second, 0) == null);
        Check(rcb.Build(first, 0) != null && rcb.Build(first, 100) == null);
        model[1].SetInteger(42); model[1].SetInteger(43);
        Check(rcb.Build(first, 50) == null && rcb.Build(first, 100) != null && rcb.Build(first, 200) == null);
        model[1].SetInteger(43); Check(rcb.Build(first, 200) == null);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) Check(rcb.Build(first, 200) == null);
        Check(GC.GetAllocatedBytesForCurrentThread() == before);
        rcb.Release(second); Check(rcb.Write(second, "RptEna", Bool(false), 300) == 3);
        rcb.Release(first); Check(rcb.Write(second, "RptEna", Bool(true), 300) == -1);
        rcb.Release(second);
        Reject<ArgumentOutOfRangeException>(() => new MmsServerDataSet("D", "S", new MmsServerVariable[17]));
        Reject<ArgumentException>(() => new MmsServerDataSet("D", "S", new[] { new MmsServerVariable("D", "L$CO$O", true) }));
        Reject<ArgumentException>(() => new Iec61850ServerSession(new ServerTests.Transcript(), model, dataSets: new[] { set, set }));
        var qset = new MmsServerDataSet("D", "Quality", new[] { model[3] });
        var qrcb = new MmsServerReportControl("D", "L$RP$Q", "QUALITY", qset);
        qrcb.Write(first, "RptEna", Bool(true), 0); model[3].SetQuality(IecQuality.Good);
        byte[] report = qrcb.Build(first, 0);
        Check(report != null && report[report.Length - 1] == 32);
        qrcb.Release(first);
        var periodic = new MmsServerReportControl("D", "L$RP$I", "INTEGRITY", set, integrityMilliseconds: 100);
        periodic.Write(first, "RptEna", Bool(true), 0);
        Check(periodic.Build(first, 99) == null && periodic.Build(first, 100) != null && periodic.Build(first, 199) == null);
        periodic.Release(first);
        BerElement Unsigned(long value) { byte[] b = MmsEncoding.Int(0x86, value); return MmsEncoding.Single(b, 0, b.Length, 0x86); }
        BerElement Signed(long value) { byte[] b = MmsEncoding.Int(0x85, value); return MmsEncoding.Single(b, 0, b.Length, 0x85); }
        var tunable = new MmsServerReportControl("D", "L$RP$T", "TUNABLE", set, minimumIntervalMilliseconds: 50);
        Check(tunable.Write(first, "IntgPd", Signed(100), 0) == 7);
        Check(tunable.Write(first, "IntgPd", Unsigned(49), 0) == 11);
        Check(tunable.Write(first, "IntgPd", Unsigned(60001), 0) == 11);
        Check(tunable.Write(first, "IntgPd", Unsigned(50), 0) == -1);
        Check(tunable.Write(first, "IntgPd", Unsigned(60000), 0) == -1);
        Check(tunable.Write(first, "IntgPd", Unsigned(0), 0) == -1);
        Check(tunable.Write(first, "RptEna", Bool(true), 0) == -1);
        Check(tunable.Build(first, 0) == null && tunable.Build(first, 100000) == null);
        Check(tunable.Write(first, "IntgPd", Unsigned(50), 0) == 3);
        Check(tunable.Write(second, "IntgPd", Unsigned(50), 0) == 3);
        Check(tunable.Write(first, "Unknown", Unsigned(50), 0) == 3);
        Check(tunable.Write(first, "RptEna", Bool(false), 0) == -1);
        Check(tunable.Write(first, "IntgPd", Unsigned(80), 0) == -1);
        Check(tunable.Write(first, "RptEna", Bool(true), 1000) == -1);
        Check(tunable.Build(first, 1079) == null && tunable.Build(first, 1080) != null);
        Check(tunable.Build(first, 1159) == null && tunable.Build(first, 1160) != null);
        Check(tunable.Write(first, "GI", Signed(1), 1200) == 7);
        tunable.Release(first);
        Reject<ArgumentOutOfRangeException>(() => new MmsServerReportControl("D", "L$RP$X", "X", set, integrityMilliseconds: 99));
        Reject<ArgumentOutOfRangeException>(() => new MmsServerReportControl("D", "L$RP$X", "X", set, integrityMilliseconds: 60001));
        Reject<ArgumentOutOfRangeException>(() => new MmsServerReportControl("D", "L$RP$X", "X", set, minimumIntervalMilliseconds: 0));
        Reject<ArgumentOutOfRangeException>(() => new MmsServerReportControl("D", "L$RP$X", "X", set, configurationRevision: 0));
    }
    private static void Integrity(Iec61850Client client, MmsReportClient report)
    {
        void Rejected<T>(Action action, Func<T, bool> accept) where T : Exception
        {
            try { action(); }
            catch (T e) { Check(accept(e)); return; }
            Check(false);
        }
        Rejected<ArgumentOutOfRangeException>(() => report.SetIntegrityPeriod(-1), e => true);
        Rejected<ArgumentOutOfRangeException>(() => report.SetIntegrityPeriod(60001), e => true);
        Rejected<MmsAccessException>(() => report.SetIntegrityPeriod(99), e => e.ErrorCode == 11);
        report.SetIntegrityPeriod(60000); report.SetIntegrityPeriod(0);
        Check(!client.Faulted && client.Associated);
        report.SetIntegrityPeriod(150);
        int received = 0; uint last = 0;
        report.ReportReceived = r => {
            Check(r.ReportId == "NFREPORT" && r.Count == 3 && r.GetReason(0) == 8 && r.GetReason(1) == 8 && r.GetReason(2) == 8);
            Check(received == 0 || r.SequenceNumber == (byte)(last + 1));
            last = r.SequenceNumber; received++;
        };
        long start = Environment.TickCount64;
        report.Enable();
        Rejected<MmsAccessException>(() => report.SetIntegrityPeriod(200), e => e.ErrorCode == 3);
        while (received < 2) client.ReceiveReport();
        Check(Environment.TickCount64 - start >= 250 && !client.Faulted);
        report.Disable();
        report.SetIntegrityPeriod(0);
    }
    private static Process Start(string executable, string args)
    { return Process.Start(new ProcessStartInfo(executable, args) { UseShellExecute = false, RedirectStandardInput = true,
        RedirectStandardOutput = true, RedirectStandardError = true }); }
    internal static void Interop(string executable)
    {
        var portProbe = new TcpListener(IPAddress.Loopback, 0); portProbe.Start();
        int port = ((IPEndPoint)portProbe.LocalEndpoint).Port; portProbe.Stop();
        using (var peer = Start(executable, "dataserver " + port))
        {
            try
            {
                Check(peer.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult() == "READY");
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    socket.Connect(IPAddress.Loopback, port);
                    var client = new Iec61850Client(new MmsSocketChannel(socket), maximumTpduSize: 128);
                    client.Associate(); DataChecks.Run(client); DataChecks.Quality(client);
                }
            }
            finally
            {
                if (!peer.HasExited)
                {
                    peer.StandardInput.WriteLine("quit"); peer.StandardInput.Flush();
                    if (!peer.WaitForExit(10000)) { peer.Kill(true); throw new Exception("Data peer shutdown timeout."); }
                }
                if (peer.ExitCode != 0) throw new Exception(peer.StandardError.ReadToEnd());
            }
        }
        Loop(executable);
        Console.WriteLine("DATA ORIGINAL: native Server + NF Client / NF Server + native Client PASS.");
    }
    private static void Loop(string executable)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            Task worker = Task.Run(() => {
                using (var socket = listener.AcceptSocketAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult())
                {
                    var model = Model(); var set = Set(model);
                    var rcb = new MmsServerReportControl(DataChecks.Domain, DataChecks.RcbName, "NFREPORT", set);
                    var server = new Iec61850ServerSession(new MmsSocketChannel(socket), model, dataSets: new[] { set }, reports: new[] { rcb });
                    try
                    {
                        server.Associate(); long deadline = Environment.TickCount64 + 20000;
                        while (Environment.TickCount64 < deadline)
                        {
                            if (socket.Poll(1000, SelectMode.SelectRead) && !server.ProcessRequest()) return;
                            server.ProcessReports(); Thread.Sleep(1);
                        }
                        throw new Exception("Data server loop deadline.");
                    }
                    finally { server.Close(); }
                }
            });
            if (executable == null)
            {
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    socket.Connect(IPAddress.Loopback, port);
                    var client = new Iec61850Client(new MmsSocketChannel(socket), maximumTpduSize: 128);
                    client.Associate(); var rcb = DataChecks.Run(client); DataChecks.Quality(client);
                    Integrity(client, rcb);
                }
            }
            else using (var peer = Start(executable, "dataclient " + port + " 127.0.0.1"))
            {
                if (!peer.WaitForExit(15000)) { peer.Kill(true); throw new Exception("Native data client timeout."); }
                string output = peer.StandardOutput.ReadToEnd(), error = peer.StandardError.ReadToEnd();
                Check(peer.ExitCode == 0 && output.Contains("NATIVE DATA REPORT PASS"));
                if (error.Length != 0) throw new Exception(error);
            }
            worker.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
        }
        finally { listener.Stop(); }
    }
}
