using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace VagabondK.nF.MQTT.Sparkplug.Mapping.Generator
{
    /// <summary>고정 스칼라 선언에서 Sparkplug 모델 변환과 수신기 바인딩을 생성합니다.</summary>
    [Generator(LanguageNames.CSharp)]
    public sealed class SparkplugMappingGenerator : IIncrementalGenerator
    {
        private const string Prefix = "VagabondK.nF.MQTT.Sparkplug.Mapping.";
        private const string Runtime = "global::VagabondK.nF.MQTT.Sparkplug.";
        private static readonly DiagnosticDescriptor Shape = Error("VKSP001", "Sparkplug 매핑 클래스");
        private static readonly DiagnosticDescriptor Member = Error("VKSP002", "Sparkplug 매핑 멤버");
        private static readonly DiagnosticDescriptor Options = Error("VKSP003", "Sparkplug 메트릭 옵션");
        private static readonly DiagnosticDescriptor Duplicate = Error("VKSP004", "Sparkplug 스키마 중복");
        private static readonly DiagnosticDescriptor Collision = Error("VKSP005", "Sparkplug 생성 이름 충돌");
        private static readonly string[] Reserved = { "Metrics", "ApplySnapshot", "SparkplugSequence", "SparkplugBinding", "ApplySparkplugSnapshot" };

        private static DiagnosticDescriptor Error(string id, string title)
        { return new DiagnosticDescriptor(id, title, "{0}", "SparkplugMapping", DiagnosticSeverity.Error, true); }

        /// <summary>선언 검색과 증분 소스 생성 파이프라인을 등록합니다.</summary>
        /// <param name="context">컴파일러 초기화 컨텍스트입니다.</param>
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            context.RegisterPostInitializationOutput(c => c.AddSource("SparkplugMappingAttributes.g.cs",
                SourceText.From(VagabondK.nF.Mapping.Generator.MappingAttributeSource.ForBuild(Attributes), Encoding.UTF8)));
            var models = context.SyntaxProvider.ForAttributeWithMetadataName(Prefix + "SparkplugMapAttribute",
                (node, _) => node is TypeDeclarationSyntax, (syntax, _) => (INamedTypeSymbol)syntax.TargetSymbol);
            context.RegisterSourceOutput(models.Collect(), (output, symbols) =>
            {
                var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
                int index = 0;
                foreach (var model in symbols.OrderBy(s => s.ToDisplayString(), StringComparer.Ordinal))
                    if (seen.Add(model)) Generate(output, model, "SparkplugMap" + (index++).ToString("D4", CultureInfo.InvariantCulture) + ".g.cs");
            });
        }

        private sealed class Point
        {
            internal ISymbol Symbol;
            internal string Name, Factory;
            internal ulong Alias;
            internal bool HasAlias;
            internal int Field;
        }

        private static void Generate(SourceProductionContext output, INamedTypeSymbol model, string hint)
        {
            bool failed = false;
            Action<DiagnosticDescriptor, ISymbol, string> report = (descriptor, symbol, message) =>
            { output.ReportDiagnostic(Diagnostic.Create(descriptor, symbol.Locations.FirstOrDefault(), message)); failed = true; };
            if (model.TypeKind != TypeKind.Class || model.IsStatic || model.IsAbstract || model.IsFileLocal ||
                model.IsGenericType || model.ContainingType != null || model.BaseType?.SpecialType != SpecialType.System_Object ||
                model.DeclaringSyntaxReferences.Any(r => !(r.GetSyntax() is ClassDeclarationSyntax c) || !c.Modifiers.Any(SyntaxKind.PartialKeyword)))
            { report(Shape, model, "최상위 비제네릭·비상속 partial class가 필요합니다."); return; }
            foreach (string name in Reserved)
                if (model.Name == name || model.GetMembers(name).Length != 0)
                    report(Collision, model, name + "은 생성 API가 사용하는 이름입니다.");
            var points = new List<Point>();
            foreach (var symbol in model.GetMembers())
            {
                var attribute = symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == Prefix + "SparkplugMetricAttribute");
                if (attribute == null) continue;
                var property = symbol as IPropertySymbol;
                var field = symbol as IFieldSymbol;
                if (symbol.IsStatic || (property == null && field == null) ||
                    property != null && (property.IsIndexer || property.ExplicitInterfaceImplementations.Length > 0 ||
                        property.ReturnsByRef || property.ReturnsByRefReadonly || property.GetMethod == null ||
                        property.SetMethod == null || property.SetMethod.IsInitOnly) ||
                    field != null && (field.IsConst || field.IsReadOnly))
                { report(Member, symbol, "읽고 쓸 수 있는 인스턴스 필드/속성이 필요합니다. private setter도 지원합니다."); continue; }
                string metricName = attribute.ConstructorArguments.Length > 0 ? attribute.ConstructorArguments[0].Value as string : null;
                var clrType = property?.Type ?? field.Type;
                int type = attribute.ConstructorArguments.Length >= 2 && attribute.ConstructorArguments[1].Kind != TypedConstantKind.Error
                    ? Convert.ToInt32(attribute.ConstructorArguments[1].Value, CultureInfo.InvariantCulture) :
                    attribute.ConstructorArguments.Length == 1 ? InferredType(clrType.SpecialType) : -1;
                var alias = attribute.NamedArguments.FirstOrDefault(a => a.Key == "Alias");
                if (string.IsNullOrEmpty(metricName) || metricName.Length > 64 || metricName.Any(c => c < 32 || c > 126) ||
                    attribute.ConstructorArguments.Any(a => a.Kind == TypedConstantKind.Error) ||
                    attribute.NamedArguments.Any(a => a.Value.Kind == TypedConstantKind.Error) ||
                    (alias.Key != null && (!(alias.Value.Value is ulong) || attribute.ConstructorArguments.Length == 3)))
                { report(Options, symbol, "메트릭 이름은 1~64자의 출력 가능한 ASCII여야 하며 옵션은 상수여야 합니다."); continue; }
                string factory = type == 11 && clrType.SpecialType == SpecialType.System_Boolean ? "Boolean" :
                    type == 3 && clrType.SpecialType == SpecialType.System_Int32 ? "Int32" :
                    type == 4 && clrType.SpecialType == SpecialType.System_Int64 ? "Int64" :
                    type == 9 && clrType.SpecialType == SpecialType.System_Single ? "Float" :
                    type == 10 && clrType.SpecialType == SpecialType.System_Double ? "Double" : null;
                if (factory == null)
                { report(Member, symbol, "Boolean=bool, Int32=int, Int64=long, Float=float, Double=double만 지원합니다."); continue; }
                points.Add(new Point { Symbol = symbol, Name = metricName, Factory = factory,
                    HasAlias = attribute.ConstructorArguments.Length == 3 || alias.Key != null,
                    Alias = attribute.ConstructorArguments.Length == 3 ? (ulong)attribute.ConstructorArguments[2].Value :
                        alias.Key != null ? (ulong)alias.Value.Value : 0,
                    Field = type == 11 ? 14 : type == 3 ? 10 : type == 4 ? 11 : type == 9 ? 12 : 13 });
            }
            if (points.Count < 1 || points.Count > 8) report(Options, model, "매핑에는 1~8개의 메트릭이 필요합니다.");
            for (int i = 0; i < points.Count; i++)
                for (int j = 0; j < i; j++)
                    if (points[i].Name == points[j].Name || points[i].HasAlias && points[j].HasAlias && points[i].Alias == points[j].Alias)
                        report(Duplicate, points[i].Symbol, "중복 메트릭 이름 또는 별칭입니다: " + points[j].Symbol.Name);
            if (failed) return;
            var b = new StringBuilder("// <auto-generated/>\n");
            if (!model.ContainingNamespace.IsGlobalNamespace) b.Append("namespace ").Append(model.ContainingNamespace.ToDisplayString()).AppendLine(" {");
            b.Append("partial class @").Append(model.Name).AppendLine(" {");
            EmitModel(b, points);
            EmitBinding(b, model, points);
            b.AppendLine("}");
            if (!model.ContainingNamespace.IsGlobalNamespace) b.AppendLine("}");
            output.AddSource(hint, SourceText.From(SyntaxFactory.ParseCompilationUnit(b.ToString()).NormalizeWhitespace().ToFullString() + "\n", Encoding.UTF8));
        }

        private static int InferredType(SpecialType type)
        {
            switch (type)
            {
                case SpecialType.System_Boolean: return 11;
                case SpecialType.System_Int32: return 3;
                case SpecialType.System_Int64: return 4;
                case SpecialType.System_Single: return 9;
                case SpecialType.System_Double: return 10;
                default: return -1;
            }
        }

        private static string Literal(string value) { return SymbolDisplay.FormatLiteral(value, true); }
        private static string Alias(Point point) { return point.Alias.ToString(CultureInfo.InvariantCulture) + "UL"; }
        private static string Decode(Point point, string value)
        {
            return point.Factory == "Boolean" ? value + ".Integer != 0" :
                point.Factory == "Int32" ? "unchecked((int)(uint)" + value + ".Integer)" :
                point.Factory == "Int64" ? "unchecked((long)" + value + ".Integer)" :
                point.Factory == "Float" ? "(float)" + value + ".Real" : value + ".Real";
        }

        private static void EmitModel(StringBuilder b, List<Point> points)
        {
            b.AppendLine("/// <summary>마지막으로 모델에 반영한 스냅샷의 seq입니다. 수신 전에는 -1입니다.</summary>");
            b.AppendLine("public int SparkplugSequence { get; private set; } = -1;");
            b.AppendLine("/// <summary>현재 모델 값을 명시적으로 메트릭 배열로 변환합니다. 네트워크 송신은 하지 않습니다.</summary>");
            b.AppendLine("/// <param name=\"birth\">참이면 이름·형식과 선언된 별칭을 포함합니다. 거짓이면 별칭이 있는 메트릭에서 이름·형식을 생략합니다.</param>");
            b.AppendLine("/// <returns>호출자가 소유하는 새 메트릭 배열입니다. 순서는 모델 멤버 선언 순서입니다.</returns>");
            b.AppendLine("/// <remarks>모델을 사용하는 앱과 같은 잠금으로 보호하십시오. 배열 생성은 수신기 내부 잠금이나 자동 게시를 수행하지 않습니다.</remarks>");
            b.AppendLine("/// <exception cref=\"global::System.ArgumentOutOfRangeException\">실수 값이 NaN 또는 무한대인 경우.</exception>");
            b.Append("public ").Append(Runtime).AppendLine("SparkplugMetric[] Metrics(bool birth) {");
            b.Append("return new ").Append(Runtime).AppendLine("SparkplugMetric[] {");
            foreach (var p in points)
            {
                b.Append(Runtime).Append("SparkplugMetric.").Append(p.Factory).Append("(");
                b.Append(p.HasAlias ? "birth ? " + Literal(p.Name) + " : null" : Literal(p.Name));
                b.Append(", this.@").Append(p.Symbol.Name);
                if (p.HasAlias) b.Append(", ").Append(Alias(p)).Append(", birth");
                b.AppendLine("),");
            }
            b.AppendLine("}; }");
            b.AppendLine("/// <summary>전체 스키마를 먼저 검사한 뒤 스냅샷 값을 모델에 반영합니다.</summary>");
            b.AppendLine("/// <param name=\"snapshot\">동일한 멤버 선언 순서의 스냅샷입니다. 별칭만 있는 메트릭은 선언한 별칭과 일치해야 합니다.</param>");
            b.AppendLine("/// <remarks>동적으로 협상한 별칭을 사용할 때는 SparkplugBinding.ReadInto를 호출하십시오. 수신 수명주기와 값의 유효 시간은 여기서 확인하지 않습니다. 앱의 접근자에서 발생한 예외는 그대로 전달하며 부작용은 되돌리지 않습니다. 모델을 사용하는 앱과 동일한 잠금 객체로 보호하십시오.</remarks>");
            b.AppendLine("/// <exception cref=\"global::System.ArgumentNullException\">스냅샷이 null인 경우.</exception>");
            b.AppendLine("/// <exception cref=\"global::System.ArgumentException\">전체 스키마 또는 값이 맞지 않는 경우. 이 경우 모델은 변경되지 않습니다.</exception>");
            b.Append("public void ApplySnapshot(").Append(Runtime).AppendLine("SparkplugSnapshot snapshot) { ApplySparkplugSnapshot(snapshot, false); }");
            b.Append("private void ApplySparkplugSnapshot(").Append(Runtime).AppendLine("SparkplugSnapshot snapshot, bool receiverValidated) {");
            b.AppendLine("if (snapshot == null) throw new global::System.ArgumentNullException(\"snapshot\");");
            b.Append("if (snapshot.Count != ").Append(points.Count).AppendLine(") throw new global::System.ArgumentException(\"Sparkplug schema count.\");");
            for (int i = 0; i < points.Count; i++)
            {
                var p = points[i];
                string m = "m" + i;
                b.Append("var ").Append(m).Append(" = snapshot[").Append(i).AppendLine("];");
                string aliasMatch = p.HasAlias ? m + ".HasAlias && " + m + ".Alias == " + Alias(p) : "false";
                b.Append("if (").Append(m).Append(" == null || ").Append(m).Append(".ValueField != ").Append(p.Field)
                    .Append(" || (").Append(m).Append(".Name != ").Append(Literal(p.Name))
                    .Append(" && (").Append(m).Append(".Name != null || !").Append(m).Append(".HasAlias || (!receiverValidated && !(").Append(aliasMatch).Append("))))")
                    .Append(" || (").Append(m).Append(".DataType != ").Append(Runtime).Append("SparkplugDataType.").Append(p.Factory)
                    .Append(" && (").Append(m).Append(".DataType != ").Append(Runtime).Append("SparkplugDataType.Unspecified || !").Append(m).Append(".HasAlias))");
                if (p.Factory == "Boolean") b.Append(" || ").Append(m).Append(".Integer > 1UL");
                if (p.Factory == "Int32") b.Append(" || ").Append(m).Append(".Integer > 4294967295UL");
                if (p.Factory == "Float" || p.Factory == "Double")
                {
                    b.Append(" || double.IsNaN(").Append(m).Append(".Real) || double.IsInfinity(").Append(m).Append(".Real)");
                    if (p.Factory == "Float") b.Append(" || ").Append(m).Append(".Real > float.MaxValue || ").Append(m).Append(".Real < -float.MaxValue");
                }
                b.AppendLine(") throw new global::System.ArgumentException(\"Sparkplug schema/value mismatch.\");");
            }
            for (int i = 0; i < points.Count; i++)
                b.Append("this.@").Append(points[i].Symbol.Name).Append(" = ").Append(Decode(points[i], "m" + i)).AppendLine(";");
            b.AppendLine("SparkplugSequence = snapshot.Sequence; }");
        }

        private static void EmitBinding(StringBuilder b, INamedTypeSymbol model, List<Point> points)
        {
            string modelType = model.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            b.AppendLine("/// <summary>정적 스키마를 기존 SparkplugReceiver에 연결합니다. 수명주기·별칭·seq·수신 값의 유효 시간 검사는 수신기가 전담합니다.</summary>");
            b.AppendLine("public sealed class SparkplugBinding {");
            b.AppendLine("/// <summary>프로토콜 상태를 관리하는 수신기입니다. MQTT 연결 상태·수신 메시지·수신 값의 유효 시간 처리를 여기에 위임하십시오.</summary>");
            b.Append("public ").Append(Runtime).AppendLine("SparkplugReceiver Receiver { get; private set; }");
            b.AppendLine("/// <summary>모델 스키마와 원격 장치 설정으로 수신기를 생성합니다.</summary>");
            b.AppendLine("/// <param name=\"group\">Sparkplug 그룹 ID입니다.</param>");
            b.AppendLine("/// <param name=\"node\">원격 Edge Node ID입니다.</param>");
            b.AppendLine("/// <param name=\"device\">원격 장치 ID입니다.</param>");
            b.AppendLine("/// <param name=\"maximumAgeMilliseconds\">마지막 값 수신 후 사용 가능으로 보는 최대 시간(밀리초)입니다. 양수여야 합니다.</param>");
            b.AppendLine("/// <exception cref=\"global::System.ArgumentException\">장치 식별자 또는 경과 시간이 잘못된 경우.</exception>");
            b.AppendLine("public SparkplugBinding(string group, string node, string device, long maximumAgeMilliseconds) {");
            b.Append("Receiver = new ").Append(Runtime).Append("SparkplugReceiver(group, node, device, new string[] {");
            b.Append(string.Join(", ", points.Select(p => Literal(p.Name)))).Append("}, new ").Append(Runtime).Append("SparkplugDataType[] {");
            b.Append(string.Join(", ", points.Select(p => Runtime + "SparkplugDataType." + p.Factory))).AppendLine("}, maximumAgeMilliseconds); }");
            b.AppendLine("/// <summary>수신기가 검증한 마지막 불변 스냅샷을 모델에 복사합니다. BIRTH에서 학습한 동적 별칭도 지원합니다.</summary>");
            b.AppendLine("/// <param name=\"model\">값을 받을 모델입니다. 앱과 동일한 잠금으로 접근하십시오.</param>");
            b.AppendLine("/// <returns>마지막 스냅샷이 있어 복사했으면 참입니다. 첫 DBIRTH 전에는 거짓입니다.</returns>");
            b.AppendLine("/// <remarks>진단용 마지막 값도 복사합니다. 현재 유효성은 Receiver.IsAvailable로 따로 확인하십시오. 앱의 접근자에서 발생한 예외는 그대로 전달하며 부작용은 되돌리지 않습니다.</remarks>");
            b.AppendLine("/// <exception cref=\"global::System.ArgumentNullException\">모델이 null인 경우.</exception>");
            b.Append("public bool ReadInto(").Append(modelType).AppendLine(" model) {");
            b.AppendLine("if (model == null) throw new global::System.ArgumentNullException(\"model\");");
            b.AppendLine("var snapshot = Receiver.Read(); if (snapshot == null) return false;");
            b.AppendLine("model.ApplySparkplugSnapshot(snapshot, true); return true; }");
            b.AppendLine("}");
        }

        private const string Attributes = @"// <auto-generated/>
