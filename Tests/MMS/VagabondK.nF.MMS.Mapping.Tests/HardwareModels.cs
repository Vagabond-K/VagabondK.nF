using System;
using System.Net.Sockets;
using VagabondK.nF.MMS;
using VagabondK.nF.MMS.TCP;
using VagabondK.nF.IEC61850;
#if !SERVER_ONLY
using VagabondK.nF.IEC61850.Client;
#endif
using VagabondK.nF.IEC61850.Mapping;
#if !CLIENT_ONLY
using VagabondK.nF.IEC61850.Server;
#endif

#if !CLIENT_ONLY
// 보드/데스크톱이 공유하는 메모리 전용 모델입니다. 일반 쓰기 검증과 enhanced 요청 수락·완료를 분리해 확인합니다.
[Iec61850ServerMap]
internal partial class MappingHardwareLocal
{
    [Iec61850Point("MAPLD0", "L$ST$Position$stVal")] public IecDoublePoint Position = IecDoublePoint.On;
    [Iec61850Point("MAPLD0", "L$ST$Status$q")] public IecQuality Quality = IecQuality.Test;
    [Iec61850Point("MAPLD0", "L$ST$Status$stVal")] public bool Status = true;
    [Iec61850Point("MAPLD0", "L$ST$Status$t", TimeQuality = nameof(TimeQuality))] public long Time = 1700000000000L;
    public byte TimeQuality = 0x3f;
    [Iec61850Point("MAPLD0", "Target", Writable = true, Validate = nameof(CheckTarget))] public int Target { get; private set; } = 42;
    [Iec61850Point("MAPLD0", "Temperature", Writable = true)] public float Temperature = 12.5f;
    public int Executions { get; private set; }
    public MmsControlRequest Pending { get; private set; }
    private int CheckTarget(MmsValue value) { return value.Integer >= 0 && value.Integer <= 1000 ? -1 : 11; }
    private int Check(MmsControlRequest request) { return request.OriginCategory == 3 ? -1 : 3; }
    [Iec61850Control("MAPLD0", "L$CO$Switch", MmsControlModel.DirectEnhanced, Validate = nameof(Check))]
    private int Execute(MmsControlRequest request)
    { Executions++; Status = request.Value; Pending = request; return -1; }
}

internal sealed class MappingServerModel
{
    private readonly object sync = new object();
    private readonly MappingHardwareLocal model = new MappingHardwareLocal();
    private readonly MappingHardwareLocal.Iec61850ServerBinding binding;
    private readonly MmsServerDataSet dataSet;
    private readonly MmsServerReportControl report;
    internal int Reports, Requests;
    internal MappingServerModel()
    {
        binding = new MappingHardwareLocal.Iec61850ServerBinding(model, sync);
        var members = new VagabondK.nF.MMS.Server.MmsServerVariable[binding.Count];
        for (int n = 0; n < members.Length; n++) members[n] = binding.GetVariable(n);
        dataSet = new MmsServerDataSet("MAPLD0", "Values", members);
        report = new MmsServerReportControl("MAPLD0", "L$RP$Events", "MappingEvents", dataSet, minimumIntervalMilliseconds: 1);
        if (binding.GetControl(0).Enabled) throw new InvalidOperationException("Control must start disabled.");
        binding.GetControl(0).Enabled = true;
#if MAPPING
        for (int n = 0; n < 100; n++) binding.Publish();
        MappingBenchmarks.HeapBefore = nanoFramework.Runtime.Native.GC.Run(true);
        long started = Environment.TickCount64;
        for (int n = 0; n < MappingBenchmarks.Iterations; n++) binding.Publish();
        MappingBenchmarks.IdleMilliseconds = (uint)(Environment.TickCount64 - started);
        started = Environment.TickCount64;
        for (int n = 0; n < MappingBenchmarks.Iterations; n++) { model.Status = !model.Status; binding.Publish(); }
        MappingBenchmarks.ChangedMilliseconds = (uint)(Environment.TickCount64 - started);
        MappingBenchmarks.HeapAfter = nanoFramework.Runtime.Native.GC.Run(true);
#endif
    }
    internal void Serve(Socket socket)
    {
        lock (sync)
        {
            var session = binding.CreateSession(new MmsSocketChannel(socket), new[] { dataSet }, new[] { report });
            try
            {
                session.Associate();
                long deadline = Environment.TickCount64 + 30000;
                while (Environment.TickCount64 < deadline)
                {
                    if (!session.ProcessRequest()) return;
                    Requests++;
                    if (session.HasPendingControl && !session.ProcessControlCompletion())
                    {
                        if (model.Pending == null) throw new InvalidOperationException("Missing execution token.");
                        binding.GetControl(0).Complete(model.Pending, true);
                        session.ProcessControlCompletion(); Reports++;
                    }
                    binding.Publish();
                    Reports += session.ProcessReports();
                }
                throw new TimeoutException("Mapping session exceeded its bounded test duration.");
            }
            finally { session.Close(); }
        }
    }
}
#endif

