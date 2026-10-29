using System;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using VagabondK.nF.DNP3;
using VagabondK.nF.DNP3.Mapping;
using VagabondK.nF.DNP3.Mapping.Generator;
using VagabondK.nF.DNP3.Mapping.Sample;

// Roslyn 진단과 생성 매핑의 모델 반영·이벤트·제어·할당을 검사한 뒤 로컬 및 독립 피어 회귀를 실행합니다.
// 기본 실행에도 사전 준비된 네이티브 Outstation 도구가 필요합니다. --hardware/--scaling은 지정한 전용 보드에 연결합니다.
internal static partial class Program
{
    private static int checks;
    private static readonly CSharpParseOptions Parse = new CSharpParseOptions(LanguageVersion.CSharp7_3);
    private static readonly MetadataReference[] References =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")).Split(Path.PathSeparator)
        .Select(p => MetadataReference.CreateFromFile(p)).Cast<MetadataReference>()
        .Append(MetadataReference.CreateFromFile(typeof(Dnp3Database).Assembly.Location)).ToArray();
    internal static void Check(bool valid, string message = "DNP3 mapping")
    {
        Interlocked.Increment(ref checks);
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
            Peer.Sample(args[1], int.Parse(args[2]));
            Console.WriteLine("DNP3 Mapping hardware: " + checks + " assertions passed.");
            return;
        }
        if (args.Length == 6 && args[0] == "--scaling")
        {
            Peer.Scaling(args[1], int.Parse(args[2]), int.Parse(args[3]), int.Parse(args[4]), args[5]);
            return;
        }
        if (args.Length != 0) throw new ArgumentException("Unknown arguments.");
        FloatData.Demonstrate();
        var floatSync = new object();
        var floatTarget = new VagabondK.nF.DNP3.MasterMapping.Sample.FloatData();
        var floatBinding = new VagabondK.nF.DNP3.MasterMapping.Sample.FloatData.Dnp3MasterBinding(floatTarget, floatSync);
        var floatReceiver = new VagabondK.nF.DNP3.MasterMapping.Sample.ReceivedData { Observer = floatBinding };
        var floatReceiverBinding = new VagabondK.nF.DNP3.MasterMapping.Sample.ReceivedData.Dnp3MasterBinding(floatReceiver, floatSync);
        floatReceiverBinding.OnMeasurements(new[] {
            new Dnp3Measurement { Type = Dnp3PointType.Analog, Index = 1, Value = Dnp3Value.Analog(25.25) },
            new Dnp3Measurement { Type = Dnp3PointType.AnalogOutputStatus, Index = 1, Value = Dnp3Value.Analog(2.5) }
        }, new Dnp3ResponseInfo());
        Check(floatTarget.Measurement == 25.25f && floatTarget.Output == 2.5f && floatTarget.Fragments == 1, "Sample Float32 Master mapping.");
        Diagnostics();
        ShorthandDeclarations();
        MasterMapping();
        Database();
        Selective();
        Commands();
        Protocol();
        SnapshotAndAllocation();
        Peer.Loopback();
        MasterPeer.Run();
        MasterSoakPeer.Run();
        MasterSerialTests.Run();
        Console.WriteLine("DNP3 Mapping: " + checks + " assertions passed.");
    }
    private static Compilation Generate(string source, out GeneratorDriverRunResult result)
    {
        var compilation = CSharpCompilation.Create("Dnp3_" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(source, Parse) }, References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new[] { new Dnp3MappingGenerator().AsSourceGenerator() }, parseOptions: Parse);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _);
        result = driver.GetRunResult();
        Check(result.Results.All(r => r.Exception == null), string.Join("\n", result.Results.Select(r => r.Exception)));
        return updated;
    }
    private static string Model(string body, string shape = "public partial class Model")
    {
        return "using VagabondK.nF.DNP3; using VagabondK.nF.DNP3.Mapping; [Dnp3Map] " + shape + " {" + body + "}";
    }
    private const string Point = "[Dnp3Point(Dnp3PointType.Analog,0)] public double Value;";
    private static void ShorthandDeclarations()
    {
        string[] types = { "Binary", "DoubleBitBinary", "Counter", "FrozenCounter", "Analog", "BinaryOutputStatus", "AnalogOutputStatus" };
        string[] attributes = { "BinaryInput", "DoubleBitInput", "Counter", "FrozenCounter", "AnalogInput", "BinaryOutput", "AnalogOutput" };
        string[] clr = { "bool", "byte", "uint", "uint", "double", "bool", "float" };
        foreach (bool master in new[] { false, true })
        for (int i = 0; i < types.Length; i++)
        {
            string options = ",Flags=nameof(Flags),Timestamp=nameof(Time)" + (master ? "" : ",EventClass=2");
            string canonical = Model("[Dnp3Point(Dnp3PointType." + types[i] + ",65535" + options + ")] public " + clr[i] +
                " Value; public byte Flags; public long Time;" + (master ?
                "[Dnp3Received] void Received(Dnp3Measurement[] m,Dnp3ResponseInfo r) {}" : ""));
            if (master) canonical = canonical.Replace("[Dnp3Map]", "[Dnp3MasterMap]");
            string shorthand = canonical.Replace("Dnp3Point(Dnp3PointType." + types[i] + ",", attributes[i] + "(");
            Valid(canonical);
            Valid(shorthand);
            Generate(canonical, out var before);
            Generate(shorthand, out var after);
            string Implementation(GeneratorDriverRunResult result) => string.Join("\n", result.Results.SelectMany(r => r.GeneratedSources)
                .Where(s => !s.HintName.EndsWith("Attributes.g.cs")).Select(s => s.SourceText.ToString()));
            Check(Implementation(before) == Implementation(after), "Shorthand preserves DNP3 runtime code: " + master + "/" + types[i]);
        }
        Invalid(Model("[AnalogInput(0)][Dnp3Point(Dnp3PointType.Analog,0)] public double Value;"), "VKDN002");
        Invalid(Model("[BinaryInput(0)] public double Value;"), "VKDN002");
        Invalid(Model("[AnalogInput(0,Deadband=-1)] public double Value;"), "VKDN003");
        string invalidMaster = Model("[AnalogInput(0,EventClass=1)] public double Value;" +
            "[Dnp3Received] void Received(Dnp3Measurement[] m,Dnp3ResponseInfo r) {}").Replace("[Dnp3Map]", "[Dnp3MasterMap]");
        Generate(invalidMaster, out var rejected);
        Check(rejected.Diagnostics.Any(d => d.Id == "VKDN003"), "Master shorthand never enables Outstation event options.");
    }
    private static void Invalid(string source, string id)
    {
        Generate(source, out var result);
        Check(result.Diagnostics.Any(d => d.Id == id), "Missing " + id + ": " + source);
        Check(!result.Results.SelectMany(r => r.GeneratedSources).Any(s => s.HintName.StartsWith("Dnp3Map0")));
    }
    private static void Valid(string source)
    {
        var compilation = Generate(source, out var result);
        Check(!compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error),
            string.Join("\n", compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)));
        Check(!result.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error));
    }
    private static void Diagnostics()
    {
        foreach (var shape in new[] { "public class Model", "public static partial class Model", "public abstract partial class Model",
            "public partial struct Model", "public partial class Model<T>", "public partial class Model : System.Exception" })
            Invalid(Model(Point, shape), "VKDN001");
        Invalid(Model(Point + " class Dnp3Binding {}"), "VKDN001");
        Invalid("using VagabondK.nF.DNP3; using VagabondK.nF.DNP3.Mapping; class Outer {[Dnp3Map] partial class Inner {" + Point + "}}", "VKDN001");
        Invalid(Model(""), "VKDN002");
        foreach (string type in new[] { "int", "uint", "string", "bool", "double[]", "System.DayOfWeek" })
            Invalid(Model(Point.Replace("double", type)), "VKDN002");
        Invalid(Model(Point.Replace("public double", "public static double")), "VKDN002");
        Invalid(Model(Point.Replace("public double Value;", "public double Value {set {}}")), "VKDN002");
        Invalid(Model(Point + Point.Replace("Value", "Other")), "VKDN004");
        foreach (string option in new[] { "EventClass=4", "Deadband=-1", "Deadband=double.NaN", "Deadband=double.PositiveInfinity",
            "StaticVariation=3", "EventVariation=9", "Flags=null", "Flags=\"Missing\"", "Flags=\"Wrong\"", "Timestamp=\"Wrong\"" })
            Invalid(Model(Point.Replace(",0)", ",0," + option + ")") + " int Wrong;"), "VKDN003");
        Invalid(Model(Point.Replace("Dnp3PointType.Analog", "(Dnp3PointType)7")), "VKDN003");
        Invalid(Model(Point.Replace("Dnp3PointType.Analog", "Dnp3PointType.Counter").Replace("double", "uint").Replace(",0)", ",0,Deadband=1)")), "VKDN003");
        Valid(Model(Point.Replace("double", "float").Replace("Value", "@event"), "public partial class @class"));
        Valid(Model(Point.Replace("public double Value;", "readonly double Value;")));
        Valid(Model(Point.Replace(",0)", ",65535,Flags=nameof(F),Timestamp=nameof(T))") + " byte F; long T;"));
        string many = string.Concat(Enumerable.Range(0, 513).Select(i => "[Dnp3Point(Dnp3PointType.Binary," + i + ")] public bool P" + i + ";"));
        Invalid(Model(many), "VKDN002");
        Valid(Model(many.Substring(0, many.LastIndexOf("[Dnp3Point", StringComparison.Ordinal))));
        const string command = "[Dnp3Control(12,0,Validate=nameof(Check))] private Dnp3CommandStatus Run(Dnp3Command c,bool selected) {return 0;} private Dnp3CommandStatus Check(Dnp3Command c) {return 0;}";
        Valid(Model(Point + command));
        Invalid(Model(Point + command.Replace("12,0", "13,0")), "VKDN005");
        Invalid(Model(Point + command.Replace("nameof(Check)", "\"Missing\"")), "VKDN005");
        Invalid(Model(Point + command.Replace("bool selected", "int selected")), "VKDN005");
        Invalid(Model(Point + command.Replace("private Dnp3CommandStatus Run", "private static Dnp3CommandStatus Run")), "VKDN005");
        Invalid(Model(Point + command + command.Replace("Run(", "Other(").Replace("Check", "Check2")), "VKDN004");
        Generate(Model("[Dnp3Point(Missing,0)] double Value;"), out _);
        Generate(Model(Point.Replace(",0)", ",0,Flags=Missing)")), out _);
    }
    private static void Database()
    {
        var data = new DeviceData();
        var map = new DeviceData.Dnp3Binding(data, new object(), 2);
        var db = map.Database;
        Check(db.PointCount == 7 && db.EventCapacity == 2 && db.EventCount == 0);
        Check(db.Read(Dnp3PointType.Analog, 0).AsAnalog() == 250);
        Check(db.Read(Dnp3PointType.DoubleBitBinary, 0).Raw == 2);
        Check(db.Read(Dnp3PointType.Counter, 0).Raw == 1000);
        Check(map.Publish() && db.EventCount == 0);
        data.Timestamp++;
        Check(map.Publish() && db.EventCount == 0, "Timestamp-only is not event.");
        data.Temperature = 250.5;
        Check(map.Publish() && db.EventCount == 0, "Deadband must be exceeded.");
        data.Temperature = 250.75;
        Check(map.Publish() && db.EventCount == 1, "Deadband baseline.");
        data.Total++;
        Check(map.Publish() && db.EventCount == 2);
        data.Contact = false;
        Check(!map.Publish() && db.Overflow && db.EventCount == 2);
        Check(db.Read(Dnp3PointType.Binary, 0).Raw == 0, "Overflow preserves Static update.");
        data.Total++;
        Check(!map.Publish() && db.Read(Dnp3PointType.Counter, 0).Raw == 1002, "Continue after first overflow.");
        Throws<InvalidOperationException>(() => db.ClearOverflow());
        var events = db.Snapshot(false, 14, 0, 0, 0, 65535, 512);
        Check(events.Length == 2 && events[0].Value.Timestamp == data.Timestamp);
        db.Confirm(events, 0, events.Length);
        db.ClearOverflow();
        Check(!db.Overflow && map.Publish() && db.EventCount == 2, "Dropped changes retried against prior baseline.");
        var clean = new DeviceData.Dnp3Binding(data, new object());
        data.Flags = 4;
        Check(clean.Publish() && clean.Database.EventCount == 7, "Quality-only events.");
        data.State = 4;
        Throws<ArgumentOutOfRangeException>(() => clean.Publish());
        data.State = 2;
        data.Timestamp = -1;
        Throws<ArgumentOutOfRangeException>(() => clean.Publish());
        data.Timestamp = 0;
        data.Temperature = double.NaN;
        Throws<ArgumentException>(() => clean.Publish());
        Throws<ArgumentNullException>(() => new DeviceData.Dnp3Binding(null, new object()));
        Throws<ArgumentNullException>(() => new DeviceData.Dnp3Binding(new DeviceData(), null));
        Throws<ArgumentException>(() => new DeviceData.Dnp3Binding(new DeviceData(), new object(), 0));
        var failure = new FailingData.Dnp3Binding(new FailingData(), new object());
        FailingData.Fail = true;
        Throws<InvalidOperationException>(() => failure.Publish());
        Throws<InvalidOperationException>(() => failure.Validate(Dnp3Command.Relay(0, 3)));
        Throws<InvalidOperationException>(() => failure.Operate(Dnp3Command.Relay(0, 3), false));
        FailingData.Fail = false;
        Throws<InvalidOperationException>(() => failure.Operate(Dnp3Command.Relay(0, 3), false));
    }
    private static void Commands()
    {
        var data = new DeviceData();
        var sync = new object();
        var map = new DeviceData.Dnp3Binding(data, sync);
        var relay = Dnp3Command.Relay(0, 4);
        Check(map.Validate(relay) == 0 && data.Enabled, "Select does not execute.");
        Check(map.Operate(relay, true) == 0 && !data.Enabled);
        Check(map.Database.Read(Dnp3PointType.BinaryOutputStatus, 0).Raw == 1, "Output publication is explicit.");
        Check(map.Publish() && map.Database.Read(Dnp3PointType.BinaryOutputStatus, 0).Raw == 0);
        Check(map.Operate(Dnp3Command.Relay(0, 1, 2, 100, 200), false) == Dnp3CommandStatus.NotSupported, "Pulse not converted to bool.");
        Check(map.Operate(Dnp3Command.Analog(0, 1001), false) == Dnp3CommandStatus.OutOfRange);
        Check(map.Operate(Dnp3Command.Analog(9, 1), false) == Dnp3CommandStatus.NotSupported);
        Check(map.Operate(default, false) == Dnp3CommandStatus.NotSupported);
        foreach (bool selected in new[] { false, true })
            foreach (byte variation in new byte[] { 1, 2, 3, 4 })
            {
                double value = variation <= 2 ? -123 : -123.25;
                Check(map.Operate(Dnp3Command.Analog(0, value, variation), selected) == 0 && data.Setpoint == value);
            }
        var custom = new CommandData();
        var customMap = new CommandData.Dnp3Binding(custom, sync);
        Check(customMap.Validate(Dnp3Command.Relay(0, 1, 5, 123, 456)) == 0);
        Check(custom.Calls == 0);
        Check(customMap.Operate(Dnp3Command.Relay(0, 1, 5, 123, 456), true) == 0);
        Check(custom.Calls == 1 && custom.Selected && custom.Command.Count == 5 &&
            custom.Command.OnMilliseconds == 123 && custom.Command.OffMilliseconds == 456, "CROB passed unchanged.");
    }
    private static void SnapshotAndAllocation()
    {
        var data = new SnapshotData();
        var sync = new object();
        var map = new SnapshotData.Dnp3Binding(data, sync, 1);
        bool stop = false;
        var writer = new Thread(() =>
        {
            while (!Volatile.Read(ref stop))
                lock (sync) { data.Value = !data.Value; data.Flags = data.Value ? (byte)1 : (byte)4; data.Time = data.Value ? 1 : 0; }
        });
        writer.Start();
        try
        {
            for (int i = 0; i < 10000; i++)
            {
                Check((i & 1) == 0 ? map.Publish() : map.Publish(Dnp3PointType.Binary, 0));
                var v = map.Database.Read(Dnp3PointType.Binary, 0);
                Check(v.Raw == 1 ? v.Flags == 1 && v.Timestamp == 1 : v.Flags == 4 && v.Timestamp == 0, "Value and metadata snapshot.");
            }
        }
        finally { Volatile.Write(ref stop, true); writer.Join(); }
        for (int i = 0; i < 100; i++) map.Publish();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) map.Publish();
        Check(GC.GetAllocatedBytesForCurrentThread() == before, "Publish allocation.");
        var eventData = new DeviceData();
        var eventMap = new DeviceData.Dnp3Binding(eventData, new object(), 1);
        eventData.Total++;
        eventMap.Publish();
        before = GC.GetAllocatedBytesForCurrentThread();
        bool accepted = true;
        for (int i = 0; i < 10000; i++) { eventData.Total++; accepted &= eventMap.Publish(); }
        Check(!accepted && eventMap.Database.Overflow);
        Check(GC.GetAllocatedBytesForCurrentThread() == before, "Overflow path allocation.");
        var acceptedData = new DeviceData();
        var acceptedMap = new DeviceData.Dnp3Binding(acceptedData, new object(), 2048);
        acceptedMap.Publish();
        before = GC.GetAllocatedBytesForCurrentThread();
        accepted = true;
        for (int i = 0; i < 1000; i++) { acceptedData.Total++; accepted &= acceptedMap.Publish(); }
        Check(GC.GetAllocatedBytesForCurrentThread() == before, "Accepted event path allocation.");
        Check(accepted && acceptedMap.Database.EventCount == 1000);
        before = GC.GetAllocatedBytesForCurrentThread();
        accepted = true;
        for (int i = 0; i < 1000; i++) { acceptedData.Total++; accepted &= acceptedMap.Publish(Dnp3PointType.Counter, 0); }
        Check(GC.GetAllocatedBytesForCurrentThread() == before, "Selective accepted event allocation.");
        Check(accepted && acceptedMap.Database.EventCount == 2000);
        for (int i = 0; i < 100; i++) eventMap.Publish(Dnp3PointType.Counter, 0);
        before = GC.GetAllocatedBytesForCurrentThread();
        accepted = true;
        for (int i = 0; i < 10000; i++) { eventData.Total++; accepted &= eventMap.Publish(Dnp3PointType.Counter, 0); }
        Check(GC.GetAllocatedBytesForCurrentThread() == before, "Selective overflow allocation.");
        Check(!accepted);
        for (int i = 0; i < 100; i++) map.Publish(Dnp3PointType.Binary, 0);
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) map.Publish(Dnp3PointType.Binary, 0);
        Check(GC.GetAllocatedBytesForCurrentThread() == before, "Selective unchanged allocation.");
    }
    private static void Selective()
    {
        var data = new DeviceData();
        var map = new DeviceData.Dnp3Binding(data, new object(), 1);
        data.Temperature = 250.5;
        data.Total = 1234;
        Check(map.Publish(Dnp3PointType.Analog, 0) && map.Database.EventCount == 0, "Selective deadband boundary.");
        Check(map.Database.Read(Dnp3PointType.Counter, 0).Raw == 1000, "Unselected Static unchanged.");
        data.Temperature = 250.75;
        Check(map.Publish(Dnp3PointType.Analog, 0) && map.Database.EventCount == 1);
        Check(!map.Publish(Dnp3PointType.Counter, 0) && map.Database.Overflow);
        Check(map.Database.Read(Dnp3PointType.Counter, 0).Raw == 1234);
        var events = map.Database.Snapshot(false, 14, 0, 0, 0, 65535, 512);
        map.Database.Confirm(events, 0, events.Length);
        map.Database.ClearOverflow();
        Check(map.Publish(Dnp3PointType.Counter, 0) && map.Database.EventCount == 1, "Dropped event retried without value change.");
        events = map.Database.Snapshot(false, 14, 0, 0, 0, 65535, 512);
        Check(events[0].Point.Type == Dnp3PointType.Counter && events[0].Value.Raw == 1234);
        map.Database.Confirm(events, 0, events.Length);
        data.Timestamp++;
        Check(map.Publish(Dnp3PointType.Counter, 0) && map.Database.EventCount == 0);
        Check(map.Database.Read(Dnp3PointType.Counter, 0).Timestamp == data.Timestamp);
        data.Flags = 4;
        Check(map.Publish(Dnp3PointType.Counter, 0) && map.Database.EventCount == 1, "Selective quality event.");
        Check(map.Database.Read(Dnp3PointType.Analog, 0).Flags == 1, "Unselected quality not implicitly published.");
        Throws<ArgumentException>(() => map.Publish((Dnp3PointType)7, 0));
        Throws<ArgumentException>(() => map.Publish(Dnp3PointType.Binary, 65535));
        data.State = 4;
        Check(map.Publish(Dnp3PointType.Binary, 0) == false, "Other invalid value not read.");
        Throws<ArgumentOutOfRangeException>(() => map.Publish(Dnp3PointType.DoubleBitBinary, 0));
        data.Timestamp = -1;
        Throws<ArgumentOutOfRangeException>(() => map.Publish(Dnp3PointType.Counter, 0));
        Check(map.Database.Read(Dnp3PointType.Counter, 0).Timestamp > 0, "Invalid timestamp not published.");
        data.Timestamp = 0;
        data.Temperature = double.NaN;
        Throws<ArgumentException>(() => map.Publish(Dnp3PointType.Analog, 0));
        Check(map.Database.Read(Dnp3PointType.Analog, 0).AsAnalog() == 250.75, "Invalid value not published.");
        var failure = new FailingData.Dnp3Binding(new FailingData(), new object());
        FailingData.Fail = true;
        Throws<InvalidOperationException>(() => failure.Publish(Dnp3PointType.Binary, 0));
        Throws<ArgumentException>(() => failure.Publish(Dnp3PointType.Binary, 1));
        Check(failure.Publish(Dnp3PointType.Binary, 65535), "Unselected throwing getter not read; sparse index.");
        FailingData.Fail = false;
        data = new DeviceData();
        map = new DeviceData.Dnp3Binding(data, new object(), 32);
        data.Contact = false;
        data.State = 3;
        data.Total = 1001;
        data.Frozen = 1000;
        data.Temperature = 251;
        Check(map.Operate(Dnp3Command.Relay(0, 4), false) == Dnp3CommandStatus.Success);
        Check(map.Operate(Dnp3Command.Analog(0, 13.5), false) == Dnp3CommandStatus.Success);
        var expected = new[] { Dnp3Value.Bits(0), Dnp3Value.Bits(3), Dnp3Value.Bits(1001),
            Dnp3Value.Bits(1000), Dnp3Value.Analog(251), Dnp3Value.Bits(0), Dnp3Value.Analog(13.5) };
        for (int i = 0; i < expected.Length; i++)
        {
            var type = (Dnp3PointType)i;
            Check(map.Publish(type, 0), "Seven selective Point kinds.");
            var actual = map.Database.Read(type, 0);
            Check(actual.Raw == expected[i].Raw && actual.Flags == data.Flags && actual.Timestamp == data.Timestamp);
            Check(map.Database.EventCount == i + 1, "Only selected Point creates event.");
        }
    }
    private static byte[] Exchange(Dnp3Station station, Dnp3Channel master, byte[] request, long now)
    {
        byte[] wire = master.Encode(request);
        var input = new Dnp3StreamDecoder();
        var output = new Dnp3StreamDecoder();
        byte[] application = null;
        foreach (byte value in wire)
        {
            byte[] frame = input.Push(value, now);
            if (frame == null) continue;
            byte[] reply = station.Process(frame, now);
            if (reply == null) continue;
            foreach (byte response in reply)
            {
                var received = output.Push(response, now);
                if (received != null)
                {
                    var decoded = master.Receive(received, now, out _);
                    if (decoded != null) application = decoded;
                }
            }
        }
        return application;
    }
    private static byte[] Request(byte sequence, byte function, Dnp3Command command)
    {
        byte[] body = Dnp3Controls.Encode(command);
        var request = new byte[body.Length + 2];
        request[0] = (byte)(0xc0 | sequence);
        request[1] = function;
        Array.Copy(body, 0, request, 2, body.Length);
        return request;
    }
    private static void Protocol()
    {
        var data = new CommandData();
        var map = new CommandData.Dnp3Binding(data, new object());
        var station = new Dnp3Station(map.Database, 1, 2, map);
        var master = new Dnp3Channel(2, 1, true);
        var command = Dnp3Command.Relay(0, 1, 5, 123, 456);
        byte[] selected = Exchange(station, master, Request(0, 3, command), 0);
        Check(selected.Last() == 0 && data.Calls == 0, "Wire Select validates only.");
        byte[] operation = Request(1, 4, command);
        Check(Exchange(station, master, operation, 1).Last() == 0 && data.Calls == 1 && data.Selected);
        Check(Exchange(station, master, operation, 2).Last() == 0 && data.Calls == 1, "Duplicate Operate not repeated.");
        Check(Exchange(station, master, Request(2, 4, command), 6000).Last() == (byte)Dnp3CommandStatus.NoSelect && data.Calls == 1);
        Check(Exchange(station, master, Request(3, 5, command), 6001).Last() == 0 && data.Calls == 2 && !data.Selected);
        var failure = new FailingData.Dnp3Binding(new FailingData(), new object());
        var failedStation = new Dnp3Station(failure.Database, 1, 2, failure);
        var failedMaster = new Dnp3Channel(2, 1, true);
        FailingData.Fail = true;
        Throws<InvalidOperationException>(() => Exchange(failedStation, failedMaster, Request(0, 5, command), 0));
        FailingData.Fail = false;
        Throws<InvalidOperationException>(() => Exchange(failedStation, failedMaster, Request(1, 5, command), 1));
    }
}

