using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace VagabondK.nF.DNP3.Mapping.Generator
{
    /// <summary>Outstation 게시 및 Master 수신의 정적 객체 매핑 코드를 생성합니다.</summary>
    [Generator(LanguageNames.CSharp)]
    public sealed partial class Dnp3MappingGenerator : IIncrementalGenerator
    {
        private const string Prefix = "VagabondK.nF.DNP3.Mapping.";
        private const string R = "global::VagabondK.nF.DNP3.";
        private static readonly string[] Types = { "Binary", "DoubleBitBinary", "Counter", "FrozenCounter", "Analog", "BinaryOutputStatus", "AnalogOutputStatus" };
        private static readonly string[] PointAttributes = { "BinaryInput", "DoubleBitInput", "Counter", "FrozenCounter", "AnalogInput", "BinaryOutput", "AnalogOutput" };
        private static readonly DiagnosticDescriptor Shape = Error("VKDN001", "DNP3 매핑 클래스");
        private static readonly DiagnosticDescriptor Member = Error("VKDN002", "DNP3 매핑 멤버");
        private static readonly DiagnosticDescriptor Options = Error("VKDN003", "DNP3 매핑 옵션");
        private static readonly DiagnosticDescriptor Duplicate = Error("VKDN004", "DNP3 매핑 중복");
        private static readonly DiagnosticDescriptor Control = Error("VKDN005", "DNP3 제어 메서드");
        private static DiagnosticDescriptor Error(string id, string title)
        {
            return new DiagnosticDescriptor(id, title, "{0}", "Dnp3Mapping", DiagnosticSeverity.Error, true);
        }

        /// <summary>매핑 선언과 생성 출력을 등록합니다.</summary>
        /// <param name="context">컴파일러 초기화 컨텍스트입니다.</param>
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            context.RegisterPostInitializationOutput(c => c.AddSource("Dnp3Attributes.g.cs",
                SourceText.From(VagabondK.nF.Mapping.Generator.MappingAttributeSource.ForBuild(Attributes + MasterAttributes), Encoding.UTF8)));
            var models = context.SyntaxProvider.ForAttributeWithMetadataName(Prefix + "Dnp3MapAttribute",
                (node, _) => node is TypeDeclarationSyntax, (syntax, _) => (INamedTypeSymbol)syntax.TargetSymbol);
            context.RegisterSourceOutput(models.Collect(), (output, symbols) =>
            {
                var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
                int index = 0;
                foreach (var model in symbols.OrderBy(s => s.ToDisplayString(), StringComparer.Ordinal))
                    if (seen.Add(model)) Generate(output, model, "Dnp3Map" + (index++).ToString("D4", CultureInfo.InvariantCulture) + ".g.cs");
            });
            var masters = context.SyntaxProvider.ForAttributeWithMetadataName(Prefix + "Dnp3MasterMapAttribute",
                (node, _) => node is TypeDeclarationSyntax, (syntax, _) => (INamedTypeSymbol)syntax.TargetSymbol);
            context.RegisterSourceOutput(masters.Collect(), (output, symbols) =>
            {
                var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
                int index = 0;
                foreach (var model in symbols.OrderBy(s => s.ToDisplayString(), StringComparer.Ordinal))
                    if (seen.Add(model)) GenerateMaster(output, model, "Dnp3MasterMap" + (index++).ToString("D4", CultureInfo.InvariantCulture) + ".g.cs");
            });
        }

        private static void Generate(SourceProductionContext output, INamedTypeSymbol model, string hint)
        {
            bool failed = false;
            Action<DiagnosticDescriptor, ISymbol, string> report = (descriptor, symbol, message) =>
            {
                output.ReportDiagnostic(Diagnostic.Create(descriptor, symbol.Locations.FirstOrDefault(), message));
                failed = true;
            };
            if (model.TypeKind != TypeKind.Class || model.IsStatic || model.IsAbstract || model.IsFileLocal ||
                model.IsGenericType || model.ContainingType != null || model.BaseType?.SpecialType != SpecialType.System_Object ||
                model.DeclaringSyntaxReferences.Any(r => !(r.GetSyntax() is ClassDeclarationSyntax c) || !c.Modifiers.Any(SyntaxKind.PartialKeyword)))
            {
                report(Shape, model, "최상위 비제네릭·비상속 partial class가 필요합니다.");
                return;
            }
            if (model.Name == "Dnp3Binding" || model.GetMembers("Dnp3Binding").Length != 0)
                report(Shape, model, "Dnp3Binding은 생성 코드가 사용하는 이름입니다.");
            if (Attribute(model, "Dnp3MasterMap") != null)
                report(Shape, model, "한 모델에서 Outstation과 Master 매핑을 혼합할 수 없습니다.");
            var points = new List<Point>();
            var controls = new List<Command>();
            foreach (var symbol in model.GetMembers())
            {
                var pointAttributes = symbol.GetAttributes().Where(IsPoint).ToArray();
                if (pointAttributes.Length > 1)
                    report(Member, symbol, "한 멤버에는 DNP3 Point 특성을 하나만 지정하십시오.");
                var a = pointAttributes.FirstOrDefault();
                if (a != null)
                {
                    if (!Readable(symbol))
                    {
                        report(Member, symbol, "읽기 가능한 인스턴스 필드 또는 속성이 필요합니다.");
                        continue;
                    }
                    if (!PointAddress(a, out int kind, out ushort index) ||
                        a.NamedArguments.Any(n => n.Value.Kind == TypedConstantKind.Error))
                    {
                        report(Options, symbol, "유효한 Point 종류와 UInt16 인덱스가 필요합니다.");
                        continue;
                    }
                    var type = TypeOf(symbol).SpecialType;
                    bool analog = kind == 4 || kind == 6;
                    bool valid = analog ? type == SpecialType.System_Double || type == SpecialType.System_Single :
                        kind == 0 || kind == 5 ? type == SpecialType.System_Boolean :
                        kind == 1 ? type == SpecialType.System_Byte : type == SpecialType.System_UInt32;
                    if (!valid)
                    {
                        report(Member, symbol, "Binary/OutputStatus는 bool, DoubleBitBinary는 byte, Counter/FrozenCounter는 uint, Analog/OutputStatus는 float 또는 double이어야 합니다.");
                        continue;
                    }
                    byte eventClass = Option(a, "EventClass", (byte)1);
                    double deadband = Option(a, "Deadband", 0d);
                    byte stat = Option(a, "StaticVariation", (byte)0), ev = Option(a, "EventVariation", (byte)0);
                    if (eventClass > 3 || double.IsNaN(deadband) || double.IsInfinity(deadband) || deadband < 0 ||
                        (!analog && deadband != 0) || !Variation(kind, false, stat) || !Variation(kind, true, ev))
                    {
                        report(Options, symbol, "Class는 0~3, Deadband는 Analog에만 유한한 0 이상, Variation은 기존 런타임 지원 범위여야 합니다.");
                        continue;
                    }
                    string flags = Metadata(model, a, "Flags", SpecialType.System_Byte, symbol, report);
                    string time = Metadata(model, a, "Timestamp", SpecialType.System_Int64, symbol, report);
                    points.Add(new Point { Symbol = symbol, Kind = kind, Index = index, Class = eventClass,
                        Deadband = deadband, Static = stat, Event = ev, Flags = flags, Time = time });
                }
                a = Attribute(symbol, "Dnp3Control");
                if (a == null) continue;
                if (a.ConstructorArguments.Length != 2 || !(a.ConstructorArguments[0].Value is byte group) ||
                    (group != 12 && group != 41) || !(a.ConstructorArguments[1].Value is ushort target) ||
                    a.NamedArguments.Any(n => n.Value.Kind == TypedConstantKind.Error))
                {
                    report(Control, symbol, "CROB Group 12 또는 Analog Output Group 41과 UInt16 인덱스를 지정해야 합니다.");
                    continue;
                }
                string validator = Option<string>(a, "Validate", null);
                var validators = validator == null ? new ISymbol[0] : model.GetMembers(validator).ToArray();
                if (!Method(symbol, true) || validators.Length != 1 || !Method(validators[0], false))
                {
                    report(Control, symbol, "Operate는 Dnp3CommandStatus Method(Dnp3Command, bool), Validate는 같은 모델의 Dnp3CommandStatus Method(Dnp3Command)여야 합니다.");
                    continue;
                }
                controls.Add(new Command { Symbol = symbol, Group = group, Index = target, Validator = validator });
            }
            if (points.Count < 1 || points.Count > 512)
                report(Member, model, "1~512개 Point가 필요합니다.");
            points.Sort((a, b) => a.Kind != b.Kind ? a.Kind.CompareTo(b.Kind) : a.Index.CompareTo(b.Index));
            for (int i = 1; i < points.Count; i++)
                if (points[i].Kind == points[i - 1].Kind && points[i].Index == points[i - 1].Index)
                    report(Duplicate, points[i].Symbol, "동일 Point 종류와 인덱스가 중복됩니다.");
            controls.Sort((a, b) => a.Group != b.Group ? a.Group.CompareTo(b.Group) : a.Index.CompareTo(b.Index));
            for (int i = 1; i < controls.Count; i++)
                if (controls[i].Group == controls[i - 1].Group && controls[i].Index == controls[i - 1].Index)
                    report(Duplicate, controls[i].Symbol, "동일 명령 Group과 인덱스가 중복됩니다.");
            if (failed) return;
            var b = new StringBuilder("// <auto-generated/>\n");
            if (!model.ContainingNamespace.IsGlobalNamespace) b.Append("namespace ").Append(model.ContainingNamespace.ToDisplayString()).AppendLine(" {");
            string name = model.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            b.Append("partial class @").Append(model.Name).AppendLine(" {");
            b.AppendLine("/// <summary>고정 Point Database와 명시적 제어 메서드를 연결합니다.</summary>");
            b.Append("public sealed class Dnp3Binding : ").Append(R).AppendLine("IDnp3ControlHandler {");
            b.Append("private readonly ").Append(name).AppendLine(" _model;");
            b.AppendLine("private readonly object _sync;");
            b.AppendLine("/// <summary>기존 이벤트·Deadband 계약을 유지하는 Database입니다.</summary>");
            b.Append("public ").Append(R).AppendLine("Dnp3Database Database { get; private set; }");
            b.AppendLine("/// <summary>공유 잠금 안에서 모델의 초기값을 읽습니다. 초기 이벤트는 생성하지 않습니다.</summary>");
            b.AppendLine("/// <param name=\"model\">앱 소유 모델입니다.</param>");
            b.AppendLine("/// <param name=\"syncRoot\">앱에서도 모델 접근에 사용하는 공유 잠금 객체입니다.</param>");
            b.AppendLine("/// <param name=\"eventCapacity\">보관할 수 있는 이벤트의 최대 개수입니다. 범위는 1~2048입니다.</param>");
            b.AppendLine("/// <remarks>모델과 잠금 객체는 호출자 소유입니다. 모델을 직접 갱신할 때도 같은 잠금을 사용해야 하며, 생성 후 모델의 변경 사항은 Publish를 호출해야 Database에 반영됩니다.</remarks>");
            b.AppendLine("/// <exception cref=\"global::System.ArgumentNullException\">모델 또는 잠금 객체가 null입니다.</exception>");
            b.AppendLine("/// <exception cref=\"global::System.ArgumentException\">이벤트 용량 또는 모델의 초기 Point 값이 유효하지 않습니다.</exception>");
            b.Append("public Dnp3Binding(").Append(name).AppendLine(" model, object syncRoot, int eventCapacity = 128) {");
            b.AppendLine("if (model == null) throw new global::System.ArgumentNullException(\"model\");");
            b.AppendLine("if (syncRoot == null) throw new global::System.ArgumentNullException(\"syncRoot\");");
            b.AppendLine("_model = model; _sync = syncRoot; lock (_sync) {");
            b.Append("Database = new ").Append(R).Append("Dnp3Database(new ").Append(R).AppendLine("Dnp3PointDefinition[] {");
            foreach (var p in points)
                b.Append("new ").Append(R).Append("Dnp3PointDefinition(").Append(Kind(p)).Append(", ").Append(p.Index)
                    .Append(", ").Append(Value(p)).Append(", ").Append(p.Class).Append(", ")
                    .Append(p.Deadband.ToString("R", CultureInfo.InvariantCulture)).Append("D, ").Append(p.Static).Append(", ").Append(p.Event).AppendLine("),");
            b.AppendLine("}, eventCapacity); } }");
            b.AppendLine("private static ulong DoubleBits(byte value) { if (value > 3) throw new global::System.ArgumentOutOfRangeException(\"value\"); return value; }");
            b.AppendLine("/// <summary>같은 Lock에서 전체 모델을 읽은 뒤 각 Point를 갱신합니다. 정상 경로에 임시 배열을 만들지 않습니다.</summary>");
            b.AppendLine("/// <returns>모든 갱신에서 필요한 이벤트가 수용되었으면 true입니다. false여도 Static 값은 갱신됩니다.</returns>");
            b.AppendLine("/// <remarks>Database 전체에 대한 원자적 트랜잭션은 아닙니다. 앱에서 발생한 예외는 전파하며 이미 적용한 변경은 되돌리지 않습니다.</remarks>");
            b.AppendLine("public bool Publish() { lock (_sync) {");
            for (int i = 0; i < points.Count; i++) b.Append("var v").Append(i).Append(" = ").Append(Value(points[i])).AppendLine(";");
            b.AppendLine("bool accepted = true;");
            for (int i = 0; i < points.Count; i++)
                b.Append("accepted = Database.Update(").Append(Kind(points[i])).Append(", ").Append(points[i].Index)
                    .Append(", v").Append(i).AppendLine(") && accepted;");
            b.AppendLine("return accepted; } }");
            b.AppendLine("/// <summary>지정한 Point의 값·품질·시각만 같은 Lock에서 읽어 게시합니다.</summary>");
            b.AppendLine("/// <param name=\"type\">매핑된 Point 종류입니다.</param>");
            b.AppendLine("/// <param name=\"index\">매핑된 프로토콜 인덱스입니다.</param>");
            b.AppendLine("/// <returns>필요한 이벤트가 수용되었으면 true입니다. false여도 Static 값은 갱신됩니다.</returns>");
            b.AppendLine("/// <exception cref=\"global::System.ArgumentException\">매핑되지 않은 종류 또는 인덱스입니다.</exception>");
            b.Append("public bool Publish(").Append(R).AppendLine("Dnp3PointType type, ushort index) { lock (_sync) { switch (type) {");
            foreach (var group in points.GroupBy(p => p.Kind))
            {
                b.Append("case ").Append(R).Append("Dnp3PointType.").Append(Types[group.Key]).AppendLine(": switch (index) {");
                foreach (var p in group)
                    b.Append("case ").Append(p.Index).Append(": return Database.Update(").Append(Kind(p)).Append(", ")
                        .Append(p.Index).Append(", ").Append(Value(p)).AppendLine(");");
                b.AppendLine("} break;");
            }
            b.AppendLine("} throw new global::System.ArgumentException(\"Unmapped DNP3 point.\"); } }");
            b.AppendLine("/// <inheritdoc/>");
            b.Append("public ").Append(R).Append("Dnp3CommandStatus Validate(").Append(R).AppendLine("Dnp3Command command) { lock (_sync) { return ValidateCore(command); } }");
            b.Append("private ").Append(R).Append("Dnp3CommandStatus ValidateCore(").Append(R).AppendLine("Dnp3Command command) {");
            foreach (var c in controls)
                b.Append("if (command.Group == ").Append(c.Group).Append(" && command.Index == ").Append(c.Index)
                    .Append(") return _model.@").Append(c.Validator).AppendLine("(command);");
            b.Append("return ").Append(R).AppendLine("Dnp3CommandStatus.NotSupported; }");
            b.AppendLine("/// <inheritdoc/>");
            b.AppendLine("/// <remarks>같은 Lock 안에서 다시 검증한 뒤 앱 메서드를 호출합니다. Status Point는 앱이 명시적으로 게시해야 합니다.</remarks>");
            b.Append("public ").Append(R).Append("Dnp3CommandStatus Operate(").Append(R).AppendLine("Dnp3Command command, bool selected) { lock (_sync) {");
            b.Append("var status = ValidateCore(command); if (status != ").Append(R).AppendLine("Dnp3CommandStatus.Success) return status;");
            foreach (var c in controls)
                b.Append("if (command.Group == ").Append(c.Group).Append(" && command.Index == ").Append(c.Index)
                    .Append(") return _model.@").Append(c.Symbol.Name).AppendLine("(command, selected);");
            b.Append("return ").Append(R).AppendLine("Dnp3CommandStatus.NotSupported; } }");
            b.AppendLine("} }");
            if (!model.ContainingNamespace.IsGlobalNamespace) b.AppendLine("}");
            output.AddSource(hint, SourceText.From(SyntaxFactory.ParseCompilationUnit(b.ToString()).NormalizeWhitespace().ToFullString() + "\n", Encoding.UTF8));
        }

        private static AttributeData Attribute(ISymbol symbol, string name)
        {
            return symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == Prefix + name + "Attribute");
        }
        private static bool IsPoint(AttributeData a)
        {
            string name = a.AttributeClass?.ToDisplayString();
            return name == Prefix + "Dnp3PointAttribute" || PointAttributes.Any(p => name == Prefix + p + "Attribute");
        }
        private static bool PointAddress(AttributeData a, out int kind, out ushort index)
        {
            bool shorthand = a.AttributeClass.Name != "Dnp3PointAttribute";
            kind = shorthand ? Array.IndexOf(PointAttributes, a.AttributeClass.Name.Replace("Attribute", "")) :
                a.ConstructorArguments.Length == 2 && a.ConstructorArguments[0].Value is int value ? value : -1;
            index = 0;
            int position = shorthand ? 0 : 1;
            if (kind < 0 || kind >= Types.Length || a.ConstructorArguments.Length != position + 1 ||
                a.ConstructorArguments.Any(v => v.Kind == TypedConstantKind.Error) ||
                !(a.ConstructorArguments[position].Value is ushort number)) return false;
            index = number;
            return true;
        }
        private static T Option<T>(AttributeData a, string key, T fallback)
        {
            var option = a.NamedArguments.FirstOrDefault(n => n.Key == key);
            return option.Key != null && option.Value.Value is T value ? value : fallback;
        }
        private static ITypeSymbol TypeOf(ISymbol s) { return s is IPropertySymbol p ? p.Type : ((IFieldSymbol)s).Type; }
        private static bool Readable(ISymbol s)
        {
            if (s.IsStatic) return false;
            if (s is IPropertySymbol p) return !p.IsIndexer && p.ExplicitInterfaceImplementations.Length == 0 &&
                !p.ReturnsByRef && !p.ReturnsByRefReadonly && p.GetMethod != null;
            return s is IFieldSymbol f && !f.IsConst;
        }
        private static string Metadata(INamedTypeSymbol model, AttributeData a, string key, SpecialType type, ISymbol symbol,
            Action<DiagnosticDescriptor, ISymbol, string> report)
        {
            if (!a.NamedArguments.Any(n => n.Key == key)) return null;
            string name = Option<string>(a, key, null);
            var members = name == null ? new ISymbol[0] : model.GetMembers(name).ToArray();
            if (members.Length != 1 || !Readable(members[0]) || TypeOf(members[0]).SpecialType != type)
                report(Options, symbol, key + "는 같은 모델의 읽기 가능한 " + (key == "Flags" ? "byte" : "long") + " 멤버 이름이어야 합니다.");
            return name;
        }
        private static bool Method(ISymbol s, bool operate)
        {
            return s is IMethodSymbol m && !m.IsStatic && !m.IsGenericMethod && !m.IsAsync && !m.ReturnsByRef &&
                !m.ReturnsByRefReadonly && m.MethodKind == MethodKind.Ordinary && m.ExplicitInterfaceImplementations.Length == 0 &&
                m.ReturnType.ToDisplayString() == "VagabondK.nF.DNP3.Dnp3CommandStatus" &&
                m.Parameters.Length == (operate ? 2 : 1) && m.Parameters.All(p => p.RefKind == RefKind.None) &&
                m.Parameters[0].Type.ToDisplayString() == "VagabondK.nF.DNP3.Dnp3Command" &&
                (!operate || m.Parameters[1].Type.SpecialType == SpecialType.System_Boolean);
        }
        private static bool Variation(int kind, bool events, byte v)
        {
            if (v == 0) return true;
            if (kind == 4) return events ? v <= 8 : v == 1 || v == 2 || v == 5 || v == 6;
            if (kind == 6) return events ? v <= 8 : v <= 4;
            if (kind == 2 || kind == 3) return events ? v == 1 || v == 2 || v == 5 || v == 6 : v <= 2;
            return events ? v <= 2 : v == 2;
        }
        private static string Kind(Point p) { return R + "Dnp3PointType." + Types[p.Kind]; }
        private static string Value(Point p)
        {
            string raw = "_model.@" + p.Symbol.Name;
            if (p.Kind == 0 || p.Kind == 5) raw += " ? 1UL : 0UL";
            if (p.Kind == 1) raw = "DoubleBits(" + raw + ")";
            return R + "Dnp3Value." + (p.Kind == 4 || p.Kind == 6 ? "Analog(" : "Bits(") + raw + ", " +
                (p.Flags == null ? "(byte)1" : "_model.@" + p.Flags) + ", " + (p.Time == null ? "0L" : "_model.@" + p.Time) + ")";
        }
        private sealed class Point
        {
            internal ISymbol Symbol;
            internal int Kind;
            internal ushort Index;
            internal byte Class, Static, Event;
            internal double Deadband;
            internal string Flags, Time;
        }
        private sealed class Command
        {
            internal ISymbol Symbol;
            internal byte Group;
            internal ushort Index;
            internal string Validator;
        }
        private static readonly string Attributes = @"// <auto-generated/>
