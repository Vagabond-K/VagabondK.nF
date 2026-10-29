using System;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using VagabondK.nF.SNMP;
using VagabondK.nF.SNMP.Mapping.Generator;
using VagabondK.nF.SNMP.Mapping.Sample;
using VagabondK.nF.SNMP.Tests;

/// <summary>SNMP 매핑 소스 생성기 테스트 진입점입니다.</summary>
/// <remarks>
/// 기본 실행: 생성기·저장소·BER·실패 시 값 보존·할당 검사와 SharpSnmpLib 루프백 상호운용 시험.
/// <c>--hardware &lt;보드 IPv4&gt; &lt;반복&gt;</c>: SharpSnmpLib로 SNMP Mapping 샘플 보드(UDP 1161)를 검사합니다.
/// <c>--robust-hardware &lt;보드 IPv4&gt;</c>: 별도 Robustness 이미지(UDP 1161/1162)의 로컬 변경 격리·다중 SET 사전 거절·비정상 데이터그램 복구를 검사합니다.
/// <c>--scaling &lt;보드 IPv4&gt; 8|32|128 &lt;라운드&gt; &lt;출력.json&gt;</c>: 자체 BER 시험기로 Mapping.Hardware 펌웨어의 OID 수별 응답 시간과 메모리를 측정합니다.
/// </remarks>
internal static class Program
{
    private static int assertions;
    private static readonly CSharpParseOptions Parse = new CSharpParseOptions(LanguageVersion.CSharp7_3);
    private static readonly MetadataReference[] References =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")).Split(Path.PathSeparator)
        .Select(p => MetadataReference.CreateFromFile(p)).Cast<MetadataReference>()
        .Append(MetadataReference.CreateFromFile(typeof(SnmpAgent).Assembly.Location)).ToArray();
    public static void Check(bool condition, string message = "SNMP mapping check")
    {
        assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { assertions++; return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private static void Main(string[] args)
    {
        Wire.LongForm = false;
        if (args.Length == 2 && args[0] == "--robust-hardware")
        {
            HardwarePeer.Robustness(args[1]);
            return;
        }
        if (args.Length == 5 && args[0] == "--scaling")
        {
            if (!int.TryParse(args[2], out int count) || (count != 8 && count != 32 && count != 128) ||
                !int.TryParse(args[3], out int rounds) || rounds < 10 || rounds > 10000)
                throw new ArgumentException("Usage: --scaling IP 8|32|128 rounds(10..10000) output.json");
            ScalingPeer.Run(args[1], count, rounds, args[4]);
            return;
        }
        if (args.Length > 0)
        {
            if (args.Length != 3 || args[0] != "--hardware" || !int.TryParse(args[2], out int iterations) || iterations < 1 || iterations > 10000)
                throw new ArgumentException("Usage: --hardware IP iterations(1..10000)");
            HardwarePeer.Run(args[1], iterations);
            Console.WriteLine("SNMP Mapping hardware: " + assertions + " assertions passed.");
            return;
        }
        Diagnostics();
        ShorthandDeclarations();
        Store();
        WireTests();
        FailureSemantics();
        AllocationCost();
        HardwarePeer.RunLocal();
        Console.WriteLine("SNMP Mapping: " + assertions + " assertions passed.");
    }
    private static Compilation Generate(string text, out GeneratorDriverRunResult result)
    {
        var compilation = CSharpCompilation.Create("Snmp_" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(text, Parse) }, References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new[] { new SnmpMappingGenerator().AsSourceGenerator() }, parseOptions: Parse);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _);
        result = driver.GetRunResult();
        Check(result.Results.All(r => r.Exception == null), string.Join("\n", result.Results.Select(r => r.Exception)));
        return updated;
    }
    private static string Model(string member, string declaration = "public partial class Model")
    {
        return "using VagabondK.nF.SNMP; using VagabondK.nF.SNMP.Mapping; [SnmpMap] " + declaration + " {" + member + "}";
    }
    private static void Invalid(string source, string id)
    {
        Generate(source, out var result);
        Check(result.Diagnostics.Any(d => d.Id == id), "Expected " + id + ": " + source);
        Check(!result.Results.SelectMany(r => r.GeneratedSources).Any(s => s.HintName.StartsWith("SnmpMap0")), "Invalid model emits no implementation.");
    }
    private static void ShorthandDeclarations()
    {
        string[] names = { "SnmpInteger", "SnmpCounter32", "SnmpGauge32", "SnmpTimeTicks", "SnmpCounter64", "SnmpOctetString", "SnmpIpAddress", "SnmpObjectIdentifier" };
        string[] tags = { "Integer32", "Counter32", "Gauge32", "TimeTicks", "Counter64", "OctetString", "IpAddress", "ObjectIdentifier" };
        string[] types = { "int", "uint", "uint", "uint", "ulong", "byte[]", "byte[]", "uint[]" };
        for (int i = 0; i < names.Length; i++)
        {
            string options = ",Writable=true" + (i == 5 ? ",MaximumLength=32" : "");
            string canonical = Model("[SnmpScalar(\"1.3.6.1.4.1.32473.1.0\",SnmpType." + tags[i] + options + ")] public " + types[i] + " V;");
            string shorthand = canonical.Replace("SnmpScalar", names[i]).Replace(",SnmpType." + tags[i], "");
            var first = Generate(canonical, out var before);
            var second = Generate(shorthand, out var after);
            Check(!first.GetDiagnostics().Concat(second.GetDiagnostics()).Any(d => d.Severity == DiagnosticSeverity.Error),
                string.Join("\n", second.GetDiagnostics()));
            Check(before.Results.SelectMany(r => r.GeneratedSources).Where(s => s.HintName != "SnmpMappingAttributes.g.cs").Select(s => s.SourceText.ToString())
                .SequenceEqual(after.Results.SelectMany(r => r.GeneratedSources).Where(s => s.HintName != "SnmpMappingAttributes.g.cs").Select(s => s.SourceText.ToString())),
                "Shorthand preserves SNMP implementation: " + tags[i]);
        }
        Invalid(Model("[SnmpInteger(\"1.3.6.1.4.1.32473.1.0\")] public uint V;"), "VKSN002");
        Invalid(Model("[SnmpInteger(\"1.3.6.1.4.1.32473.1.0\",MaximumLength=1)] public int V;"), "VKSN003");
        Invalid(Model("[SnmpInteger(\"1.3.6.1.2.1.1.1.0\")] public int V;"), "VKSN003");
        Invalid(Model("[SnmpInteger(\"1.3.6.1.4.1.32473.1.0\")][SnmpScalar(\"1.3.6.1.4.1.32473.1.0\",SnmpType.Integer32)] public int V;"), "VKSN002");
    }

