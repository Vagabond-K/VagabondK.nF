using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using VagabondK.nF.MQTT.Sparkplug;
using VagabondK.nF.MQTT.Sparkplug.Mapping;
using VagabondK.nF.MQTT.Sparkplug.Mapping.Generator;

internal static class Program
{
    private static int assertions;
    private static readonly CSharpParseOptions Parse = new CSharpParseOptions(LanguageVersion.CSharp7_3, DocumentationMode.Diagnose);
    private static readonly MetadataReference[] References = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
        .Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path)).Cast<MetadataReference>()
        .Concat(new[] { MetadataReference.CreateFromFile(typeof(SparkplugReceiver).Assembly.Location) }).ToArray();

    private static void Main()
    {
        Diagnostics();
        ShorthandDeclarations();
        ScalarsAndLifecycle();
        SchemaAndInvalidInput();
        SampleContract();
        Console.WriteLine("PASS Sparkplug mapping: " + assertions + " assertions; generator diagnostics, C# 7.3, scalar boundaries, schema atomicity, lifecycle, sample wire contract.");
    }

    private static void Check(bool condition, string message)
    { assertions++; if (!condition) throw new InvalidOperationException(message); }

    private static void Throws<T>(Action action) where T : Exception
    {
        bool caught = false;
        try { action(); } catch (T) { caught = true; }
        Check(caught, "Expected " + typeof(T).Name);
    }

    private static GeneratorDriver Generate(string source, out Compilation result)
    {
        var compilation = CSharpCompilation.Create("GeneratedCheck", new[] { CSharpSyntaxTree.ParseText(source, Parse) },
            References, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, checkOverflow: true));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new[] { new SparkplugMappingGenerator().AsSourceGenerator() }, parseOptions: Parse);
        return driver.RunGeneratorsAndUpdateCompilation(compilation, out result, out _);
    }

    private static void Negative(string declaration, string expected)
    {
        Compilation result;
        var driver = Generate("using VagabondK.nF.MQTT.Sparkplug; using VagabondK.nF.MQTT.Sparkplug.Mapping; " + declaration, out result);
        var diagnostics = driver.GetRunResult().Diagnostics;
        Check(!diagnostics.Any(d => d.Id == "CS8785"), "Generator never crashes.");
        Check(diagnostics.Any(d => d.Id == expected), expected + ": " + declaration + "\n" + string.Join("\n", diagnostics));
    }

    private static void ShorthandDeclarations()
    {
        string[] types = { "Boolean", "Int32", "Int64", "Float", "Double" };
        string[] clr = { "bool", "int", "long", "float", "double" };
        for (int i = 0; i < types.Length; i++)
        foreach (string alias in new[] { "", ",0", ",ulong.MaxValue" })
        {
            const string prefix = "using VagabondK.nF.MQTT.Sparkplug; using VagabondK.nF.MQTT.Sparkplug.Mapping; [SparkplugMap] partial class M { ";
            string canonical = prefix + "[SparkplugMetric(\"x\",SparkplugDataType." + types[i] + alias + ")] public " + clr[i] + " V; }";
            string shorthand = canonical.Replace(",SparkplugDataType." + types[i] + alias, alias.Length == 0 ? "" : ",Alias=" + alias.Substring(1));
            Compilation oldCompilation, newCompilation;
            var before = Generate(canonical, out oldCompilation).GetRunResult();
            var after = Generate(shorthand, out newCompilation).GetRunResult();
            Check(!oldCompilation.GetDiagnostics().Concat(newCompilation.GetDiagnostics()).Concat(before.Diagnostics).Concat(after.Diagnostics)
                .Any(d => d.Severity == DiagnosticSeverity.Error), string.Join("\n", newCompilation.GetDiagnostics()));
            Check(before.Results.SelectMany(r => r.GeneratedSources).Where(s => s.HintName != "SparkplugMappingAttributes.g.cs").Select(s => s.SourceText.ToString())
                .SequenceEqual(after.Results.SelectMany(r => r.GeneratedSources).Where(s => s.HintName != "SparkplugMappingAttributes.g.cs").Select(s => s.SourceText.ToString())),
                "Inferred metric type and alias preserve generated runtime: " + types[i] + alias);
        }
        Negative("[SparkplugMap] partial class M { [SparkplugMetric(\"x\")] public uint V; }", "VKSP002");
        Negative("[SparkplugMap] partial class M { [SparkplugMetric(\"x\",Alias=0)] public int V; [SparkplugMetric(\"y\",Alias=0)] public long W; }", "VKSP004");
        Negative("[SparkplugMap] partial class M { [SparkplugMetric(\"x\",SparkplugDataType.Int32,0,Alias=1)] public int V; }", "VKSP003");
        Negative("[SparkplugMap] partial class M { [SparkplugMetric(\"x\",Alias=-1)] public int V; }", "VKSP003");
    }

    private static void Diagnostics()
    {
        const string point = "[SparkplugMetric(\"x\", SparkplugDataType.Int32)] public int Value { get; private set; }";
        Negative("[SparkplugMap] class M { " + point + " }", "VKSP001");
        Negative("[SparkplugMap] partial class M<T> { " + point + " }", "VKSP001");
        Negative("class Outer { [SparkplugMap] partial class M { " + point + " } }", "VKSP001");
        Negative("class Parent {} [SparkplugMap] partial class M : Parent { " + point + " }", "VKSP001");
        Negative("[SparkplugMap] static partial class M { }", "VKSP001");
        Negative("[SparkplugMap] abstract partial class M { }", "VKSP001");
        Negative("[SparkplugMap] partial struct M { }", "VKSP001");
        Negative("[SparkplugMap] partial class M { [SparkplugMetric(\"x\", SparkplugDataType.Int32)] public readonly int V; }", "VKSP002");
        Negative("[SparkplugMap] partial class M { [SparkplugMetric(\"x\", SparkplugDataType.Int32)] public int V { get; } }", "VKSP002");
        Negative("[SparkplugMap] partial class M { [SparkplugMetric(\"x\", SparkplugDataType.Int32)] public int V { set { } } }", "VKSP002");
        Negative("[SparkplugMap] partial class M { [SparkplugMetric(\"x\", SparkplugDataType.Int32)] public static int V; }", "VKSP002");
        Negative("[SparkplugMap] partial class M { [SparkplugMetric(\"x\", SparkplugDataType.Int32)] public int this[int i] { get { return 0; } set { } } }", "VKSP002");
        Negative("[SparkplugMap] partial class M { [SparkplugMetric(\"x\", SparkplugDataType.Boolean)] public int V; }", "VKSP002");
        Negative("[SparkplugMap] partial class M { [SparkplugMetric(\"x\", (SparkplugDataType)99)] public int V; }", "VKSP002");
        Negative("[SparkplugMap] partial class M { [SparkplugMetric(\"x\", SparkplugDataType.Int32)] public uint V; }", "VKSP002");
        Negative("[SparkplugMap] partial class M { [SparkplugMetric(\"x\", SparkplugDataType.Int32)] public const int V = 0; }", "VKSP002");
        Negative("interface I { int V { get; set; } } [SparkplugMap] partial class M : I { [SparkplugMetric(\"x\", SparkplugDataType.Int32)] int I.V { get; set; } }", "VKSP002");
        Negative("[SparkplugMap] partial class M { [SparkplugMetric(\"\", SparkplugDataType.Int32)] public int V; }", "VKSP003");
        Negative("[SparkplugMap] partial class M { [SparkplugMetric(\"x\", SparkplugDataType.Int32, -1)] public int V; }", "VKSP003");
        Negative("[SparkplugMap] partial class M { [SparkplugMetric(null, SparkplugDataType.Int32)] public int V; }", "VKSP003");
        Negative("[SparkplugMap] partial class M { [SparkplugMetric(\"한글\", SparkplugDataType.Int32)] public int V; }", "VKSP003");
        Negative("[SparkplugMap] partial class M { [SparkplugMetric(\"a\\n\", SparkplugDataType.Int32)] public int V; }", "VKSP003");
        Negative("[SparkplugMap] partial class M { [SparkplugMetric(\"" + new string('a', 65) + "\", SparkplugDataType.Int32)] public int V; }", "VKSP003");
        Negative("[SparkplugMap] partial class M { }", "VKSP003");
        Negative("[SparkplugMap] partial class M { " + string.Join(" ", Enumerable.Range(0, 9).Select(i =>
            "[SparkplugMetric(\"x" + i + "\", SparkplugDataType.Int32)] public int V" + i + ";")) + " }", "VKSP003");
        Negative("[SparkplugMap] partial class M { " + point + " [SparkplugMetric(\"x\", SparkplugDataType.Int64)] public long Other; }", "VKSP004");
        Negative("[SparkplugMap] partial class M { [SparkplugMetric(\"x\", SparkplugDataType.Int32, 0)] public int V; [SparkplugMetric(\"y\", SparkplugDataType.Int32, 0)] public int W; }", "VKSP004");
        Negative("[SparkplugMap] partial class M { [SparkplugMetric(\"x\", SparkplugDataType.Int32, ulong.MaxValue)] public int V; [SparkplugMetric(\"y\", SparkplugDataType.Int32, ulong.MaxValue)] public int W; }", "VKSP004");
        foreach (var name in new[] { "Metrics", "ApplySnapshot", "SparkplugSequence", "SparkplugBinding", "ApplySparkplugSnapshot" })
            Negative("[SparkplugMap] partial class M { " + point + " public int " + name + "; }", "VKSP005");
        Negative("[SparkplugMap] partial class SparkplugBinding { " + point + " }", "VKSP005");
        Negative("partial class M {} [SparkplugMap] class M { " + point + " }", "VKSP001");

        Compilation output;
        var valid = Generate(@"using VagabondK.nF.MQTT.Sparkplug; using VagabondK.nF.MQTT.Sparkplug.Mapping;
namespace @class { [SparkplugMap] public sealed partial class @event {
/// <summary>값입니다.</summary>
[SparkplugMetric(""name \"" \\ "", SparkplugDataType.Int64, ulong.MaxValue)] private long @int { get; set; }
} }", out output);
        var errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Check(errors.Length == 0, "C#7.3 escaped identifiers/literals: " + string.Join("\n", errors.Select(d => d.ToString())));
        Check(!valid.GetRunResult().Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error), "Valid diagnostics.");
        Check(valid.GetRunResult().GeneratedTrees.All(t => !t.GetText().ToString().Contains("Reflection")), "No generated reflection.");
        Check(!output.GetDiagnostics().Any(d => d.Id == "CS1572" || d.Id == "CS1573" || d.Id == "CS1584"), "Generated XML parameter documentation.");
        using (var stream = new MemoryStream())
            Check(output.Emit(stream).Success, "Generated C#7.3 assembly emits.");

        var maximum = Generate("using VagabondK.nF.MQTT.Sparkplug; using VagabondK.nF.MQTT.Sparkplug.Mapping; [SparkplugMap] partial class M { " +
            string.Join(" ", Enumerable.Range(0, 8).Select(i => "[SparkplugMetric(\"" + new string((char)('a' + i), 64) +
            "\", SparkplugDataType.Int32, " + i + ")] private int V" + i + ";")) + " }", out output);
        Check(!maximum.GetRunResult().Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error) &&
            !output.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "8 metrics, 64-char names compile.");
        const string prefix = "using VagabondK.nF.MQTT.Sparkplug; using VagabondK.nF.MQTT.Sparkplug.Mapping; ";
        var partialCompilation = CSharpCompilation.Create("MultiPartial", new[] {
            CSharpSyntaxTree.ParseText(prefix + "[SparkplugMap] partial class M { [SparkplugMetric(\"b\", SparkplugDataType.Int32)] int B; }", Parse),
            CSharpSyntaxTree.ParseText(prefix + "partial class M { [SparkplugMetric(\"a\", SparkplugDataType.Int64)] long A; }", Parse)
        }, References, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver partialDriver = CSharpGeneratorDriver.Create(new[] { new SparkplugMappingGenerator().AsSourceGenerator() }, parseOptions: Parse);
        partialDriver = partialDriver.RunGeneratorsAndUpdateCompilation(partialCompilation, out output, out _);
        Check(!output.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error) &&
            partialDriver.GetRunResult().GeneratedTrees.Count() == 2, "One mapping generated across partial files.");
    }

    private static void ScalarsAndLifecycle()
    {
        var source = new Scalars();
        var target = new Scalars();
        Check(target.SparkplugSequence == -1, "Initial sequence.");
        var binding = new Scalars.SparkplugBinding("group", "node", "device", 100);
        var receiver = binding.Receiver;
        var session = new SparkplugSession("group", "node", 255);
        Check(!binding.ReadInto(target), "No DBIRTH no model.");
        Throws<ArgumentNullException>(() => binding.ReadInto(null));
        receiver.SetConnected(true);
        Check(receiver.Apply(receiver.Topic("NBIRTH"), session.NodeBirth(1), false, 0) == SparkplugResult.NodeBirth, "NBIRTH delegates.");
        int[] counts = { int.MinValue, -1, 0, int.MaxValue };
        long[] lifetimes = { long.MinValue, -1, 0, long.MaxValue };
        float[] ratios = { -float.MaxValue, -float.Epsilon, -0.0f, float.Epsilon, float.MaxValue };
        double[] totals = { -double.MaxValue, -double.Epsilon, -0.0d, double.Epsilon, double.MaxValue };
        int sequence = 0;
        for (int i = 0; i < 5; i++)
        {
            source.Set((i & 1) != 0, counts[i % 4], lifetimes[i % 4], ratios[i], totals[i]);
            bool birth = i == 0;
            var metrics = source.Metrics(birth);
            Check(metrics.Length == 5 && metrics[0].Alias == 0 && metrics[0].HasAlias && metrics[2].Alias == ulong.MaxValue, "Alias0/high ulong.");
            Check(metrics.All(m => birth ? m.Name != null && m.DataType != SparkplugDataType.Unspecified : m.Name == null && m.DataType == SparkplugDataType.Unspecified), "Explicit birth/data metrics.");
            var bytes = session.Next((ulong)i + 2, metrics, birth);
            Check(receiver.Apply(receiver.Topic(birth ? "DBIRTH" : "DDATA"), bytes, false, i) == SparkplugResult.Applied && binding.ReadInto(target), "Mapped roundtrip.");
            sequence++;
            Check(target.Enabled == source.Enabled && target.Count == source.Count && target.Lifetime == source.Lifetime &&
                BitConverter.SingleToInt32Bits(target.Ratio) == BitConverter.SingleToInt32Bits(source.Ratio) &&
                BitConverter.DoubleToInt64Bits(target.Total) == BitConverter.DoubleToInt64Bits(source.Total) &&
                target.SparkplugSequence == sequence, "All scalar boundary bits.");
            var direct = new Scalars(); direct.ApplySnapshot(receiver.Read());
            Check(direct.Lifetime == source.Lifetime && direct.Count == source.Count, "Direct static snapshot.");
        }
        Check(receiver.IsAvailable(103) && !receiver.IsAvailable(104), "Freshness owned by runtime.");
        var last = receiver.Read();
        var delta = session.Next(20, new[] { SparkplugMetric.Int32(null, 42, 1, false) });
        Check(receiver.Apply(receiver.Topic("DDATA"), delta, false, 10) == SparkplugResult.Applied && binding.ReadInto(target) && target.Count == 42 &&
            target.Lifetime == source.Lifetime, "Partial DDATA runtime merging.");
        Check(last[1].Integer != receiver.Read()[1].Integer, "Immutable old snapshot.");
        var before = receiver.Read();
        Check(receiver.Apply(receiver.Topic("DDATA"), delta, false, 11) == SparkplugResult.SequenceGap && receiver.Read() == before &&
            !receiver.IsAvailable(11) && receiver.NeedsRebirth && binding.ReadInto(target) && target.Count == 42, "Failure retains diagnostic model.");
        receiver.SetConnected(false);
        Check(receiver.Apply(receiver.Topic("DDATA"), delta, false, 12) == SparkplugResult.Disconnected, "Disconnected delegates.");

        receiver.SetConnected(true);
        Check(receiver.Apply(receiver.Topic("NBIRTH"), session.NodeBirth(30), false, 20) == SparkplugResult.NodeBirth, "Rebirth resets.");
        var changed = new[] {
            SparkplugMetric.Boolean("Enabled", true, 100),
            SparkplugMetric.Int32("Count", 77, 101),
            SparkplugMetric.Int64("Lifetime", -9, 102),
            SparkplugMetric.Float("Ratio", 0.25f, 103),
            SparkplugMetric.Double("Total", 2, 104) };
        Check(receiver.Apply(receiver.Topic("DBIRTH"), session.Next(31, changed, true), false, 20) == SparkplugResult.Applied, "Dynamic aliases birth.");
        Check(receiver.Apply(receiver.Topic("DDATA"), session.Next(32, new[] { SparkplugMetric.Int64(null, -88, 102, false) }), false, 21) == SparkplugResult.Applied &&
            binding.ReadInto(target) && target.Lifetime == -88, "Binder trusts runtime learned alias, not static sender alias.");
        Throws<ArgumentException>(() => target.ApplySnapshot(receiver.Read()));
        Check(receiver.Apply(receiver.Topic("NDEATH"), session.NodeDeath(), false, 22) == SparkplugResult.NodeDeath && !receiver.IsAvailable(22), "Death delegates.");

        var named = new Named();
        var namedBinding = new Named.SparkplugBinding("g", "n", "d", 1);
        namedBinding.Receiver.SetConnected(true);
        var namedSession = new SparkplugSession("g", "n", 0);
        namedBinding.Receiver.Apply(namedBinding.Receiver.Topic("NBIRTH"), namedSession.NodeBirth(0), false, 0);
        Check(namedBinding.Receiver.Apply(namedBinding.Receiver.Topic("DBIRTH"), namedSession.Next(1, new[] { SparkplugMetric.Int32("value", -99) }, true), false, 0) == SparkplugResult.Applied &&
            namedBinding.ReadInto(named) && named.Value == -99 && named.Metrics(false)[0].Name == "value", "No-alias private field.");
        Throws<ArgumentException>(() => new Scalars.SparkplugBinding("g/invalid", "n", "d", 1));
        Throws<ArgumentException>(() => new Scalars.SparkplugBinding("g", "n", "d", 0));
    }

    private static SparkplugSnapshot Snapshot(params SparkplugMetric[] metrics) { return new SparkplugSnapshot(metrics, 7); }

    private static void SchemaAndInvalidInput()
    {
        var model = new Scalars();
        model.Set(false, 123, 456, 0.5f, 1.5);
        var source = new Scalars();
        source.Set(true, int.MinValue, long.MinValue, float.Epsilon, double.Epsilon);
        Throws<ArgumentNullException>(() => model.ApplySnapshot(null));
        Throws<ArgumentException>(() => model.ApplySnapshot(Snapshot()));
        Throws<ArgumentException>(() => model.ApplySnapshot(Snapshot(source.Metrics(true).Concat(source.Metrics(true)).ToArray())));
        for (int i = 0; i < 5; i++)
        {
            var wrong = source.Metrics(true);
            wrong[i] = SparkplugMetric.Boolean("wrong", true);
            Throws<ArgumentException>(() => model.ApplySnapshot(Snapshot(wrong)));
            Check(model.Count == 123 && model.Lifetime == 456 && !model.Enabled && model.Ratio == 0.5f &&
                model.Total == 1.5 && model.SparkplugSequence == -1, "No partial schema apply at " + i);
            wrong = source.Metrics(true); wrong[i] = null;
            Throws<ArgumentException>(() => model.ApplySnapshot(Snapshot(wrong)));
        }
        var duplicate = source.Metrics(true); duplicate[1] = duplicate[0];
        Throws<ArgumentException>(() => model.ApplySnapshot(Snapshot(duplicate)));
        var reorder = source.Metrics(true); var first = reorder[0]; reorder[0] = reorder[1]; reorder[1] = first;
        Throws<ArgumentException>(() => model.ApplySnapshot(Snapshot(reorder)));
        var invalid = source.Metrics(true); invalid[4].Real = double.NaN;
        Throws<ArgumentException>(() => model.ApplySnapshot(Snapshot(invalid)));
        invalid = source.Metrics(true); invalid[3].Real = double.MaxValue;
        Throws<ArgumentException>(() => model.ApplySnapshot(Snapshot(invalid)));
        invalid = source.Metrics(true); invalid[0].Integer = 2;
        Throws<ArgumentException>(() => model.ApplySnapshot(Snapshot(invalid)));
        invalid = source.Metrics(true); invalid[1].Integer = ulong.MaxValue;
        Throws<ArgumentException>(() => model.ApplySnapshot(Snapshot(invalid)));
        invalid = source.Metrics(true); invalid[2].DataType = SparkplugDataType.Int32;
        Throws<ArgumentException>(() => model.ApplySnapshot(Snapshot(invalid)));
        invalid = source.Metrics(true); invalid[2].ValueField = 10;
        Throws<ArgumentException>(() => model.ApplySnapshot(Snapshot(invalid)));
        invalid = source.Metrics(true); invalid[3].Real = double.PositiveInfinity;
        Throws<ArgumentException>(() => model.ApplySnapshot(Snapshot(invalid)));
        invalid = source.Metrics(true); invalid[4].Real = double.NegativeInfinity;
        Throws<ArgumentException>(() => model.ApplySnapshot(Snapshot(invalid)));
        invalid = source.Metrics(false); invalid[0].Alias = 99;
        Throws<ArgumentException>(() => model.ApplySnapshot(Snapshot(invalid)));
        Check(model.Count == 123 && !model.Enabled && model.SparkplugSequence == -1, "All invalid inputs no mutation.");
        foreach (float f in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        { source.Set(true, 0, 0, f, 1); Throws<ArgumentOutOfRangeException>(() => source.Metrics(true)); }
        foreach (double d in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        { source.Set(true, 0, 0, 1, d); Throws<ArgumentOutOfRangeException>(() => source.Metrics(false)); }
        var throwing = new Throwing { Reject = true };
        Throws<InvalidOperationException>(() => throwing.ApplySnapshot(Snapshot(SparkplugMetric.Int32("value", 1))));
    }

    private static void SampleContract()
    {
        var mapping = new SparkplugMapping();
        Check(mapping.Read() == null, "Sample no birth.");
        mapping.SetConnected(true);
        var session = new SparkplugSession("VKNF", SparkplugMapping.Node, 10);
        Check(mapping.Apply(SparkplugMapping.Topic("NBIRTH"), session.NodeBirth(1), false, 0) == SparkplugResult.NodeBirth, "Sample node birth.");
        var metrics = SparkplugEncoder.Metrics(true, true, int.MinValue, float.Epsilon, double.MaxValue);
        string[] names = { "Sensor/Running", "Sensor/Count", "Sensor/Temperature", "Sensor/Energy" };
        Check(metrics.Select(m => m.Name).SequenceEqual(names) && metrics.Select(m => m.Alias).SequenceEqual(new ulong[] { 10, 11, 12, 13 }), "Unchanged sample metric order/names/alias.");
        Check(mapping.Apply(SparkplugMapping.Topic("DBIRTH"), session.Next(2, metrics, true), false, 0) == SparkplugResult.Applied, "Sample birth.");
        var old = mapping.Read();
        Check(old.SourceRunning && old.SourceCount == int.MinValue && old.ExternalTemperature == float.Epsilon && old.ImportedEnergy == double.MaxValue &&
            old.Sequence == 1, "Sample generated model conversion.");
        Check(mapping.Apply(SparkplugMapping.Topic("DDATA"), session.Next(3, new[] { SparkplugMetric.Int32(null, 42, 11, false) }), false, 1) == SparkplugResult.Applied &&
            mapping.Read().SourceCount == 42 && old.SourceCount == int.MinValue, "Sample immutable read wrapper.");
        Check(mapping.IsAvailable(3000) && !mapping.IsAvailable(3001), "Sample freshness contract.");
        SparkplugPayload payload;
        Check(SparkplugPayload.TryDecode(SparkplugEncoder.Values(false, 1, 255, false, -1, -float.Epsilon, -double.Epsilon), out payload) &&
            payload.Sequence == 255 && payload.Metrics.All(m => m.Name == null && m.DataType == SparkplugDataType.Unspecified), "Sample encoder public wrapper.");
        Check(SparkplugEncoder.Topic("DBIRTH") == "spBv1.0/VKNF/DBIRTH/edge-b/sensor-b" &&
            SparkplugMapping.CommandTopic == "spBv1.0/VKNF/NCMD/edge-a", "Sample topic contract.");
        metrics = SparkplugEncoder.Metrics(true, false, 77, 0.25f, 4);
        for (int i = 0; i < metrics.Length; i++) metrics[i].Alias = (ulong)(100 + i);
        Check(mapping.Apply(SparkplugMapping.Topic("DBIRTH"), session.Next(4, metrics, true), false, 2) == SparkplugResult.Applied &&
            mapping.Read().SourceCount == 77 && old.SourceCount == int.MinValue, "Sample replacement aliases preserve old read.");
        Check(mapping.Apply(SparkplugMapping.Topic("DDATA"), session.Next(5, new[] { SparkplugMetric.Int32(null, -88, 101, false) }), false, 3) == SparkplugResult.Applied &&
            mapping.Read().SourceCount == -88, "Sample generated binder follows replaced alias.");
    }
}
