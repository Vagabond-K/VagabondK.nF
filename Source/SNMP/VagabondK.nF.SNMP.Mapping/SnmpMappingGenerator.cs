using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace VagabondK.nF.SNMP.Mapping.Generator
{
    /// <summary>고정 SNMP 말단 OID 선언에서 모델에 직접 접근하는 저장소를 생성합니다.</summary>
    [Generator(LanguageNames.CSharp)]
    public sealed class SnmpMappingGenerator : IIncrementalGenerator
    {
        private const string Prefix = "VagabondK.nF.SNMP.Mapping.";
        private const string Runtime = "global::VagabondK.nF.SNMP.";
        private static readonly string[] ScalarAttributes = { "SnmpInteger", "SnmpCounter32", "SnmpGauge32", "SnmpTimeTicks", "SnmpCounter64", "SnmpOctetString", "SnmpIpAddress", "SnmpObjectIdentifier" };
        private static readonly int[] ScalarTags = { 2, 65, 66, 67, 70, 4, 64, 6 };
        private static readonly string[] ScalarTypes = { "Integer32", "Counter32", "Gauge32", "TimeTicks", "Counter64", "OctetString", "IpAddress", "ObjectIdentifier" };
        private static readonly DiagnosticDescriptor Shape = Error("VKSN001", "SNMP 매핑 클래스");
        private static readonly DiagnosticDescriptor Member = Error("VKSN002", "SNMP 매핑 멤버");
        private static readonly DiagnosticDescriptor Options = Error("VKSN003", "SNMP 매핑 OID 및 옵션");
        private static readonly DiagnosticDescriptor Duplicate = Error("VKSN004", "SNMP OID 충돌");
        private static readonly DiagnosticDescriptor Collision = Error("VKSN005", "SNMP 생성 이름 충돌");

        private static DiagnosticDescriptor Error(string id, string title)
        {
            return new DiagnosticDescriptor(id, title, "{0}", "SnmpMapping", DiagnosticSeverity.Error, true);
        }

        /// <summary>Attribute 검색 및 소스 생성 파이프라인을 등록합니다.</summary>
        /// <param name="context">컴파일러 초기화 컨텍스트입니다.</param>
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            context.RegisterPostInitializationOutput(c => c.AddSource("SnmpMappingAttributes.g.cs",
                SourceText.From(VagabondK.nF.Mapping.Generator.MappingAttributeSource.ForBuild(Attributes), Encoding.UTF8)));
            var models = context.SyntaxProvider.ForAttributeWithMetadataName(Prefix + "SnmpMapAttribute",
                (node, _) => node is TypeDeclarationSyntax, (syntax, _) => (INamedTypeSymbol)syntax.TargetSymbol);
            context.RegisterSourceOutput(models.Collect(), (output, symbols) =>
            {
                var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
                int index = 0;
                foreach (var model in symbols.OrderBy(s => s.ToDisplayString(), StringComparer.Ordinal))
                    if (seen.Add(model))
                        Generate(output, model, "SnmpMap" + (index++).ToString("D4", CultureInfo.InvariantCulture) + ".g.cs");
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
                model.DeclaringSyntaxReferences.Any(r => !(r.GetSyntax() is ClassDeclarationSyntax c) ||
                    !c.Modifiers.Any(SyntaxKind.PartialKeyword)))
            {
                report(Shape, model, "최상위 비제네릭·비상속 partial class가 필요합니다.");
                return;
            }
            if (model.Name == "SnmpStore" || model.GetMembers("SnmpStore").Length > 0)
                report(Collision, model, "SnmpStore는 생성 저장소가 사용하는 이름입니다.");
            var points = new List<Point>();
            foreach (var symbol in model.GetMembers())
            {
                var attributes = symbol.GetAttributes().Where(a => a.AttributeClass?.ToDisplayString() == Prefix + "SnmpScalarAttribute" ||
                    ScalarAttributes.Any(n => a.AttributeClass?.ToDisplayString() == Prefix + n + "Attribute")).ToArray();
                if (attributes.Length == 0)
                    continue;
                if (attributes.Length != 1)
                {
                    report(Member, symbol, "한 멤버에는 SNMP 매핑 특성을 하나만 지정하십시오.");
                    continue;
                }
                var attribute = attributes[0];
                var property = symbol as IPropertySymbol;
                var field = symbol as IFieldSymbol;
                bool writable = Named(attribute, "Writable", 0) == 1;
                if (symbol.IsStatic || (property == null && field == null) ||
                    (property != null && (property.IsIndexer || property.ExplicitInterfaceImplementations.Length > 0 ||
                        property.ReturnsByRef || property.ReturnsByRefReadonly || property.GetMethod == null ||
                        (writable && (property.SetMethod == null || property.SetMethod.IsInitOnly)))) ||
                    (field != null && (field.IsConst || (writable && field.IsReadOnly))))
                {
                    report(Member, symbol, "읽기 가능한 인스턴스 필드/속성이 필요하며 Writable 멤버에는 Setter 또는 변경 가능한 필드가 필요합니다.");
                    continue;
                }
                string oidText = attribute.ConstructorArguments.Length > 0 ? attribute.ConstructorArguments[0].Value as string : null;
                uint[] oid = ParseOid(oidText);
                int scalar = Array.IndexOf(ScalarAttributes, attribute.AttributeClass.Name.Replace("Attribute", ""));
                int tag = scalar >= 0 ? ScalarTags[scalar] :
                    attribute.ConstructorArguments.Length == 2 && attribute.ConstructorArguments[1].Kind != TypedConstantKind.Error
                    ? Convert.ToInt32(attribute.ConstructorArguments[1].Value, CultureInfo.InvariantCulture) : -1;
                int maximum = Named(attribute, "MaximumLength", 255);
                var type = property?.Type ?? field.Type;
                string factory = Factory(tag, type);
                if (oid == null || oid[oid.Length - 1] != 0 || Reserved(oid) || maximum < 0 || maximum > 1024 ||
                    attribute.NamedArguments.Any(a => a.Value.Kind == TypedConstantKind.Error) ||
                    (tag != 4 && attribute.NamedArguments.Any(a => a.Key == "MaximumLength")))
                {
                    report(Options, symbol, "유효한 전체 scalar OID(.0), system 그룹 이외의 주소, OctetString의 MaximumLength(0~1024)를 사용하십시오.");
                    continue;
                }
                if (factory == null)
                {
                    report(Member, symbol, "Integer32=int, Counter32/Gauge32/TimeTicks=uint, Counter64=ulong, OctetString/IpAddress=byte[], ObjectIdentifier=uint[]만 지원합니다.");
                    continue;
                }
                points.Add(new Point(symbol, oid, tag, factory, maximum, writable));
            }
            if (points.Count < 1 || points.Count > 512)
                report(Member, model, "1~512개 scalar 멤버가 필요합니다.");
            points.Sort((a, b) => Compare(a.Oid, b.Oid));
            for (int i = 1; i < points.Count; i++)
                if (StartsWith(points[i].Oid, points[i - 1].Oid))
                    report(Duplicate, points[i].Symbol, "중복되거나 다른 leaf의 접두사인 OID입니다: " + points[i - 1].Symbol.Name);
            if (failed)
                return;
            var b = new StringBuilder("// <auto-generated/>\n");
            b.Append("// SNMP mapping: scalars=").Append(points.Count).AppendLine("; definitions sorted at build time; values allocated by SnmpValue factories.");
            if (!model.ContainingNamespace.IsGlobalNamespace)
                b.Append("namespace ").Append(model.ContainingNamespace.ToDisplayString()).AppendLine(" {");
            b.Append("partial class @").Append(model.Name).AppendLine(" {");
            EmitStore(b, model, points);
            b.AppendLine("}");
            if (!model.ContainingNamespace.IsGlobalNamespace)
                b.AppendLine("}");
            output.AddSource(hint, SourceText.From(SyntaxFactory.ParseCompilationUnit(b.ToString())
                .NormalizeWhitespace().ToFullString() + "\n", Encoding.UTF8));
        }

        private static int Named(AttributeData attribute, string name, int fallback)
        {
            foreach (var pair in attribute.NamedArguments)
                if (pair.Key == name)
                    return pair.Value.Kind == TypedConstantKind.Error ? -1 : Convert.ToInt32(pair.Value.Value, CultureInfo.InvariantCulture);
            return fallback;
        }

        private static uint[] ParseOid(string text)
        {
            if (text == null)
                return null;
            string[] parts = text.Split('.');
            if (parts.Length < 2 || parts.Length > 128)
                return null;
            var result = new uint[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                if (parts[i].Length == 0 || parts[i].Length > 10 ||
                    parts[i].Any(c => c < '0' || c > '9') ||
                    !uint.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out result[i]))
                    return null;
            return result[0] <= 2 && (result[0] < 2 ? result[1] < 40 : result[1] <= uint.MaxValue - 80) ? result : null;
        }

        private static int Compare(uint[] a, uint[] b)
        {
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
                if (a[i] != b[i])
                    return a[i] < b[i] ? -1 : 1;
            return a.Length.CompareTo(b.Length);
        }

        private static bool StartsWith(uint[] value, uint[] prefix)
        {
            return value.Length >= prefix.Length && prefix.Where((arc, i) => arc != value[i]).Any() == false;
        }

        private static bool Reserved(uint[] oid)
        {
            var system = new uint[] { 1, 3, 6, 1, 2, 1, 1 };
            return StartsWith(oid, system) || StartsWith(system, oid);
        }

        private static string Factory(int tag, ITypeSymbol type)
        {
            switch (tag)
            {
                case 2: return type.SpecialType == SpecialType.System_Int32 ? "Integer32" : null;
                case 65: return type.SpecialType == SpecialType.System_UInt32 ? "Counter32" : null;
                case 66: return type.SpecialType == SpecialType.System_UInt32 ? "Gauge32" : null;
                case 67: return type.SpecialType == SpecialType.System_UInt32 ? "TimeTicks" : null;
                case 70: return type.SpecialType == SpecialType.System_UInt64 ? "Counter64" : null;
                case 4: case 64: case 6:
                    var array = type as IArrayTypeSymbol;
                    return array != null && array.IsSZArray && array.ElementType.SpecialType ==
                        (tag == 6 ? SpecialType.System_UInt32 : SpecialType.System_Byte)
                        ? (tag == 4 ? "OctetString" : tag == 64 ? "IpAddress" : "ObjectIdentifier") : null;
                default: return null;
            }
        }

        private static string Decode(Point p)
        {
            return "value." + (p.Tag == 2 ? "Integer" : p.Tag == 70 ? "Unsigned64" :
                p.Tag == 6 ? "GetObjectIdentifier()" : p.Tag == 4 || p.Tag == 64 ? "GetBytes()" : "Unsigned");
        }

        private static void EmitStore(StringBuilder b, INamedTypeSymbol model, List<Point> points)
        {
            string modelType = model.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            b.AppendLine("/// <summary>고정 OID를 모델에 직접 연결합니다. 앱도 같은 잠금으로 모델에 접근해야 합니다.</summary>");
            b.Append("public sealed class SnmpStore : ").Append(Runtime).AppendLine("ISnmpValueStore {");
            b.Append("private readonly ").Append(modelType).AppendLine(" _model;");
            b.AppendLine("private readonly object _sync;");
            b.Append("private readonly ").Append(Runtime).AppendLine("SnmpObject[] _objects;");
            b.AppendLine("/// <summary>모델과 앱이 공유하는 잠금 객체를 지정하여 저장소를 생성합니다.</summary>");
            b.AppendLine("/// <param name=\"model\">앱이 수명을 관리하는 모델입니다.</param>");
            b.AppendLine("/// <param name=\"syncRoot\">모델 접근에 공통으로 사용할 잠금 객체입니다.</param>");
            b.AppendLine("/// <exception cref=\"global::System.ArgumentNullException\">모델 또는 잠금 객체가 null입니다.</exception>");
            b.Append("public SnmpStore(").Append(modelType).AppendLine(" model, object syncRoot) {");
            b.AppendLine("if (model == null) throw new global::System.ArgumentNullException(\"model\");");
            b.AppendLine("if (syncRoot == null) throw new global::System.ArgumentNullException(\"syncRoot\");");
            b.AppendLine("_model = model; _sync = syncRoot;");
            b.Append("_objects = new ").Append(Runtime).AppendLine("SnmpObject[] {");
            foreach (var p in points)
                b.Append("new ").Append(Runtime).Append("SnmpObject(new uint[] {")
                    .Append(string.Join(",", p.Oid.Select(v => v.ToString(CultureInfo.InvariantCulture) + "U")))
                    .Append("}, ").Append(Runtime).Append("SnmpType.").Append(p.Factory).Append(", ")
                    .Append(p.Writable ? "true" : "false").Append(", ").Append(p.Maximum).AppendLine("),");
            b.AppendLine("}; }");
            b.AppendLine("/// <inheritdoc/>");
            b.Append("public int Count { get { return ").Append(points.Count).AppendLine("; } }");
            b.AppendLine("private void CheckIndex(int index) { if (index < 0 || index >= Count) throw new global::System.ArgumentOutOfRangeException(\"index\"); }");
            b.AppendLine("/// <inheritdoc/>");
            b.Append("public ").Append(Runtime).AppendLine("SnmpObject GetObject(int index) { CheckIndex(index); return _objects[index]; }");
            b.AppendLine("/// <inheritdoc/>");
            b.Append("public ").Append(Runtime).AppendLine("SnmpValue Read(int index) { CheckIndex(index); lock (_sync) { switch (index) {");
            for (int i = 0; i < points.Count; i++)
                b.Append("case ").Append(i).Append(": return ").Append(Runtime).Append("SnmpValue.").Append(points[i].Factory)
                    .Append("(_model.@").Append(points[i].Symbol.Name).AppendLine(");");
            b.AppendLine("default: throw new global::System.ArgumentOutOfRangeException(\"index\"); } } }");
            b.AppendLine("/// <inheritdoc/>");
            b.Append("public bool AcceptsWrite(int index, ").Append(Runtime).AppendLine("SnmpValue value) { CheckIndex(index);");
            b.AppendLine("return value != null && _objects[index].Writable && value.Type == _objects[index].Type &&");
            b.Append("(value.Type != ").Append(Runtime).AppendLine("SnmpType.OctetString || value.ByteLength <= _objects[index].MaximumLength); }");
            b.AppendLine("/// <inheritdoc/>");
            b.AppendLine("/// <remarks>검증 후 setter나 필드를 통해 값을 즉시 반영합니다. 앞선 쓰기의 변경을 되돌리거나 요청 전체의 원자성을 보장하지 않습니다.</remarks>");
            b.AppendLine("/// <exception cref=\"global::System.ArgumentException\">AcceptsWrite 검사(쓰기 허용 여부, 형식 일치 여부, OctetString 최대 길이)를 통과하지 못했습니다.</exception>");
            b.Append("public void QueueWrite(int index, ").Append(Runtime).AppendLine("SnmpValue value) {");
            b.AppendLine("if (!AcceptsWrite(index, value)) throw new global::System.ArgumentException(\"Invalid SNMP write.\");");
            b.AppendLine("lock (_sync) { switch (index) {");
            for (int i = 0; i < points.Count; i++)
                if (points[i].Writable)
                    b.Append("case ").Append(i).Append(": _model.@").Append(points[i].Symbol.Name).Append(" = ").Append(Decode(points[i])).AppendLine("; return;");
            b.AppendLine("default: throw new global::System.ArgumentException(\"Read-only SNMP object.\"); } } }");
            b.AppendLine("}");
        }

        private sealed class Point
        {
            internal Point(ISymbol symbol, uint[] oid, int tag, string factory, int maximum, bool writable)
            {
                Symbol = symbol; Oid = oid; Tag = tag; Factory = factory; Maximum = maximum; Writable = writable;
            }
            internal ISymbol Symbol { get; }
            internal uint[] Oid { get; }
            internal int Tag { get; }
            internal string Factory { get; }
            internal int Maximum { get; }
            internal bool Writable { get; }
        }

        private static readonly string Attributes = @"// <auto-generated/>
namespace VagabondK.nF.SNMP.Mapping {
    /// <summary>SNMP scalar 저장소를 생성합니다. 런타임 리플렉션 바인딩은 사용하지 않습니다.</summary>
    /// <remarks>최상위 partial class에 지정하면 모델 안에 중첩 <c>SnmpStore</c> 클래스(<c>ISnmpValueStore</c> 구현)가 생성됩니다.
    /// 객체는 <see cref=""SnmpScalarAttribute""/>가 붙은 멤버에서 만들어집니다.</remarks>
    [global::System.AttributeUsage(global::System.AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    internal sealed class SnmpMapAttribute : global::System.Attribute { }
    /// <summary>모델 멤버를 전체 숫자 OID로 지정한 scalar 말단 인스턴스에 연결합니다.</summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Property | global::System.AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
    internal class SnmpScalarAttribute : global::System.Attribute {
        /// <summary>인스턴스를 나타내는 .0을 포함한 전체 OID와 SMI 형식을 지정합니다.</summary>
        /// <param name=""oid"">숫자 OID입니다. MIB-II system 그룹은 예약되어 있습니다.</param>
        /// <param name=""type"">멤버 CLR 형식과 일치하는 SMI 형식입니다.</param>
        public SnmpScalarAttribute(string oid, global::VagabondK.nF.SNMP.SnmpType type) { }
        /// <summary>SET을 허용합니다. 기본값은 false입니다.</summary>
        public bool Writable { get; set; }
        /// <summary>OctetString SET에서 허용할 최대 바이트 수입니다. 기본값은 255이며 허용 범위는 0~1024입니다.</summary>
        public int MaximumLength { get; set; }
    }
}" + ScalarAttributeDeclarations();

        private static string ScalarAttributeDeclarations()
        {
            var b = new StringBuilder("\nnamespace VagabondK.nF.SNMP.Mapping {\n");
            for (int i = 0; i < ScalarAttributes.Length; i++)
            {
                b.Append("/// <summary>").Append(ScalarTypes[i]).AppendLine(" scalar를 선언합니다. 기존 SnmpScalar와 같은 형식 검사를 적용합니다.</summary>");
                b.AppendLine("[global::System.AttributeUsage(global::System.AttributeTargets.Property | global::System.AttributeTargets.Field, AllowMultiple=false, Inherited=false)]");
                b.Append("internal sealed class ").Append(ScalarAttributes[i]).AppendLine("Attribute : SnmpScalarAttribute {");
                b.AppendLine("/// <summary>scalar의 전체 OID를 지정합니다.</summary>");
                b.AppendLine("/// <param name=\"oid\">말단 .0을 포함한 전체 숫자 OID입니다.</param>");
                b.Append("public ").Append(ScalarAttributes[i]).Append("Attribute(string oid) : base(oid, global::VagabondK.nF.SNMP.SnmpType.")
                    .Append(ScalarTypes[i]).AppendLine(") { } }");
            }
            return b.AppendLine("}").ToString();
        }
    }
}
