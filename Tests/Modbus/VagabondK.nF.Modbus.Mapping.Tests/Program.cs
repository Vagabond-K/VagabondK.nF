using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using VagabondK.nF.Modbus;
using VagabondK.nF.Modbus.Client;
using VagabondK.nF.Modbus.Mapping.Generator;
using VagabondK.nF.Modbus.Mapping.Sample;
using VagabondK.nF.Modbus.Server;

// Roslyn으로 매핑 선언을 컴파일한 뒤 생성 API의 형식·주소 정책과 실제 읽기/쓰기 결과를 검증합니다.
// 메모리 peer를 사용하므로 보드 배선이나 TCP/UART 타이밍 검증은 포함하지 않습니다.
internal static class Program
{
    private static int assertions;
    private static readonly CSharpParseOptions ParseOptions = new CSharpParseOptions(LanguageVersion.CSharp7_3);
    private static readonly MetadataReference[] References =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")).Split(Path.PathSeparator)
        .Select(p => MetadataReference.CreateFromFile(p)).Cast<MetadataReference>()
        .Append(MetadataReference.CreateFromFile(typeof(ModbusClient).Assembly.Location)).ToArray();

    private static void Main()
    {
        AllFormats.Run();
        Diagnostics();
        ShorthandDeclarations();
        GeneratedShape();
        RoundTrip();
        ServerPolicy();
        NonThrowingServerValidation();
        ServerBoundaries();
        AllScalars();
        ClientScalars();
        Scaling();
        BufferLimits();
        ReadGroups();
        RangeOracle();
        VagabondK.nF.Modbus.Mapping.Sample.LoopbackSample.Main();
        Console.WriteLine("Modbus Mapping: " + assertions + " assertions passed.");
    }

    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static Compilation Generate(string source, out GeneratorDriverRunResult result, OptimizationLevel optimization = OptimizationLevel.Debug)
    {
        var compilation = CSharpCompilation.Create("Mapping_" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(source, ParseOptions) }, References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: optimization));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new ModbusMappingGenerator().AsSourceGenerator() }, parseOptions: ParseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out var diagnostics);
        result = driver.GetRunResult();
        Check(result.Results.All(r => r.Exception == null), "Generator must not throw: " +
            string.Join("\n", result.Results.Where(r => r.Exception != null).Select(r => r.Exception.ToString())));
        return updated;
    }

    private static string Model(string members, string role = "Client", string declaration = "public partial class Model")
    {
        return "using VagabondK.nF.Modbus.Mapping;\n[ModbusMap(ModbusMapRole." + role + ")]\n" +
            declaration + " {\n" + members + "\n}";
    }

    private static void Invalid(string source, string id)
    {
        Generate(source, out var result);
        Check(result.Diagnostics.Any(d => d.Id == id), "Expected " + id + ": " + source);
        Check(!result.Results.SelectMany(r => r.GeneratedSources).Any(s => s.HintName.EndsWith(".Modbus.g.cs")),
            "Invalid model must not emit implementation.");
    }

    // 부적절한 모델·형식·주소·그룹 이름은 진단으로 거부하고 잘못된 구현 코드를 생성하지 않아야 합니다.
    private static void Diagnostics()
    {
        string member = "[ModbusPoint(ModbusArea.HoldingRegister, 0)] public int Value {get;set;}";
        foreach (string group in new[] { "null", "\"1Fast\"", "\"Fast-Slow\"", "\"한글\"", "\" \"", "MissingConstant" })
            Invalid(Model(member.Replace(", 0)", ", 0, ReadGroup=" + group + ")")), "VKNF003");
        Invalid(Model(member.Replace(", 0)", ", 0, ReadGroup=\"Fast\")"), "Server"), "VKNF003");
        Invalid(Model(member.Replace(", 0)", ", 0, ReadGroup=\"Fast\")") + " public void ReadModbusGroupFast() {}"), "VKNF005");
        Invalid(Model(member.Replace(", 0)", ", 0, ReadGroup=\"Fast\")"), declaration: "public partial class ReadModbusGroupFast"), "VKNF005");
        var groupNames = Generate(Model(
            "[ModbusPoint(ModbusArea.HoldingRegister,0,ReadGroup=\"class\")] public ushort A;" +
            "[ModbusPoint(ModbusArea.HoldingRegister,1,ReadGroup=\"Fast_1\")] public ushort B;" +
            "[ModbusPoint(ModbusArea.HoldingRegister,2,ReadGroup=\"fast_1\")] public ushort C;" +
            "[ModbusPoint(ModbusArea.HoldingRegister,3,ReadGroup=\"\")] public ushort D;"), out var groupResult);
        NoErrors(groupNames);
        Check(Implementation(groupResult).Contains("ReadModbusGroupclass") &&
            Implementation(groupResult).Contains("ReadModbusGroupFast_1") &&
            Implementation(groupResult).Contains("ReadModbusGroupfast_1") &&
            !Implementation(groupResult).Contains("ReadModbusGroup("), "Group names are case sensitive and empty means full-only.");
        Invalid(Model(member, declaration: "public class Model"), "VKNF001");
        Invalid(Model(member, declaration: "public partial class Model<T>"), "VKNF001");
        Invalid(Model(member, declaration: "public abstract partial class Model"), "VKNF001");
        Invalid(Model(member, declaration: "public partial struct Model"), "VKNF001");
        Invalid(Model(member, declaration: "public partial class Model : System.Exception"), "VKNF001");
        Invalid(Model(""), "VKNF002");
        Invalid(Model(member.Replace("int Value", "string Value")), "VKNF002");
        Invalid(Model(member.Replace("{get;set;}", "{get;}")), "VKNF002");
        Invalid(Model(member.Replace("public int", "public static int")), "VKNF002");
        Invalid(Model("[ModbusPoint(ModbusArea.Coil, 0)] public int Value;"), "VKNF002");
        Invalid(Model("[ModbusPoint(ModbusArea.InputRegister, 0)] public bool Value;"), "VKNF002");
        Invalid(Model(member.Replace(", 0)", ", -1)")), "VKNF003");
        Invalid(Model(member.Replace(", 0)", ", 65535)")), "VKNF003");
        Invalid(Model(member.Replace(", 0)", ", 65536)")), "VKNF003");
        Invalid(Model(member.Replace("HoldingRegister, 0", "InputRegister, 0, Writable=true")), "VKNF003");
        Invalid(Model(member.Replace(", 0)", ", 0, WordOrder=(ModbusWordOrder)2)")), "VKNF003");
        Invalid(Model("[ModbusPoint(ModbusArea.HoldingRegister, 0, WordOrder=ModbusWordOrder.LowWordFirst)] public short Value;"), "VKNF003");
        Invalid(Model(member + "\n[ModbusPoint(ModbusArea.HoldingRegister, 1)] public short Other;"), "VKNF004");
        Invalid(Model(member + "\npublic void ReadModbus() {}"), "VKNF005");
        Invalid(Model(member, declaration: "public partial class ReadModbus"), "VKNF005");
        Invalid(Model(member).Replace("ModbusMapRole.Client", ""), "VKNF003");
        Invalid(Model(member).Replace("HoldingRegister, 0", "HoldingRegister, MissingConstant"), "VKNF003");
        Invalid("using VagabondK.nF.Modbus.Mapping; interface IValue { int Value {get;set;} } " +
            "[ModbusMap(ModbusMapRole.Client)] partial class Model : IValue {" +
            "[ModbusPoint(ModbusArea.HoldingRegister,0)] int IValue.Value {get;set;} }", "VKNF002");
        Invalid(Model(member, declaration: "public partial class Model").Replace("ModbusMapRole.Client", "(ModbusMapRole)2"), "VKNF003");
        Invalid(Model(member).Replace("ModbusMapRole.Client)", "ModbusMapRole.Client, MaxReadRegisters=0)"), "VKNF003");
        Invalid(Model(member).Replace("ModbusMapRole.Client)", "ModbusMapRole.Client, MaxReadRegisters=126)"), "VKNF003");
        Invalid(Model(member).Replace("ModbusMapRole.Client)", "ModbusMapRole.Client, MaxReadRegisters=1)"), "VKNF003");
        Invalid(Model(member).Replace("ModbusMapRole.Client)", "ModbusMapRole.Client, MaxReadBits=0)"), "VKNF003");
        Invalid(Model(member).Replace("ModbusMapRole.Client)", "ModbusMapRole.Client, MaxReadBits=2001)"), "VKNF003");
        Invalid(Model(member).Replace("ModbusMapRole.Client)", "ModbusMapRole.Client, BufferBudgetBytes=-1)"), "VKNF003");
        Invalid(Model(member, "Server").Replace("ModbusMapRole.Server)", "ModbusMapRole.Server, MaxReadRegisters=125)"), "VKNF003");
        var valid = Generate(Model("[ModbusPoint(ModbusArea.HoldingRegister, 65535)] public ushort @event {get;private set;}"), out _);
        NoErrors(valid);
        valid = Generate(Model("[ModbusPoint(ModbusArea.InputRegister, 0)] public int Value {get{return 42;}}", "Server"), out _);
        NoErrors(valid);
        valid = Generate(Model("[ModbusPoint(ModbusArea.Coil, 0)] public bool Value;", "Server"), out _);
        NoErrors(valid);
        valid = Generate(Model("[ModbusPoint(ModbusArea.Coil, 0, Writable=true)] private bool value;", "Client"), out _);
        NoErrors(valid);
        valid = Generate("namespace @event {" + Model(member, declaration: "public partial class @class") + "} ", out _);
        NoErrors(valid);
    }

    private static void NoErrors(Compilation compilation)
    {
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Check(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(e => e.ToString())));
    }

    private static string Implementation(GeneratorDriverRunResult result)
    {
        return string.Join("\n", result.Results.SelectMany(r => r.GeneratedSources)
            .Where(s => s.HintName.EndsWith(".Modbus.g.cs")).Select(s => s.SourceText.ToString()));
    }

    private static void ShorthandDeclarations()
    {
        string[] areas = { "Coil", "DiscreteInput", "HoldingRegister", "InputRegister" };
        foreach (string role in new[] { "Client", "Server" })
        foreach (string area in areas)
        {
            string options = area == "Coil" || area == "HoldingRegister" ? ", Writable=true" : "";
            string type = area == "Coil" || area == "DiscreteInput" ? "bool" : "int";
            string canonical = "[ModbusPoint(ModbusArea." + area + ", 10" + options + ")] public " + type + " Value;";
            string shorthand = "[" + area + "(10" + options + ")] public " + type + " Value;";
            var oldCompilation = Generate(Model(canonical, role), out var oldResult);
            var newCompilation = Generate(Model(shorthand, role), out var newResult);
            NoErrors(oldCompilation);
            NoErrors(newCompilation);
            Check(Implementation(oldResult) == Implementation(newResult), "Identical runtime implementation: " + role + "/" + area);
        }
        Invalid(Model("[HoldingRegister(65535)] public int V;"), "VKNF003");
        Invalid(Model("[Coil(0)] public int V;"), "VKNF002");
        Invalid(Model("[HoldingRegister(0)][InputRegister(0)] public int V;"), "VKNF002");
        Invalid(Model("[ModbusPoint(ModbusArea.HoldingRegister,0)][HoldingRegister(0)] public int V;"), "VKNF002");
        Invalid(Model("[InputRegister(0,Writable=true)] public int V;"), "VKNF003");
        const string grouped = "[ModbusPoint(ModbusArea.HoldingRegister,0,ReadGroup=\"Fast\",WordOrder=ModbusWordOrder.LowWordFirst,ByteOrder=ModbusByteOrder.LowByteFirst)] public int V;";
        Generate(Model(grouped), out var before);
        NoErrors(Generate(Model(grouped.Replace("ModbusPoint(ModbusArea.HoldingRegister,", "HoldingRegister(")), out var after));
        Check(Implementation(before) == Implementation(after), "Shorthand preserves groups, buffer limits and byte/word order.");
    }

    private static void GeneratedShape()
    {
        var members = new StringBuilder();
        for (int i = 0; i < 124; i++)
            members.Append("[ModbusPoint(ModbusArea.HoldingRegister, ").Append(i).Append(")] public ushort R").Append(i).AppendLine(";");
        members.AppendLine("[ModbusPoint(ModbusArea.HoldingRegister, 124)] public long Wide;");
        members.AppendLine("[ModbusPoint(ModbusArea.HoldingRegister, 130)] public short Gap;");
        for (int i = 0; i < 2001; i++)
            members.Append("[ModbusPoint(ModbusArea.Coil, ").Append(i).Append(")] public bool B").Append(i).AppendLine(";");
        var compilation = Generate(Model(members.ToString()), out var result);
        NoErrors(compilation);
        string code = Implementation(result);
        Check(code.Contains("ReadHoldingRegisters(unitId, 0, 124,"), "Don't split 64-bit value at read boundary.");
        Check(code.Contains("ReadHoldingRegisters(unitId, 124, 4,"), "Second register request.");
        Check(code.Contains("ReadHoldingRegisters(unitId, 130, 1,"), "Don't merge holes.");
        Check(code.Contains("ReadCoils(unitId, 0, 2000,"), "Coil maximum.");
        Check(code.Contains("ReadCoils(unitId, 2000, 1,"), "Coil remainder.");
        Check(code.Contains("new ushort[124]") && code.Contains("new bool[2000]"), "Exact maximum request buffers.");
        Check(!code.Contains(".Server.") && !code.Contains("Reflection") && !code.Contains("Dictionary"), "Client dependencies.");

        Generate(Model("[ModbusPoint(ModbusArea.Coil, 0)] public bool Flag;", "Server"), out result);
        code = Implementation(result);
        Check(!code.Contains(".Client.") && !code.Contains(".Data.") && !code.Contains("new ushort["), "Bool-only server dependencies.");
        Generate(Model("[ModbusPoint(ModbusArea.Coil, 0)] public bool Flag;"), out result);
        code = Implementation(result);
        Check(!code.Contains(".Data.") && !code.Contains("new ushort["), "Bool-only client dependencies.");
        var first = result.Results.SelectMany(r => r.GeneratedSources).Select(s => s.SourceText.ToString()).ToArray();
        Generate(Model("[ModbusPoint(ModbusArea.Coil, 0)] public bool Flag;"), out result);
        Check(first.SequenceEqual(result.Results.SelectMany(r => r.GeneratedSources).Select(s => s.SourceText.ToString())), "Deterministic output.");
    }

    private static void RoundTrip()
    {
        var local = new LocalData { Temperature = 12.5f, Status = -123, Setpoint = int.MinValue, Enabled = true };
        var store = new LocalData.ModbusStore(local, new object());
        var loop = new Loopback(store);
        var client = new ModbusClient(loop);
        var remote = new RemoteData();
        remote.ReadModbus(client, 1);
        Check(remote.Enabled && remote.Temperature == 12.5f && remote.Status == -123 && remote.Setpoint == int.MinValue, "Read actual wire PDU.");
        Check(loop.Requests.SequenceEqual(new[] { "1:10:1", "3:200:2", "4:100:2", "4:102:1" }), "Contiguous values grouped within configured cap.");
        remote.Setpoint = -2;
        remote.WriteModbusSetpoint(client, 1);
        remote.Enabled = false;
        remote.WriteModbusEnabled(client, 1);
        Check(local.Setpoint == -2 && !local.Enabled, "Explicit writes through real server.");
        remote.Setpoint = 99;
        Check(local.Setpoint == -2, "No implicit setter transmission.");
        loop.FailInput = true;
        local.Enabled = true;
        try { remote.ReadModbus(client, 1); throw new InvalidOperationException("Missing failure."); }
        catch (TimeoutException) { }
        Check(remote.Enabled && remote.Setpoint == -2 && remote.Temperature == 12.5f, "Earlier read groups remain on failure.");
        loop.FailInput = false;
        for (int i = 0; i < 20; i++) remote.ReadModbus(client, 1);
        var registers = typeof(RemoteData).GetField("__modbusRegisters", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(remote);
        var bits = typeof(RemoteData).GetField("__modbusBits", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(remote);
        for (int i = 0; i < 100; i++) remote.ReadModbus(client, 1);
        Check(ReferenceEquals(registers, typeof(RemoteData).GetField("__modbusRegisters", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(remote)), "Register buffer reused.");
        Check(ReferenceEquals(bits, typeof(RemoteData).GetField("__modbusBits", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(remote)), "Bit buffer reused.");
    }

    private static void AddressFailure(Action action)
    {
        try { action(); }
        catch (ModbusException ex)
        {
            Check(ex.Code == ExceptionCode.IllegalDataAddress, "Address exception code.");
            return;
        }
        throw new InvalidOperationException("Expected IllegalDataAddress.");
    }

    private static void ServerPolicy()
    {
        var data = new LocalData { Setpoint = 0x12345678, Temperature = 12.5f };
        var store = new LocalData.ModbusStore(data, new object());
        var values = new ushort[4];
        store.ReadRegisters(false, 201, 1, values);
        Check(values[0] == 0x5678, "Partial scalar read permitted.");
        AddressFailure(() => store.WriteHoldingRegisters(200, 1, new ushort[] { 0 }));
        AddressFailure(() => store.WriteHoldingRegisters(201, 1, new ushort[] { 0 }));
        AddressFailure(() => store.WriteHoldingRegisters(200, 3, new ushort[] { 0, 0, 0 }));
        Check(data.Setpoint == 0x12345678, "Invalid full range never changes model.");
        values[0] = 0xAAAA;
        AddressFailure(() => store.ReadRegisters(false, 200, 3, values));
        Check(values[0] == 0xAAAA, "Validate full read range before modifying buffer.");
        AddressFailure(() => store.ReadRegisters(false, 65535, 2, values));
        AddressFailure(() => store.ReadRegisters(false, 200, 0, values));
        var packet = new Loopback(store);
        var client = new ModbusClient(packet);
        AddressFailure(() => client.WriteSingleRegister(1, 200, 0));
        Check(data.Setpoint == 0x12345678, "Wire exception for partial writes.");
        AddressFailure(() => client.ReadHoldingRegisters(1, 199, 2, values, 0));

        var policy = new PolicyModel();
        IModbusDataStore policyStore = new PolicyModel.ModbusStore(policy, new object());
        AddressFailure(() => policyStore.WriteHoldingRegisters(0, 3, new ushort[] { 1, 2, 3 }));
        Check(policy.First == 0 && policy.Second == 0, "Read-only range checked before any setter.");
        try { policyStore.WriteHoldingRegisters(0, 2, new ushort[] { 10, 13 }); }
        catch (InvalidOperationException ex) { Check(ex.Message == "Rejected setter.", "Original setter exception."); }
        Check(policy.First == 10 && policy.Second == 0, "Setter failure is propagated; earlier setters are not rolled back.");
        try
        {
            new ModbusClient(new Loopback(policyStore)).WriteMultipleRegisters(1, 0, 2, new ushort[] { 11, 13 }, 0);
            throw new InvalidOperationException("Missing wire setter failure.");
        }
        catch (InvalidOperationException ex) { Check(ex.Message == "Rejected setter.", "Wire path preserves unexpected setter failure."); }
        Check(policy.First == 11 && policy.Second == 0, "Wire validation does not add setter rollback.");
        var reusable = new ushort[] { 1, 2 };
        for (int i = 0; i < 20; i++) policyStore.WriteHoldingRegisters(0, 2, reusable);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++)
        {
            policyStore.WriteHoldingRegisters(0, 2, reusable);
            policyStore.ReadRegisters(false, 0, 2, reusable);
        }
        Check(GC.GetAllocatedBytesForCurrentThread() == before, "Generated scalar mapping has no per-call managed allocation on PC.");
    }

    private static void AllScalars()
    {
        var model = new ScalarModel();
        var store = new ScalarModel.ModbusStore(model, new object());
        var values = new ushort[4];
        var random = new Random(7331);
        for (int i = 0; i < 1000; i++)
        {
            random.NextBytes(model.Bytes);
            ulong raw = BitConverter.ToUInt64(model.Bytes, 0);
            for (int format = 0; format < 8; format++)
            {
                int width = format < 2 ? 1 : format < 5 ? 2 : 4;
                for (int j = 0; j < width; j++) values[j] = (ushort)(raw >> (16 * j));
                store.WriteHoldingRegisters((ushort)(format * 4), (ushort)width, values);
                var expected = new byte[8];
                bool lowWord = format == 2 || format == 5 || format == 7;
                bool lowByte = format == 1 || format == 3 || format == 6 || format == 7;
                for (int word = 0; word < width; word++)
                {
                    int sourceWord = lowWord ? width - 1 - word : word;
                    ushort value = values[sourceWord];
                    int offset = (width - word - 1) * 2;
                    expected[offset] = (byte)(lowByte ? value >> 8 : value);
                    expected[offset + 1] = (byte)(lowByte ? value : value >> 8);
                }
                switch (format)
                {
                    case 0: Check(model.I16 == BitConverter.ToInt16(expected), "Int16 oracle."); break;
                    case 1: Check(model.U16 == BitConverter.ToUInt16(expected), "UInt16 oracle."); break;
                    case 2: Check(model.I32 == BitConverter.ToInt32(expected), "Int32 oracle."); break;
                    case 3: Check(model.U32 == BitConverter.ToUInt32(expected), "UInt32 oracle."); break;
                    case 4: Check(model.F32.Equals(BitConverter.ToSingle(expected)), "Single oracle."); break;
                    case 5: Check(model.I64 == BitConverter.ToInt64(expected), "Int64 oracle."); break;
                    case 6: Check(model.U64 == BitConverter.ToUInt64(expected), "UInt64 oracle."); break;
                    case 7: Check(model.F64.Equals(BitConverter.ToDouble(expected)), "Double oracle."); break;
                }
                var actual = new ushort[4];
                store.ReadRegisters(false, (ushort)(format * 4), (ushort)width, actual);
                // NaN 페이로드 정규화는 매핑 계층이 아닌 RegisterConvert의 책임입니다.
                bool nan = (format == 4 && float.IsNaN(model.F32)) || (format == 7 && double.IsNaN(model.F64));
                for (int j = 0; j < width; j++)
                    Check(nan || values[j] == actual[j], "Scalar word/byte round trip " + format);
            }
        }
    }

    private static void NonThrowingServerValidation()
    {
        var wire = new ServerTransport { Context = new FrameContext(1, 0) };
        var server = new ModbusServer(1, wire, new RejectingStore());
        byte[][] requests = {
            new byte[] { 127 },
            new byte[] { 3, 0, 0, 0, 0 },
            new byte[] { 3, 255, 255, 0, 2 },
            new byte[] { 5, 0, 0, 0, 1 },
            new byte[] { 6, 0, 0, 0, 1 },
            new byte[] { 16, 0, 0, 0, 1, 1, 0 },
            new byte[] { 16, 0, 0, 0, 1, 2, 0, 0 },
            new byte[] { 15, 0, 0, 0, 1, 1, 0 },
            new byte[] { 1, 0, 0, 0, 1 },
            new byte[] { 2, 0, 0, 0, 1 },
            new byte[] { 4, 0, 0, 0, 1 }
        };
        byte[] codes = { 1, 3, 2, 3, 2, 3, 2, 2, 2, 2, 2 };
        int thrown = 0;
        EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> observe = (_, e) =>
        {
            if (e.Exception is ModbusException) thrown++;
        };
        AppDomain.CurrentDomain.FirstChanceException += observe;
        try
        {
            for (int i = 0; i < requests.Length; i++)
            {
                Array.Copy(requests[i], wire.Request, requests[i].Length);
                wire.Count = requests[i].Length;
                server.ProcessNextRequest();
                Check(wire.ResponseCount == 2 && wire.Response[0] == (requests[i][0] | 0x80) &&
                    wire.Response[1] == codes[i], "Expected exception response without invoking store.");
            }
            Check(thrown == 0, "Expected request rejection must not throw even an internally caught ModbusException.");
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= observe; }
        var broadcast = new ServerTransport { Serial = true, Context = new FrameContext(0, 0), Count = 5 };
        Array.Copy(new byte[] { 6, 0, 0, 0, 1 }, broadcast.Request, 5);
        var serialServer = new ModbusServer(1, broadcast, new RejectingStore());
        AddressFailure(() => serialServer.ProcessNextRequest());
        Check(broadcast.ResponseCount == 0, "Rejected serial broadcast still propagates error without wire response.");
    }

    private sealed class RejectingStore : IModbusDataStore, IModbusDataStoreValidator
    {
        public bool IsValidRange(FunctionCode function, ushort address, ushort count) { return false; }
        public void ReadBits(bool discreteInputs, ushort address, ushort count, bool[] values) { throw new InvalidOperationException("Rejected read reached store."); }
        public void ReadRegisters(bool inputRegisters, ushort address, ushort count, ushort[] values) { throw new InvalidOperationException("Rejected read reached store."); }
        public void WriteCoils(ushort address, ushort count, bool[] values) { throw new InvalidOperationException("Rejected write reached store."); }
        public void WriteHoldingRegisters(ushort address, ushort count, ushort[] values) { throw new InvalidOperationException("Rejected write reached store."); }
    }

    private static void ServerBoundaries()
    {
        var compilation = Generate(Model(
            "[ModbusPoint(ModbusArea.Coil,10,Writable=true)] public bool A;" +
            "[ModbusPoint(ModbusArea.Coil,11,Writable=true)] public bool B;" +
            "[ModbusPoint(ModbusArea.Coil,12)] public readonly bool C;" +
            "[ModbusPoint(ModbusArea.DiscreteInput,20)] public bool State {get{return true;}}" +
            "[ModbusPoint(ModbusArea.HoldingRegister,65531,Writable=true)] public ushort W;" +
            "[ModbusPoint(ModbusArea.HoldingRegister,65532,Writable=true)] public int X;" +
            "[ModbusPoint(ModbusArea.HoldingRegister,65534,Writable=true)] public short Y;" +
            "[ModbusPoint(ModbusArea.HoldingRegister,65535,Writable=true)] public ushort Z;", "Server"), out _);
        NoErrors(compilation);
        using var stream = new MemoryStream();
        Check(compilation.Emit(stream).Success, "Emit boundary model.");
        var type = Assembly.Load(stream.ToArray()).GetType("Model");
        var model = Activator.CreateInstance(type);
        var store = (IModbusDataStore)Activator.CreateInstance(type.GetNestedType("ModbusStore"), model, new object());
        var bits = new bool[] { true, true, true };
        var validator = (IModbusDataStoreValidator)store;
        Check(validator.IsValidRange(FunctionCode.ReadCoils, 10, 3), "Coil read validator includes readonly.");
        Check(validator.IsValidRange(FunctionCode.WriteMultipleCoils, 10, 2), "Writable coil range validator.");
        Check(!validator.IsValidRange(FunctionCode.WriteMultipleCoils, 10, 3), "Readonly coil range validator.");
        Check(!validator.IsValidRange(FunctionCode.WriteSingleCoil, 10, 2), "Single coil validator count.");
        Check(validator.IsValidRange(FunctionCode.ReadDiscreteInputs, 20, 1), "Discrete Input validator.");
        Check(!validator.IsValidRange(FunctionCode.ReadInputRegisters, 20, 1), "Wrong area validator.");
        Check(!validator.IsValidRange((FunctionCode)127, 10, 1), "Unsupported function validator.");
        Check(!validator.IsValidRange(FunctionCode.WriteSingleRegister, 65532, 1), "Partial single register validator.");
        Check(!validator.IsValidRange(FunctionCode.WriteSingleRegister, 65531, 2), "Single register validator count.");
        Check(validator.IsValidRange(FunctionCode.ReadHoldingRegisters, 65535, 1), "Last address validator.");
        Check(!validator.IsValidRange(FunctionCode.ReadHoldingRegisters, 65535, 2), "Overflow validator.");
        AddressFailure(() => store.WriteCoils(10, 3, bits));
        Check(!(bool)type.GetField("A").GetValue(model), "Readonly coil validated before earlier coil mutation.");
        store.WriteCoils(10, 2, bits);
        Array.Clear(bits);
        store.ReadBits(false, 10, 3, bits);
        Check(bits[0] && bits[1] && !bits[2], "Coil grouping.");
        store.ReadBits(true, 20, 1, bits);
        Check(bits[0], "Discrete Input accessor.");
        AddressFailure(() => store.ReadBits(true, 10, 1, bits));
        var values = new ushort[] { 0x8000, 0xFFFF, 0xFFFE, 0x8000, 0xFFFF };
        AddressFailure(() => store.WriteHoldingRegisters(65531, 2, values));
        Check((ushort)type.GetField("W").GetValue(model) == 0, "Partial trailing scalar checked before first setter.");
        store.WriteHoldingRegisters(65531, 5, values);
        Check((ushort)type.GetField("W").GetValue(model) == 0x8000 &&
            (int)type.GetField("X").GetValue(model) == -2 &&
            (short)type.GetField("Y").GetValue(model) == short.MinValue &&
            (ushort)type.GetField("Z").GetValue(model) == ushort.MaxValue, "Full multi-scalar write at address limit.");
        store.ReadRegisters(false, 65535, 1, values);
        Check(values[0] == ushort.MaxValue, "Last address read.");
        AddressFailure(() => store.WriteHoldingRegisters(65535, 2, values));
    }

    private static void ClientScalars()
    {
        var members = new StringBuilder();
        var types = new[] { "short", "ushort", "int", "uint", "float", "long", "ulong", "double" };
        for (int i = 0; i < types.Length; i++)
            members.Append("[ModbusPoint(ModbusArea.HoldingRegister, ").Append(i * 4)
                .Append(", Writable=true)] public ").Append(types[i]).Append(" V").Append(i).AppendLine(" {get;set;}");
        var compilation = Generate(Model(members.ToString()), out _);
        NoErrors(compilation);
        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        Check(emit.Success, string.Join("\n", emit.Diagnostics));
        var type = Assembly.Load(stream.ToArray()).GetType("Model");
        var remote = Activator.CreateInstance(type);
        var local = new ScalarModel
        {
            I16 = short.MinValue,
            U16 = ushort.MaxValue,
            I32 = int.MinValue,
            U32 = uint.MaxValue,
            F32 = -12.5f,
            I64 = long.MinValue,
            U64 = ulong.MaxValue,
            F64 = -12345.125
        };
        // 이 서버는 워드 순서가 다르므로 전송하는 원시 값은 서버의 주소 맵을 따라야 합니다.
        var store = new ScalarModel.ModbusStore(local, new object());
        var client = new ModbusClient(new Loopback(store));
        type.GetMethod("ReadModbus").Invoke(remote, new object[] { client, (byte)1 });
        var raw = new ushort[4];
        for (int i = 0; i < types.Length; i++)
        {
            int width = i < 2 ? 1 : i < 5 ? 2 : 4;
            store.ReadRegisters(false, (ushort)(i * 4), (ushort)width, raw);
            object expected;
            switch (i)
            {
                case 0: expected = RegisterConvert.ReadInt16(raw, 0); break;
                case 1: expected = RegisterConvert.ReadUInt16(raw, 0); break;
                case 2: expected = RegisterConvert.ReadInt32(raw, 0); break;
                case 3: expected = RegisterConvert.ReadUInt32(raw, 0); break;
                case 4: expected = RegisterConvert.ReadSingle(raw, 0); break;
                case 5: expected = RegisterConvert.ReadInt64(raw, 0); break;
                case 6: expected = RegisterConvert.ReadUInt64(raw, 0); break;
                default: expected = RegisterConvert.ReadDouble(raw, 0); break;
            }
            Check(expected.Equals(type.GetProperty("V" + i).GetValue(remote)), "Client scalar " + i);
            type.GetMethod("WriteModbusV" + i).Invoke(remote, new object[] { client, (byte)1 });
            var after = new ushort[4];
            store.ReadRegisters(false, (ushort)(i * 4), (ushort)width, after);
            for (int j = 0; j < width; j++) Check(raw[j] == after[j], "Client explicit scalar write " + i);
        }
    }
    private static void Scaling()
    {
        foreach (int count in new[] { 8, 32, 128 })
        {
            var members = new StringBuilder();
            for (int i = 0; i < count; i++)
                members.Append("[ModbusPoint(ModbusArea.HoldingRegister, ").Append(i)
                    .Append(", Writable=true)] public ushort R").Append(i).AppendLine(";");
            var compilation = Generate(Model(members.ToString(), "Server"), out _);
            NoErrors(compilation);
            using var stream = new MemoryStream();
            Check(compilation.Emit(stream).Success, "Emit scaling model.");
            var type = Assembly.Load(stream.ToArray()).GetType("Model");
            var storeType = type.GetNestedType("ModbusStore");
            var model = Activator.CreateInstance(type);
            var store = (IModbusDataStore)Activator.CreateInstance(storeType, model, new object());
            var values = new ushort[Math.Min(count, 123)];
            for (int i = 0; i < values.Length; i++) values[i] = (ushort)(i + 1);
            for (int i = 0; i < 20; i++)
            {
                store.WriteHoldingRegisters(0, (ushort)values.Length, values);
                store.ReadRegisters(false, 0, (ushort)values.Length, values);
            }
            var timer = System.Diagnostics.Stopwatch.StartNew();
            // 매핑 자체의 할당량을 측정하므로 Stopwatch 할당은 제외합니다.
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++)
            {
                store.WriteHoldingRegisters(0, (ushort)values.Length, values);
                store.ReadRegisters(false, 0, (ushort)values.Length, values);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            timer.Stop();
            Check(allocated == 0, "Scaling mapping allocation.");
            for (int i = 0; i < values.Length; i++)
                Check((ushort)type.GetField("R" + i).GetValue(model) == i + 1, "Scaling mapped values.");
            var methods = storeType.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            int ilBytes = methods.Sum(m => m.GetMethodBody()?.GetILAsByteArray().Length ?? 0);
            int locals = storeType.GetMethod("WriteHoldingRegisters").GetMethodBody().LocalVariables.Count;
            Console.WriteLine("SCALING points=" + count + " IL=" + ilBytes + " writeLocals=" + locals +
                " allocated=" + allocated + " elapsedMs=" + timer.ElapsedMilliseconds + " (PC, 10000 read/write pairs)");
            var optimized = Generate(Model(members.ToString(), "Server"), out var result, OptimizationLevel.Release);
            using var optimizedStream = new MemoryStream();
            Check(optimized.Emit(optimizedStream).Success, "Emit release scaling model.");
            var optimizedStore = Assembly.Load(optimizedStream.ToArray()).GetType("Model").GetNestedType("ModbusStore");
            Check(optimizedStore.GetMethod("WriteHoldingRegisters").GetMethodBody().LocalVariables.Count <= 4,
                "Release write locals must not grow with mapped points.");
            Check(optimizedStore.GetMethod("ReadRegisters").GetMethodBody().LocalVariables.Count <= 8,
                "Release read locals must not grow with mapped points.");
            Console.WriteLine("RELEASE points=" + count +
                " readLocals=" + optimizedStore.GetMethod("ReadRegisters").GetMethodBody().LocalVariables.Count +
                " writeLocals=" + optimizedStore.GetMethod("WriteHoldingRegisters").GetMethodBody().LocalVariables.Count);
            Check(!Implementation(result).Contains("while (") && !Implementation(result).Contains("v_R"),
                "No restarted point search or per-member staging locals.");
        }
    }

    private static void BufferLimits()
    {
        var members = new StringBuilder();
        for (int i = 0; i < 9; i++)
            members.Append("[ModbusPoint(ModbusArea.Coil,").Append(i).Append(")] public bool B").Append(i).AppendLine(";");
        for (int i = 0; i < 6; i++)
            members.Append("[ModbusPoint(ModbusArea.HoldingRegister,").Append(i * 2)
                .Append(",Writable=true)] public int R").Append(i).AppendLine(";");
        string source = Model(members.ToString()).Replace("ModbusMapRole.Client)",
            "ModbusMapRole.Client, MaxReadRegisters=4, MaxReadBits=3, BufferBudgetBytes=11)");
        var compilation = Generate(source, out var result);
        NoErrors(compilation);
        string code = Implementation(result);
        Check(code.Contains("readRequests=6") && code.Contains("registerBufferLength=4") &&
            code.Contains("bitBufferLength=3") && code.Contains("bufferPayloadBytes=11"), "Build-time cost report.");
        Check(!result.Diagnostics.Any(d => d.Id == "VKNF006"), "Exact budget permitted.");
        Generate(source.Replace("BufferBudgetBytes=11", "BufferBudgetBytes=10"), out result);
        Check(result.Diagnostics.Count(d => d.Id == "VKNF006" && d.Severity == DiagnosticSeverity.Warning) == 1,
            "Budget exceeded is an explicit warning.");
        Generate(source.Replace("BufferBudgetBytes=11", "BufferBudgetBytes=0"), out result);
        Check(!result.Diagnostics.Any(d => d.Id == "VKNF006"), "Zero disables budget warning.");
        var smaller = Generate(source.Replace("MaxReadRegisters=4", "MaxReadRegisters=3")
            .Replace("MaxReadBits=3", "MaxReadBits=1"), out result);
        NoErrors(smaller);
        Check(Implementation(result).Contains("readRequests=15") &&
            Implementation(result).Contains("registerBufferLength=2") &&
            Implementation(result).Contains("bitBufferLength=1"), "Never split scalar to fill a request cap; allocate actual request size.");
        var single = Generate(Model("[ModbusPoint(ModbusArea.HoldingRegister,65535)] public ushort Last;")
            .Replace("ModbusMapRole.Client)", "ModbusMapRole.Client, MaxReadRegisters=1, MaxReadBits=1, BufferBudgetBytes=2)"), out result);
        NoErrors(single);
        Check(Implementation(result).Contains("ReadHoldingRegisters(unitId, 65535, 1,") &&
            Implementation(result).Contains("bufferPayloadBytes=2"), "Minimum request cap at maximum address.");
        using var stream = new MemoryStream();
        Check(compilation.Emit(stream).Success, "Emit capped client.");
        var type = Assembly.Load(stream.ToArray()).GetType("Model");
        var model = Activator.CreateInstance(type);
        var store = new LinearStore();
        var loop = new Loopback(store);
        var client = new ModbusClient(loop);
        type.GetMethod("ReadModbus").Invoke(model, new object[] { client, (byte)1 });
        Check(loop.Requests.SequenceEqual(new[] { "1:0:3", "1:3:3", "1:6:3", "3:0:4", "3:4:4", "3:8:4" }),
            "Configured request limits on real Client/Server PDU path.");
        for (int i = 0; i < 6; i++)
            Check((int)type.GetField("R" + i).GetValue(model) == ((i * 2) << 16 | (i * 2 + 1)), "Capped read decode.");
        for (int i = 0; i < 9; i++)
            Check((bool)type.GetField("B" + i).GetValue(model) == (i % 2 == 0), "Capped bit decode.");
        Check(((ushort[])type.GetField("__modbusRegisters", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(model)).Length == 4,
            "Configured register buffer length.");
        Check(((bool[])type.GetField("__modbusBits", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(model)).Length == 3,
            "Configured bit buffer length.");
        string server = Model("[ModbusPoint(ModbusArea.HoldingRegister,0,Writable=true)] public long Value;", "Server");
        Generate(server.Replace("ModbusMapRole.Server)", "ModbusMapRole.Server, BufferBudgetBytes=7)"), out result);
        Check(result.Diagnostics.Any(d => d.Id == "VKNF006") && Implementation(result).Contains("bufferPayloadBytes=8"),
            "Server scratch budget report.");
    }

    private static void RangeOracle()
    {
        int[] starts = { 1, 2, 4, 6, 10, 11 };
        int[] widths = { 1, 2, 1, 4, 1, 1 };
        bool[] writable = { true, true, false, true, true, false };
        var members = new StringBuilder();
        for (int i = 0; i < starts.Length; i++)
            members.Append("[ModbusPoint(ModbusArea.HoldingRegister,").Append(starts[i])
                .Append(",Writable=").Append(writable[i] ? "true" : "false").Append(")] public ")
                .Append(widths[i] == 1 ? "ushort" : widths[i] == 2 ? "int" : "long").Append(" R").Append(i).AppendLine(";");
        var compilation = Generate(Model(members.ToString(), "Server"), out _);
        NoErrors(compilation);
        using var stream = new MemoryStream();
        Check(compilation.Emit(stream).Success, "Emit range oracle model.");
        var type = Assembly.Load(stream.ToArray()).GetType("Model");
        var model = Activator.CreateInstance(type);
        var store = (IModbusDataStore)Activator.CreateInstance(type.GetNestedType("ModbusStore"), model, new object());
        for (int address = 0; address < 14; address++)
            for (int count = 0; count <= 14 - address; count++)
                foreach (bool write in new[] { false, true })
                {
                    int end = address + count;
                    bool valid = count > 0;
                    for (int word = address; word < end; word++)
                    {
                        int point = -1;
                        for (int i = 0; i < starts.Length; i++)
                            if (starts[i] <= word && word < starts[i] + widths[i])
                                point = i;
                        if (point < 0 || (write && (!writable[point] || starts[point] < address || starts[point] + widths[point] > end)))
                            valid = false;
                    }

                    var values = Enumerable.Repeat((ushort)0x1234, 14).ToArray();
                    var before = starts.Select((_, i) => type.GetField("R" + i).GetValue(model)).ToArray();
                    bool accepted = true;
                    Check(((IModbusDataStoreValidator)store).IsValidRange(
                        write ? FunctionCode.WriteMultipleRegisters : FunctionCode.ReadHoldingRegisters,
                        (ushort)address, (ushort)count) == valid, "Non-throwing validator matches range oracle.");
                    try
                    {
                        if (write) store.WriteHoldingRegisters((ushort)address, (ushort)count, values);
                        else store.ReadRegisters(false, (ushort)address, (ushort)count, values);
                    }
                    catch (ModbusException ex)
                    {
                        Check(ex.Code == ExceptionCode.IllegalDataAddress, "Oracle address exception.");
                        accepted = false;
                    }
                    Check(accepted == valid, "Range oracle address=" + address + " count=" + count + " write=" + write);
                    if (!accepted)
                    {
                        Check(values.All(v => v == 0x1234), "Rejected range leaves caller values intact.");
                        Check(before.SequenceEqual(starts.Select((_, i) => type.GetField("R" + i).GetValue(model))),
                            "Rejected range leaves model intact.");
                    }
                }
    }

    private static void ReadGroups()
    {
        string source = Model(
            "[ModbusPoint(ModbusArea.HoldingRegister,0,ReadGroup=\"Other\")] public ushort A;" +
            "[ModbusPoint(ModbusArea.HoldingRegister,1,ReadGroup=\"Fast\")] public int B;" +
            "[ModbusPoint(ModbusArea.HoldingRegister,3,ReadGroup=\"Fast\")] public int C;" +
            "[ModbusPoint(ModbusArea.HoldingRegister,5)] public ushort Ungrouped;" +
            "[ModbusPoint(ModbusArea.Coil,10,ReadGroup=\"Fast\")] public bool Enabled;" +
            "[ModbusPoint(ModbusArea.Coil,11,ReadGroup=\"Other\")] public bool Neighbor;" +
            "[ModbusPoint(ModbusArea.Coil,12,ReadGroup=\"Fast\")] public bool Tail;")
            .Replace("ModbusMapRole.Client)", "ModbusMapRole.Client, MaxReadRegisters=4, MaxReadBits=2)");
        var compilation = Generate(source, out var generated, OptimizationLevel.Release);
        NoErrors(compilation);
        using var stream = new MemoryStream();
        Check(compilation.Emit(stream).Success, "Emit read group model.");
        var type = Assembly.Load(stream.ToArray()).GetType("Model");
        var model = Activator.CreateInstance(type);
        var loop = new Loopback(new LinearStore());
        var client = new ModbusClient(loop);
        var registers = type.GetField("__modbusRegisters", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(model);
        type.GetMethod("ReadModbusGroupFast").Invoke(model, new object[] { client, (byte)1 });
        Check(loop.Requests.SequenceEqual(new[] { "1:10:1", "1:12:1", "3:1:2", "3:3:2" }),
            "Group never reads other points, gaps or beyond full-read buffer boundaries.");
        Check((int)type.GetField("B").GetValue(model) == 0x10002 &&
            (int)type.GetField("C").GetValue(model) == 0x30004, "Group decodes selected numeric values.");
        Check((bool)type.GetField("Enabled").GetValue(model) && (bool)type.GetField("Tail").GetValue(model), "Group bits.");
        Check((ushort)type.GetField("A").GetValue(model) == 0 &&
            (ushort)type.GetField("Ungrouped").GetValue(model) == 0, "Group leaves unselected fields intact.");
        Check(ReferenceEquals(registers, type.GetField("__modbusRegisters", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(model)) &&
            ((ushort[])registers).Length == 3, "Group reuses original full-read buffer without enlargement.");
        loop.Requests.Clear();
        type.GetMethod("ReadModbus").Invoke(model, new object[] { client, (byte)1 });
        Check(loop.Requests.SequenceEqual(new[] { "1:10:2", "1:12:1", "3:0:3", "3:3:3" }),
            "Full read grouping unchanged across read groups.");
        Check((ushort)type.GetField("Ungrouped").GetValue(model) == 5, "Full read includes ungrouped points.");
        Check(Implementation(generated).Contains("bufferPayloadBytes=8"), "Group adds no payload budget.");
        var remote = new RemoteData { Setpoint = 77 };
        var local = new LocalData { Temperature = 12.5f, Status = -3, Setpoint = 100, Enabled = true };
        var typedLoop = new Loopback(new LocalData.ModbusStore(local, new object()));
        var typedClient = new ModbusClient(typedLoop);
        remote.ReadModbusGroupStatus(typedClient, 1);
        Check(remote.Temperature == 12.5f && remote.Status == -3 && remote.Enabled && remote.Setpoint == 77,
            "Private setters and independent group state.");
        remote.ReadModbusGroupSettings(typedClient, 1);
        Check(remote.Setpoint == 100, "Settings group updates only settings.");
        local.Enabled = false;
        typedLoop.FailInput = true;
        try { remote.ReadModbusGroupStatus(typedClient, 1); throw new InvalidOperationException("Missing group timeout."); }
        catch (TimeoutException) { }
        Check(!remote.Enabled && remote.Temperature == 12.5f && remote.Setpoint == 100,
            "Group failure preserves previous successful request and other groups.");
        try { remote.ReadModbusGroupStatus(null, 1); throw new InvalidOperationException("Missing null client rejection."); }
        catch (ArgumentNullException) { }
        var allocationClient = new ModbusClient(new ZeroReadTransport());
        for (int i = 0; i < 100; i++)
        {
            remote.ReadModbusGroupStatus(allocationClient, 1);
            remote.ReadModbusGroupSettings(allocationClient, 1);
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            remote.ReadModbusGroupStatus(allocationClient, 1);
            remote.ReadModbusGroupSettings(allocationClient, 1);
        }
        Check(GC.GetAllocatedBytesForCurrentThread() == before, "Group read adds no per-call allocation on PC.");
    }

    private sealed class ZeroReadTransport : IModbusTransport
    {
        private FrameContext context;
        private byte function;
        private int bytes;
        public bool IsSerial => false;
        public void Send(FrameContext value, byte[] pdu, int count)
        {
            context = value;
            function = pdu[0];
            int length = Protocol.ReadUInt16(pdu, 3);
            bytes = function <= 2 ? (length + 7) / 8 : length * 2;
        }
        public int Receive(byte[] pdu, out FrameContext value)
        {
            value = context;
            pdu[0] = function;
            pdu[1] = (byte)bytes;
            Array.Clear(pdu, 2, bytes);
            return bytes + 2;
        }
    }

    private sealed class LinearStore : IModbusDataStore
    {
        public void ReadBits(bool discreteInputs, ushort address, ushort count, bool[] values)
        {
            for (int i = 0; i < count; i++) values[i] = (address + i) % 2 == 0;
        }

        public void ReadRegisters(bool inputRegisters, ushort address, ushort count, ushort[] values)
        {
            for (int i = 0; i < count; i++) values[i] = (ushort)(address + i);
        }

        public void WriteCoils(ushort address, ushort count, bool[] values) { throw new NotSupportedException(); }
        public void WriteHoldingRegisters(ushort address, ushort count, ushort[] values) { throw new NotSupportedException(); }
    }

    private sealed class Loopback : IModbusTransport
    {
        private readonly ModbusServer server;
        private readonly ServerTransport wire = new ServerTransport();
        public readonly List<string> Requests = new List<string>();
        public bool FailInput;
        public bool IsSerial => false;

        public Loopback(IModbusDataStore store)
        {
            server = new ModbusServer(1, wire, store);
        }

        public void Send(FrameContext context, byte[] pdu, int count)
        {
            if (pdu[0] == 4 && FailInput) throw new TimeoutException();
            Requests.Add(pdu[0] + ":" + ((pdu[1] << 8) | pdu[2]) + ":" + ((pdu[3] << 8) | pdu[4]));
            wire.Context = context;
            wire.Count = count;
            Array.Copy(pdu, wire.Request, count);
            server.ProcessNextRequest();
        }

        public int Receive(byte[] pdu, out FrameContext context)
        {
            context = wire.Context;
            Array.Copy(wire.Response, pdu, wire.ResponseCount);
            return wire.ResponseCount;
        }
    }

    private sealed class ServerTransport : IModbusTransport
    {
        public readonly byte[] Request = new byte[253];
        public readonly byte[] Response = new byte[253];
        public FrameContext Context;
        public int Count;
        public int ResponseCount;
        public bool Serial;
        public bool IsSerial => Serial;

        public void Send(FrameContext context, byte[] pdu, int count)
        {
            Context = context;
            Array.Copy(pdu, Response, count);
            ResponseCount = count;
        }

        public int Receive(byte[] pdu, out FrameContext context)
        {
            context = Context;
            Array.Copy(Request, pdu, Count);
            return Count;
        }
    }
}

[VagabondK.nF.Modbus.Mapping.ModbusMap(VagabondK.nF.Modbus.Mapping.ModbusMapRole.Server)]
internal partial class PolicyModel
{
    [VagabondK.nF.Modbus.Mapping.ModbusPoint(VagabondK.nF.Modbus.Mapping.ModbusArea.HoldingRegister, 0, Writable = true)]
    public ushort First { get; private set; }

    private ushort second;

    [VagabondK.nF.Modbus.Mapping.ModbusPoint(VagabondK.nF.Modbus.Mapping.ModbusArea.HoldingRegister, 1, Writable = true)]
    public ushort Second
    {
        get { return second; }
        private set
        {
            if (value == 13) throw new InvalidOperationException("Rejected setter.");
            second = value;
        }
    }

    [VagabondK.nF.Modbus.Mapping.ModbusPoint(VagabondK.nF.Modbus.Mapping.ModbusArea.HoldingRegister, 2)]
    public ushort ReadOnly { get { return 42; } }
}

[VagabondK.nF.Modbus.Mapping.ModbusMap(VagabondK.nF.Modbus.Mapping.ModbusMapRole.Server)]
internal partial class ScalarModel
{
    public byte[] Bytes = new byte[8];

    [VagabondK.nF.Modbus.Mapping.ModbusPoint(VagabondK.nF.Modbus.Mapping.ModbusArea.HoldingRegister, 0, Writable = true)]
    public short I16;

    [VagabondK.nF.Modbus.Mapping.ModbusPoint(VagabondK.nF.Modbus.Mapping.ModbusArea.HoldingRegister, 4, Writable = true, ByteOrder = VagabondK.nF.Modbus.Mapping.ModbusByteOrder.LowByteFirst)]
    public ushort U16;

    [VagabondK.nF.Modbus.Mapping.ModbusPoint(VagabondK.nF.Modbus.Mapping.ModbusArea.HoldingRegister, 8, Writable = true, WordOrder = VagabondK.nF.Modbus.Mapping.ModbusWordOrder.LowWordFirst)]
    public int I32;

    [VagabondK.nF.Modbus.Mapping.ModbusPoint(VagabondK.nF.Modbus.Mapping.ModbusArea.HoldingRegister, 12, Writable = true, ByteOrder = VagabondK.nF.Modbus.Mapping.ModbusByteOrder.LowByteFirst)]
    public uint U32;

    [VagabondK.nF.Modbus.Mapping.ModbusPoint(VagabondK.nF.Modbus.Mapping.ModbusArea.HoldingRegister, 16, Writable = true)]
    public float F32;

    [VagabondK.nF.Modbus.Mapping.ModbusPoint(VagabondK.nF.Modbus.Mapping.ModbusArea.HoldingRegister, 20, Writable = true, WordOrder = VagabondK.nF.Modbus.Mapping.ModbusWordOrder.LowWordFirst)]
    public long I64;

    [VagabondK.nF.Modbus.Mapping.ModbusPoint(VagabondK.nF.Modbus.Mapping.ModbusArea.HoldingRegister, 24, Writable = true, ByteOrder = VagabondK.nF.Modbus.Mapping.ModbusByteOrder.LowByteFirst)]
    public ulong U64;

    [VagabondK.nF.Modbus.Mapping.ModbusPoint(VagabondK.nF.Modbus.Mapping.ModbusArea.HoldingRegister, 28, Writable = true, WordOrder = VagabondK.nF.Modbus.Mapping.ModbusWordOrder.LowWordFirst, ByteOrder = VagabondK.nF.Modbus.Mapping.ModbusByteOrder.LowByteFirst)]
    public double F64;
}
