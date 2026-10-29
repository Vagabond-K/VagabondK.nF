using System;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using VagabondK.nF.BACnet;
using VagabondK.nF.BACnet.Mapping;
using VagabondK.nF.BACnet.Mapping.Generator;
using VagabondK.nF.BACnet.Mapping.Sample;

/// <summary>BACnet 매핑 소스 생성기 테스트 진입점입니다.</summary>
/// <remarks>
/// 기본 실행: 메모리 컴파일로 생성기 진단(VKBN001~005) 확인 → 생성 저장소 동작 → float 비트 보존·할당 → 품질 스냅샷 → 루프백 UDP로 샘플 모델 검사.
/// <c>--hardware &lt;보드 IPv4&gt; &lt;반복&gt;</c>: BACnet Mapping 샘플을 실행 중인 보드를 독립 클라이언트로 검사합니다.
/// <c>--scaling &lt;보드 IPv4&gt; 8|32|64 &lt;라운드&gt; &lt;출력.json&gt;</c>: Mapping.Hardware 펌웨어로 객체 수별 응답 시간과 메모리를 측정합니다.
/// </remarks>
internal static class Program
{
    private static int checks;
    private static readonly CSharpParseOptions Parse = new CSharpParseOptions(LanguageVersion.CSharp7_3);
    private static readonly MetadataReference[] References =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")).Split(Path.PathSeparator)
        .Select(p => MetadataReference.CreateFromFile(p)).Cast<MetadataReference>()
        .Append(MetadataReference.CreateFromFile(typeof(BacnetDevice).Assembly.Location)).ToArray();
    internal static void Check(bool valid, string message = "BACnet mapping")
    {
        checks++;
        if (!valid) throw new InvalidOperationException(message);
    }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { checks++; return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private static void Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--hardware")
        {
            if (!int.TryParse(args[2], out int iterations) || iterations < 14 || iterations > 10000)
                throw new ArgumentException("Hardware iterations: 14..10000.");
            Peer.Sample(args[1], 47808, iterations);
            Console.WriteLine("BACnet Mapping hardware: " + checks + " assertions passed.");
            return;
        }
        if (args.Length == 5 && args[0] == "--scaling")
        {
            if (!int.TryParse(args[2], out int count) || (count != 8 && count != 32 && count != 64) ||
                !int.TryParse(args[3], out int rounds) || rounds < 10 || rounds > 1000)
                throw new ArgumentException("Scaling: --scaling IP 8|32|64 rounds(10..1000) output.json.");
            Peer.Scaling(args[1], count, rounds, args[4]);
            return;
        }
        if (args.Length != 0) throw new ArgumentException("Unknown test arguments.");
        Diagnostics();
        ShorthandDeclarations();
        Store();
        FloatsAndAllocation();
        QualitySnapshot();
        Peer.Loopback();
        Console.WriteLine("BACnet Mapping: " + checks + " assertions passed.");
    }
    private static Compilation Generate(string text, out GeneratorDriverRunResult result)
    {
        var compilation = CSharpCompilation.Create("Bacnet_" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(text, Parse) }, References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new[] { new BacnetMappingGenerator().AsSourceGenerator() }, parseOptions: Parse);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _);
        result = driver.GetRunResult();
        Check(result.Results.All(r => r.Exception == null), string.Join("\n", result.Results.Select(r => r.Exception)));
        return updated;
    }
    private static string Model(string members, string declaration = "public partial class Model")
    {
        return "using VagabondK.nF.BACnet.Mapping; [BacnetMap] " + declaration + " {" + members + "}";
    }
    private static void Invalid(string source, string id)
    {
        Generate(source, out var result);
        Check(result.Diagnostics.Any(d => d.Id == id), "Expected " + id + ": " + source);
        Check(!result.Results.SelectMany(r => r.GeneratedSources).Any(s => s.HintName.StartsWith("BacnetMap0")), "Invalid emits no store.");
    }
    private static void Valid(string text, out GeneratorDriverRunResult result)
    {
        var compilation = Generate(text, out result);
        Check(!compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), string.Join("\n", compilation.GetDiagnostics()));
    }
    private static void ShorthandDeclarations()
    {
        foreach (string type in new[] { "float", "bool" })
        {
            string canonical = Model("[BacnetPoint(4194302,Writable=true,Quality=nameof(Good))] public " + type + " V; public bool Good;");
            string shorthand = canonical.Replace("BacnetPoint", type == "float" ? "BacnetAnalogValue" : "BacnetBinaryValue");
            Valid(canonical, out var before);
            Valid(shorthand, out var after);
            Check(before.Results.SelectMany(r => r.GeneratedSources).Where(s => s.HintName != "BacnetAttributes.g.cs").Select(s => s.SourceText.ToString())
                .SequenceEqual(after.Results.SelectMany(r => r.GeneratedSources).Where(s => s.HintName != "BacnetAttributes.g.cs").Select(s => s.SourceText.ToString())),
                "Shorthand preserves BACnet store and float conversion.");
        }
        Invalid(Model("[BacnetAnalogValue(0)] public bool V;"), "VKBN002");
        Invalid(Model("[BacnetBinaryValue(0)] public float V;"), "VKBN002");
        Invalid(Model("[BacnetPoint(0)][BacnetAnalogValue(0)] public float V;"), "VKBN002");
        Invalid(Model("[BacnetAnalogValue(4194303)] public float V;"), "VKBN003");
    }

    private static void Diagnostics()
    {
        const string point = "[BacnetPoint(1,Writable=true)] public float Value {get;private set;}";
        foreach (string shape in new[] { "public class Model", "public static partial class Model", "public abstract partial class Model",
            "public partial class Model<T>", "public partial struct Model", "public partial class Model : System.Exception" })
            Invalid(Model(point, shape), "VKBN001");
        Invalid("using VagabondK.nF.BACnet.Mapping; class Outer {[BacnetMap] partial class Inner {" + point + "}}", "VKBN001");
        foreach (string type in new[] { "int", "double", "uint", "string", "float[]", "System.DayOfWeek" })
            Invalid(Model(point.Replace("float", type)), "VKBN002");
        Invalid(Model(point.Replace("public float", "public static float")), "VKBN002");
        Invalid(Model(point.Replace("{get;private set;}", "{get;}")), "VKBN002");
        Invalid(Model(point.Replace("{get;private set;}", "{set {}}")), "VKBN002");
        Invalid(Model("[BacnetPoint(0,Writable=true)] readonly float Value;"), "VKBN002");
        Invalid(Model("[BacnetPoint(0)] const float Value = 0;"), "VKBN002");
        Invalid(Model("[BacnetPoint(0)] public float this[int i] {get {return 0;}}"), "VKBN002");
        Invalid(Model(point.Replace("(1,", "(4194303,")), "VKBN003");
        Invalid(Model(point + point.Replace("Value", "Other")), "VKBN004");
        Invalid(Model(point + " public class BacnetStore {}"), "VKBN005");
        Invalid(Model(point, "public partial class BacnetStore"), "VKBN005");
        foreach (string quality in new[] { "null", "\"\"", "\"Missing\"", "\"Bad\"", "\"Static\"", "\"Setter\"" })
            Invalid(Model(point.Replace("Writable=true", "Writable=true,Quality=" + quality) +
                " int Bad; static bool Static; bool Setter {set {}}"), "VKBN003");
        Invalid(Model(""), "VKBN002");
        string many = string.Concat(Enumerable.Range(0, 65).Select(i => "[BacnetPoint(" + i + ")] public bool R" + i + ";"));
        Invalid(Model(many), "VKBN002");
        Valid(Model(many.Substring(0, many.LastIndexOf("[BacnetPoint", StringComparison.Ordinal))), out var binary);
        Check(!binary.Results.SelectMany(r => r.GeneratedSources).Any(s => s.HintName == "BacnetRealBits.g.cs"), "BV-only omits real helper.");
        Valid(Model("[BacnetPoint(4194302)] readonly float AV; [BacnetPoint(4194302)] bool BV;"), out _);
        Valid("namespace @event {" + Model(point.Replace("Value", "@event"), "public partial class @class") + "}", out _);
        Valid(Model(point.Replace("Writable=true", "Writable=true,Quality=nameof(Good)") + " private bool Good {get {return true;}}"), out _);
        Valid("namespace @event {" + Model(point.Replace("Value", "@float").Replace("Writable=true",
            "Writable=true,Quality=nameof(@bool)") + " private bool @bool;") + "}", out _);
        Generate(Model("[BacnetPoint(Missing)] public float Value;"), out _);
        Generate(Model("[BacnetPoint(0, Quality=Missing)] public float Value;"), out _);
        Valid(Model(string.Concat(Enumerable.Range(0, 64).Select(i => "[BacnetPoint(" + i + ")] public float A" + i + ";"))), out _);
        Valid(Model(point) + "namespace Other {" + Model(point) + "}", out var two);
        Check(two.Results.SelectMany(r => r.GeneratedSources).Count(s => s.HintName == "BacnetRealBits.g.cs") == 1, "Shared helper emitted once.");
    }
    private static void Store()
    {
        var model = new DeviceData();
        var sync = new object();
        var store = new DeviceData.BacnetStore(model, sync);
        Check(store.Count == 4);
        Check(!store.GetObject(0).Binary && store.GetObject(0).Instance == 0 && !store.GetObject(0).Writable);
        Check(!store.GetObject(1).Binary && store.GetObject(1).Instance == 100);
        Check(store.GetObject(2).Binary && store.GetObject(2).Instance == 0);
        Check(store.Read(0).Bits == BitConverter.SingleToUInt32Bits(250f) && store.Read(0).Good);
        store.QueueWrite(1, BitConverter.SingleToUInt32Bits(-13.5f));
        store.QueueWrite(2, 0);
        Check(model.Setpoint == -13.5f && !model.Enabled, "Private setters.");
        store.QueueWrite(3, 0);
        Check(!store.Read(0).Good && store.Read(1).Good, "Quality mapped in snapshot, writes do not reset it.");
        Throws<ArgumentNullException>(() => new DeviceData.BacnetStore(null, sync));
        Throws<ArgumentNullException>(() => new DeviceData.BacnetStore(model, null));
        Throws<ArgumentOutOfRangeException>(() => store.Read(-1));
        Throws<ArgumentOutOfRangeException>(() => store.GetObject(4));
        Throws<ArgumentOutOfRangeException>(() => store.QueueWrite(4, 0));
        Throws<InvalidOperationException>(() => store.QueueWrite(0, 0));
        Throws<ArgumentOutOfRangeException>(() => store.QueueWrite(2, 2));
        foreach (uint bad in new[] { 0x7f800000U, 0xff800000U, 0x7fc00000U, 0xffffffffU })
            Throws<ArgumentOutOfRangeException>(() => store.QueueWrite(1, bad));
        Check(model.Setpoint == -13.5f && !model.Enabled);
        using var entered = new ManualResetEventSlim();
        var read = new Thread(() => { entered.Set(); store.Read(0); });
        lock (sync) { read.Start(); entered.Wait(); Check(!read.Join(50)); }
        Check(read.Join(1000));
        entered.Reset();
        var write = new Thread(() => { entered.Set(); store.QueueWrite(2, 1); });
        lock (sync) { write.Start(); entered.Wait(); Check(!write.Join(50) && !model.Enabled); }
        Check(write.Join(1000) && model.Enabled);
        var throwing = new FailureData.BacnetStore(new FailureData(), new object());
        Throws<InvalidOperationException>(() => throwing.Read(0));
        Throws<InvalidOperationException>(() => throwing.QueueWrite(0, 0));
        var failingDevice = new BacnetDevice(throwing, 12345, 0, "Failure propagation");
        byte[] get = { 0x81, 0x0a, 0, 17, 1, 0, 0, 5, 1, 12, 0x0c, 0, 0x80, 0, 0, 0x19, 85 };
        byte[] set = { 0x81, 0x0a, 0, 24, 1, 0, 0, 5, 2, 15, 0x0c, 0, 0x80, 0, 0, 0x19, 85,
            0x3e, 0x44, 0, 0, 0, 0, 0x3f };
        Throws<InvalidOperationException>(() => failingDevice.Process(get));
        Throws<InvalidOperationException>(() => failingDevice.Process(set));
    }
    private static void FloatsAndAllocation()
    {
        var store = new DeviceData.BacnetStore(new DeviceData(), new object());
        uint random = 20261025;
        uint[] edges = { 0, 0x80000000, 1, 0x80000001, 0x007fffff, 0x807fffff, 0x00800000, 0x80800000,
            0x7f7fffff, 0xff7fffff, 0x3f800000, 0xbf800000 };
        for (int i = 0; i < 100000; i++)
        {
            random ^= random << 13; random ^= random >> 17; random ^= random << 5;
            uint bits = i < edges.Length ? edges[i] : random;
            if ((bits & 0x7f800000) == 0x7f800000) bits ^= 0x00800000;
            store.QueueWrite(1, bits);
            Check(store.Read(1).Bits == bits, "Finite float32 bit-exact roundtrip.");
        }
        long start = GC.GetAllocatedBytesForCurrentThread();
        uint total = 0;
        for (int i = 0; i < 10000; i++)
        {
            store.QueueWrite(1, 0x80000001);
            total |= store.Read(1).Bits;
            store.QueueWrite(2, 1);
            total |= store.Read(2).Bits;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Check(total == 0x80000001 && allocated == 0, "Direct AV/BV read/write allocates zero after construction.");
    }
    private static void QualitySnapshot()
    {
        var model = new SnapshotData();
        var sync = new object();
        var store = new SnapshotData.BacnetStore(model, sync);
        var thread = new Thread(() =>
        {
            for (int i = 0; i < 10000; i++)
                lock (sync)
                {
                    model.Value = (i & 1) != 0 ? 1f : 0f;
                    model.Good = (i & 1) != 0;
                }
        });
        thread.Start();
        for (int i = 0; i < 10000; i++)
        {
            BacnetSample snapshot = store.Read(0);
            Check(snapshot.Good == (snapshot.Bits == 0x3f800000), "Value and quality must be a single lock snapshot.");
        }
        Check(thread.Join(1000));
    }
}

[BacnetMap]
internal partial class FailureData
{
    [BacnetPoint(0, Writable = true)]
    private float Value
    {
        get { throw new InvalidOperationException("Application getter."); }
        set { throw new InvalidOperationException("Application setter."); }
    }

}

[BacnetMap]
internal partial class SnapshotData
{
    [BacnetPoint(0, Quality = nameof(Good))]
    internal float Value;
    internal bool Good;
}
