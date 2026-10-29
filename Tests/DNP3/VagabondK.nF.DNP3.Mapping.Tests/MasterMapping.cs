using System;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using VagabondK.nF.DNP3;
using VagabondK.nF.DNP3.Mapping;

// 잘못된 Master 선언의 진단과 Fragment 전체 사전 검증, 순차 반영 및 콜백의 잠금·예외 처리 계약을 검사합니다.
internal static partial class Program
{
    private const string Received = "[Dnp3Received] private void Received(Dnp3Measurement[] m,Dnp3ResponseInfo r) {}";
    private static string MasterModel(string body, string shape = "public partial class Model")
    {
        return "using VagabondK.nF.DNP3; using VagabondK.nF.DNP3.Mapping; [Dnp3MasterMap] " + shape + " {" + body + "}";
    }
    private static void MasterInvalid(string source, string id)
    {
        Generate(source, out var result);
        Check(result.Diagnostics.Any(d => d.Id == id), "Master diagnostic " + id);
        Check(!result.Results.SelectMany(r => r.GeneratedSources).Any(s => s.HintName.StartsWith("Dnp3MasterMap")));
    }
    private static Dnp3Measurement Measurement(Dnp3PointType type, ushort index, Dnp3Value value, bool events = false)
    {
        return new Dnp3Measurement { Type = type, Index = index, Value = value, IsEvent = events };
    }
    private static void MasterMapping()
    {
        MasterInvalid(MasterModel(Point + Received, "public class Model"), "VKDN001");
        MasterInvalid(MasterModel(Point + Received + "class Dnp3MasterBinding {}"), "VKDN001");
        MasterInvalid(MasterModel(Point), "VKDN006");
        MasterInvalid(MasterModel(Point + "[Dnp3Received] partial void Received(Dnp3Measurement[] m,Dnp3ResponseInfo r);"), "VKDN006");
        MasterInvalid(MasterModel(Point + Received.Replace("[Dnp3Received]", "[Dnp3Received,System.Diagnostics.Conditional(\"DEBUG\")]")), "VKDN006");
        MasterInvalid(MasterModel(Point + Received + Received.Replace("Received(", "Other(")), "VKDN006");
        foreach (var callback in new[] { Received.Replace("void Received", "async void Received"),
            Received.Replace("void Received", "static void Received"), Received.Replace("Dnp3Measurement[]", "Dnp3Measurement"),
            Received.Replace("Dnp3ResponseInfo r", "ref Dnp3ResponseInfo r") })
            MasterInvalid(MasterModel(Point + callback), "VKDN006");
        foreach (var value in new[] { "readonly double Value;", "double Value {get;}", "static double Value;", "int Value;",
            "double Value {get;init;}" })
            MasterInvalid(MasterModel(Point.Replace("public double Value;", value) + Received), "VKDN002");
        foreach (string option in new[] { "Deadband=0", "EventClass=0", "StaticVariation=0", "EventVariation=0",
            "Flags=null", "Timestamp=\"Wrong\"", "Flags=\"F\"" })
            MasterInvalid(MasterModel(Point.Replace(",0)", ",0," + option + ")") + "readonly byte F; int Wrong;" + Received), "VKDN003");
        MasterInvalid(MasterModel(Point + Point.Replace("Value", "Other") + Received), "VKDN004");
        MasterInvalid(MasterModel(Point.Replace(",0)", ",0,Flags=nameof(F))") +
            Point.Replace(",0)", ",1,Flags=nameof(F))").Replace("Value", "Other") + "byte F;" + Received), "VKDN004");
        MasterInvalid(MasterModel("[Dnp3Point(Dnp3PointType.DoubleBitBinary,0,Flags=nameof(Value))] byte Value;" + Received), "VKDN004");
        MasterInvalid(MasterModel(Point + Received + "[Dnp3Control(12,0)] void Control() {}"), "VKDN005");
        MasterInvalid(MasterModel(Point + Received).Replace("[Dnp3MasterMap]", "[Dnp3MasterMap,Dnp3Map]"), "VKDN001");
        Valid(MasterModel(Point.Replace("public double Value;", "public double Value {private get;set;}") + Received));
        Valid(MasterModel(Point.Replace("public double Value;", "private float Value {set {}}") + Received));
        string many = string.Concat(Enumerable.Range(0, 513).Select(i => "[Dnp3Point(Dnp3PointType.Binary," + i + ")] public bool P" + i + ";"));
        MasterInvalid(MasterModel(many + Received), "VKDN002");
        MasterInvalid(MasterModel(many.Substring(0, many.LastIndexOf("[Dnp3Point", StringComparison.Ordinal)) + Received), "VKDN007");
        string fields255 = string.Concat(Enumerable.Range(0, 255).Select(i => "[Dnp3Point(Dnp3PointType.Binary," + i + ")] public bool P" + i + ";"));
        Valid(MasterModel(fields255 + Received));
        MasterInvalid(MasterModel(fields255 + "byte Extra;" + Received), "VKDN007");
        string properties126 = string.Concat(Enumerable.Range(0, 126).Select(i => "[Dnp3Point(Dnp3PointType.Binary," + i + ")] public bool P" + i + " {get;set;}"));
        Valid(MasterModel(properties126 + Received + "void Extra() {}"));
        MasterInvalid(MasterModel(properties126 + Received + "void Extra() {} void Extra2() {}"), "VKDN007");
        MasterInvalid(MasterModel(fields255 + "byte Extra {get;set;}" + Received), "VKDN007");
        MasterLarge();
        MasterInvalid(MasterModel("" + Received), "VKDN002");
        var model = new Receiver();
        var sync = new object();
        var binding = new Receiver.Dnp3MasterBinding(model, sync);
        Throws<ArgumentNullException>(() => new Receiver.Dnp3MasterBinding(null, sync));
        Throws<ArgumentNullException>(() => new Receiver.Dnp3MasterBinding(model, null));
        Throws<ArgumentNullException>(() => binding.OnMeasurements(null, default));
        var records = new[] {
            Measurement(Dnp3PointType.Binary, 0, Dnp3Value.Bits(1, 129, 10)),
            Measurement(Dnp3PointType.DoubleBitBinary, 0, Dnp3Value.Bits(3, 63, 11)),
            Measurement(Dnp3PointType.Counter, 0, Dnp3Value.Bits(uint.MaxValue, 4, 12)),
            Measurement(Dnp3PointType.FrozenCounter, 0, Dnp3Value.Bits(123, 2, 13)),
            Measurement(Dnp3PointType.Analog, 0, Dnp3Value.Analog(-12.25, 32, 14)),
            Measurement(Dnp3PointType.BinaryOutputStatus, 0, Dnp3Value.Bits(1, 65, 15)),
            Measurement(Dnp3PointType.AnalogOutputStatus, 65535, Dnp3Value.Analog(0.5, 1, 16)),
            Measurement(Dnp3PointType.Analog, 99, Dnp3Value.Analog(42), true),
            Measurement(Dnp3PointType.Analog, 0, Dnp3Value.Analog(10, 4, 2), true),
            Measurement(Dnp3PointType.Analog, 0, Dnp3Value.Analog(11, 8, 2), true) };
        var response = new Dnp3ResponseInfo { First = true, Final = false, Unsolicited = true, Sequence = 15, Iin1 = 2, Iin2 = 8 };
        binding.OnMeasurements(records, response);
        Check(model.Binary && model.Double == 3 && model.Counter == uint.MaxValue && model.Frozen == 123 && model.Output && model.Setpoint == 0.5f);
        Check(model.Analog == 11 && model.Flags == 8 && model.Time == 2, "Receipt order, not timestamp order.");
        Check(ReferenceEquals(model.Last, records) && model.Events == 3 && model.Calls == 1, "Original duplicate/unmapped events preserved.");
        Check(model.Info.Sequence == 15 && model.Info.Unsolicited && model.Info.First && !model.Info.Final && model.Info.Iin2 == 8);
        var empty = Array.Empty<Dnp3Measurement>();
        binding.OnMeasurements(empty, new Dnp3ResponseInfo { Final = true });
        Check(model.Calls == 2 && model.Info.Final && ReferenceEquals(empty, model.Last), "Empty Fragment delivered.");
        var invalid = new[] { records[4], Measurement(Dnp3PointType.AnalogOutputStatus, 65535, Dnp3Value.Analog(double.MaxValue)) };
        Throws<ArgumentException>(() => binding.OnMeasurements(invalid, default));
        Check(model.Analog == 11 && model.Calls == 2, "Preflight failure does not partially update.");
        foreach (ulong bits in new[] { 0x8000000000000000UL, 1UL, 0x8000000000000001UL, 0x7fefffffffffffffUL })
        {
            binding.OnMeasurements(new[] { Measurement(Dnp3PointType.Analog, 0, Dnp3Value.Bits(bits, 255, 0xffffffffffffL)) }, default);
            Check(unchecked((ulong)BitConverter.DoubleToInt64Bits(model.Analog)) == bits &&
                model.Flags == 255 && model.Time == 0xffffffffffffL, "Exact double bits and metadata boundaries.");
        }
        binding.OnMeasurements(new[] { records[9] }, response);
        foreach (var bad in new[] {
            Measurement(Dnp3PointType.Binary, 0, Dnp3Value.Bits(2)),
            Measurement(Dnp3PointType.DoubleBitBinary, 0, Dnp3Value.Bits(4)),
            Measurement(Dnp3PointType.Counter, 0, Dnp3Value.Bits((ulong)uint.MaxValue + 1)),
            Measurement(Dnp3PointType.FrozenCounter, 0, Dnp3Value.Bits(ulong.MaxValue)),
            Measurement(Dnp3PointType.Analog, 0, Dnp3Value.Bits(0x7ff0000000000000UL)),
            Measurement(Dnp3PointType.BinaryOutputStatus, 0, Dnp3Value.Bits(2)) })
            Throws<ArgumentException>(() => binding.OnMeasurements(new[] { bad }, default));
        model.Fail = true;
        Throws<InvalidOperationException>(() => binding.OnMeasurements(records, response));
        model.Fail = false;
        for (int i = 0; i < 100; i++) binding.OnMeasurements(records, response);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) binding.OnMeasurements(records, response);
        Check(GC.GetAllocatedBytesForCurrentThread() == before, "Master mapping adds no managed allocation.");
        bool stop = false;
        var alternative = new[] { records[8] };
        var writer = new Thread(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                binding.OnMeasurements(alternative, response);
                binding.OnMeasurements(records, response);
            }
        });
        writer.Start();
        try { for (int i = 0; i < 10000; i++) lock (sync) Check(
            (model.Analog == 11 && model.Flags == 8 || model.Analog == 10 && model.Flags == 4) && model.Time == 2); }
        finally { Volatile.Write(ref stop, true); writer.Join(); }
        var setter = new ThrowingSetter.Dnp3MasterBinding(new ThrowingSetter(), new object());
        Throws<InvalidOperationException>(() => setter.OnMeasurements(new[] { records[4] }, response));
        MasterWire();
    }
    private static void MasterLarge()
    {
        string members = "bool[] values = new bool[250];" + string.Concat(Enumerable.Range(0, 500).Select(i =>
            "[Dnp3Point(Dnp3PointType.Binary," + i * 131 + ")] public bool P" + i +
            (i < 250 ? ";" : " {set {values[" + (i - 250) + "] = value;}}")));
        string verify = "public bool Verify() { return " + string.Join(" && ", Enumerable.Range(0, 500).Select(i =>
            i < 250 ? "P" + i : "values[" + (i - 250) + "]")) + ";}";
        var compilation = Generate(MasterModel(members + verify + Received), out var result);
        Check(!result.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error));
        Check(result.Results.SelectMany(r => r.GeneratedSources).SelectMany(s => s.SyntaxTree.GetRoot().DescendantNodes())
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.SwitchStatementSyntax>()
            .All(s => s.Sections.Sum(section => section.Labels.Count) <= 32), "Bounded nanoCLR switch tables.");
        using var stream = new System.IO.MemoryStream();
        var emitted = compilation.Emit(stream);
        Check(emitted.Success, string.Join("\n", emitted.Diagnostics));
        var assembly = System.Reflection.Assembly.Load(stream.ToArray());
        var type = assembly.GetType("Model");
        var model = Activator.CreateInstance(type);
        var binding = (IDnp3MasterHandler)Activator.CreateInstance(type.GetNestedType("Dnp3MasterBinding"), model, new object());
        var records = Enumerable.Range(0, 500).Select(i => Measurement(Dnp3PointType.Binary, (ushort)(i * 131), Dnp3Value.Bits(1))).ToArray();
        binding.OnMeasurements(records, default);
        Check((bool)type.GetMethod("Verify").Invoke(model, null), "All 500 sparse points updated.");
        binding.OnMeasurements(new[] { Measurement(Dnp3PointType.Binary, 1, Dnp3Value.Bits(0)) }, default);
        Check((bool)type.GetMethod("Verify").Invoke(model, null), "Unmapped sparse point does not alias a slot.");
    }
    private static void MasterWire()
    {
        var model = new Receiver();
        var binding = new Receiver.Dnp3MasterBinding(model, new object());
        var master = new Dnp3Master(2, 1, binding);
        var db = new Dnp3Database(new[] { new Dnp3PointDefinition(Dnp3PointType.Analog, 0, Dnp3Value.Analog(1), staticVariation: 6, eventVariation: 8) });
        var station = new Dnp3Station(db, 1, 2, fragmentSize: 64);
        Exchange(master, station, master.ReadClasses(1, 0), 0);
        Check(model.Analog == 1 && model.Events == 0 && model.Info.Final);
        db.Update(Dnp3PointType.Analog, 0, Dnp3Value.Analog(2, 4, 100));
        db.Update(Dnp3PointType.Analog, 0, Dnp3Value.Analog(3, 8, 100));
        Exchange(master, station, master.ReadClasses(2, 1), 1);
        Check(model.Analog == 3 && model.Events == 2 && model.Time == 100 && db.EventCount == 0);
        Exchange(master, station, master.SetUnsolicited(2, true, 2), 2);
        db.Update(Dnp3PointType.Analog, 0, Dnp3Value.Analog(4, 1, 99));
        byte[] unsolicited = station.Poll(3);
        Check(unsolicited != null);
        Exchange(master, station, unsolicited, 3, true);
        unsolicited = station.Poll(3);
        if (unsolicited != null) Exchange(master, station, unsolicited, 3, true);
        Check(model.Analog == 4 && model.Info.Unsolicited && model.Events == 3 && db.EventCount == 0);
        master.ResetConnection();
        Exchange(master, station, master.ReadClasses(1, 4), 4);
        Check(model.Analog == 4 && model.Time == 0 && !model.Info.Unsolicited, "Reconnect preserves model until real Static refresh.");
        model.Fail = true;
        db.Update(Dnp3PointType.Analog, 0, Dnp3Value.Analog(5));
        byte[] frames = station.Process(master.ReadClasses(2, 5), 5);
        Throws<InvalidOperationException>(() => master.Process(frames, 5));
        Check(db.EventCount == 1, "Callback failure does not return Confirmation.");
    }
    private static void Exchange(Dnp3Master master, Dnp3Station station, byte[] frames, long now, bool toMaster = false)
    {
        for (int guard = 0; frames != null; guard++, toMaster = !toMaster)
        {
            Check(guard < 50, "Exchange bounded.");
            byte[] output = null;
            for (int p = 0; p < frames.Length;)
            {
                int size = frames[p + 2] + 5;
                size += (size - 10 + 15) / 16 * 2;
                var frame = new byte[size];
                Array.Copy(frames, p, frame, 0, size);
                p += size;
                byte[] next = toMaster ? master.Process(frame, now) : station.Process(frame, now);
                output = Dnp3Channel.Join(output, next);
            }
            frames = output;
        }
    }
}