    private static void Diagnostics()
    {
        const string member = "[SnmpScalar(\"1.3.6.1.4.1.32473.1.0\", SnmpType.Integer32, Writable=true)] public int Value {get;private set;}";
        foreach (string declaration in new[] { "public class Model", "public partial class Model<T>",
            "public static partial class Model", "public abstract partial class Model", "public partial struct Model",
            "public partial class Model : System.Exception" })
            Invalid(Model(member, declaration), "VKSN001");
        foreach (string oid in new[] { "", "1", "3.1.0", "1.40.0", "2.4294967295.0", "1..0", "1.3. ",
            "1.3.6", "1.3.6.1.2.1.1.9.0", "1.3.4294967296.0", "1.3.-1.0", "1.3.00000000000.0" })
            Invalid(Model(member.Replace("1.3.6.1.4.1.32473.1.0", oid)), "VKSN003");
        Invalid(Model(member.Replace("\"1.3.6.1.4.1.32473.1.0\"", "null")), "VKSN003");
        Invalid(Model(member.Replace("public int", "public float")), "VKSN002");
        Invalid(Model(member.Replace("public int", "public bool")), "VKSN002");
        Invalid(Model(member.Replace("public int", "public string")), "VKSN002");
        Invalid(Model(member.Replace("{get;private set;}", "{set {}}")), "VKSN002");
        Invalid(Model(member.Replace("public int", "public static int")), "VKSN002");
        Invalid(Model(member.Replace("{get;private set;}", "{get;}")), "VKSN002");
        Invalid(Model(member.Replace("SnmpType.Integer32", "(SnmpType)99")), "VKSN002");
        Invalid(Model(member.Replace("Writable=true", "Writable=true, MaximumLength=8")), "VKSN003");
        Invalid(Model(member + member.Replace("Value", "Other")), "VKSN004");
        Invalid(Model(member + member.Replace("Value", "Other").Replace(".32473.1.0", ".032473.01.0")), "VKSN004");
        Invalid(Model(member + member.Replace("Value", "Other").Replace(".1.0\"", ".1.0.1.0\"")), "VKSN004");
        Invalid(Model(member.Replace("1.3.6.1.4.1.32473.1.0", "1.3." + string.Join(".", Enumerable.Repeat("1", 126)) + ".0")), "VKSN003");
        Invalid("using VagabondK.nF.SNMP; using VagabondK.nF.SNMP.Mapping; public class Outer { [SnmpMap] public partial class Model {" + member + "}}", "VKSN001");
        Invalid(Model("[SnmpScalar(\"1.3.0\", SnmpType.Counter32, Writable=true)] public readonly uint X;"), "VKSN002");
        Invalid(Model("[SnmpScalar(\"1.3.0\", SnmpType.OctetString)] public byte[,] X;"), "VKSN002");
        Invalid(Model("[SnmpScalar(\"1.3.0\", SnmpType.ObjectIdentifier)] public int[] X;"), "VKSN002");
        Invalid(Model(member + " public int SnmpStore;"), "VKSN005");
        Invalid(Model(member, "public partial class SnmpStore"), "VKSN005");
        Invalid(Model(""), "VKSN002");
        foreach (int maximum in new[] { -1, 1025 })
            Invalid(Model("[SnmpScalar(\"1.3.0\",SnmpType.OctetString,MaximumLength=" + maximum + ")] public byte[] X;"), "VKSN003");
        var valid = Generate("namespace @event {" + Model(member.Replace("Value", "@event"), "public partial class @class") + "}", out var result);
        Check(!valid.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), string.Join("\n", valid.GetDiagnostics()));
        string generated = result.Results.SelectMany(r => r.GeneratedSources).Single(s => s.HintName.StartsWith("SnmpMap0")).SourceText.ToString();
        Check(!generated.Contains("SnmpOid.Parse") && !generated.Contains("Reflection") && !generated.Contains("Dictionary"), "Literal OIDs and direct access.");
        var many = string.Concat(Enumerable.Range(1, 513).Select(i =>
            "[SnmpScalar(\"1.3." + i + ".0\",SnmpType.Integer32)] public int X" + i + ";"));
        Invalid(Model(many), "VKSN002");
        var large = Generate(Model(many.Substring(0, many.LastIndexOf("[SnmpScalar", StringComparison.Ordinal))), out _);
        Check(!large.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "512 leaves compile.");
        var readOnly = Generate(Model("[SnmpScalar(\"2.4294967215.0\",SnmpType.Counter32)] private readonly uint X = 7;" +
            "[SnmpScalar(\"1.3.0\",SnmpType.Integer32)] private int Y { get { return 9; } }"), out _);
        Check(!readOnly.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Readonly-only fields/properties and largest first combined arc compile.");
        var maximumOid = Generate(Model(member.Replace("1.3.6.1.4.1.32473.1.0",
            "1.3." + string.Join(".", Enumerable.Repeat("4294967295", 125)) + ".0")), out _);
        Check(!maximumOid.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "128 OID arcs compile.");
        var sorted = Generate(Model(
            "[SnmpScalar(\"1.3.10.0\",SnmpType.Integer32)] public int Ten;" +
            "[SnmpScalar(\"1.3.2.0\",SnmpType.Integer32)] public int Two;"), out var sortedResult);
        Check(!sorted.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error));
        string sortedSource = sortedResult.Results.SelectMany(r => r.GeneratedSources)
            .Single(s => s.HintName.StartsWith("SnmpMap0")).SourceText.ToString();
        int two = sortedSource.IndexOf("Integer32(_model.@Two)", StringComparison.Ordinal);
        int ten = sortedSource.IndexOf("Integer32(_model.@Ten)", StringComparison.Ordinal);
        Check(two >= 0 && ten > two, "Numeric 2 precedes 10, not lexical order.");
        Generate(Model("[SnmpScalar(\"1.3.0\",SnmpType.OctetString,Writable=true)] public byte[] Text;"), out var octetResult);
        string octetSource = octetResult.Results.SelectMany(r => r.GeneratedSources)
            .Single(s => s.HintName.StartsWith("SnmpMap0")).SourceText.ToString();
        int acceptsAt = octetSource.IndexOf("public bool AcceptsWrite", StringComparison.Ordinal);
        int queueAt = octetSource.IndexOf("public void QueueWrite", StringComparison.Ordinal);
        string validation = octetSource.Substring(acceptsAt, queueAt - acceptsAt);
        Check(validation.Contains("value.ByteLength") && !validation.Contains("GetBytes()"),
            "Generated validation inspects length without copying or exposing storage.");
        Check(octetSource.Substring(queueAt).Contains("value.GetBytes()"),
            "SET still copies into model-owned storage.");
    }
    private static void Store()
    {
        var model = new DeviceData();
        var sync = new object();
        var store = new DeviceData.SnmpStore(model, sync);
        Check(store.Count == 8);
        for (int i = 0; i < 8; i++)
            Check(SnmpOid.Format(store.GetObject(i).GetOid()) == DeviceData.Enterprise + "." + (i + 1) + ".0", "Numeric OID order.");
        Throws<ArgumentNullException>(() => new DeviceData.SnmpStore(null, sync));
        Throws<ArgumentNullException>(() => new DeviceData.SnmpStore(model, null));
        Throws<ArgumentOutOfRangeException>(() => store.Read(-1));
        Throws<ArgumentOutOfRangeException>(() => store.GetObject(8));
        Throws<ArgumentOutOfRangeException>(() => store.AcceptsWrite(8, null));
        Check(!store.AcceptsWrite(0, null) && !store.AcceptsWrite(0, SnmpValue.Gauge32(1)));
        Check(!store.AcceptsWrite(2, SnmpValue.Counter32(2)), "Readonly rejects SET.");
        Check(!store.AcceptsWrite(1, SnmpValue.OctetString(new byte[33])), "Octet length limit.");
        Check(store.AcceptsWrite(1, SnmpValue.OctetString(new byte[32])), "Octet exact length.");
        Throws<ArgumentException>(() => store.QueueWrite(2, SnmpValue.Counter32(1)));
        SnmpValue[] values = { SnmpValue.Integer32(int.MinValue), SnmpValue.OctetString(new byte[] { 0, 255 }),
            SnmpValue.Counter32(1), SnmpValue.Gauge32(uint.MaxValue), SnmpValue.TimeTicks(uint.MaxValue),
            SnmpValue.Counter64(ulong.MaxValue), SnmpValue.IpAddress(new byte[] { 198, 51, 100, 1 }),
            SnmpValue.ObjectIdentifier(new uint[] { 2, 100, uint.MaxValue }) };
        for (int i = 0; i < values.Length; i++)
        {
            if (i == 2) continue;
            Check(store.AcceptsWrite(i, values[i]));
            store.QueueWrite(i, values[i]);
            Check(store.Read(i).Type == values[i].Type);
        }
        Check(model.Setpoint == int.MinValue && model.Level == uint.MaxValue && model.Delay == uint.MaxValue && model.Total == ulong.MaxValue, "Private setters.");
        byte[] bytes = store.Read(1).GetBytes(); bytes[0] = 7;
        Check(model.Label[0] == 0, "Read arrays are copied.");
        model.Label[0] = 3;
        Check(values[1].GetBytes()[0] == 0, "SET arrays are copied.");
        Check(model.Profile.SequenceEqual(new uint[] { 2, 100, uint.MaxValue }));
        using var entered = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        var thread = new Thread(() => { entered.Set(); store.Read(0); finished.Set(); });
        lock (sync)
        {
            thread.Start(); entered.Wait();
            Check(!finished.Wait(50), "Generated Read honors application lock.");
        }
        Check(thread.Join(1000) && finished.IsSet);
        Throws<ArgumentOutOfRangeException>(() => store.QueueWrite(8, SnmpValue.Integer32(1)));
        Throws<ArgumentException>(() => store.QueueWrite(0, null));
        Throws<ArgumentException>(() => store.QueueWrite(0, SnmpValue.Gauge32(1)));
        Throws<ArgumentException>(() => store.QueueWrite(1, SnmpValue.OctetString(new byte[33])));
        Check(model.Setpoint == int.MinValue && model.Label.Length == 2, "Invalid direct writes do not mutate.");
        var definition = store.GetObject(0).GetOid();
        definition[0] = 2;
        Check(store.GetObject(0).GetOid()[0] == 1, "Definitions do not expose mutable OIDs.");
    }
    private static Wire.Reply Ask(SnmpAgent agent, int pdu, params byte[][] bindings)
    {
        return Wire.Parse(agent.Process(Wire.Message(1, pdu == 0xa3 ? "private" : "public", pdu, 9, 0, 0, bindings)));
    }
    private static void WireTests()
    {
        var model = new DeviceData();
        var store = new DeviceData.SnmpStore(model, new object());
        var agent = new SnmpAgent(store, new SnmpSystem("mapped", "node"), "public", "private");
        string e = DeviceData.Enterprise + ".";
        var get = Ask(agent, 0xa0, Enumerable.Range(1, 8).Select(i => Wire.Bind(e + i + ".0")).ToArray());
        Check(get.Status == 0 && get.Bindings.Count == 8);
        Check(get.Bindings[0].Signed == -250 && get.Bindings[2].Unsigned == 1 && get.Bindings[5].Unsigned == 0x100000000UL);
        var next = Ask(agent, 0xa1, Wire.Bind(e + "1.0"));
        Check(next.Bindings[0].Oid == e + "2.0");
        var bulk = Wire.Parse(agent.Process(Wire.Message(1, "public", 0xa5, 10, 0, 8, Wire.Bind(DeviceData.Enterprise))));
        Check(bulk.Status == 0 && bulk.Bindings.Count == 8 && bulk.Bindings[7].Oid == e + "8.0");
        var v1 = Wire.Parse(agent.Process(Wire.Message(0, "public", 0xa0, 11, 0, 0, Wire.Bind(e + "6.0"))));
        Check(v1.Status == 2, "Counter64 remains hidden in v1.");
        var set = Ask(agent, 0xa3, Wire.Bind(e + "1.0", Wire.Int(int.MaxValue)), Wire.Bind(e + "4.0", Wire.UInt(uint.MaxValue, 0x42)));
        Check(set.Status == 0 && model.Setpoint == int.MaxValue && model.Level == uint.MaxValue);
        set = Ask(agent, 0xa3, Wire.Bind(e + "1.0", Wire.Int(3)), Wire.Bind(e + "3.0", Wire.UInt(9, 0x41)));
        Check(set.Status != 0 && set.Index == 2 && model.Setpoint == int.MaxValue, "All bindings validated before mutation.");
        set = Ask(agent, 0xa3, Wire.Bind(e + "1.0", Wire.UInt(1, 0x42)));
        Check(set.Status == 7 && model.Setpoint == int.MaxValue, "wrongType");
        set = Ask(agent, 0xa3, Wire.Bind(e + "2.0", Wire.Text(new string('x', 33))));
        Check(set.Status == 8 && model.Label.Length == 4, "wrongLength");
        set = Ask(agent, 0xa3, Wire.Bind(e + "2.0", Wire.Tlv(4, new byte[] { 0, 255 })),
            Wire.Bind(e + "6.0", Wire.UInt(ulong.MaxValue, 0x46)),
            Wire.Bind(e + "7.0", Wire.Tlv(0x40, new byte[] { 198, 51, 100, 1 })), Wire.Bind(e + "8.0", Wire.Oid("2.100.4294967295")));
        Check(set.Status == 0 && model.Label.SequenceEqual(new byte[] { 0, 255 }) && model.Total == ulong.MaxValue &&
            model.Peer.SequenceEqual(new byte[] { 198, 51, 100, 1 }) && model.Profile.SequenceEqual(new uint[] { 2, 100, uint.MaxValue }));
        for (int i = 0; i < 128; i++)
        {
            set = Ask(agent, 0xa3, Wire.Bind(e + "1.0", Wire.Int(-i)));
            Check(set.Status == 0 && model.Setpoint == -i);
            Check(Ask(agent, 0xa0, Wire.Bind(e + "1.0")).Bindings[0].Signed == -i);
        }
    }
    private static void FailureSemantics()
    {
        var model = new FailureData();
        var sync = new object();
        var store = new FailureData.SnmpStore(model, sync);
        Check(store.Read(2).Unsigned == 4 && store.Read(3).GetBytes().Length == 0);
        Check(!store.AcceptsWrite(2, SnmpValue.Counter32(3)) && !store.AcceptsWrite(3, SnmpValue.OctetString(new byte[0])));
        var agent = new SnmpAgent(store, new SnmpSystem("failures", "node"), "public", "private");
        Throws<InvalidOperationException>(() => Ask(agent, 0xa3,
            Wire.Bind("1.3.6.1.4.1.32473.20.1.0", Wire.Int(42)),
            Wire.Bind("1.3.6.1.4.1.32473.20.2.0", Wire.Int(99))));
        Check(model.First == 42 && model.SecondValue == 0, "Setter exceptions propagate, prior writes are not rolled back.");
        model.FailRead = true;
        Throws<InvalidOperationException>(() => store.Read(1));
        Throws<InvalidOperationException>(() => Ask(agent, 0xa0, Wire.Bind("1.3.6.1.4.1.32473.20.2.0")));
        model.FailRead = false;
        using var entered = new ManualResetEventSlim();
        var thread = new Thread(() => { entered.Set(); store.QueueWrite(0, SnmpValue.Integer32(7)); });
        lock (sync)
        {
            thread.Start(); entered.Wait();
            Check(!thread.Join(50) && model.First == 42, "Generated SET honors application lock.");
        }
        Check(thread.Join(1000) && model.First == 7);
    }
    private static void AllocationCost()
    {
        var store = new DeviceData.SnmpStore(new DeviceData(), new object());
        var valid = SnmpValue.OctetString(new byte[32]);
        var invalid = SnmpValue.OctetString(new byte[512]);
        for (int i = 0; i < 100; i++)
        {
            store.AcceptsWrite(1, valid);
            store.AcceptsWrite(1, invalid);
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        int accepted = 0;
        for (int i = 0; i < 10000; i++)
        {
            if (store.AcceptsWrite(1, valid)) accepted++;
            if (store.AcceptsWrite(1, invalid)) accepted++;
        }
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(accepted == 10000);
        Check(bytes == 0, "Octet length validation must not allocate after warm-up.");
        Console.WriteLine("Octet validation allocated bytes: " + bytes + " / 10000 accepted32 + 10000 rejected512.");
    }
}

