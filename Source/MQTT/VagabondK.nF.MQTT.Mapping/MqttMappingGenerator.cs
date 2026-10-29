using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace VagabondK.nF.MQTT.Mapping.Generator
{
    /// <summary>명시한 토픽과 고정 JSON 스칼라 계약에 맞춰 정적 코덱과 모델 바인딩을 생성합니다.</summary>
    [Generator(LanguageNames.CSharp)]
    public sealed class MqttMappingGenerator : IIncrementalGenerator
    {
        private const string Prefix = "VagabondK.nF.MQTT.Mapping.";
        private static readonly DiagnosticDescriptor Shape = Error("VKMQ001", "MQTT 매핑 클래스");
        private static readonly DiagnosticDescriptor Member = Error("VKMQ002", "MQTT 매핑 멤버");
        private static readonly DiagnosticDescriptor Options = Error("VKMQ003", "MQTT JSON 계약 옵션");
        private static readonly DiagnosticDescriptor Duplicate = Error("VKMQ004", "MQTT JSON 계약 중복");
        private static readonly DiagnosticDescriptor Collision = Error("VKMQ005", "MQTT 생성 이름 충돌");

        private static DiagnosticDescriptor Error(string id, string title)
        {
            return new DiagnosticDescriptor(id, title, "{0}", "MqttMapping", DiagnosticSeverity.Error, true);
        }

        /// <summary>매핑 특성과 증분 소스 생성 파이프라인을 등록합니다.</summary>
        /// <param name="context">컴파일러 초기화 컨텍스트입니다.</param>
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            context.RegisterPostInitializationOutput(c => c.AddSource("MqttJsonAttributes.g.cs",
                SourceText.From(VagabondK.nF.Mapping.Generator.MappingAttributeSource.ForBuild(Attributes), Encoding.UTF8)));
            var models = context.SyntaxProvider.ForAttributeWithMetadataName(Prefix + "MqttJsonMapAttribute",
                (node, _) => node is TypeDeclarationSyntax, (syntax, _) => (INamedTypeSymbol)syntax.TargetSymbol);
            context.RegisterSourceOutput(models.Collect(), (output, symbols) =>
            {
                var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
                int index = 0;
                foreach (var model in symbols.OrderBy(s => s.ToDisplayString(), StringComparer.Ordinal))
                    if (seen.Add(model)) Generate(output, model, "MqttMap" + (index++).ToString("D4", CultureInfo.InvariantCulture) + ".g.cs");
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
            if (model.Name == "MqttBinding" || model.GetMembers("MqttBinding").Length != 0)
                report(Collision, model, "MqttBinding은 생성 코드가 사용하는 이름입니다.");
            var mapAttributes = model.GetAttributes().Where(a => a.AttributeClass?.ToDisplayString() == Prefix + "MqttJsonMapAttribute").ToArray();
            var map = mapAttributes[0];
            string topic = map.ConstructorArguments.Length == 1 ? map.ConstructorArguments[0].Value as string : null;
            int maximum = Named(map, "MaximumBytes", 1024);
            if (mapAttributes.Length != 1 || !Ascii(topic, 256, false) || topic.IndexOf('+') >= 0 || topic.IndexOf('#') >= 0 ||
                maximum < 2 || maximum > 4096 || HasErrors(map))
                report(Options, model, "토픽은 와일드카드 없는 1~256자 ASCII이며 MaximumBytes는 2~4096이어야 합니다.");
            var points = new List<Point>();
            foreach (var symbol in model.GetMembers())
            {
                var attributes = symbol.GetAttributes().Where(a => a.AttributeClass?.ToDisplayString() == Prefix + "MqttJsonMemberAttribute").ToArray();
                if (attributes.Length == 0) continue;
                var attribute = attributes[0];
                var property = symbol as IPropertySymbol;
                var field = symbol as IFieldSymbol;
                if (symbol.IsStatic || (property == null && field == null) ||
                    (property != null && (property.IsIndexer || property.ExplicitInterfaceImplementations.Length != 0 ||
                        property.ReturnsByRef || property.ReturnsByRefReadonly || property.GetMethod == null ||
                        property.SetMethod == null || property.SetMethod.IsInitOnly)) ||
                    (field != null && (field.IsReadOnly || field.IsConst)))
                {
                    report(Member, symbol, "읽기/쓰기 가능한 인스턴스 필드 또는 비인덱서 속성이 필요합니다.");
                    continue;
                }
                var type = property?.Type ?? field.Type;
                bool binary = type.SpecialType == SpecialType.System_Boolean;
                if (!binary && type.SpecialType != SpecialType.System_Int32)
                {
                    report(Member, symbol, "고정 JSON scalar 계약은 bool과 int만 지원합니다.");
                    continue;
                }
                int order = attribute.ConstructorArguments.Length >= 1 && attribute.ConstructorArguments[0].Value is int n ? n : -1;
                string name = attribute.ConstructorArguments.Length == 1 ? symbol.Name :
                    attribute.ConstructorArguments.Length == 2 ? attribute.ConstructorArguments[1].Value as string : null;
                int minimum = Named(attribute, "Minimum", int.MinValue), limit = Named(attribute, "Maximum", int.MaxValue);
                if (attributes.Length != 1 || order < 0 || order > 31 || !Ascii(name, 64, true) ||
                    HasErrors(attribute) || minimum > limit ||
                    (binary && attribute.NamedArguments.Any(a => a.Key == "Minimum" || a.Key == "Maximum")))
                {
                    report(Options, symbol, "Order=0~31, 이스케이프 없는 1~64자 ASCII JSON 이름, int의 Minimum<=Maximum이 필요합니다. bool에는 범위를 지정하지 마십시오.");
                    continue;
                }
                if (symbol.Name == "Snapshot")
                    report(Collision, symbol, "Snapshot은 생성 스냅숏 형식 이름입니다.");
                points.Add(new Point(symbol, order, name, binary, minimum, limit));
            }
            if (points.Count < 1 || points.Count > 32)
                report(Member, model, "1~32개 JSON 멤버가 필요합니다.");
            points.Sort((a, b) => a.Order.CompareTo(b.Order));
            if (points.Select(p => p.Order).Distinct().Count() != points.Count ||
                points.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != points.Count)
                report(Duplicate, model, "JSON 이름과 Order는 각각 중복될 수 없습니다.");
            int required = 1 + points.Sum(p => p.Name.Length + 4 + (p.Binary ? 5 :
                Math.Max(p.Minimum.ToString(CultureInfo.InvariantCulture).Length, p.Maximum.ToString(CultureInfo.InvariantCulture).Length)));
            if (required > maximum)
                report(Options, model, "MaximumBytes는 최장 인코딩 " + required.ToString(CultureInfo.InvariantCulture) + "바이트 이상이어야 합니다.");
            if (failed) return;

            var b = new StringBuilder("// <auto-generated/>\n");
            if (!model.ContainingNamespace.IsGlobalNamespace)
                b.Append("namespace ").Append(model.ContainingNamespace.ToDisplayString()).AppendLine(" {");
            b.Append("partial class @").Append(model.Name).AppendLine(" {");
            EmitBinding(b, model, topic, maximum, points);
            b.AppendLine("}");
            if (!model.ContainingNamespace.IsGlobalNamespace) b.AppendLine("}");
            output.AddSource(hint, SourceText.From(SyntaxFactory.ParseCompilationUnit(b.ToString()).NormalizeWhitespace().ToFullString() + "\n", Encoding.UTF8));
        }

        private static bool HasErrors(AttributeData attribute)
        {
            return attribute.ConstructorArguments.Any(a => a.Kind == TypedConstantKind.Error) ||
                attribute.NamedArguments.Any(a => a.Value.Kind == TypedConstantKind.Error);
        }

        private static int Named(AttributeData attribute, string name, int fallback)
        {
            foreach (var pair in attribute.NamedArguments)
                if (pair.Key == name) return pair.Value.Value is int value ? value : fallback;
            return fallback;
        }

        private static bool Ascii(string text, int maximum, bool json)
        {
            return text != null && text.Length > 0 && text.Length <= maximum &&
                text.All(c => c >= 32 && c <= 126 && (!json || (c != '"' && c != '\\')));
        }

        private static string Quote(string text) => SymbolDisplay.FormatLiteral(text, true);
        private static string Int(int value) => value == int.MinValue ? "int.MinValue" : value.ToString(CultureInfo.InvariantCulture);

        private static void EmitBinding(StringBuilder b, INamedTypeSymbol model, string topic, int maximum, List<Point> points)
        {
            string type = model.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            b.AppendLine("/// <summary>고정 JSON 코덱과 명시적 모델 접근 API입니다. MQTT 연결은 앱이 소유합니다.</summary>");
            b.AppendLine("public static class MqttBinding {");
            b.AppendLine("/// <summary>발행과 TryApply에서 사용하는 정확한 토픽입니다.</summary>");
            b.Append("public const string Topic = ").Append(Quote(topic)).AppendLine(";");
            b.AppendLine("/// <summary>입력 페이로드의 최대 바이트 수입니다.</summary>");
            b.Append("public const int MaximumBytes = ").Append(maximum).AppendLine(";");
            b.AppendLine("/// <summary>모든 멤버의 구문과 범위를 검증한 불변 스냅샷입니다.</summary>");
            b.AppendLine("public sealed class Snapshot {");
            for (int i = 0; i < points.Count; i++)
            {
                b.AppendLine("/// <summary>계약에 따라 검증한 스칼라 값입니다.</summary>");
                b.Append("public ");
                if (new[] { "Equals", "ReferenceEquals", "GetHashCode", "GetType", "ToString", "MemberwiseClone", "Finalize" }.Contains(points[i].Symbol.Name))
                    b.Append("new ");
                b.Append(points[i].Type).Append(" @").Append(points[i].Symbol.Name).AppendLine(" { get; private set; }");
            }
            b.Append("internal Snapshot(");
            for (int i = 0; i < points.Count; i++)
            {
                if (i != 0) b.Append(", ");
                b.Append(points[i].Type).Append(" v").Append(i);
            }
            b.AppendLine(") {");
            for (int i = 0; i < points.Count; i++)
            {
                var p = points[i];
                if (!p.Binary)
                    b.Append("if (v").Append(i).Append(" < ").Append(Int(p.Minimum)).Append(" || v").Append(i).Append(" > ").Append(Int(p.Maximum))
                        .AppendLine(") throw new global::System.ArgumentOutOfRangeException(\"snapshot\");");
                b.Append("this.@").Append(p.Symbol.Name).Append(" = v").Append(i).AppendLine(";");
            }
            b.AppendLine("} }");
            b.AppendLine("/// <summary>모델 값을 한 번씩 읽고 범위를 확인해 Order 순서의 JSON을 생성합니다.</summary>");
            b.AppendLine("/// <param name=\"model\">앱이 소유하는 모델입니다. 일관된 값을 읽도록 앱에서 잠금으로 보호하십시오.</param>");
            b.AppendLine("/// <returns>새 ASCII JSON 페이로드입니다.</returns>");
            b.AppendLine("/// <exception cref=\"global::System.ArgumentNullException\">model이 null인 경우.</exception>");
            b.AppendLine("/// <exception cref=\"global::System.ArgumentOutOfRangeException\">모델 값이 선언 범위를 벗어난 경우.</exception>");
            b.Append("public static byte[] Encode(").Append(type).AppendLine(" model) {");
            b.AppendLine("int length; return EncodeCore(model, null, 0, out length); }");
            b.AppendLine("/// <summary>모델 값을 제공된 버퍼에 기록하고 기록한 바이트 수를 반환합니다. 생성 인코더 내부의 성공 경로는 힙을 할당하지 않습니다.</summary>");
            b.AppendLine("/// <remarks>앱 접근자와 전송 계층의 할당은 별개입니다. 반환 길이만 송신하고 비동기 송신 완료 전에는 버퍼를 재사용하지 마십시오.</remarks>");
            b.AppendLine("/// <param name=\"model\">한 번씩 읽을 모델입니다. 앱에서 잠금으로 보호하십시오.</param>");
            b.AppendLine("/// <param name=\"buffer\">호출자가 소유하며 송신이 끝날 때까지 유지할 버퍼입니다.</param>");
            b.AppendLine("/// <param name=\"offset\">기록을 시작할 위치입니다.</param>");
            b.AppendLine("/// <returns>offset부터 기록한 바이트 수입니다.</returns>");
            b.AppendLine("/// <exception cref=\"global::System.ArgumentNullException\">model 또는 buffer가 null입니다.</exception>");
            b.AppendLine("/// <exception cref=\"global::System.ArgumentOutOfRangeException\">offset 또는 모델 값이 범위를 벗어났습니다.</exception>");
            b.AppendLine("/// <exception cref=\"global::System.ArgumentException\">버퍼의 남은 공간이 부족합니다. 버퍼는 변경되지 않습니다.</exception>");
            b.Append("public static int Encode(").Append(type).AppendLine(" model, byte[] buffer, int offset = 0) {");
            b.AppendLine("if(buffer==null) throw new global::System.ArgumentNullException(\"buffer\"); int length; EncodeCore(model,buffer,offset,out length); return length; }");
            b.Append("private static byte[] EncodeCore(").Append(type).AppendLine(" model, byte[] buffer, int offset, out int length) {");
            b.AppendLine("if (model == null) throw new global::System.ArgumentNullException(\"model\");");
            b.AppendLine("if(offset<0 || (buffer!=null && offset>buffer.Length)) throw new global::System.ArgumentOutOfRangeException(\"offset\");");
            for (int i = 0; i < points.Count; i++)
            {
                var p = points[i];
                b.Append(p.Type).Append(" v").Append(i).Append(" = model.@").Append(p.Symbol.Name).AppendLine(";");
                if (!p.Binary)
                    b.Append("if (v").Append(i).Append(" < ").Append(Int(p.Minimum)).Append(" || v").Append(i).Append(" > ").Append(Int(p.Maximum))
                        .AppendLine(") throw new global::System.ArgumentOutOfRangeException(\"model\");");
            }
            int fixedBytes = 1 + points.Sum(p => p.Name.Length + 4);
            b.Append("length = ").Append(fixedBytes);
            for (int i = 0; i < points.Count; i++)
            {
                var p = points[i];
                b.Append(" + ").Append(p.Binary ? "(v" + i + " ? 4 : 5)" : "IntegerLength(v" + i + ")");
            }
            b.AppendLine(";");
            b.AppendLine("if(buffer==null) buffer=new byte[length]; else if(buffer.Length-offset<length) throw new global::System.ArgumentException(\"JSON buffer capacity.\",\"buffer\");");
            b.AppendLine("int position=offset;");
            for (int i = 0; i < points.Count; i++)
            {
                var p = points[i];
                b.Append("WriteText(buffer,ref position,").Append(Quote((i == 0 ? "{" : ",") + "\"" + p.Name + "\":")).AppendLine(");");
                if (p.Binary)
                    b.Append("WriteText(buffer,ref position,v").Append(i).AppendLine(" ? \"true\" : \"false\");");
                else
                    b.Append("WriteInteger(buffer,ref position,v").Append(i).AppendLine(");");
            }
            b.AppendLine("buffer[position]=(byte)'}'; return buffer; }");
            b.AppendLine("/// <summary>고정 계약에 따라 전체 JSON을 해석합니다. 필드 누락·중복·정의되지 않은 이름·이스케이프·비ASCII 문자·후행 데이터는 거부합니다.</summary>");
            b.AppendLine("/// <param name=\"bytes\">JSON 페이로드입니다. null, 빈 배열, MaximumBytes를 초과하는 배열은 거부합니다.</param>");
            b.AppendLine("/// <param name=\"snapshot\">성공 시 검증한 값이며 실패 시 null입니다.</param>");
            b.AppendLine("/// <returns>모든 값이 구문과 범위를 만족하면 true입니다.</returns>");
            b.AppendLine("public static bool TryDecode(byte[] bytes, out Snapshot snapshot) {");
            b.AppendLine("snapshot = null; if (bytes == null || bytes.Length == 0 || bytes.Length > MaximumBytes) return false;");
            b.AppendLine("int p = 0; uint seen = 0U;");
            for (int i = 0; i < points.Count; i++) b.Append(points[i].Type).Append(" v").Append(i).Append(" = ").Append(points[i].Binary ? "false" : "0").AppendLine(";");
            b.AppendLine("if (!Take(bytes, ref p, '{')) return false;");
            b.Append("for (int field = 0; field < ").Append(points.Count).AppendLine("; field++) { Skip(bytes, ref p); int index;");
            for (int i = 0; i < points.Count; i++)
                b.Append(i == 0 ? "if" : "else if").Append(" (Literal(bytes, ref p, ").Append(Quote("\"" + points[i].Name + "\"")).Append(")) index = ").Append(i).AppendLine(";");
            b.AppendLine("else return false; uint bit = 1U << index; if ((seen & bit) != 0U || !Take(bytes, ref p, ':')) return false; seen |= bit; Skip(bytes, ref p); switch (index) {");
            for (int i = 0; i < points.Count; i++)
            {
                var p = points[i];
                b.Append("case ").Append(i).AppendLine(":");
                if (p.Binary)
                    b.Append("if (Literal(bytes, ref p, \"true\")) v").Append(i).AppendLine(" = true; else if (!Literal(bytes, ref p, \"false\")) return false;");
                else
                    b.Append("if (!Number(bytes, ref p, out v").Append(i).Append(") || v").Append(i).Append(" < ").Append(Int(p.Minimum))
                        .Append(" || v").Append(i).Append(" > ").Append(Int(p.Maximum)).AppendLine(") return false;");
                b.AppendLine("break;");
            }
            b.AppendLine("}");
            b.Append("if (!Take(bytes, ref p, field == ").Append(points.Count - 1).AppendLine(" ? '}' : ',')) return false; }");
            b.AppendLine("Skip(bytes, ref p); if (p != bytes.Length) return false;");
            b.Append("snapshot = new Snapshot(").Append(string.Join(", ", Enumerable.Range(0, points.Count).Select(i => "v" + i))).AppendLine("); return true; }");
            b.AppendLine("/// <summary>검증한 스냅샷을 Order 순서로 명시적으로 반영합니다.</summary>");
            b.AppendLine("/// <param name=\"model\">대상 모델입니다.</param>");
            b.AppendLine("/// <param name=\"snapshot\">TryDecode로 얻은 스냅샷입니다.</param>");
            b.AppendLine("/// <remarks>접근자에서 발생한 예외는 그대로 전달하며, 이미 발생한 부작용은 되돌리지 않습니다. 앱에서 잠금으로 보호하십시오.</remarks>");
            b.AppendLine("/// <exception cref=\"global::System.ArgumentNullException\">인수가 null인 경우.</exception>");
            b.Append("public static void Apply(").Append(type).AppendLine(" model, Snapshot snapshot) {");
            b.AppendLine("if (model == null) throw new global::System.ArgumentNullException(\"model\"); if (snapshot == null) throw new global::System.ArgumentNullException(\"snapshot\");");
            foreach (var p in points) b.Append("model.@").Append(p.Symbol.Name).Append(" = snapshot.@").Append(p.Symbol.Name).AppendLine(";");
            b.AppendLine("}");
            b.AppendLine("/// <summary>토픽이 정확히 일치하고 전체 페이로드가 유효할 때만 모델에 대입합니다.</summary>");
            b.AppendLine("/// <param name=\"model\">대상 모델입니다.</param>");
            b.AppendLine("/// <param name=\"receivedTopic\">수신한 정확한 토픽입니다.</param>");
            b.AppendLine("/// <param name=\"payload\">수신 JSON입니다.</param>");
            b.AppendLine("/// <returns>토픽과 데이터가 유효하고 적용을 완료하면 true입니다.</returns>");
            b.AppendLine("/// <remarks>보관 메시지 여부·순번·값의 유효 시간·연결 정책은 반영 전에 앱이 검사해야 합니다. 접근자에서 발생한 예외는 숨기지 않습니다.</remarks>");
            b.AppendLine("/// <exception cref=\"global::System.ArgumentNullException\">model이 null인 경우.</exception>");
            b.Append("public static bool TryApply(").Append(type).AppendLine(" model, string receivedTopic, byte[] payload) {");
            b.AppendLine("if (model == null) throw new global::System.ArgumentNullException(\"model\"); if (receivedTopic != Topic) return false; Snapshot snapshot; if (!TryDecode(payload, out snapshot)) return false; Apply(model, snapshot); return true; }");
            b.AppendLine("/// <summary>모델을 한 번 인코딩한 뒤 앱에서 제공한 발행 함수에 전달합니다.</summary>");
            b.AppendLine("/// <param name=\"model\">발행할 모델입니다.</param>");
            b.AppendLine("/// <param name=\"publisher\">앱의 MQTT 연결·QoS·보관 메시지·ACK 정책을 구현하는 함수입니다.</param>");
            b.AppendLine("/// <remarks>이 API 자체는 연결 생성, 재시도, ACK 대기를 하지 않습니다. 발행 함수와 접근자에서 발생한 예외는 그대로 전달합니다.</remarks>");
            b.AppendLine("/// <exception cref=\"global::System.ArgumentNullException\">인수가 null인 경우.</exception>");
            b.AppendLine("/// <exception cref=\"global::System.ArgumentOutOfRangeException\">모델 값이 선언 범위를 벗어난 경우.</exception>");
            b.Append("public static void Publish(").Append(type).AppendLine(" model, global::VagabondK.nF.MQTT.Mapping.MqttJsonPublisher publisher) {");
            b.AppendLine("if (publisher == null) throw new global::System.ArgumentNullException(\"publisher\"); publisher(Topic, Encode(model)); }");
            using (var reader = new StreamReader(typeof(MqttMappingGenerator).Assembly.GetManifestResourceStream("JsonReader.cs.txt")))
                b.AppendLine(reader.ReadToEnd());
            b.AppendLine("}");
        }

        private sealed class Point
        {
            internal Point(ISymbol symbol, int order, string name, bool binary, int minimum, int maximum)
            { Symbol = symbol; Order = order; Name = name; Binary = binary; Minimum = minimum; Maximum = maximum; }
            internal readonly ISymbol Symbol;
            internal readonly int Order, Minimum, Maximum;
            internal readonly string Name;
            internal readonly bool Binary;
            internal string Type => Binary ? "bool" : "int";
        }

        private const string Attributes = @"// <auto-generated/>
namespace VagabondK.nF.MQTT.Mapping {
/// <summary>앱이 소유하는 MQTT 연결을 통해 고정 JSON을 발행하는 함수입니다.</summary>
/// <param name=""topic"">선언한 정확한 토픽입니다.</param>
/// <param name=""payload"">새 JSON 페이로드입니다.</param>
public delegate void MqttJsonPublisher(string topic, byte[] payload);
/// <summary>고정 평면 JSON 계약과 정확한 MQTT 토픽을 선언합니다.</summary>
[global::System.AttributeUsage(global::System.AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
internal sealed class MqttJsonMapAttribute : global::System.Attribute {
/// <summary>정확한 ASCII 토픽으로 매핑을 선언합니다.</summary>
/// <param name=""topic"">와일드카드 없는 1~256자 ASCII 토픽입니다.</param>
public MqttJsonMapAttribute(string topic) { Topic = topic; MaximumBytes = 1024; }
/// <summary>정확한 토픽입니다.</summary>
public string Topic { get; private set; }
/// <summary>입력의 최대 바이트 수입니다. 2~4096이며 최장 인코딩을 수용해야 합니다.</summary>
public int MaximumBytes { get; set; }
}
/// <summary>bool 또는 int 멤버의 필수 JSON 이름과 인코딩 순서를 선언합니다.</summary>
[global::System.AttributeUsage(global::System.AttributeTargets.Field | global::System.AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
internal sealed class MqttJsonMemberAttribute : global::System.Attribute {
/// <summary>이스케이프 없는 ASCII JSON 이름과 순서를 지정합니다.</summary>
/// <param name=""order"">중복 없는 0~31 순서입니다.</param>
/// <param name=""name"">큰따옴표와 역슬래시가 없는 1~64자 ASCII 이름입니다.</param>
public MqttJsonMemberAttribute(int order, string name) { Order = order; Name = name; Minimum = int.MinValue; Maximum = int.MaxValue; }
/// <summary>멤버 이름을 그대로 JSON 이름으로 사용합니다. 멤버 이름을 바꾸면 전송 계약도 바뀝니다.</summary>
/// <param name=""order"">중복 없는 0~31 순서입니다.</param>
public MqttJsonMemberAttribute(int order) : this(order, null) { }
/// <summary>인코딩과 적용 순서입니다.</summary>
public int Order { get; private set; }
/// <summary>필수 JSON 이름입니다.</summary>
public string Name { get; private set; }
/// <summary>int 멤버에 허용하는 최솟값입니다. 이 값도 허용하며 기본값은 int.MinValue입니다.</summary>
public int Minimum { get; set; }
/// <summary>int 멤버에 허용하는 최댓값입니다. 이 값도 허용하며 기본값은 int.MaxValue입니다.</summary>
public int Maximum { get; set; }
}
}";
    }
}