namespace VagabondK.nF.DNP3.Mapping {
    /// <summary>Outstation Database와 명시적 제어 처리를 생성합니다.</summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Class, AllowMultiple=false, Inherited=false)]
    internal sealed class Dnp3MapAttribute : global::System.Attribute { }
    /// <summary>Point 정의와 Outstation의 모델 값 읽기 또는 Master의 수신값 반영을 지정합니다.</summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Field | global::System.AttributeTargets.Property, AllowMultiple=false, Inherited=false)]
    internal class Dnp3PointAttribute : global::System.Attribute {
        /// <summary>Point 종류와 인덱스를 지정합니다.</summary>
        /// <param name=""type"">기존 런타임 Point 종류입니다.</param>
        /// <param name=""index"">0~65535 인덱스입니다.</param>
        public Dnp3PointAttribute(global::VagabondK.nF.DNP3.Dnp3PointType type, ushort index) { }
        /// <summary>이벤트 클래스를 지정합니다. 범위는 0~3이며 기본값은 1입니다. 0이면 이벤트를 생성하지 않습니다.</summary>
        public byte EventClass { get; set; }
        /// <summary>Outstation의 Analog/AnalogOutputStatus에만 사용하는 유한한 0 이상 변화량입니다. 마지막 이벤트 기준으로 이 값을 초과하면 이벤트를 만듭니다. 기본값은 0입니다.</summary>
        public double Deadband { get; set; }
        /// <summary>0이면 런타임 기본 Static Variation을 사용합니다.</summary>
        public byte StaticVariation { get; set; }
        /// <summary>0이면 런타임 기본 Event Variation을 사용합니다.</summary>
        public byte EventVariation { get; set; }
        /// <summary>같은 모델의 byte 품질 Flags 멤버 이름입니다. Outstation에서 미지정 시 Online(1)입니다.</summary>
        public string Flags { get; set; }
        /// <summary>같은 모델의 long 시각 멤버 이름입니다. 1970-01-01 UTC부터의 경과 밀리초이며 0~0xFFFFFFFFFFFF 범위입니다. Outstation에서 미지정 시 0이며 시각만 변경해도 이벤트는 만들지 않습니다. Master에서는 수신한 측정값에 포함된 시각을 기록합니다.</summary>
        public string Timestamp { get; set; }
    }
    /// <summary>제어 실행 메서드와 부작용 없는 검증 메서드를 연결합니다.</summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Method, AllowMultiple=false, Inherited=false)]
    internal sealed class Dnp3ControlAttribute : global::System.Attribute {
        /// <summary>명령의 Group과 인덱스를 지정합니다.</summary>
        /// <param name=""group"">CROB 12 또는 Analog Output 41입니다.</param>
        /// <param name=""index"">명령 대상 인덱스입니다.</param>
        public Dnp3ControlAttribute(byte group, ushort index) { }
        /// <summary>Dnp3CommandStatus Method(Dnp3Command) 형식인 검증 메서드 이름입니다.</summary>
        public string Validate { get; set; }
    }
}" + PointAttributeDeclarations();

        private static string PointAttributeDeclarations()
        {
            var b = new StringBuilder("\nnamespace VagabondK.nF.DNP3.Mapping {\n");
            for (int i = 0; i < PointAttributes.Length; i++)
            {
                b.Append("/// <summary>").Append(Types[i]).AppendLine(" Point를 선언합니다. 출력 특성도 상태 매핑이며 자동 제어를 수행하지 않습니다.</summary>");
                b.AppendLine("[global::System.AttributeUsage(global::System.AttributeTargets.Field | global::System.AttributeTargets.Property, AllowMultiple=false, Inherited=false)]");
                b.Append("internal sealed class ").Append(PointAttributes[i]).AppendLine("Attribute : Dnp3PointAttribute {");
                b.AppendLine("/// <summary>Point 인덱스를 지정합니다.</summary>");
                b.AppendLine("/// <param name=\"index\">0~65535의 인덱스입니다.</param>");
                b.Append("public ").Append(PointAttributes[i]).Append("Attribute(ushort index) : base(global::VagabondK.nF.DNP3.Dnp3PointType.")
                    .Append(Types[i]).AppendLine(", index) { } }");
            }
            return b.AppendLine("}").ToString();
        }
    }
}