#if !SERVER_ONLY
[Iec61850ClientMap]
[Iec61850RemoteControl("MAPLD0", "L$CO$Switch")]
internal partial class MappingHardwareRemote
{
    [Iec61850Point("MAPLD0", "L$ST$Position$stVal")] public IecDoublePoint Position { get; private set; }
    [Iec61850Point("MAPLD0", "L$ST$Status$q")] public IecQuality Quality { get; private set; }
    [Iec61850Point("MAPLD0", "L$ST$Status$stVal")] public bool Status { get; private set; }
    [Iec61850Point("MAPLD0", "L$ST$Status$t", TimeQuality = nameof(TimeQuality))] public long Time { get; private set; }
    public byte TimeQuality { get; private set; }
    [Iec61850Point("MAPLD0", "Target", Writable = true)] public int Target { get; set; }
    [Iec61850Point("MAPLD0", "Temperature", Writable = true)] public float Temperature { get; set; }
}

internal sealed class MappingClientModel
{
    internal System.Net.IPAddress Peer { get; set; }
    internal int Reports, Requests;
    internal void Serve()
    {
        using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            socket.Connect(new System.Net.IPEndPoint(Peer, 18105));
            MappingHardwareChecks.Run(socket);
            Requests += MappingHardwareChecks.Checks; Reports += MappingHardwareChecks.Reports;
        }
    }
}

internal static class MappingHardwareChecks
{
    internal static int Checks, Reports;
    private static void Ensure(bool value)
    { Checks++; if (!value) throw new InvalidOperationException("Mapping hardware assertion " + Checks); }
    internal static void Run(Socket socket)
    {
        Checks = Reports = 0;
        var client = new Iec61850Client(new MmsSocketChannel(socket), maximumTpduSize: 128);
        var model = new MappingHardwareRemote();
        var binding = new MappingHardwareRemote.Iec61850ClientBinding(model, new object(), client);
        client.Associate();
        binding.ReadAll();
        Ensure(model.Position == IecDoublePoint.On && model.Quality == IecQuality.Test &&
            model.Time == 1700000000000L && model.TimeQuality == 0x3f);
        var set = client.GetDataSetDirectory("MAPLD0", "Values");
        binding.ApplyDataSet(set, client.ReadDataSet(set));
#if MAPPING_CLIENT
        if (MappingBenchmarks.HeapBefore == 0)
        {
            var snapshot = client.ReadDataSet(set);
            for (int n = 0; n < 100; n++) binding.ApplyDataSet(set, snapshot);
            MappingBenchmarks.HeapBefore = nanoFramework.Runtime.Native.GC.Run(true);
            long started = Environment.TickCount64;
            for (int n = 0; n < MappingBenchmarks.Iterations; n++) binding.ApplyDataSet(set, snapshot);
            MappingBenchmarks.ApplyMilliseconds = (uint)(Environment.TickCount64 - started);
            MappingBenchmarks.HeapAfter = nanoFramework.Runtime.Native.GC.Run(true);
        }
#endif
        var rcb = new MmsReportClient(client, "MAPLD0", "L$RP$Events", set, report => {
            binding.ApplyReport(set, report); Reports++;
        });
        rcb.Reserve(true); rcb.Enable(); rcb.GeneralInterrogation();
        if (Reports == 0) client.ReceiveReport();
        Ensure(Reports == 1);
        for (int n = 0; n < 32; n++)
        {
            model.Target = n;
            binding.Write(4);
            Ensure(client.Read("MAPLD0", "Target").Integer == n);
            model.Temperature = n + 0.25f;
            binding.Write(5);
            Ensure(client.Read("MAPLD0", "Temperature").FloatingPoint == n + 0.25f);
            binding.ReadAll();
            Ensure(model.Target == n && model.Temperature == n + 0.25f);
        }
        int original = model.Target;
        model.Target = -1;
        bool denied = false;
        try { binding.Write(4); } catch (MmsAccessException error) { denied = error.ErrorCode == 11; }
        Ensure(denied && !client.Faulted && client.Read("MAPLD0", "Target").Integer == original);
        rcb.Disable(); rcb.Reserve(false);
        var control = binding.CreateControl(0, new byte[] { 77, 65, 80 });
        bool state = model.Status;
        Ensure(control.OperateAndWait(!state, 0, out bool accepted, out int code, true) && accepted && code == -1);
        binding.ReadAll(); Ensure(model.Status == state);
        Ensure(control.OperateAndWait(!state, 0, out accepted, out code) && accepted && code == -1);
        binding.ReadAll(); Ensure(model.Status != state);
        client.Release();
    }
}
#endif

#if MAPPING || MAPPING_CLIENT
internal static class MappingBenchmarks
{
    internal const int Iterations = 1000;
    internal static uint IdleMilliseconds = 0, ChangedMilliseconds = 0, ApplyMilliseconds = 0, HeapBefore = 0, HeapAfter = 0;
}
#endif
