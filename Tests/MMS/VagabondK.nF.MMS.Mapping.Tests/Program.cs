using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using VagabondK.nF.MMS;
using VagabondK.nF.MMS.Client;
using VagabondK.nF.MMS.Server;
using VagabondK.nF.MMS.TCP;
using VagabondK.nF.MMS.Mapping.Generator;
using VagabondK.nF.IEC61850;
using VagabondK.nF.IEC61850.Client;
using VagabondK.nF.IEC61850.Server;
using VagabondK.nF.IEC61850.Mapping.Generator;
using MmsLocal = VagabondK.nF.MMS.Mapping.Sample.LocalData;
using MmsRemote = VagabondK.nF.MMS.Mapping.Sample.RemoteData;
using IecLocal = VagabondK.nF.IEC61850.Mapping.Sample.LocalData;
using IecRemote = VagabondK.nF.IEC61850.Mapping.Sample.RemoteData;
using TypedLocal = VagabondK.nF.IEC61850.Mapping.Sample.TypedLocal;
using TypedRemote = VagabondK.nF.IEC61850.Mapping.Sample.TypedRemote;
using WritableScalars = VagabondK.nF.MMS.Mapping.Sample.WritableScalars;
using WritableRemote = VagabondK.nF.MMS.Mapping.Sample.WritableRemote;