[Dnp3MasterMap]
internal partial class Receiver
{
    [Dnp3Point(Dnp3PointType.Binary, 0)] public bool Binary { get; private set; }
    [Dnp3Point(Dnp3PointType.DoubleBitBinary, 0)] public byte Double;
    [Dnp3Point(Dnp3PointType.Counter, 0)] public uint Counter;
    [Dnp3Point(Dnp3PointType.FrozenCounter, 0)] public uint Frozen;
    [Dnp3Point(Dnp3PointType.Analog, 0, Flags = nameof(Flags), Timestamp = nameof(Time))] public double Analog { get; private set; }
    [Dnp3Point(Dnp3PointType.BinaryOutputStatus, 0)] public bool Output;
    [Dnp3Point(Dnp3PointType.AnalogOutputStatus, 65535)] public float Setpoint;
    public byte Flags { get; private set; }
    public long Time { get; private set; }
    public int Events, Calls;
    public bool Fail;
    public Dnp3Measurement[] Last;
    public Dnp3ResponseInfo Info;
    [Dnp3Received]
    private void Received(Dnp3Measurement[] measurements, Dnp3ResponseInfo response)
    {
        if (Fail) throw new InvalidOperationException("receive");
        Calls++;
        Last = measurements;
        Info = response;
        for (int i = 0; i < measurements.Length; i++) if (measurements[i].IsEvent) Events++;
    }

}

[Dnp3MasterMap]
internal partial class ThrowingSetter
{
    [Dnp3Point(Dnp3PointType.Analog, 0)] private double Value { set { throw new InvalidOperationException("setter"); } }
    [Dnp3Received] private void Received(Dnp3Measurement[] measurements, Dnp3ResponseInfo response) { }
}