[VagabondK.nF.SNMP.Mapping.SnmpMap]
internal partial class FailureData
{
    [VagabondK.nF.SNMP.Mapping.SnmpScalar("1.3.6.1.4.1.32473.20.1.0", SnmpType.Integer32, Writable = true)]
    internal int First;
    internal bool FailRead;
    internal int SecondValue { get; private set; }
    [VagabondK.nF.SNMP.Mapping.SnmpScalar("1.3.6.1.4.1.32473.20.2.0", SnmpType.Integer32, Writable = true)]
    private int Second
    {
        get { if (FailRead) throw new InvalidOperationException("Application getter."); return SecondValue; }
        set { throw new InvalidOperationException("Application setter."); }
    }
    [VagabondK.nF.SNMP.Mapping.SnmpScalar("1.3.6.1.4.1.32473.20.3.0", SnmpType.Counter32)]
    private readonly uint Counter = 4;
    [VagabondK.nF.SNMP.Mapping.SnmpScalar("1.3.6.1.4.1.32473.20.4.0", SnmpType.OctetString, MaximumLength = 0)]
    private readonly byte[] Empty = new byte[0];
}

namespace VagabondK.nF.SNMP.Tests
{
    internal static class Cases
    {
        internal static void Check(bool value) { global::Program.Check(value, "Independent BER check."); }
    }
}