internal static class Program
{
    private static int checks;
    private static void Check(bool value, string message = "Mapping assertion")
    { checks++; if (!value) throw new Exception(message); }
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { checks++; return; } throw new Exception("Expected " + typeof(T).Name); }
    // 기본 실행은 생성기 진단과 실제 매핑·통신·실패 원자성 시험입니다. 보드 모드는 별도 peer와 함께 실행합니다.
    private static void Main(string[] args)
    {
        if (args.Length != 0) { Hardware(args); return; }
        Diagnostics();
        ShorthandDeclarations();
        MmsModel();
        ClientAtomicity();
        InformationReportAtomicity();
        WireMms();
        WireWritableFormats();
        WireIec();
        WireTypedControls();
        FailureSemantics();
        MappedResponseLoss();
        MappedControlResponseLoss();
        Wire(socket => new MappingServerModel().Serve(socket), socket => MappingHardwareChecks.Run(socket));
        UpdateSemantics();
        MappingPerformance();
        Console.WriteLine("MMS/IEC STATIC MAPPING PASS: " + checks + " checks.");
    }
    // 서버 보드에는 잘린 TPKT 뒤 새 연결로 복구되는지 확인하고, 클라이언트 보드에는 16회 세션을 제공합니다.
    // 지정 주소·포트는 격리 시험망의 전용 peer여야 하며 실제 설비에 연결하지 마십시오.
    private static void Hardware(string[] args)
    {
        if (args.Length == 4 && args[0] == "--mapping-server")
        {
            int count = int.Parse(args[3]);
            using (var partial = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                partial.Connect(IPAddress.Parse(args[1]), int.Parse(args[2]));
                partial.Send(new byte[] { 3, 0, 0 });
            }
            for (int n = 0; n < count; n++)
            {
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    socket.Connect(IPAddress.Parse(args[1]), int.Parse(args[2]));
                    MappingHardwareChecks.Run(socket);
                    Check(MappingHardwareChecks.Checks >= 100 && MappingHardwareChecks.Reports > 0);
                }
            }
            Console.WriteLine("MAPPING HOST PASS sessions=" + count);
            return;
        }
        if (args.Length == 2 && args[0] == "--mapping-client")
        {
            var model = new MappingServerModel();
            var listener = new TcpListener(IPAddress.Any, int.Parse(args[1]));
            listener.Start();
            Console.WriteLine("READY");
            try { for (int n = 0; n < 16; n++) using (var socket = listener.AcceptSocket()) model.Serve(socket); }
            finally { listener.Stop(); }
            Console.WriteLine("MAPPING CLIENT PEER PASS");
            return;
        }
        if (args.Length == 3 && (args[0] == "--interop-iec-server" || args[0] == "--interop-mms-server"))
        {
            bool iec = args[0] == "--interop-iec-server";
            var iecServer = iec ? new InteropIecServer() : null;
            var mmsServer = iec ? null : new InteropMmsServer();
            int sessions = 2 * int.Parse(args[2]), faults = 0;
            var listener = new TcpListener(IPAddress.Loopback, int.Parse(args[1]));
            listener.Start();
            Console.WriteLine("READY");
            try
            {
                for (int n = 0; n < sessions; n++)
                    using (var socket = listener.AcceptSocket())
                    {
                        try { if (iec) iecServer.Serve(socket); else mmsServer.Serve(socket); }
                        catch (IOException) { faults++; }
                        catch (SocketException) { faults++; }
                        catch (MmsProtocolException) { faults++; }
                    }
            }
            finally { listener.Stop(); }
            Console.WriteLine("INTEROP " + (iec ? "IEC" : "MMS") + " SERVER PASS sessions=" + sessions + " faults=" + faults +
                " writes=" + (iec ? iecServer.Writes : mmsServer.Writes) + " reports=" + (iec ? iecServer.Reports : mmsServer.Reports) +
                " requests=" + (iec ? iecServer.Requests : mmsServer.Requests));
            return;
        }
        if (args.Length == 4 && (args[0] == "--interop-iec-client" || args[0] == "--interop-mms-client"))
        {
            int iterations = int.Parse(args[3]);
            try
            {
                for (int n = 0; n < iterations; n++)
                    if (args[0] == "--interop-iec-client") InteropClients.RunIec(IPAddress.Parse(args[1]), int.Parse(args[2]));
                    else InteropClients.RunMms(IPAddress.Parse(args[1]), int.Parse(args[2]));
            }
            catch { Console.Error.WriteLine("INTEROP TRACE" + InteropStats.Describe()); throw; }
            Console.WriteLine("INTEROP " + (args[0] == "--interop-iec-client" ? "IEC" : "MMS") + " CLIENT PASS iterations=" + iterations +
                " checks=" + InteropStats.Checks + " applied=" + InteropStats.Applied + " rejected=" + InteropStats.Rejected);
            return;
        }
        throw new ArgumentException("Expected --mapping-server, --mapping-client, --interop-{iec|mms}-server port runs or --interop-{iec|mms}-client host port iterations.");
    }
    private static void Measure(string name, Action action, bool allocationFree = false)
    {
        for (int n = 0; n < 10000; n++) action();
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int n = 0; n < 10000; n++) action();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        double us = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000000.0 /
            System.Diagnostics.Stopwatch.Frequency / 10000;
        Console.WriteLine("MAPPING PERF " + name + " bytes/call=" + allocated / 10000.0 + " us/call=" + us);
        if (allocationFree) Check(allocated == 0, name + " must allocate zero bytes after warmup.");
    }
    private static void MappingPerformance()
    {
        var model = new IecLocal();
        var server = new IecLocal.Iec61850ServerBinding(model, new object());
        var variables = Enumerable.Range(0, server.Count).Select(server.GetVariable).ToArray();
        var set = new MmsServerDataSet("NFTESTLD0", "Values", variables);
        var rcb = new MmsServerReportControl("NFTESTLD0", "GGIO1$RP$Events", "Events", set, minimumIntervalMilliseconds: 1);
        var owner = new object();
        byte[] enabled = MmsEncoding.Tlv(0x83, new byte[] { 1 });
        Check(rcb.Write(owner, "RptEna", MmsEncoding.Single(enabled, 0, enabled.Length, 0x83), 0) == -1);
        long now = 0;
        for (int n = 0; n < 10; n++) { server.Publish(); Check(rcb.Build(owner, ++now) == null); }
        model.Status = !model.Status; server.Publish();
        Check(rcb.Build(owner, ++now) != null && rcb.Build(owner, ++now) == null, "Only the actual state transition produces a change report.");
        Check(rcb.Write(owner, "GI", MmsEncoding.Single(enabled, 0, enabled.Length, 0x83), now) == -1);
        Check(rcb.Build(owner, ++now) != null, "GI reports unchanged values.");
        Measure("idlePublishAndReportPoll", () => { server.Publish(); rcb.Build(owner, ++now); }, true);
        Measure("changedPublishAndReport", () => { model.Status = !model.Status; server.Publish(); rcb.Build(owner, ++now); });
        var refs = variables.Select(v => new MmsVariableReference(v.Domain, v.Item)).ToArray();
        var clientSet = new MmsDataSet(new MmsNamedVariableList(set.Domain, set.Item, false, refs));
        var client = new IecRemote.Iec61850ClientBinding(new IecRemote(), new object(), new Iec61850Client(new NoPeer()));
        var values = variables.Select(Value).ToArray();
        var results = values.Select(v => new MmsReadResult(v, -1)).ToArray();
        var report = new MmsReport("Events", 0, "NFTESTLD0/Values", 1, values, new byte[values.Length]);
        Measure("applyDataSet", () => client.ApplyDataSet(clientSet, results), true);
        Measure("applyReport", () => client.ApplyReport(clientSet, report), true);
        Measure("decodeIecLeaves", () => {
            Iec61850Data.ReadDoublePoint(values[0]); Iec61850Data.ReadQuality(values[1]);
            Iec61850Data.ReadTimestampMilliseconds(values[3]); Iec61850Data.ReadTimeQuality(values[3]);
        }, true);
    }
    private static void UpdateSemantics()
    {
        var flag = new MmsServerVariable("P", "B", false);
        long revision = flag.ChangeVersion;
        flag.UpdateBoolean(false); Check(flag.ChangeVersion == revision);
        flag.UpdateBoolean(true); Check(flag.ChangeVersion == revision + 1 && Value(flag).Boolean);
        revision = flag.ChangeVersion; flag.SetBoolean(true); Check(flag.ChangeVersion == revision + 1);
        foreach (int n in new[] { int.MinValue, -32769, -32768, -129, -128, -1, 0, 1, 127, 128, 32767, 32768, int.MaxValue })
        {
            var integer = new MmsServerVariable("P", "I", n);
            revision = integer.ChangeVersion;
            integer.UpdateInteger(n); Check(integer.ChangeVersion == revision && Value(integer).Integer == n);
            integer.UpdateInteger(~n); Check(integer.ChangeVersion == revision + 1 && Value(integer).Integer == ~n);
            revision = integer.ChangeVersion; integer.SetInteger(~n); Check(integer.ChangeVersion == revision + 1);
        }
        var floating = new MmsServerVariable("P", "F", 0f);
        float negativeZero = BitConverter.Int32BitsToSingle(unchecked((int)0x80000000));
        foreach (float n in new[] { negativeZero, 0f, float.MinValue, float.MaxValue, float.Epsilon, -float.Epsilon,
            float.PositiveInfinity, float.NegativeInfinity, float.NaN })
        {
            floating.UpdateFloatingPoint(n); revision = floating.ChangeVersion;
            floating.UpdateFloatingPoint(n); Check(floating.ChangeVersion == revision);
            Check(floating.Encoded.Skip(3).Take(4).SequenceEqual(new MmsServerVariable("P", "F", n).Encoded.Skip(3).Take(4)));
            floating.SetFloatingPoint(n); Check(floating.ChangeVersion == revision + 1);
        }
        floating.SetFloatingPoint(0f); revision = floating.ChangeVersion;
        floating.UpdateFloatingPoint(negativeZero); Check(floating.ChangeVersion == revision + 1 && floating.Encoded[3] == 128);
        Reject<InvalidOperationException>(() => flag.UpdateInteger(1));
        Reject<InvalidOperationException>(() => flag.UpdateFloatingPoint(1));
        Reject<InvalidOperationException>(() => floating.UpdateBoolean(true));
        var quality = Iec61850ServerData.CreateQuality("P", "Q");
        for (int n = 0; n < 8192; n++)
        {
            quality.UpdateQuality((IecQuality)n); revision = quality.ChangeVersion;
            Check(Iec61850Data.ReadQuality(Value(quality)) == (IecQuality)n);
            quality.UpdateQuality((IecQuality)n); Check(quality.ChangeVersion == revision);
        }
        revision = quality.ChangeVersion; quality.SetQuality((IecQuality)8191); Check(quality.ChangeVersion == revision + 1);
        Reject<ArgumentOutOfRangeException>(() => quality.UpdateQuality((IecQuality)8192));
        var position = Iec61850ServerData.CreateDoublePoint("P", "D");
        for (int n = 0; n < 4; n++)
        {
            position.UpdateDoublePoint((IecDoublePoint)n); revision = position.ChangeVersion;
            position.UpdateDoublePoint((IecDoublePoint)n); Check(position.ChangeVersion == revision);
            Check(Iec61850Data.ReadDoublePoint(Value(position)) == (IecDoublePoint)n);
        }
        revision = position.ChangeVersion; position.SetDoublePoint(IecDoublePoint.Bad); Check(position.ChangeVersion == revision + 1);
        Reject<ArgumentOutOfRangeException>(() => position.UpdateDoublePoint((IecDoublePoint)4));
        Reject<InvalidOperationException>(() => position.UpdateQuality(IecQuality.Good));
        Reject<InvalidOperationException>(() => quality.UpdateDoublePoint(IecDoublePoint.On));
        var timestamp = Iec61850ServerData.CreateTimestamp("P", "T");
        foreach (long n in new[] { 0L, 1L, 999L, 1000L, 1700000000001L, uint.MaxValue * 1000L + 999 })
        {
            timestamp.UpdateTimestamp(n, 0x80); revision = timestamp.ChangeVersion;
            timestamp.UpdateTimestamp(n, 0x80); Check(timestamp.ChangeVersion == revision);
            Check(timestamp.Encoded.SequenceEqual(Iec61850ServerData.CreateTimestamp("P", "T", n, 0x80).Encoded));
            timestamp.UpdateTimestamp(n, 0x3f); Check(timestamp.ChangeVersion == revision + 1 && timestamp.Encoded[9] == 0x3f);
            revision = timestamp.ChangeVersion; timestamp.SetTimestamp(n); Check(timestamp.ChangeVersion == revision + 1);
        }
        revision = timestamp.ChangeVersion;
        Reject<ArgumentOutOfRangeException>(() => timestamp.UpdateTimestamp(-1));
        Reject<ArgumentOutOfRangeException>(() => timestamp.UpdateTimestamp((uint.MaxValue + 1L) * 1000L));
        Check(timestamp.ChangeVersion == revision);
        Reject<InvalidOperationException>(() => flag.UpdateTimestamp(0));
        var snapshot = Value(timestamp);
        byte original = snapshot.GetByte(0); var copy = snapshot.GetBytes(); copy[0] ^= 255;
        timestamp.SetTimestamp(0); Check(snapshot.GetByte(0) == original, "GetByte never exposes mutable storage or aliases server data.");
        Reject<ArgumentOutOfRangeException>(() => snapshot.GetByte(-1));
        Reject<ArgumentOutOfRangeException>(() => snapshot.GetByte(8));
        Reject<InvalidOperationException>(() => MmsValue.FromBoolean(true).GetByte(0));
        byte[] empty = MmsEncoding.Tlv(0x89);
        var emptyValue = MmsValue.Decode(MmsEncoding.Single(empty, 0, empty.Length, 0x89));
        Reject<ArgumentOutOfRangeException>(() => emptyValue.GetByte(0));
        var local = new MmsLocal(); var binding = new MmsLocal.MmsServerBinding(local, new object());
        binding.GetVariable(0).SetBoolean(!local.Contact); binding.Publish();
        Check(Value(binding.GetVariable(0)).Boolean == local.Contact, "Publish compares actual variable state, not stale model cache.");
        revision = binding.GetVariable(0).ChangeVersion; binding.Publish();
        Check(binding.GetVariable(0).ChangeVersion == revision);
        var count = binding.GetVariable(1);
        revision = count.ChangeVersion;
        Check(count.ApplyWrite(Int(local.Count)) == -1 && count.ChangeVersion == revision + 1,
            "Explicit same-value remote Write keeps its original version and handler semantics.");
    }
    private static readonly CSharpParseOptions Parse = new CSharpParseOptions(LanguageVersion.CSharp7_3);
    private static readonly MetadataReference[] References =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")).Split(Path.PathSeparator)
        .Select(p => MetadataReference.CreateFromFile(p)).Cast<MetadataReference>()
        .Append(MetadataReference.CreateFromFile(typeof(MmsValue).Assembly.Location)).ToArray();
    private static string Model(bool iec, bool server, string members, string attributes = "", string declaration = "public partial class Model")
    {
        string name = iec ? "Iec61850" : "Mms";
        return "using VagabondK.nF.MMS; using VagabondK.nF." + (iec ? "IEC61850" : "MMS") + ".Mapping; " +
            "using VagabondK.nF.IEC61850; using VagabondK.nF.IEC61850.Server; [" + name + (server ? "Server" : "Client") + "Map] " +
            attributes + declaration + " {" + members + "}";
    }
    private static Compilation Generate(bool iec, string text, out GeneratorDriverRunResult result)
    {
        var compilation = CSharpCompilation.Create("Mapping_" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(text, Parse) }, References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        IIncrementalGenerator generator = iec ? new Iec61850MappingGenerator() : new MmsMappingGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new[] { generator.AsSourceGenerator() }, parseOptions: Parse);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _);
        result = driver.GetRunResult();
        Check(result.Results.All(r => r.Exception == null), string.Join("\n", result.Results.Select(r => r.Exception)));
        return updated;
    }
    private static void Invalid(bool iec, string text, int id)
    {
        Generate(iec, text, out var result);
        Check(result.Diagnostics.Any(d => d.Id == (iec ? "VKIE" : "VKMM") + id.ToString("D3")), "Missing diagnostic: " + text);
        Check(!result.Results.SelectMany(r => r.GeneratedSources).Any(s => !s.HintName.Contains("Attributes")), "Invalid model emitted implementation.");
    }
    private static void Valid(bool iec, string source)
    {
        var compiled = Generate(iec, source, out var result);
        Check(!compiled.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error),
            string.Join("\n", compiled.GetDiagnostics()));
        foreach (var generated in result.Results.SelectMany(r => r.GeneratedSources))
            Check(!generated.SourceText.ToString().Contains("Reflection") && !generated.SourceText.ToString().Contains("Dictionary"));
    }
    private static void ShorthandDeclarations()
    {
        foreach (bool iec in new[] { false, true })
        foreach (bool server in new[] { false, true })
        {
            string name = iec ? "Iec61850" : "Mms";
            string map = name + (server ? "Server" : "Client") + "Map";
            string canonical = Model(iec, server,
                "[" + name + "Point(\"Plant\",\"Temperature\",Writable=true)] public float Value;" +
                "[" + name + "Point(\"Other\",\"Target\")] public int Target;");
            string shorthand = canonical.Replace(map + "]", map + "(Domain=\"Plant\")]")
                .Replace("Point(\"Plant\",\"Temperature\"", "Point(\"Temperature\"");
            Valid(iec, canonical);
            Valid(iec, shorthand);
            Generate(iec, canonical, out var before);
            Generate(iec, shorthand, out var after);
            Check(before.Results.SelectMany(r => r.GeneratedSources).Where(s => !s.HintName.Contains("Attributes")).Select(s => s.SourceText.ToString())
                .SequenceEqual(after.Results.SelectMany(r => r.GeneratedSources).Where(s => !s.HintName.Contains("Attributes")).Select(s => s.SourceText.ToString())),
                "Common domain and explicit override preserve runtime implementation: " + name + "/" + server);
            Invalid(iec, Model(iec, server, "[" + name + "Point(\"Temperature\")] public float Value;"), 3);
            Invalid(iec, shorthand.Replace("Domain=\"Plant\"", "Domain=\"\""), 3);
            Invalid(iec, shorthand.Replace("Domain=\"Plant\"", "Domain=null"), 3);
            Invalid(iec, shorthand.Replace("Domain=\"Plant\"", "Domain=\"한글\""), 3);
            Invalid(iec, shorthand.Replace("\"Temperature\"", "null"), 3);
            Invalid(iec, shorthand.Replace("\"Other\",\"Target\"", "\"Plant\",\"Temperature\""), 4);
        }
    }

    private static void Diagnostics()
    {
        foreach (bool iec in new[] { false, true })
        foreach (bool server in new[] { false, true })
        {
            string point = iec ? "Iec61850Point" : "MmsPoint";
            string member = "[" + point + "(\"Plant\",\"Temperature\")] public float Value {get;set;}";
            Valid(iec, Model(iec, server, member));
            Valid(iec, Model(iec, server, member.Replace("Value", "@event"), declaration: "public partial class @class"));
            foreach (string decl in new[] { "public class Model", "public static partial class Model", "public abstract partial class Model",
                "public partial class Model<T>", "public partial struct Model", "public partial class Model : System.Exception" })
                Invalid(iec, Model(iec, server, member, declaration: decl), 1);
            Invalid(iec, Model(iec, server, member.Replace("public float", "public static float")), 2);
            Invalid(iec, Model(iec, server, member.Replace("float", "string")), 2);
            Invalid(iec, Model(iec, server, member.Replace("{get;set;}", server ? "{set{}}" : "{get;}")), 2);
            foreach (string bad in new[] { "", "a b", "한글", new string('a', 65), "bad\nname" })
                Invalid(iec, Model(iec, server, member.Replace("\"Temperature\"", SymbolDisplay.FormatLiteral(bad, true))), 3);
            Invalid(iec, Model(iec, server, member.Replace("\"Plant\"", "null")), 3);
            Invalid(iec, Model(iec, server, member + member.Replace("Value", "Other")), 4);
            Invalid(iec, Model(iec, server, member + "public int " + (iec ? "Iec61850" : "Mms") + (server ? "Server" : "Client") + "Binding;"), 5);
            Invalid(iec, Model(iec, server, string.Concat(Enumerable.Range(0, 17).Select(n =>
                "[" + point + "(\"Plant\",\"I" + n + "\")] public int V" + n + ";"))), 2);
            Valid(iec, Model(iec, server, string.Concat(Enumerable.Range(0, 16).Select(n =>
                "[" + point + "(\"Plant\",\"I" + n + "\")] public int V" + n + ";"))));
            Invalid(iec, Model(iec, server, member.Replace(")]", ",Validate=\"Missing\")]")), 3);
            if (!server) Invalid(iec, Model(iec, false, member.Replace("{get;set;}", "{set{}}").Replace(")]", ",Writable=true)]")), 2);
        }
        Valid(false, Model(false, true, "[MmsPoint(\"a/b\",\"c\")]public int A;[MmsPoint(\"a\",\"b/c\")]public int B;"));
        Valid(false, Model(false, true, "[MmsPoint(\"P\",\"I\")]private readonly int X=3;"));
        Valid(false, Model(false, false, "[MmsPoint(\"P\",\"I\")]public int X{set{}}"));
        Valid(true, Model(true, true, "[Iec61850Point(\"P\",\"LN$ST$X$t\",TimeQuality=nameof(Q))] public long Time; public byte Q;"));
        Invalid(true, Model(true, false, "[Iec61850Point(\"P\",\"Time\",TimeQuality=\"X\")]public long T; public int X;"), 3);
        Invalid(true, Model(true, true, "[Iec61850Point(\"P\",\"Time\",Writable=true)]public long T;"), 3);
        Invalid(true, Model(true, true, "[Iec61850Point(\"P\",\"L$CO$X\")]public bool T;"), 3);
        Invalid(true, Model(true, true, "[Iec61850Point(\"P\",\"L$RP$X\")]public bool T;"), 3);
        string scalar = "[Iec61850Point(\"P\",\"Value\")]public bool Value;";
        string control = "[Iec61850Control(\"P\",\"L$CO$Switch\",MmsControlModel.DirectNormal,Validate=\"Check\")]private int Execute(MmsControlRequest r){return -1;} private int Check(MmsControlRequest r){return -1;}";
        Valid(true, Model(true, true, scalar + control));
        Invalid(true, Model(true, true, scalar + control.Replace("Validate=\"Check\"", "Validate=\"Missing\"")), 6);
        Invalid(true, Model(true, true, scalar + control.Replace("DirectNormal", "SboEnhanced").Replace("L$CO$Switch", "L$CO$X$Y")), 6);
        Invalid(true, Model(true, true, scalar + control.Replace("return -1;", "return -1;").Replace("private int Execute", "private static int Execute")), 6);
        Invalid(true, Model(true, true, scalar + control + control.Replace("Execute", "Other").Replace("Check", "CheckOther")), 4);
        Invalid(true, Model(true, true, scalar.Replace("\"Value\"", "\"L$ST$Switch$stVal\"") + control), 4);
        Valid(true, Model(true, false, scalar, "[Iec61850RemoteControl(\"P\",\"L$CO$Switch\",ValueKind=MmsControlValueKind.AnalogFloat32)]"));
        Invalid(true, Model(true, false, scalar, "[Iec61850RemoteControl(\"P\",\"invalid\")]"), 6);
        Invalid(true, Model(true, false, scalar + control), 6);
        Invalid(true, Model(true, true, scalar, "[Iec61850RemoteControl(\"P\",\"L$CO$Switch\")]"), 6);
        Invalid(false, Model(false, true, "[MmsPoint(\"P\",\"I\")]public int X;", "[MmsClientMap]"), 1);
        Invalid(true, Model(true, false, "[Iec61850Point(\"P\",\"A\",TimeQuality=\"Q\")]public long A;[Iec61850Point(\"P\",\"B\",TimeQuality=\"Q\")]public long B;public byte Q;"), 4);
        foreach (bool iec in new[] { false, true })
        {
            string point = iec ? "Iec61850Point" : "MmsPoint";
            string first = "[" + point + "(\"P\",\"X\")]public int X;";
            string fields = string.Concat(Enumerable.Range(0, 254).Select(n => "public int F" + n + ";"));
            Valid(iec, Model(iec, true, first + fields));
            Invalid(iec, Model(iec, true, first + fields + "public int Overflow;"), 7);
            string methods = string.Concat(Enumerable.Range(0, 255).Select(n => "public void F" + n + "(){}"));
            Invalid(iec, Model(iec, true, first + methods), 7);
            foreach (string malformed in new[] { "Writable=Missing", "Writable=\"bad\"" })
                Generate(iec, Model(iec, true, first.Replace(")]", "," + malformed + ")]")), out _);
            Generate(iec, Model(iec, true, "[" + point + "(\"P\")]public int X;"), out _);
        }
        Invalid(true, Model(true, true, scalar + string.Concat(Enumerable.Range(0, 9).Select(n =>
            control.Replace("Switch", "C" + n).Replace("Execute", "E" + n).Replace("Check", "V" + n)))), 6);
        Valid(true, Model(true, true, scalar + string.Concat(Enumerable.Range(0, 8).Select(n =>
            control.Replace("Switch", "C" + n).Replace("Execute", "E" + n).Replace("Check", "V" + n)))));
    }
    private sealed class NoPeer : IMmsChannel
    {
        public long Milliseconds { get { return Environment.TickCount64; } }
        public int Read(byte[] buffer, int offset, int count, int timeoutMilliseconds) { throw new IOException("No peer."); }
        public int Write(byte[] buffer, int offset, int count, int timeoutMilliseconds) { throw new IOException("No peer."); }
    }
    private static MmsValue Value(MmsServerVariable variable)
    { byte[] bytes = variable.EncodeValue(); return MmsValue.Decode(MmsEncoding.Single(bytes, 0, bytes.Length, bytes[0])); }
    private static BerElement Int(int value)
    { byte[] bytes = MmsEncoding.Int(0x85, value); return MmsEncoding.Single(bytes, 0, bytes.Length, 0x85); }
    private static void MmsModel()
    {
        var local = new MmsLocal(); var sync = new object();
        var binding = new MmsLocal.MmsServerBinding(local, sync);
        Check(binding.Count == 3 && binding.GetVariable(0).Item == "Contact");
        var count = binding.GetVariable(1);
        Check(count.ApplyWrite(Int(123)) == -1 && local.Count == 123);
        Check(count.ApplyWrite(Int(-1)) == 11 && local.Count == 123 && Value(count).Integer == 123);
        Check(binding.GetVariable(0).ApplyWrite(Int(1)) == 3);
        Reject<ArgumentNullException>(() => new MmsLocal.MmsServerBinding(null, sync));
        local.Temperature = float.NaN; local.Contact = false;
        Reject<ArgumentOutOfRangeException>(() => binding.Publish());
        Check(Value(binding.GetVariable(0)).Boolean, "Publish validates before changing any variable.");
        local.Temperature = 4.5f; binding.Publish(); Check(!Value(binding.GetVariable(0)).Boolean);
        var scalars = new WritableScalars(); var sb = new WritableScalars.MmsServerBinding(scalars, sync);
        byte[] encoded = new MmsServerVariable("P", "X", 12.25f).EncodeValue();
        Check(sb.GetVariable(1).ApplyWrite(MmsEncoding.Single(encoded, 0, encoded.Length, 0x87)) == -1 && scalars.Number == 12.25f);
        encoded = new MmsServerVariable("P", "X", float.NaN).EncodeValue();
        Check(sb.GetVariable(1).ApplyWrite(MmsEncoding.Single(encoded, 0, encoded.Length, 0x87)) == 11 && scalars.Number == 12.25f);
        encoded = MmsEncoding.Tlv(0x83, new byte[] { 1 });
        Check(sb.GetVariable(0).ApplyWrite(MmsEncoding.Single(encoded, 0, encoded.Length, 0x83)) == -1 && scalars.Flag);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int n = 0; n < 10000; n++) binding.Publish();
        Check(GC.GetAllocatedBytesForCurrentThread() == before, "MMS Publish allocates no temporary arrays.");
        var iec = new IecLocal(); var ib = new IecLocal.Iec61850ServerBinding(iec, sync);
        Check(!ib.GetControl(0).Enabled && ib.GetControl(0).Model == MmsControlModel.DirectEnhanced);
        iec.Quality = IecQuality.Test; iec.TimeQuality = 0x80; ib.Publish();
        Check(Iec61850Data.ReadQuality(Value(ib.GetVariable(1))) == IecQuality.Test);
        Check(Iec61850Data.ReadTimeQuality(Value(ib.GetVariable(3))) == 0x80);
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int n = 0; n < 10000; n++) ib.Publish();
        Check(GC.GetAllocatedBytesForCurrentThread() == before, "IEC Publish is allocation free.");
        iec.Time = -1; Reject<ArgumentOutOfRangeException>(() => ib.Publish());
        iec.Time = 1700000000000L; iec.Quality = (IecQuality)8192; Reject<ArgumentOutOfRangeException>(() => ib.Publish());
        iec.Quality = IecQuality.Good; iec.Position = (IecDoublePoint)4; Reject<ArgumentOutOfRangeException>(() => ib.Publish());
    }
    private static void ClientAtomicity()
    {
        var model = new IecRemote();
        var binding = new IecRemote.Iec61850ClientBinding(model, new object(), new Iec61850Client(new NoPeer()));
        var refs = Enumerable.Range(0, binding.Count).Select(n => binding.GetReference(n)).ToArray();
        var list = new MmsDataSet(new MmsNamedVariableList("NFTESTLD0", "Values", false, refs));
        var local = new IecLocal();
        var server = new IecLocal.Iec61850ServerBinding(local, new object());
        var results = Enumerable.Range(0, binding.Count).Select(n => new MmsReadResult(Value(server.GetVariable(n)), -1)).ToArray();
        binding.ApplyDataSet(list, results);
        Check(model.Status && model.Position == IecDoublePoint.On && model.TimeQuality == 0x3f);
        results[2] = new MmsReadResult(MmsValue.FromBoolean(false), -1);
        results[3] = new MmsReadResult(null, 10);
        Reject<InvalidOperationException>(() => binding.ApplyDataSet(list, results));
        Check(model.Status, "All mapped results validated before assignment.");
        Reject<ArgumentException>(() => binding.ApplyDataSet(list, new MmsReadResult[1]));
        var values = new MmsValue[4]; values[2] = MmsValue.FromBoolean(false);
        binding.ApplyReport(list, new MmsReport("R", 0, "NFTESTLD0/Values", 1, values, new byte[4]));
        Check(!model.Status && model.Position == IecDoublePoint.On, "Omitted report members unchanged.");
        Reject<ArgumentException>(() => binding.ApplyReport(list, new MmsReport("R", 0, "Other", 1, values, new byte[4])));
        var rm = new MmsRemote(); var mb = new MmsRemote.MmsClientBinding(rm, new object(), new MmsClient(new NoPeer()));
        Reject<InvalidOperationException>(() => mb.Apply(1, MmsValue.FromFloatingPoint(1)));
        Reject<InvalidOperationException>(() => mb.Apply(1, MmsValue.FromBoolean(true)));
        Reject<InvalidOperationException>(() => mb.Write(0));
        Reject<InvalidOperationException>(() => mb.ReadAll());
        byte[] largeInt = MmsEncoding.Int(0x85, (long)int.MaxValue + 1);
        Reject<ArgumentOutOfRangeException>(() => mb.Apply(1, MmsValue.Decode(MmsEncoding.Single(largeInt, 0, largeInt.Length, 0x85))));
        byte[] binary64 = new byte[9]; binary64[0] = 11;
        byte[] bits = BitConverter.GetBytes(double.MaxValue);
        for (int n = 0; n < 8; n++) binary64[n + 1] = bits[7 - n];
        byte[] largeFloat = MmsEncoding.Tlv(0x87, binary64);
        Reject<ArgumentOutOfRangeException>(() => mb.Apply(2, MmsValue.Decode(MmsEncoding.Single(largeFloat, 0, largeFloat.Length, 0x87))));
        Reject<ArgumentOutOfRangeException>(() => mb.Apply(2, MmsValue.FromFloatingPoint(float.NaN)));
        var extra = new MmsDataSet(new MmsNamedVariableList("NFTESTLD0", "Extra", false, new[] {
            new MmsVariableReference("Other","Unmapped"), refs[2] }));
        binding.ApplyDataSet(extra, new[] { new MmsReadResult(null, 3), new MmsReadResult(MmsValue.FromBoolean(true), -1) });
        Check(model.Status, "Unmapped report/dataset results do not overwrite model.");
        values[0] = MmsValue.FromBoolean(true); values[2] = MmsValue.FromBoolean(false);
        Reject<ArgumentException>(() => binding.ApplyReport(list, new MmsReport("R", 0, "NFTESTLD0/Values", 1, values, new byte[4])));
        Check(model.Status, "Report type error is detected before assignment.");
    }
    private static void Wire(Action<Socket> server, Action<Socket> client)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var work = Task.Run(() => { using (var socket = listener.AcceptSocket()) server(socket); });
        try
        {
            using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            { socket.Connect(IPAddress.Loopback, port); client(socket); }
            Check(work.Wait(15000), "Server finished.");
        }
        finally { listener.Stop(); }
    }
    private static MmsInformationReport Report(byte[][] names, params byte[][] results)
    {
        var entries = names.Select(n => MmsEncoding.Tlv(0x30, MmsEncoding.Tlv(0xa0, n))).ToArray();
        byte[] pdu = MmsEncoding.Tlv(0xa3, MmsEncoding.Tlv(0xa0, MmsEncoding.Tlv(0xa0, entries), MmsEncoding.Tlv(0xa0, results)));
        return MmsInformationReport.Decode(MmsEncoding.Single(pdu, 0, pdu.Length, 0xa3));
    }
    private static byte[] Name(string item)
    { return MmsEncoding.Tlv(0xa1, MmsEncoding.Identifier("Plant"), MmsEncoding.Identifier(item)); }
    private static void InformationReportAtomicity()
    {
        var model = new MmsRemote { Count = 9 };
        var binding = new MmsRemote.MmsClientBinding(model, new object(), new MmsClient(new NoPeer()));
        binding.Apply(0, MmsValue.FromBoolean(true));
        var names = new[] { Name("Contact"), Name("Count"), Name("Unmapped") };
        byte[] flag = MmsEncoding.Tlv(0x83, new byte[] { 0 });
        var report = Report(names, flag, MmsEncoding.Int(0x80, 3), MmsEncoding.Int(0x80, 10));
        Reject<InvalidOperationException>(() => binding.ApplyInformationReport(report));
        Check(model.Contact && model.Count == 9, "Access error in later mapped result never partially changes the model.");
        report = Report(names, flag, MmsEncoding.Int(0x85, 31), MmsEncoding.Int(0x80, 10));
        binding.ApplyInformationReport(report);
        Check(!model.Contact && model.Count == 31);
        var scopes = new[] { MmsEncoding.Tlv(0x80, new byte[] { 67 }), MmsEncoding.Tlv(0x82, new byte[] { 67 }), Name("Count") };
        binding.ApplyInformationReport(Report(scopes, MmsEncoding.Int(0x80, 3), MmsEncoding.Int(0x80, 3), MmsEncoding.Int(0x85, 32)));
        Check(model.Count == 32);
    }
    private static void WireMms()
    {
        var model = new MmsLocal(); var sync = new object(); var binding = new MmsLocal.MmsServerBinding(model, sync);
        Wire(socket => {
            var list = new MmsServerNamedVariableList("Plant", "Measurements", Enumerable.Range(0, 3).Select(binding.GetVariable).ToArray());
            var session = binding.CreateSession(new MmsSocketChannel(socket), new[] { list });
            try
            {
                session.Associate();
                while (session.ProcessRequest())
                    if (model.Count == 81) { session.SendInformationReport(list); }
            }
            finally { session.Close(); }
        }, socket => {
            var client = new MmsClient(new MmsSocketChannel(socket), maximumTpduSize: 128);
            var remote = new MmsRemote(); var mapped = new MmsRemote.MmsClientBinding(remote, new object(), client);
            client.Associate(); mapped.ReadAll();
            Check(remote.Contact && remote.Count == 42 && remote.Temperature == 12.5f);
            var list = client.GetNamedVariableListDirectory("Plant", "Measurements");
            remote.Count = -1;
            Reject<MmsAccessException>(() => mapped.Write(1));
            Check(model.Count == 42 && !client.Faulted);
            remote.Count = 81; mapped.Write(1);
            Check(model.Count == 81);
            remote.Count = 0;
            var report = client.ReceiveInformationReport();
            Reject<ArgumentException>(() => mapped.ApplyInformationReport(report));
            mapped.ApplyInformationReport(report, list);
            Check(remote.Count == 81);
            client.Release();
        });
    }
    private static void WireIec()
    {
        var local = new IecLocal(); var sync = new object(); var binding = new IecLocal.Iec61850ServerBinding(local, sync);
        Wire(socket => {
            var set = new MmsServerDataSet("NFTESTLD0", "Values", Enumerable.Range(0, binding.Count).Select(binding.GetVariable).ToArray());
            var rcb = new MmsServerReportControl("NFTESTLD0", "GGIO1$RP$Events", "Events", set, minimumIntervalMilliseconds: 1);
            var session = binding.CreateSession(new MmsSocketChannel(socket), new[] { set }, new[] { rcb });
            binding.GetControl(0).Enabled = true;
            try
            {
                session.Associate();
                while (session.ProcessRequest())
                {
                    if (session.HasPendingControl && !session.ProcessControlCompletion())
                    {
                        binding.GetControl(0).Complete(local.Pending, true);
                        session.ProcessControlCompletion();
                    }
                    binding.Publish(); session.ProcessReports();
                }
            }
            finally { session.Close(); }
        }, socket => {
            var client = new Iec61850Client(new MmsSocketChannel(socket), maximumTpduSize: 128);
            var remote = new IecRemote(); var mapped = new IecRemote.Iec61850ClientBinding(remote, new object(), client);
            client.Associate(); mapped.ReadAll();
            Check(remote.Status && remote.Position == IecDoublePoint.On && remote.Time == 1700000000000L);
            var set = client.GetDataSetDirectory("NFTESTLD0", "Values");
            mapped.ApplyDataSet(set, client.ReadDataSet(set));
            int reports = 0;
            var rcb = new MmsReportClient(client, "NFTESTLD0", "GGIO1$RP$Events", set, report => { mapped.ApplyReport(set, report); reports++; });
            rcb.Reserve(true); rcb.Enable(); rcb.GeneralInterrogation();
            if (reports == 0) client.ReceiveReport();
            Check(reports == 1 && remote.Status);
            rcb.Disable(); rcb.Reserve(false);
            var control = mapped.CreateControl(0, new byte[] { 78, 70 });
            Check(control.Model == MmsControlModel.DirectEnhanced);
            Check(control.OperateAndWait(false, 0, out bool accepted, out int error, true) && accepted && error == -1);
            Check(local.Executions == 0 && local.Status, "Test never invokes model execution.");
            Check(control.OperateAndWait(false, 0, out accepted, out error) && accepted && error == -1);
            Check(local.Executions == 1 && !local.Status, "Explicit operation and completion.");
            var denied = mapped.CreateControl(0, new byte[0], 1);
            Check(!denied.OperateAndWait(true, 0, out accepted, out error) && error == 3 && !accepted);
            Check(local.Executions == 1);
            client.Release();
        });
    }
    private static void FailureSemantics()
    {
        var variable = new MmsServerVariable("P", "I", 3); int commits = 0;
        variable.EnableWrite(v => 11, v => { commits++; return -1; });
        Check(variable.ApplyWrite(Int(8)) == 11 && commits == 0 && Value(variable).Integer == 3);
        variable.EnableWrite(null, v => { commits++; return 3; });
        Check(variable.ApplyWrite(Int(8)) == 3 && commits == 1 && Value(variable).Integer == 3);
        variable.EnableWrite(null, v => 12);
        Reject<InvalidOperationException>(() => variable.ApplyWrite(Int(8)));
        variable.EnableWrite(null, v => { throw new ApplicationException("setter failed"); });
        Reject<ApplicationException>(() => variable.ApplyWrite(Int(8)));
        Check(Value(variable).Integer == 3);
        variable.DisableWrite(); Check(variable.ApplyWrite(Int(8)) == 3);
        int effects = 0;
        Wire(socket => {
            var broken = new MmsServerVariable("P", "I", 0);
            broken.EnableWrite(null, v => { effects++; throw new ApplicationException("Model side effect already happened."); });
            var session = new MmsServerSession(new MmsSocketChannel(socket), new[] { broken });
            try
            {
                session.Associate(); Reject<ApplicationException>(() => session.ProcessRequest());
                Check(session.Faulted && !session.Associated && effects == 1);
            }
            finally { session.Close(); }
        }, socket => {
            var client = new MmsClient(new MmsSocketChannel(socket)); client.Associate();
            Reject<IOException>(() => client.Write("P", "I", 7));
            Check(client.Faulted);
            Reject<InvalidOperationException>(() => client.Write("P", "I", 7));
        });
        Check(effects == 1, "A lost response never automatically replays a model side effect.");
    }
    private static void WireWritableFormats()
    {
        var source = new WritableScalars();
        var mapped = new WritableScalars.MmsServerBinding(source, new object());
        Wire(socket => {
            var session = mapped.CreateSession(new MmsSocketChannel(socket));
            try { session.Associate(); while (session.ProcessRequest()) { } }
            finally { session.Close(); }
        }, socket => {
            var client = new MmsClient(new MmsSocketChannel(socket));
            client.Associate();
            var target = new WritableRemote { Flag = true, Number = -12.5f, Count = int.MinValue };
            var binding = new WritableRemote.MmsClientBinding(target, new object(), client);
            for (int i = 0; i < binding.Count; i++) binding.Write(i);
            Check(source.Flag && source.Number == -12.5f && source.Count == int.MinValue, "All sample MMS write types.");
            binding.ReadAll();
            Check(target.Flag && target.Number == source.Number && target.Count == source.Count, "All sample MMS read types.");
            client.Release();
        });
    }
    private static void WireTypedControls()
    {
        var model = new TypedLocal(); var binding = new TypedLocal.Iec61850ServerBinding(model, new object());
        Check(binding.ControlCount == 4);
        Wire(socket => {
            var session = binding.CreateSession(new MmsSocketChannel(socket));
            for (int n = 0; n < binding.ControlCount; n++) binding.GetControl(n).Enabled = true;
            try
            {
                session.Associate();
                while (session.ProcessRequest())
                    if (session.HasPendingControl && !session.ProcessControlCompletion())
                    { binding.GetControl(model.PendingIndex).Complete(model.Pending, true); session.ProcessControlCompletion(); }
            }
            finally { session.Close(); }
        }, socket => {
            var client = new Iec61850Client(new MmsSocketChannel(socket)); client.Associate();
            var received = new TypedRemote();
            var remote = new TypedRemote.Iec61850ClientBinding(received, new object(), client);
            remote.ReadAll();
            Check(remote.Count == 6 && received.Value && received.Count == -42 && received.Temperature == 12.5f &&
                received.Position == IecDoublePoint.On && received.Quality == IecQuality.Good && received.Time == model.Time,
                "Sample covers all six IEC value types.");
            received.Count = 123;
            remote.Write(0);
            received.Temperature = -5.5f;
            remote.Write(3);
            received.Value = false;
            remote.Write(5);
            Check(model.Count == 123 && model.Temperature == -5.5f && !model.Value, "All writable IEC scalar types.");
            var spc = remote.CreateControl(0, new byte[0]);
            Check(model.Executions == 0, "Creating control never executes it.");
            Check(spc.Operate(true, 0, out int error) && error == -1 && model.Executions == 1);
            var dpc = remote.CreateControl(1, new byte[0]);
            Reject<InvalidOperationException>(() => dpc.Operate(false, 0, out int ignored));
            Check(dpc.Select() && dpc.Operate(true, 0, out error));
            Check(model.Pending.Value && model.Pending.ValueKind == MmsControlValueKind.DoublePoint);
            var dpc2 = remote.CreateControl(2, new byte[0]);
            Check(dpc2.OperateAndWait(false, 0, out bool accepted, out error) && accepted && error == -1);
            var apc = remote.CreateControl(3, new byte[0]);
            Check(apc.SelectWithValue(21.5f, 0, out error));
            Check(apc.OperateAndWait(21.5f, 0, out accepted, out error) && accepted && error == -1);
            Check(model.Pending.AnalogValue == 21.5f && model.Executions == 4);
            Check(apc.SelectWithValue(42f, 0, out error) && apc.Cancel(0, out error));
            Check(model.Executions == 4);
            client.Release();
        });
    }
    private sealed class DropMappedResponse : IMmsChannel
    {
        private readonly IMmsChannel inner;
        private readonly Func<bool> drop;
        internal DropMappedResponse(IMmsChannel inner, Func<bool> drop) { this.inner = inner; this.drop = drop; }
        public long Milliseconds { get { return inner.Milliseconds; } }
        public int Read(byte[] b, int offset, int count, int timeout) { return inner.Read(b, offset, count, timeout); }
        public int Write(byte[] b, int offset, int count, int timeout)
        {
            if (drop()) throw new IOException("Injected lost mapped Write response.");
            return inner.Write(b, offset, count, timeout);
        }
    }
    private static void MappedResponseLoss()
    {
        var model = new LostResponseModel();
        var binding = new LostResponseModel.MmsServerBinding(model, new object());
        Wire(socket => {
            var session = binding.CreateSession(new DropMappedResponse(new MmsSocketChannel(socket), () => model.Executions != 0));
            try
            {
                session.Associate();
                Reject<IOException>(() => session.ProcessRequest());
                Check(session.Faulted && model.Executions == 1 && model.Value == 17);
            }
            finally { session.Close(); }
        }, socket => {
            var client = new MmsClient(new MmsSocketChannel(socket)); client.Associate();
            Reject<IOException>(() => client.Write("P", "Value", 17));
            Reject<InvalidOperationException>(() => client.Write("P", "Value", 17));
        });
        Wire(socket => {
            var session = binding.CreateSession(new MmsSocketChannel(socket));
            try { session.Associate(); while (session.ProcessRequest()) { } } finally { session.Close(); }
        }, socket => {
            var client = new MmsClient(new MmsSocketChannel(socket)); client.Associate();
            Check(client.Read("P", "Value").Integer == 17 && model.Executions == 1);
            client.Release();
        });
    }
    private static void MappedControlResponseLoss()
    {
        var model = new TypedLocal();
        var binding = new TypedLocal.Iec61850ServerBinding(model, new object());
        binding.GetControl(0).Enabled = true;
        Wire(socket => {
            var session = binding.CreateSession(new DropMappedResponse(new MmsSocketChannel(socket), () => model.Executions != 0));
            try
            {
                session.Associate();
                Reject<IOException>(() => { while (session.ProcessRequest()) { } });
                Check(session.Faulted && model.Executions == 1 && !model.Pending.Value);
            }
            finally { session.Close(); }
        }, socket => {
            var client = new Iec61850Client(new MmsSocketChannel(socket)); client.Associate();
            var remote = new TypedRemote.Iec61850ClientBinding(new TypedRemote(), new object(), client);
            var control = remote.CreateControl(0, new byte[0]);
            Reject<IOException>(() => control.Operate(false, 0, out int ignored));
            Check(client.Faulted);
            Reject<InvalidOperationException>(() => control.Operate(false, 0, out int ignored));
        });
        Wire(socket => {
            var session = binding.CreateSession(new MmsSocketChannel(socket));
            try { session.Associate(); while (session.ProcessRequest()) { } } finally { session.Close(); }
        }, socket => {
            var client = new Iec61850Client(new MmsSocketChannel(socket)); client.Associate();
            var remote = new TypedRemote.Iec61850ClientBinding(new TypedRemote(), new object(), client);
            remote.CreateControl(0, new byte[0]);
            Check(model.Executions == 1, "Reconnect and binding creation never replay an uncertain Operate.");
            client.Release();
        });
    }
}
