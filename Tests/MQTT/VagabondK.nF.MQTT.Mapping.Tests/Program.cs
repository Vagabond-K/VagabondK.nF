using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using VagabondK.nF.MQTT.Mapping;
using VagabondK.nF.MQTT.Mapping.Generator;

internal static class Program
{
    private static int checks;
    private static readonly CSharpParseOptions Parse = new CSharpParseOptions(LanguageVersion.CSharp7_3);
    private static readonly MetadataReference[] References =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")).Split(Path.PathSeparator)
        .Select(p => MetadataReference.CreateFromFile(p)).Cast<MetadataReference>().ToArray();

    private static void Check(bool value, string message)
    {
        checks++;
        if (!value) throw new InvalidOperationException(message);
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { checks++; return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ": " + message);
    }

    private static void Main()
    {
        Diagnostics();
        ShorthandDeclarations();
        NanoCompilation();
        Codec();
        BufferEncoding();
        ApplyAndPublish();
        Sample();
        SnapshotJson.DemonstrateBufferEncoding();
        Console.WriteLine("MQTT Mapping: " + checks + " assertions passed (C# 7.3 + nanoFramework reference compilation).");
    }

    private static Compilation Generate(string text, out GeneratorDriverRunResult result, MetadataReference[] references = null)
    {
        var compilation = CSharpCompilation.Create("MQTT_" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(text, Parse) }, references ?? References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new[] { new MqttMappingGenerator().AsSourceGenerator() }, parseOptions: Parse);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _);
        result = driver.GetRunResult();
        Check(result.Results.All(r => r.Exception == null), string.Join("\n", result.Results.Select(r => r.Exception)));
        return updated;
    }

    private static string ModelSource(string members, string declaration = "public partial class TestModel", string map = "\"sensor\"")
    {
        return "using VagabondK.nF.MQTT.Mapping; [MqttJsonMap(" + map + ")] " + declaration + " {" + members + "}";
    }

    private static void Invalid(string source, string id)
    {
        Generate(source, out var result);
        Check(result.Diagnostics.Any(d => d.Id == id), "Expected " + id + ": " + source);
        Check(!result.Results.SelectMany(r => r.GeneratedSources).Any(s => s.HintName.StartsWith("MqttMap0", StringComparison.Ordinal)),
            "Invalid model emits no binding.");
    }

    private static void Valid(string source)
    {
        var compilation = Generate(source, out var result);
        Check(!result.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error), string.Join("\n", result.Diagnostics));
        Check(!compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), string.Join("\n", compilation.GetDiagnostics()));
    }

    private static void ShorthandDeclarations()
    {
        string canonical = ModelSource("[MqttJsonMember(0,\"Value\",Minimum=-10,Maximum=10)] public int Value;" +
            "[MqttJsonMember(1,\"event\")] public bool @event;");
        string shorthand = canonical.Replace("(0,\"Value\",", "(0,").Replace("(1,\"event\")", "(1)");
        Valid(canonical);
        Valid(shorthand);
        Generate(canonical, out var before);
        Generate(shorthand, out var after);
        Check(before.Results.SelectMany(r => r.GeneratedSources).Where(s => s.HintName != "MqttJsonAttributes.g.cs").Select(s => s.SourceText.ToString())
            .SequenceEqual(after.Results.SelectMany(r => r.GeneratedSources).Where(s => s.HintName != "MqttJsonAttributes.g.cs").Select(s => s.SourceText.ToString())),
            "Inferred JSON names preserve exact codec and wire contract.");
        Invalid(ModelSource("[MqttJsonMember(0)] public int 한글;"), "VKMQ003");
        Invalid(ModelSource("[MqttJsonMember(0)] public int Value;[MqttJsonMember(1,\"Value\")] public int Other;"), "VKMQ004");
        Invalid(ModelSource("[MqttJsonMember(32)] public int Value;"), "VKMQ003");
    }

    private static void Diagnostics()
    {
        const string point = "[MqttJsonMember(0,\"value\")] public int Value {get;private set;}";
        foreach (string declaration in new[] { "public class TestModel", "public static partial class TestModel",
            "public abstract partial class TestModel", "public partial class TestModel<T>", "public partial struct TestModel",
            "public partial class TestModel : System.Exception" })
            Invalid(ModelSource(point, declaration), "VKMQ001");
        Invalid("using VagabondK.nF.MQTT.Mapping; class Outer {[MqttJsonMap(\"x\")] partial class Inner {" + point + "}}", "VKMQ001");
        foreach (string type in new[] { "long", "uint", "float", "double", "string", "int[]", "System.DayOfWeek" })
            Invalid(ModelSource(point.Replace("int Value", type + " Value")), "VKMQ002");
        foreach (string member in new[] { "[MqttJsonMember(0,\"x\")] public static int Value;", "[MqttJsonMember(0,\"x\")] readonly int Value;",
            "[MqttJsonMember(0,\"x\")] const int Value=1;", "[MqttJsonMember(0,\"x\")] public int this[int i] {get{return 0;}set{}}",
            "[MqttJsonMember(0,\"x\")] int Value {get;}", "[MqttJsonMember(0,\"x\")] int Value {set{}}" })
            Invalid(ModelSource(member), "VKMQ002");
        Invalid(ModelSource(""), "VKMQ002");
        string many = string.Concat(Enumerable.Range(0, 33).Select(i => "[MqttJsonMember(" + (i % 32) + ",\"v" + i + "\")] int V" + i + ";"));
        Invalid(ModelSource(many, map: "\"x\",MaximumBytes=4096"), "VKMQ002");
        Valid(ModelSource(string.Concat(Enumerable.Range(0, 32).Select(i => "[MqttJsonMember(" + i + ",\"v" + i + "\")] int V" + i + ";")),
            map: "\"x\",MaximumBytes=4096"));
        foreach (string map in new[] { "null", "\"\"", "\"a/+\"", "\"a/#\"", "\"한글\"", "\"a\\0b\"",
            "\"x\",MaximumBytes=1", "\"x\",MaximumBytes=4097", "\"x\",MaximumBytes=10" })
            Invalid(ModelSource(point, map: map), "VKMQ003");
        foreach (string arguments in new[] { "-1,\"x\"", "32,\"x\"", "0,null", "0,\"\"", "0,\"한글\"", "0,\"a\\\\b\"",
            "0,\"a\\\"b\"", "0,\"a\\nb\"", "0,\"x\",Minimum=2,Maximum=1" })
            Invalid(ModelSource("[MqttJsonMember(" + arguments + ")] int Value;"), "VKMQ003");
        Invalid(ModelSource("[MqttJsonMember(0,\"x\",Minimum=0)] bool Value;"), "VKMQ003");
        Invalid(ModelSource(point + "[MqttJsonMember(0,\"other\")] int Other;"), "VKMQ004");
        Invalid(ModelSource(point + "[MqttJsonMember(1,\"value\")] bool Other;"), "VKMQ004");
        Invalid(ModelSource(point + "class MqttBinding {}"), "VKMQ005");
        Invalid(ModelSource(point, "partial class MqttBinding"), "VKMQ005");
        Invalid(ModelSource("[MqttJsonMember(0,\"x\")] int Snapshot;"), "VKMQ005");
        Invalid(ModelSource("[MqttJsonMember(0,\"x\"),MqttJsonMember(1,\"y\")] int Value;"), "VKMQ003");
        Invalid("using VagabondK.nF.MQTT.Mapping; [MqttJsonMap(\"a\"), MqttJsonMap(\"b\")] partial class TestModel {" + point + "}", "VKMQ003");
        Valid("namespace @event {" + ModelSource(point.Replace("Value", "@event"), "public sealed partial class @class") + "}");
        Valid(ModelSource(point) + "namespace Other {" + ModelSource(point) + "}");
        Valid(ModelSource("[MqttJsonMember(31,\"a b\")] bool Value;"));
        Generate(ModelSource("[MqttJsonMember(Missing,\"x\")] int Value;"), out _);
        Generate(ModelSource("[MqttJsonMember(0,\"x\",Maximum=Missing)] int Value;"), out _);
    }

    private static void NanoCompilation()
    {
        string root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "Source", "MQTT")))
        {
            var parent = Directory.GetParent(root);
            if (parent == null) throw new DirectoryNotFoundException("Repository root not found.");
            root = parent.FullName;
        }
        string core = Path.Combine(root, "packages", "nanoFramework.CoreLibrary.1.17.11", "lib", "mscorlib.dll");
        Check(File.Exists(core), "nanoFramework CoreLibrary required for API compatibility validation.");
        string source = File.ReadAllText(Path.Combine(root, "Samples", "MQTT", "VagabondK.nF.MQTT.Mapping", "SharingContract.cs"));
        var compilation = Generate(source, out var result, new MetadataReference[] { MetadataReference.CreateFromFile(core) });
        Check(!result.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error), string.Join("\n", result.Diagnostics));
        using (var stream = new MemoryStream())
        {
            var emit = compilation.Emit(stream);
            Check(emit.Success, string.Join("\n", emit.Diagnostics));
        }
    }

    private static byte[] Bytes(string json) { return Encoding.UTF8.GetBytes(json); }
    private static bool Decode(string json, out Model.MqttBinding.Snapshot snapshot)
    {
        return Model.MqttBinding.TryDecode(Bytes(json), out snapshot);
    }

    private static void Codec()
    {
        foreach (int value in new[] { int.MinValue, int.MinValue + 1, -100, -1, 0, 1, 100, int.MaxValue })
        {
            string json = "{\"number\":" + value.ToString(CultureInfo.InvariantCulture) + ",\"other\":-3,\"enabled\":true}";
            Check(Decode(json, out var snapshot), "Int32 boundary " + value);
            Check(snapshot.Number == value && snapshot.Other == -3 && snapshot.Enabled, "Boundary values.");
            var model = new Model();
            Model.MqttBinding.Apply(model, snapshot);
            Check(Encoding.UTF8.GetString(Model.MqttBinding.Encode(model)) == json, "Order and invariant int formatting.");
        }
        Check(Decode(" \r\n{ \"enabled\" : false,\t\"other\": -0, \"number\" : 42 } \t", out var reordered) &&
            reordered.Number == 42 && reordered.Other == 0 && !reordered.Enabled, "Reordered JSON whitespace and negative zero.");
        string valid = "{\"number\":1,\"other\":2,\"enabled\":true}";
        foreach (string bad in new[] {
            "", " ", "{}", "[]", "null", valid + "x", valid + "{}", "\ufeff" + valid,
            valid.Replace("number", "unknown"), valid.Replace("other", "number"), valid.Replace("\"other\":2,", ""),
            valid.Replace("true", "TRUE"), valid.Replace("true", "1"), valid.Replace("true", "truee"), valid.Replace("true", "null"),
            valid.Replace(":1,", ":2147483648,"), valid.Replace(":1,", ":-2147483649,"),
            valid.Replace(":1,", ":9999999999999999999999999999999999999,"),
            valid.Replace(":1,", ":01,"), valid.Replace(":1,", ":-01,"), valid.Replace(":1,", ":+1,"),
            valid.Replace(":1,", ":1.0,"), valid.Replace(":1,", ":1e0,"), valid.Replace(":1,", ":-,"),
            valid.Replace(":1,", ":\"1\","), valid.Replace(":1,", ":false,"), valid.Replace(":1,", ":[1],"),
            valid.Replace(":1,", ":{\"v\":1},"), valid.Replace("number", "\\u006eumber"), valid.Replace("number", "nümber"),
            valid.Replace("}", ",}"), valid.Replace("}", ",\"x\":0}"), valid.Replace(",", "\v,")
        })
            Check(!Decode(bad, out var snapshot) && snapshot == null, "Reject malformed: " + bad);
        byte[] bytes = Bytes(valid);
        for (int length = 0; length < bytes.Length; length++)
            Check(!Model.MqttBinding.TryDecode(bytes.Take(length).ToArray(), out _), "Truncated " + length);
        Check(!Model.MqttBinding.TryDecode(null, out _), "Null input.");
        Check(!Model.MqttBinding.TryDecode(new byte[161], out _), "Input length bound.");
        string padded = valid + new string(' ', Model.MqttBinding.MaximumBytes - valid.Length);
        Check(Decode(padded, out _), "Exact input upper bound.");
        Check(!Decode(padded + " ", out _), "One byte past upper bound.");
        foreach (int value in new[] { -500, 1500 })
            Check(Ranged.MqttBinding.TryDecode(Bytes("{\"value\":" + value + "}"), out _), "Range endpoints.");
        foreach (int value in new[] { -501, 1501 })
        {
            Check(!Ranged.MqttBinding.TryDecode(Bytes("{\"value\":" + value + "}"), out _), "Reject range.");
            Throws<ArgumentOutOfRangeException>(() => Ranged.MqttBinding.Encode(new Ranged { Value = value }), "Encode range.");
            Throws<ArgumentOutOfRangeException>(() => new Ranged.MqttBinding.Snapshot(value), "Cannot construct invalid snapshot inside the app assembly.");
        }
        var random = new Random(3711);
        for (int iteration = 0; iteration < 256; iteration++)
        {
            int value = random.Next();
            if ((iteration & 1) != 0) value = -value;
            string json = "{\"number\":" + value.ToString(CultureInfo.InvariantCulture) + ",\"other\":0,\"enabled\":false}";
            Check(Decode(json, out var snapshot) && snapshot.Number == value, "Random Int32 decode.");
            var model = new Model();
            Model.MqttBinding.Apply(model, snapshot);
            Check(Encoding.UTF8.GetString(Model.MqttBinding.Encode(model)) == json, "Random Int32 roundtrip.");
        }
        var previous = CultureInfo.CurrentCulture;
        try
        {
            var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            culture.NumberFormat.NegativeSign = "NEG";
            CultureInfo.CurrentCulture = culture;
            Check(Decode("{\"number\":-100,\"other\":-1,\"enabled\":false}", out var snapshot), "Decode culture.");
            var model = new Model();
            Model.MqttBinding.Apply(model, snapshot);
            Check(Encoding.UTF8.GetString(Model.MqttBinding.Encode(model)).Contains(":-100"), "Encode invariant negative sign.");
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static void BufferEncoding()
    {
        var buffer = Enumerable.Repeat((byte)0xa5, Model.MqttBinding.MaximumBytes + 4).ToArray();
        foreach (int value in new[] { int.MinValue, -1000000000, -10, -1, 0, 1, 9, 10, 1000000000, int.MaxValue })
        foreach (bool enabled in new[] { false, true })
        {
            string json = "{\"number\":" + value.ToString(CultureInfo.InvariantCulture) + ",\"other\":-3,\"enabled\":" +
                (enabled ? "true" : "false") + "}";
            Check(Decode(json, out var snapshot), "Buffer model input.");
            var model = new Model();
            Model.MqttBinding.Apply(model, snapshot);
            Array.Fill(buffer, (byte)0xa5);
            int length = Model.MqttBinding.Encode(model, buffer, 2);
            Check(length == json.Length && Encoding.ASCII.GetString(buffer, 2, length) == json, "Buffer JSON boundary encoding.");
            Check(buffer.Take(2).All(b => b == 0xa5) && buffer.Skip(2 + length).All(b => b == 0xa5), "Buffer surrounding bytes preserved.");
            var exact = new byte[length];
            Check(Model.MqttBinding.Encode(model, exact) == length && Encoding.ASCII.GetString(exact) == json, "Exact capacity.");
            var shortBuffer = Enumerable.Repeat((byte)0xa5, length - 1).ToArray();
            Throws<ArgumentException>(() => Model.MqttBinding.Encode(model, shortBuffer), "Insufficient capacity.");
            Check(shortBuffer.All(b => b == 0xa5), "Capacity rejection is atomic.");
        }
        var stable = new Model();
        Throws<ArgumentNullException>(() => Model.MqttBinding.Encode(stable, null), "Null buffer.");
        Throws<ArgumentNullException>(() => Model.MqttBinding.Encode(null, buffer), "Null model.");
        foreach (int offset in new[] { -1, buffer.Length + 1, int.MaxValue })
            Throws<ArgumentOutOfRangeException>(() => Model.MqttBinding.Encode(stable, buffer, offset), "Invalid offset.");
        Throws<ArgumentException>(() => Model.MqttBinding.Encode(stable, buffer, buffer.Length), "No remaining capacity.");
        Array.Fill(buffer, (byte)0xa5);
        Throws<ArgumentOutOfRangeException>(() => Ranged.MqttBinding.Encode(new Ranged { Value = 1501 }, buffer), "Invalid value.");
        Check(buffer.All(b => b == 0xa5), "Range rejection is atomic.");
        for (int n = 0; n < 10000; n++) Model.MqttBinding.Encode(stable, buffer);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int n = 0; n < 10000; n++) Model.MqttBinding.Encode(stable, buffer);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(allocated == 0, "Reusable JSON encoding allocates zero bytes.");
        Console.WriteLine("MQTT JSON BUFFER PERF allocated=" + allocated + " rounds=10000");
        for (int n = 0; n < 10000; n++) GC.KeepAlive(Model.MqttBinding.Encode(stable));
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int n = 0; n < 10000; n++) GC.KeepAlive(Model.MqttBinding.Encode(stable));
        long encodedAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
        int encodedLength = Model.MqttBinding.Encode(stable).Length;
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int n = 0; n < 10000; n++) GC.KeepAlive(new byte[encodedLength]);
        long arrayAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(encodedAllocated == arrayAllocated, "Convenience encoding allocates only its result array.");
        Console.WriteLine("MQTT JSON ARRAY PERF bytes/call=" + encodedAllocated / 10000.0 + " resultLength=" + encodedLength);
    }

    private static void ApplyAndPublish()
    {
        Check(Names.MqttBinding.TryDecode(Bytes("{\"a\":42,\"b\":43}"), out var names) &&
            names.v0 == 42 && names.ToString == 43, "Snapshot constructor parameter and object member names do not collide.");
        var namedModel = new Names();
        Names.MqttBinding.Apply(namedModel, names);
        Check(namedModel.v0 == 42 && namedModel.ToString == 43, "Names map directly.");
        string valid = "{\"number\":5,\"other\":6,\"enabled\":true}";
        var model = new Model();
        Check(!Model.MqttBinding.TryApply(model, "wrong", Bytes(valid)) && model.Writes == 0, "Wrong topic never writes.");
        Check(!Model.MqttBinding.TryApply(model, Model.MqttBinding.Topic, Bytes(valid.Replace("true", "bad"))) &&
            model.Writes == 0 && model.Other == 0, "Final field failure never writes.");
        Check(Model.MqttBinding.TryApply(model, Model.MqttBinding.Topic, Bytes(valid)) && model.Writes == 1 &&
            model.Number == 5 && model.Other == 6 && model.Enabled, "Private setters and field apply.");
        int calls = 0;
        model.Reads = 0;
        Model.MqttBinding.Publish(model, (topic, bytes) =>
        {
            calls++;
            Check(topic == "plant/sensor" && Encoding.UTF8.GetString(bytes) == valid, "Publish callback exact wire contract.");
        });
        Check(calls == 1 && model.Reads == 1, "Single getter read and callback, no automatic networking.");
        Throws<InvalidOperationException>(() => Model.MqttBinding.Publish(model, (topic, bytes) => { throw new InvalidOperationException("network"); }), "Callback propagation.");
        model.ThrowWrite = true;
        Throws<InvalidOperationException>(() => Model.MqttBinding.TryApply(model, Model.MqttBinding.Topic, Bytes(valid)), "Setter propagation.");
        model.ThrowRead = true;
        Throws<InvalidOperationException>(() => Model.MqttBinding.Encode(model), "Getter propagation.");
        Throws<ArgumentNullException>(() => Model.MqttBinding.Encode(null), "Null encode.");
        Throws<ArgumentNullException>(() => Model.MqttBinding.Publish(model, null), "Null publisher.");
        Throws<ArgumentNullException>(() => Model.MqttBinding.Apply(model, null), "Null snapshot.");
        Throws<ArgumentNullException>(() => Model.MqttBinding.Apply(null, null), "Null model.");
        Throws<ArgumentNullException>(() => Model.MqttBinding.TryApply(null, "wrong", null), "Null try apply.");
    }

    private static void Sample()
    {
        var sent = new SensorSnapshot(1, 235, true);
        Check(Encoding.UTF8.GetString(SnapshotJson.Encode(sent)) == "{\"sequence\":1,\"temperatureDeciC\":235,\"running\":true}", "Existing sample wire contract.");
        Check(SnapshotJson.TryDecode(SnapshotJson.Encode(sent), out var decoded) &&
            decoded.Sequence == sent.Sequence && decoded.TemperatureDeciC == 235 && decoded.Running, "Existing wrapper API.");
        Check(!SnapshotJson.TryDecode(Bytes("{\"sequence\":0,\"temperatureDeciC\":235,\"running\":true}"), out _), "Sequence semantic validation.");
        Check(!SnapshotJson.TryDecode(Bytes("{\"sequence\":1,\"temperatureDeciC\":1501,\"running\":true}"), out _), "Temperature semantic validation.");
        var mapping = new RemoteSensorMapping("incoming", 3000);
        byte[] bytes = SnapshotJson.Encode(sent);
        Check(mapping.Apply("wrong", bytes, false, 0) == ApplyResult.WrongTopic, "Sample wrong topic.");
        Check(mapping.Apply("incoming", bytes, true, 0) == ApplyResult.Retained, "Sample retained.");
        Check(mapping.Apply("incoming", bytes, false, 0) == ApplyResult.Disconnected, "Sample disconnected.");
        mapping.SetConnected(true);
        Check(mapping.Apply("incoming", bytes, false, 10) == ApplyResult.Applied, "Sample applied.");
        Check(mapping.Read().ExternalTemperatureDeciC == 235 && mapping.IsAvailable(3009) && !mapping.IsAvailable(3010), "Sample field renaming and freshness.");
        Check(mapping.Apply("incoming", bytes, false, 20) == ApplyResult.OldSequence, "Sample old sequence.");
        mapping.SetConnected(false);
        mapping.SetConnected(true);
        Check(!mapping.IsAvailable(20), "Reconnect does not refresh old snapshot.");
    }
}