namespace VagabondK.nF.MQTT.Sparkplug.Mapping
{
    /// <summary>고정 스칼라 메트릭을 가진 partial 모델을 선언합니다.</summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Class, Inherited = false)]
    internal sealed class SparkplugMapAttribute : global::System.Attribute { }
    /// <summary>멤버의 메트릭 이름·형식·선택적 별칭을 선언합니다. 속성에 값을 대입해도 네트워크로 송신하지 않습니다.</summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Field | global::System.AttributeTargets.Property, Inherited = false)]
    internal sealed class SparkplugMetricAttribute : global::System.Attribute
    {
        /// <summary>멤버의 CLR 형식으로부터 Sparkplug 스칼라 형식을 빌드 시 확정합니다.</summary>
        /// <param name=""name"">1~64자의 출력 가능한 ASCII 메트릭 이름입니다.</param>
        public SparkplugMetricAttribute(string name) { }
        /// <summary>선택적 별칭입니다. 미지정과 0을 구별하며 생성자의 alias와 함께 지정할 수 없습니다.</summary>
        public ulong Alias { get; set; }
        /// <summary>이름 기반 스칼라 매핑을 선언합니다.</summary>
        /// <param name=""name"">1~64자의 출력 가능한 ASCII 메트릭 이름입니다.</param>
        /// <param name=""dataType"">멤버의 CLR 형식과 일치하는 Sparkplug 스칼라 형식입니다.</param>
        public SparkplugMetricAttribute(string name, global::VagabondK.nF.MQTT.Sparkplug.SparkplugDataType dataType) { }
        /// <summary>별칭을 포함한 스칼라 매핑을 선언합니다.</summary>
        /// <param name=""name"">1~64자의 출력 가능한 ASCII 메트릭 이름입니다.</param>
        /// <param name=""dataType"">멤버의 CLR 형식과 일치하는 Sparkplug 스칼라 형식입니다.</param>
        /// <param name=""alias"">BIRTH에 선언할 별칭입니다. 0과 ulong 최댓값도 유효합니다.</param>
        public SparkplugMetricAttribute(string name, global::VagabondK.nF.MQTT.Sparkplug.SparkplugDataType dataType, ulong alias) { }
    }
}
";
    }
}