[Dnp3Map]
internal partial class SnapshotData
{
    [Dnp3Point(Dnp3PointType.Binary, 0, EventClass = 0, Flags = nameof(Flags), Timestamp = nameof(Time))]
    public bool Value;
    public byte Flags = 4;
    public long Time;
}

[Dnp3Map]
internal partial class FailingData
{
    internal static bool Fail;
    [Dnp3Point(Dnp3PointType.Binary, 0)] private bool Value { get { if (Fail) throw new InvalidOperationException("getter"); return true; } }
    [Dnp3Point(Dnp3PointType.Binary, 65535)] private bool Other { get { return false; } }
    private Dnp3CommandStatus Check(Dnp3Command command) { if (Fail) throw new InvalidOperationException("validator"); return 0; }
    [Dnp3Control(12, 0, Validate = nameof(Check))]
    private Dnp3CommandStatus Run(Dnp3Command command, bool selected) { throw new InvalidOperationException("operate"); }
}

[Dnp3Map]
internal partial class CommandData
{
    [Dnp3Point(Dnp3PointType.Binary, 0)] public bool Value { get { return false; } }
    public int Calls;
    public bool Selected;
    public Dnp3Command Command;
    private Dnp3CommandStatus Check(Dnp3Command command) { return 0; }
    [Dnp3Control(12, 0, Validate = nameof(Check))]
    private Dnp3CommandStatus Run(Dnp3Command command, bool selected) { Command = command; Selected = selected; Calls++; return 0; }
}
