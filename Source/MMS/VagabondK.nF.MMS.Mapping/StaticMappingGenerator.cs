using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace VagabondK.nF.Mapping.Generator
{
    internal sealed class StaticMappingGenerator
    {
        private const string M = "global::VagabondK.nF.MMS.";
        private const string I = "global::VagabondK.nF.IEC61850.";
        private readonly bool iec;
        private readonly string name, prefix, code;
        internal StaticMappingGenerator(bool iec)
        {
            this.iec = iec; name = iec ? "Iec61850" : "Mms";
            prefix = "VagabondK.nF." + (iec ? "IEC61850" : "MMS") + ".Mapping.";
            code = iec ? "VKIE" : "VKMM";
        }
        internal void Initialize(IncrementalGeneratorInitializationContext context)
        {
            context.RegisterPostInitializationOutput(c => c.AddSource(name + "MappingAttributes.g.cs",
                SourceText.From(Attributes(), Encoding.UTF8)));
            foreach (string role in new[] { "Client", "Server" })
            {
                string selected = role;
                var models = context.SyntaxProvider.ForAttributeWithMetadataName(prefix + name + role + "MapAttribute",
                    (node, _) => node is TypeDeclarationSyntax, (syntax, _) => (INamedTypeSymbol)syntax.TargetSymbol);
                context.RegisterSourceOutput(models.Collect(), (output, symbols) =>
                {
                    var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
                    int index = 0;
                    foreach (var model in symbols.OrderBy(s => s.ToDisplayString(), StringComparer.Ordinal))
                        if (seen.Add(model)) Generate(output, model, selected, index++);
                });
            }
        }
        private DiagnosticDescriptor Error(int id)
        { return new DiagnosticDescriptor(code + id.ToString("D3", CultureInfo.InvariantCulture), name + " mapping", "{0}", name + "Mapping", DiagnosticSeverity.Error, true); }
        private string Attributes()
        {
            string p = "namespace " + prefix.TrimEnd('.') + " {\n";
            foreach (string role in new[] { "Client", "Server" })
                p += "/// <summary>모델에서 " + (role == "Client" ? "수신 값을 반영하는 클라이언트" : "제공할 값을 보관하는 서버") + " 바인딩을 정적으로 생성하도록 지정합니다.</summary>\n" +
                    "[global::System.AttributeUsage(global::System.AttributeTargets.Class)] internal sealed class " + name + role + "MapAttribute : global::System.Attribute {\n" +
                    "/// <summary>item만 지정한 Point에 적용할 공통 MMS domain입니다. 두 인수 Point의 domain이 우선합니다.</summary>\n" +
                    "public string Domain { get; set; } }\n";
            p += "/// <summary>모델 멤버를 고정된 MMS domain/item에 연결합니다.</summary>\n" +
                "[global::System.AttributeUsage(global::System.AttributeTargets.Field | global::System.AttributeTargets.Property)] internal sealed class " + name + "PointAttribute : global::System.Attribute {\n" +
                "/// <summary>전송 이름을 지정합니다. 값 변환은 생성된 바인딩이 수행합니다.</summary>\n" +
                "/// <param name=\"domain\">공백 없는 ASCII 1~64자의 MMS domain입니다.</param>\n" +
                "/// <param name=\"item\">공백 없는 ASCII 1~64자의 MMS item입니다.</param>\n" +
                "public " + name + "PointAttribute(string domain, string item) { }\n" +
                "/// <summary>클래스의 공통 Domain에 속하는 item을 지정합니다.</summary>\n" +
                "/// <param name=\"item\">공백 없는 ASCII 1~64자의 MMS item입니다.</param>\n" +
                "public " + name + "PointAttribute(string item) { }\n" +
                "/// <summary>명시적 쓰기를 허용할지 지정합니다. Boolean·Int32·Float32에만 사용할 수 있습니다.</summary>\n" +
                "public bool Writable { get; set; }\n" +
                "/// <summary>서버 쓰기 전 호출할 부작용 없는 검증 메서드 이름입니다. -1은 허용, 0~11은 접근 거절입니다.</summary>\n" +
                "public string Validate { get; set; }\n";
            if (iec) p += "/// <summary>UTC 시각과 함께 읽고 쓸 원시 시간 품질 바이트를 보관하는 멤버의 이름입니다.</summary>\npublic string TimeQuality { get; set; }\n";
            p += "}\n";
            if (iec)
            {
                p += "/// <summary>서버 모델 메서드를 IEC 61850 제어 실행 처리기로 연결합니다.</summary>\n" +
                    "[global::System.AttributeUsage(global::System.AttributeTargets.Method)] internal sealed class Iec61850ControlAttribute : global::System.Attribute {\n" +
                    "/// <summary>제어의 고정 전송 이름과 모델을 지정합니다.</summary>\n" +
                    "/// <param name=\"domain\">제어가 속한 MMS domain입니다.</param>\n" +
                    "/// <param name=\"item\">LN$CO$object 형식의 제어 item입니다.</param>\n" +
                    "/// <param name=\"model\">Direct/SBO normal/enhanced 제어 모델입니다.</param>\n" +
                    "public Iec61850ControlAttribute(string domain, string item, " + I + "MmsControlModel model) { }\n" +
                    "/// <summary>Test 요청에도 실행할 필수 허용 검사 메서드 이름입니다. -1은 허용, 0~11은 접근 거절입니다.</summary>\n" +
                    "public string Validate { get; set; }\n" +
                    "/// <summary>Boolean·DoublePoint·AnalogFloat32 중 명시적으로 사용할 제어 값 프로파일입니다.</summary>\n" +
                    "public " + I + "MmsControlValueKind ValueKind { get; set; } }\n";
                p += "/// <summary>클라이언트 바인딩에서 생성할 원격 제어의 전송 이름을 선언합니다.</summary>\n" +
                    "[global::System.AttributeUsage(global::System.AttributeTargets.Class, AllowMultiple=true)] internal sealed class Iec61850RemoteControlAttribute : global::System.Attribute {\n" +
                    "/// <summary>원격 제어의 고정 MMS 이름을 지정합니다.</summary>\n" +
                    "/// <param name=\"domain\">제어가 속한 MMS domain입니다.</param>\n" +
                    "/// <param name=\"item\">LN$CO$object 형식의 제어 item입니다.</param>\n" +
                    "public Iec61850RemoteControlAttribute(string domain, string item) { }\n" +
                    "/// <summary>원격 제어와 일치시킬 Boolean·DoublePoint·AnalogFloat32 값 프로파일입니다.</summary>\n" +
                    "public " + I + "MmsControlValueKind ValueKind { get; set; } }\n";
            }
            return MappingAttributeSource.ForBuild(p + "}\n");
        }
        private static string Text(AttributeData a, string key)
        { return a.NamedArguments.FirstOrDefault(p => p.Key == key).Value.Value as string; }
        private static int Number(AttributeData a, string key)
        {
            var value = a.NamedArguments.FirstOrDefault(p => p.Key == key).Value.Value;
            return value == null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        private static bool Has(AttributeData a, string key) { return a.NamedArguments.Any(p => p.Key == key); }
        private static bool Identifier(string value)
        { return value != null && value.Length >= 1 && value.Length <= 64 && value.All(c => c >= 33 && c <= 126); }
        private static string Literal(string value) { return SymbolDisplay.FormatLiteral(value, true); }
        private static string Type(ISymbol s) { return (s is IFieldSymbol f ? f.Type : ((IPropertySymbol)s).Type).ToDisplayString(); }
        private static bool Member(ISymbol s, bool read, bool write)
        {
            if (s == null || s.IsStatic) return false;
            if (s is IFieldSymbol f) return !f.IsConst && (!write || !f.IsReadOnly);
            return s is IPropertySymbol p && !p.IsIndexer && !p.ReturnsByRef && !p.ReturnsByRefReadonly &&
                p.ExplicitInterfaceImplementations.Length == 0 && (!read || p.GetMethod != null) &&
                (!write || (p.SetMethod != null && !p.SetMethod.IsInitOnly));
        }
        private static bool Method(INamedTypeSymbol model, string name, string type)
        {
            if (name == null) return false;
            var methods = model.GetMembers(name).OfType<IMethodSymbol>().ToArray();
            return methods.Length == 1 && methods[0].MethodKind == MethodKind.Ordinary && !methods[0].IsStatic && !methods[0].IsGenericMethod &&
                methods[0].ReturnsByRef == false && methods[0].ReturnType.SpecialType == SpecialType.System_Int32 &&
                !methods[0].IsAsync && methods[0].Parameters.Length == 1 &&
                methods[0].Parameters[0].RefKind == RefKind.None && methods[0].Parameters[0].Type.ToDisplayString() == type &&
                methods[0].ExplicitInterfaceImplementations.Length == 0;
        }
        private sealed class Point
        {
            internal ISymbol Symbol;
            internal string Domain, Item, Kind, Validate, TimeQuality;
            internal bool Writable;
        }
        private sealed class Control
        {
            internal ISymbol Symbol;
            internal string Domain, Item, Validate;
            internal int Model, Kind;
        }
        private void Generate(SourceProductionContext output, INamedTypeSymbol model, string role, int index)
        {
            bool server = role == "Server", failed = false;
            Action<int, ISymbol, string> report = (id, s, message) =>
            { failed = true; output.ReportDiagnostic(Diagnostic.Create(Error(id), s.Locations.FirstOrDefault(), message)); };
            if (model.TypeKind != TypeKind.Class || model.IsStatic || model.IsAbstract || model.IsFileLocal ||
                model.IsGenericType || model.ContainingType != null || model.BaseType?.SpecialType != SpecialType.System_Object ||
                model.DeclaringSyntaxReferences.Any(r => !(r.GetSyntax() is ClassDeclarationSyntax c) || !c.Modifiers.Any(SyntaxKind.PartialKeyword)))
            { report(1, model, "최상위 비제네릭·비상속 partial class가 필요합니다."); return; }
            string binding = name + role + "Binding";
            if (model.Name == binding || model.GetMembers(binding).Length > 0) report(5, model, binding + "은 예약된 생성 이름입니다.");
            if (model.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == prefix + name + (server ? "Client" : "Server") + "MapAttribute"))
                report(1, model, "같은 모델에 Client와 Server 역할을 혼합하지 마십시오.");
            var map = model.GetAttributes().First(a => a.AttributeClass?.ToDisplayString() == prefix + name + role + "MapAttribute");
            string defaultDomain = Text(map, "Domain");
            if (Has(map, "Domain") && (!Identifier(defaultDomain) || map.NamedArguments.Any(a => a.Value.Kind == TypedConstantKind.Error)))
                report(3, model, "공통 Domain은 공백 없는 ASCII 1~64자여야 합니다.");
            var points = new List<Point>();
            foreach (var symbol in model.GetMembers())
            {
                var a = symbol.GetAttributes().FirstOrDefault(x => x.AttributeClass?.ToDisplayString() == prefix + name + "PointAttribute");
                if (a == null) continue;
                if (a.NamedArguments.Any(x => x.Value.Kind == TypedConstantKind.Error))
                { report(3, symbol, "매핑 옵션에는 올바른 상수 인수가 필요합니다."); continue; }
                bool writable = Number(a, "Writable") != 0;
                if (!Member(symbol, server || writable, !server || writable))
                { report(2, symbol, "역할에 맞는 인스턴스 필드/속성의 getter/setter가 필요합니다."); continue; }
                string kind = Type(symbol) == "bool" ? "Boolean" : Type(symbol) == "int" ? "Integer" : Type(symbol) == "float" ? "FloatingPoint" :
                    iec && Type(symbol) == "VagabondK.nF.IEC61850.IecDoublePoint" ? "DoublePoint" :
                    iec && Type(symbol) == "VagabondK.nF.IEC61850.IecQuality" ? "Quality" :
                    iec && Type(symbol) == "long" ? "Timestamp" : null;
                if ((a.ConstructorArguments.Length != 1 && a.ConstructorArguments.Length != 2) ||
                    a.ConstructorArguments.Any(x => x.Kind == TypedConstantKind.Error))
                { report(3, symbol, "domain/item 상수 문자열이 필요합니다."); continue; }
                bool shorthand = a.ConstructorArguments.Length == 1;
                string domain = shorthand ? defaultDomain : a.ConstructorArguments[0].Value as string,
                    item = a.ConstructorArguments[shorthand ? 0 : 1].Value as string;
                string validator = Text(a, "Validate"), time = Text(a, "TimeQuality");
                if (kind == null) { report(2, symbol, "bool/int/float 또는 IEC IecDoublePoint/IecQuality/UTC long만 지원합니다."); continue; }
                if (!Identifier(domain) || !Identifier(item) || a.NamedArguments.Any(p => p.Value.Kind == TypedConstantKind.Error) ||
                    (iec && (item.Contains("$CO$") || item.Contains("$RP$"))) ||
                    (writable && (kind == "DoublePoint" || kind == "Quality" || kind == "Timestamp")) ||
                    (Has(a, "Validate") && (!server || !writable || !Method(model, validator, "VagabondK.nF.MMS.MmsValue"))))
                    report(3, symbol, "ASCII 1~64자 이름, scalar 쓰기, Server Writable의 int Validate(MmsValue)만 지원합니다. CO/RP는 별도 제어를 사용하십시오.");
                var qualityMember = model.GetMembers(time ?? "").FirstOrDefault();
                if (Has(a, "TimeQuality") && (kind != "Timestamp" || !Member(qualityMember, server, !server) ||
                    Type(qualityMember) != "byte"))
                    report(3, symbol, "TimeQuality는 Timestamp와 같은 모델의 인스턴스 byte 멤버여야 합니다.");
                points.Add(new Point { Symbol = symbol, Domain = domain, Item = item, Kind = kind, Writable = writable, Validate = validator, TimeQuality = time });
            }
            points.Sort((a, b) => string.CompareOrdinal(a.Domain + "\0" + a.Item, b.Domain + "\0" + b.Item));
            if (points.Count < 1 || points.Count > 16) report(2, model, "한 모델은 1~16개 Point를 지원합니다.");
            var occupied = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in points)
                if (!occupied.Add(p.Domain + "\0" + p.Item)) report(4, p.Symbol, "중복된 MMS 이름입니다.");
            if (!server)
            {
                var targets = new HashSet<string>(points.Select(p => p.Symbol.Name), StringComparer.Ordinal);
                foreach (var p in points.Where(p => p.TimeQuality != null))
                    if (!targets.Add(p.TimeQuality)) report(4, p.Symbol, "수신 값/시간 품질의 저장 멤버를 공유할 수 없습니다.");
            }
            var controls = new List<Control>();
            if (iec)
            {
                foreach (var symbol in server ? model.GetMembers().ToArray() : new ISymbol[] { model })
                foreach (var a in symbol.GetAttributes().Where(a => a.AttributeClass?.ToDisplayString() == prefix + name + (server ? "ControlAttribute" : "RemoteControlAttribute")))
                {
                    if (a.ConstructorArguments.Length != (server ? 3 : 2) || a.ConstructorArguments.Any(x => x.Kind == TypedConstantKind.Error) ||
                        a.NamedArguments.Any(x => x.Value.Kind == TypedConstantKind.Error))
                    { report(6, symbol, "제어에는 올바른 상수 인수가 필요합니다."); continue; }
                    string domain = a.ConstructorArguments[0].Value as string, item = a.ConstructorArguments[1].Value as string;
                    string validate = Text(a, "Validate"); int kind = Number(a, "ValueKind");
                    int mode = server ? Convert.ToInt32(a.ConstructorArguments[2].Value, CultureInfo.InvariantCulture) : 0;
                    int co = item == null ? -1 : item.IndexOf("$CO$", StringComparison.Ordinal);
                    if (!Identifier(domain) || !Identifier(item) || item.Length > 53 || co < 1 ||
                        item.IndexOf('$') != co || item.Substring(co + 4).Length == 0 || item.Substring(co + 4).Contains("$") ||
                        kind < 0 || kind > 2 || (server && (mode < 1 || mode > 4 ||
                        !Method(model, symbol.Name, "VagabondK.nF.IEC61850.Server.MmsControlRequest") ||
                        !Method(model, validate, "VagabondK.nF.IEC61850.Server.MmsControlRequest"))))
                    { report(6, symbol, "CO 이름(최대 53자), 모델 1~4, 값 종류 0~2, int 실행/Validate(MmsControlRequest)가 필요합니다."); continue; }
                    controls.Add(new Control { Symbol = symbol, Domain = domain, Item = item, Model = mode, Kind = kind, Validate = validate });
                }
                if (controls.Count > 8) report(6, model, "제어는 모델당 최대 8개입니다.");
                if (server && model.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == prefix + name + "RemoteControlAttribute") ||
                    !server && model.GetMembers().Any(s => s.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == prefix + name + "ControlAttribute")))
                    report(6, model, "반대 역할의 제어 특성을 사용할 수 없습니다.");
                foreach (var c in controls)
                {
                    var names = new List<string> { c.Item };
                    if (server)
                    {
                        string stem = c.Item.Substring(0, c.Item.IndexOf("$CO$", StringComparison.Ordinal)), obj = c.Item.Substring(stem.Length + 4);
                        names.AddRange(new[] { c.Item + "$Oper", c.Item + "$Cancel", stem + "$CF$" + obj + "$ctlModel",
                            stem + (c.Kind == 2 ? "$MX$" : "$ST$") + obj + (c.Kind == 2 ? "$mxVal" : "$stVal") });
                        if (c.Kind == 2) names.Add(stem + "$MX$" + obj + "$mxVal$f");
                        if (c.Model == 2 || c.Model == 4) names.AddRange(new[] { c.Item + (c.Model == 2 ? "$SBO" : "$SBOw"),
                            stem + "$CF$" + obj + "$sboTimeout", stem + "$CF$" + obj + "$sboClass" });
                    }
                    foreach (string n in names)
                        if (!occupied.Add(c.Domain + "\0" + n)) report(4, c.Symbol, "제어 확장 이름과 Point/다른 제어 이름이 충돌합니다.");
                }
            }
            int fields = model.GetMembers().OfType<IFieldSymbol>().Count();
            int methods = model.GetMembers().OfType<IMethodSymbol>().Count(m => m.AssociatedSymbol == null) +
                model.GetMembers().OfType<IPropertySymbol>().Sum(p => (p.GetMethod == null ? 0 : 1) + (p.SetMethod == null ? 0 : 1)) +
                model.GetMembers().OfType<IEventSymbol>().Sum(e => (e.AddMethod == null ? 0 : 1) + (e.RemoveMethod == null ? 0 : 1));
            if (fields > 255 || methods > 255) report(7, model, "nanoFramework 모델 필드/메서드 상한 255를 초과합니다.");
            if (failed) return;
            var b = new StringBuilder("// <auto-generated/>\n");
            if (!model.ContainingNamespace.IsGlobalNamespace) b.Append("namespace ").Append(model.ContainingNamespace.ToDisplayString()).AppendLine(" {");
            b.AppendLine("/// <summary>고정 MMS 전송 이름과 연결된 사용자 데이터 모델입니다.</summary>");
            b.Append("partial class @").Append(model.Name).AppendLine(" {");
            b.AppendLine(server
                ? "/// <summary>모델 값을 서버 변수로 게시하고 명시적으로 허용한 쓰기와 제어를 연결합니다.</summary>"
                : "/// <summary>원격 값을 검증한 뒤 사용자 모델에 반영하는 정적 클라이언트 바인딩입니다.</summary>");
            b.AppendLine("/// <remarks>syncRoot는 모델 접근을 보호합니다. 네트워크 호출과 세션 처리는 호출자가 별도로 직렬화해야 합니다.</remarks>");
            b.Append("public sealed class ").Append(binding).AppendLine(" {");
            b.Append("private readonly ").Append(model.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).AppendLine(" _model;");
            b.AppendLine("private readonly object _sync;");
            b.AppendLine("/// <summary>고정 MMS 이름 순서로 매핑한 포인트 수입니다. 제어 확장 변수는 제외합니다.</summary>");
            b.Append("public int Count { get { return ").Append(points.Count).AppendLine("; } }");
            if (server) EmitServer(b, model, binding, points, controls);
            else EmitClient(b, model, binding, points, controls);
            b.AppendLine("} }");
            if (!model.ContainingNamespace.IsGlobalNamespace) b.AppendLine("}");
            output.AddSource(name + role + index.ToString("D4", CultureInfo.InvariantCulture) + ".g.cs",
                SourceText.From(SyntaxFactory.ParseCompilationUnit(b.ToString()).NormalizeWhitespace().ToFullString() + "\n", Encoding.UTF8));
        }
        private static void Constructor(StringBuilder b, INamedTypeSymbol model, string binding, string extra = "")
        {
            b.AppendLine("/// <summary>사용자 모델과 동기화 객체를 연결하고 고정 매핑을 준비합니다.</summary>");
            b.AppendLine("/// <param name=\"model\">바인딩이 수명 동안 참조하는 사용자 모델입니다.</param>");
            b.AppendLine("/// <param name=\"syncRoot\">모델에 접근하는 애플리케이션 코드에서도 공유할 잠금 객체입니다.</param>");
            if (extra.Length != 0) b.AppendLine("/// <param name=\"client\">호출자 소유의 연결된 클라이언트입니다. 네트워크 작업은 별도로 직렬화하십시오.</param>");
            b.AppendLine("/// <exception cref=\"global::System.ArgumentNullException\">필수 모델·동기화 객체 또는 클라이언트가 null입니다.</exception>");
            b.Append("public ").Append(binding).Append("(").Append(model.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).Append(" model, object syncRoot").Append(extra).AppendLine(") {");
            b.AppendLine("if (model == null || syncRoot == null) throw new global::System.ArgumentNullException(\"model/syncRoot\"); _model = model; _sync = syncRoot;");
        }
        private static string Access(Point p) { return "_model.@" + p.Symbol.Name; }
        private static string LocalType(Point p)
        { return p.Kind == "Boolean" ? "bool" : p.Kind == "Integer" ? "int" : p.Kind == "FloatingPoint" ? "float" : p.Kind == "Timestamp" ? "long" : I + (p.Kind == "Quality" ? "IecQuality" : "IecDoublePoint"); }
        private static string Decode(Point p, string value)
        { return p.Kind == "DoublePoint" || p.Kind == "Quality" || p.Kind == "Timestamp" ? I + "Iec61850Data.Read" + (p.Kind == "Timestamp" ? "TimestampMilliseconds" : p.Kind) + "(" + value + ")" :
                "(" + LocalType(p) + ")" + value + "." + p.Kind; }
        private static void Guard(StringBuilder b, Point p, string value)
        {
            string condition = p.Kind == "FloatingPoint" ? "float.IsNaN(" + value + ") || float.IsInfinity(" + value + ")" :
                p.Kind == "Quality" ? "((int)" + value + " & ~8191) != 0" : p.Kind == "DoublePoint" ? "(int)" + value + " < 0 || (int)" + value + " > 3" :
                p.Kind == "Timestamp" ? value + " < 0 || " + value + " / 1000 > uint.MaxValue" : null;
            if (condition != null) b.Append("if (").Append(condition).AppendLine(") throw new global::System.ArgumentOutOfRangeException(\"value\");");
        }
        private void EmitServer(StringBuilder b, INamedTypeSymbol model, string binding, List<Point> points, List<Control> controls)
        {
            b.Append("private readonly ").Append(M).AppendLine("Server.MmsServerVariable[] _variables;");
            b.AppendLine("/// <summary>매핑된 서버 변수를 고정 MMS 이름 순서로 반환합니다.</summary>");
            b.AppendLine("/// <param name=\"index\">0 이상 Count 미만의 포인트 인덱스입니다.</param>");
            b.AppendLine("/// <returns>세션과 공유하는 변수 객체입니다. 갱신과 세션 처리를 직렬화하십시오.</returns>");
            b.Append("public ").Append(M).AppendLine("Server.MmsServerVariable GetVariable(int index) { return _variables[index]; }");
            if (iec)
            {
                b.Append("private readonly ").Append(I).AppendLine("Server.MmsServerControl[] _controls;");
                b.AppendLine("/// <summary>모델에 선언한 고정 제어 수입니다.</summary>");
                b.Append("public int ControlCount { get { return ").Append(controls.Count).AppendLine("; } }");
                b.AppendLine("/// <summary>세션에 연결할 공유 제어 모델을 반환합니다.</summary>");
                b.AppendLine("/// <param name=\"index\">0 이상 ControlCount 미만의 고정 제어 인덱스입니다.</param>");
                b.AppendLine("/// <returns>허용 검사와 실행 처리기가 연결된 제어 객체입니다.</returns>");
                b.Append("public ").Append(I).AppendLine("Server.MmsServerControl GetControl(int index) { return _controls[index]; }");
            }
            Constructor(b, model, binding); b.AppendLine("lock (_sync) {");
            b.Append("_variables = new ").Append(M).Append("Server.MmsServerVariable[").Append(points.Count).AppendLine("];");
            for (int n = 0; n < points.Count; n++)
            {
                var p = points[n];
                b.Append(LocalType(p)).Append(" initial").Append(n).Append(" = ").Append(Access(p)).AppendLine(";");
                Guard(b, p, "initial" + n);
                b.Append("_variables[").Append(n).Append("] = ");
                if (p.Kind == "DoublePoint" || p.Kind == "Quality" || p.Kind == "Timestamp")
                    b.Append(I).Append("Server.Iec61850ServerData.Create").Append(p.Kind);
                else b.Append("new ").Append(M).Append("Server.MmsServerVariable");
                b.Append("(").Append(Literal(p.Domain)).Append(", ").Append(Literal(p.Item)).Append(", initial").Append(n);
                if (p.Kind == "Timestamp") b.Append(", ").Append(p.TimeQuality == null ? "(byte)0x3f" : "_model.@" + p.TimeQuality);
                b.AppendLine(");");
                if (p.Writable)
                {
                    b.Append("_variables[").Append(n).Append("].EnableWrite(").Append(p.Validate == null ? "null" : "Validate" + n).Append(", Write").Append(n).AppendLine(");");
                }
            }
            if (iec)
            {
                b.Append("_controls = new ").Append(I).Append("Server.MmsServerControl[").Append(controls.Count).AppendLine("];");
                for (int n = 0; n < controls.Count; n++)
                {
                    var c = controls[n];
                    b.Append("_controls[").Append(n).Append("] = new ").Append(I).Append("Server.MmsServerControl(").Append(Literal(c.Domain)).Append(", ").Append(Literal(c.Item))
                        .Append(", (").Append(I).Append("MmsControlModel)").Append(c.Model).Append(", valueKind: (").Append(I).Append("MmsControlValueKind)").Append(c.Kind).AppendLine(");");
                    b.Append("_controls[").Append(n).Append("].SetHandlers(ValidateControl").Append(n).Append(", ExecuteControl").Append(n).AppendLine(");");
                }
            }
            b.AppendLine("} }");
            for (int n = 0; n < points.Count; n++)
            {
                var p = points[n]; if (!p.Writable) continue;
                if (p.Validate != null) b.Append("private int Validate").Append(n).Append("(").Append(M).Append("MmsValue value) { lock (_sync) { return _model.@").Append(p.Validate).AppendLine("(value); } }");
                b.Append("private int Write").Append(n).Append("(").Append(M).Append("MmsValue value) { lock (_sync) { ");
                if (p.Kind == "FloatingPoint") b.AppendLine("if(float.IsNaN((float)value.FloatingPoint) || float.IsInfinity((float)value.FloatingPoint)) return 11;");
                b.Append(Access(p)).Append(" = ").Append(Decode(p, "value")).AppendLine("; return -1; } }");
            }
            for (int n = 0; n < controls.Count; n++)
            {
                var c = controls[n];
                b.Append("private int ValidateControl").Append(n).Append("(").Append(I).Append("Server.MmsControlRequest request) { lock (_sync) { return _model.@").Append(c.Validate).AppendLine("(request); } }");
                b.Append("private int ExecuteControl").Append(n).Append("(").Append(I).Append("Server.MmsControlRequest request) { lock (_sync) { return _model.@").Append(c.Symbol.Name).AppendLine("(request); } }");
            }
            b.AppendLine("/// <summary>잠금 안에서 모든 모델 값을 검증한 뒤 변경된 전송 값만 서버 변수에 게시합니다.</summary>");
            b.AppendLine("/// <remarks>네트워크 송신은 하지 않습니다. 같은 변수의 세션 처리와 직렬화하십시오. 잘못된 값은 인수 범위 예외입니다.</remarks>");
            b.AppendLine("public void Publish() { lock (_sync) {");
            for (int n = 0; n < points.Count; n++)
            {
                var p = points[n]; b.Append(LocalType(p)).Append(" v").Append(n).Append(" = ").Append(Access(p)).AppendLine(";"); Guard(b, p, "v" + n);
                if (p.TimeQuality != null) b.Append("byte q").Append(n).Append(" = _model.@").Append(p.TimeQuality).AppendLine(";");
            }
            for (int n = 0; n < points.Count; n++)
            {
                var p = points[n];
                if (p.Kind == "DoublePoint" || p.Kind == "Quality" || p.Kind == "Timestamp")
                    b.Append(I).Append("Server.Iec61850ServerData.Update").Append(p.Kind).Append("(_variables[").Append(n).Append("], v").Append(n);
                else b.Append("_variables[").Append(n).Append("].Update").Append(p.Kind).Append("(v").Append(n);
                if (p.Kind == "Timestamp") b.Append(", ").Append(p.TimeQuality == null ? "(byte)0x3f" : "q" + n);
                b.AppendLine(");");
            }
            b.AppendLine("} }");
            b.AppendLine("/// <summary>매핑 변수와 제어를 공유하는 서버 세션을 생성합니다. Association은 별도로 수행합니다.</summary>");
            b.AppendLine("/// <param name=\"channel\">이미 연결된 호출자 소유 채널입니다. 세션은 채널을 해제하지 않습니다.</param>");
            if (iec)
            {
                b.AppendLine("/// <param name=\"dataSets\">매핑 변수 인스턴스를 참조하는 정적 DataSet 목록 또는 null입니다.</param>");
                b.AppendLine("/// <param name=\"reports\">등록 DataSet을 참조하는 URCB 목록 또는 null입니다.</param>");
                b.AppendLine("/// <returns>호출자가 직렬로 처리하고 종료할 IEC 61850 세션입니다.</returns>");
                b.Append("public ").Append(I).Append("Server.Iec61850ServerSession CreateSession(").Append(M).Append("IMmsChannel channel, ").Append(I).Append("Server.MmsServerDataSet[] dataSets = null, ").Append(I).Append("Server.MmsServerReportControl[] reports = null) { return new ").Append(I).AppendLine("Server.Iec61850ServerSession(channel, _variables, controls: _controls, dataSets: dataSets, reports: reports); }");
            }
            else
            {
                b.AppendLine("/// <param name=\"lists\">매핑 변수 인스턴스를 참조하는 정적 명명 변수 목록 또는 null입니다.</param>");
                b.AppendLine("/// <returns>호출자가 직렬로 처리하고 종료할 MMS 세션입니다.</returns>");
                b.Append("public ").Append(M).Append("Server.MmsServerSession CreateSession(").Append(M).Append("IMmsChannel channel, ").Append(M).Append("Server.MmsServerNamedVariableList[] lists = null) { return new ").Append(M).AppendLine("Server.MmsServerSession(channel, _variables, namedVariableLists: lists); }");
            }
        }
        private void EmitClient(StringBuilder b, INamedTypeSymbol model, string binding, List<Point> points, List<Control> controls)
        {
            string client = iec ? I + "Client.Iec61850Client" : M + "Client.MmsClient";
            b.Append("private readonly ").Append(client).AppendLine(" _client;");
            b.Append("private readonly ").Append(M).AppendLine("MmsVariableReference[] _references;");
            Constructor(b, model, binding, ", " + client + " client");
            b.AppendLine("if (client == null) throw new global::System.ArgumentNullException(\"client\"); _client = client;");
            b.Append("_references = new ").Append(M).AppendLine("MmsVariableReference[] {");
            foreach (var p in points) b.Append("new ").Append(M).Append("MmsVariableReference(").Append(Literal(p.Domain)).Append(", ").Append(Literal(p.Item)).AppendLine("),");
            b.AppendLine("}; }");
            b.AppendLine("/// <summary>포인트에 대응하는 고정 MMS 참조를 반환합니다.</summary>");
            b.AppendLine("/// <param name=\"index\">0 이상 Count 미만의 포인트 인덱스입니다.</param>");
            b.AppendLine("/// <returns>매핑에 선언한 domain/item 참조입니다.</returns>");
            b.Append("public ").Append(M).AppendLine("MmsVariableReference GetReference(int index) { return _references[index]; }");
            b.AppendLine("private int Find(string domain, string item) { for (int n=0; n<Count; n++) if (_references[n].Domain == domain && _references[n].Item == item) return n; return -1; }");
            b.Append("private void Validate(int index, ").Append(M).AppendLine("MmsValue value) { if (value == null) throw new global::System.ArgumentNullException(\"value\"); switch (index) {");
            for (int n = 0; n < points.Count; n++)
            {
                var p = points[n]; b.Append("case ").Append(n).AppendLine(": {");
                if (p.Kind == "Integer") b.AppendLine("if (value.Integer < int.MinValue || value.Integer > int.MaxValue) throw new global::System.ArgumentOutOfRangeException(\"value\");");
                b.Append(LocalType(p)).Append(" decoded = ").Append(Decode(p, "value")).AppendLine(";"); Guard(b, p, "decoded");
                b.AppendLine("_ = decoded; return; }");
            }
            b.AppendLine("default: throw new global::System.ArgumentOutOfRangeException(\"index\"); } }");
            b.Append("private void Assign(int index, ").Append(M).AppendLine("MmsValue value) { switch (index) {");
            for (int n = 0; n < points.Count; n++)
            {
                var p = points[n]; b.Append("case ").Append(n).Append(": ").Append(Access(p)).Append(" = ").Append(Decode(p, "value")).AppendLine(";");
                if (p.TimeQuality != null) b.Append("_model.@").Append(p.TimeQuality).Append(" = ").Append(I).AppendLine("Iec61850Data.ReadTimeQuality(value);");
                b.AppendLine("return;");
            }
            b.AppendLine("default: throw new global::System.ArgumentOutOfRangeException(\"index\"); } }");
            b.AppendLine("/// <summary>한 수신 값을 형식·범위 검증 후 잠금 안에서 모델에 반영합니다. 네트워크 호출은 없습니다.</summary>");
            b.AppendLine("/// <param name=\"index\">0 이상 Count 미만의 포인트 인덱스입니다.</param>");
            b.AppendLine("/// <param name=\"value\">해당 포인트의 형식과 일치하는 null이 아닌 값 스냅샷입니다.</param>");
            b.Append("public void Apply(int index, ").Append(M).AppendLine("MmsValue value) { lock (_sync) { Validate(index, value); Assign(index, value); } }");
            b.AppendLine("/// <summary>모든 포인트를 다중 읽기로 조회하고 전체 결과를 검증한 뒤 잠금 안에서 모델에 반영합니다.</summary>");
            b.AppendLine("/// <remarks>전체 결과의 검증을 마치기 전에는 모델을 변경하지 않습니다. 네트워크 오류는 클라이언트 계약대로 전파됩니다.</remarks>");
            b.AppendLine("public void ReadAll() { var results = _client.ReadMultiple(_references); lock (_sync) {");
            b.AppendLine("for (int n=0; n<Count; n++) { if (results[n].ErrorCode != -1) throw new global::System.InvalidOperationException(\"Mapped Read failed: \" + results[n].ErrorCode); Validate(n, results[n].Value); }");
            b.AppendLine("for (int n=0; n<Count; n++) Assign(n, results[n].Value); } }");
            b.AppendLine("/// <summary>Writable을 명시적으로 지정한 포인트의 현재 모델 값을 검증하여 원격 변수에 씁니다.</summary>");
            b.AppendLine("/// <param name=\"index\">고정 포인트 인덱스입니다. 쓰기를 허용하지 않은 포인트이면 예외가 발생합니다.</param>");
            b.AppendLine("/// <remarks>잠금은 모델 값 취득에만 사용합니다. 네트워크 호출은 직렬화하고 응답 유실 시 자동 재시도하지 마십시오.</remarks>");
            b.AppendLine("public void Write(int index) { switch(index) {");
            for (int n = 0; n < points.Count; n++)
            {
                var p = points[n]; if (!p.Writable) continue;
                b.Append("case ").Append(n).Append(": { ").Append(LocalType(p)).Append(" value; lock (_sync) { value = ").Append(Access(p)).AppendLine("; }");
                Guard(b, p, "value");
                b.Append("_client.Write(").Append(Literal(p.Domain)).Append(", ").Append(Literal(p.Item)).AppendLine(", value); return; }");
            }
            b.AppendLine("default: throw new global::System.InvalidOperationException(\"Point is not mapped for explicit Write.\"); } }");
            if (!iec)
            {
                b.AppendLine("/// <summary>일반 MMS 보고에서 매핑된 domain/item만 검증한 뒤 모델에 일괄 반영합니다.</summary>");
                b.AppendLine("/// <param name=\"report\">수신한 일반 InformationReport 스냅샷입니다.</param>");
                b.AppendLine("/// <param name=\"list\">명명 변수 목록 보고의 참조를 해석할 일치하는 디렉터리입니다. 명시적 변수 보고는 null이어도 됩니다.</param>");
                b.AppendLine("/// <remarks>알 수 없는 참조는 무시합니다. 매핑된 항목의 접근 오류나 형식 오류가 있으면 반영 전에 예외를 발생시킵니다.</remarks>");
                b.Append("public void ApplyInformationReport(").Append(M).Append("MmsInformationReport report, ").Append(M).AppendLine("MmsNamedVariableList list = null) {");
                b.AppendLine("if(report==null) throw new global::System.ArgumentNullException(\"report\");");
                b.Append("if(report.NamedVariableList!=null && (list==null || report.NamedVariableList.Scope!=").Append(M).AppendLine("MmsObjectScope.Domain || report.NamedVariableList.Domain!=list.Domain || report.NamedVariableList.Item!=list.Item || report.Count!=list.Count)) throw new global::System.ArgumentException(\"Report/list mismatch.\");");
                b.AppendLine("lock(_sync) { for(int n=0;n<report.Count;n++) {");
                b.AppendLine("string domain, item;");
                b.Append("if(report.NamedVariableList==null) { var r=report.GetVariable(n); if(r.Scope!=").Append(M).AppendLine("MmsObjectScope.Domain) continue; domain=r.Domain; item=r.Item; } else { var r=list.GetMember(n); domain=r.Domain; item=r.Item; }");
                b.AppendLine("int p=Find(domain,item); if(p<0) continue; var result=report.GetResult(n); if(result.ErrorCode!=-1) throw new global::System.InvalidOperationException(\"Mapped report access error.\"); Validate(p,result.Value); }");
                b.AppendLine("for(int n=0;n<report.Count;n++) { string domain,item;");
                b.Append("if(report.NamedVariableList==null) { var r=report.GetVariable(n); if(r.Scope!=").Append(M).AppendLine("MmsObjectScope.Domain) continue; domain=r.Domain; item=r.Item; } else { var r=list.GetMember(n); domain=r.Domain; item=r.Item; }");
                b.AppendLine("int p=Find(domain,item); if(p>=0) Assign(p,report.GetResult(n).Value); } } }");
                return;
            }
            b.AppendLine("/// <summary>DataSet 읽기 결과의 매핑된 항목을 전체 검증한 뒤 모델에 반영합니다.</summary>");
            b.AppendLine("/// <param name=\"dataSet\">읽기에 사용한 고정 DataSet 디렉터리입니다.</param>");
            b.AppendLine("/// <param name=\"results\">DataSet 순서와 개수가 일치하는 결과 배열입니다.</param>");
            b.AppendLine("/// <remarks>매핑되지 않은 참조는 무시합니다. 매핑된 항목의 접근 오류·형식 오류는 모델 반영 전에 예외로 전파됩니다.</remarks>");
            b.Append("public void ApplyDataSet(").Append(I).Append("Client.MmsDataSet dataSet, ").Append(M).AppendLine("MmsReadResult[] results) {");
            b.AppendLine("if (dataSet == null || results == null || results.Length != dataSet.Count) throw new global::System.ArgumentException(\"DataSet result count mismatch.\"); lock (_sync) {");
            b.AppendLine("for (int n=0; n<dataSet.Count; n++) { var r = dataSet.GetMember(n); int p=Find(r.Domain,r.Item); if(p<0) continue; if(results[n]==null || results[n].ErrorCode!=-1) throw new global::System.InvalidOperationException(\"Mapped DataSet access error.\"); Validate(p,results[n].Value); }");
            b.AppendLine("for (int n=0; n<dataSet.Count; n++) { var r=dataSet.GetMember(n); int p=Find(r.Domain,r.Item); if(p>=0) Assign(p,results[n].Value); } } }");
            b.AppendLine("/// <summary>일치하는 DataSet의 보고에 포함된 값 중 매핑 대상만 검증하여 모델에 반영합니다.</summary>");
            b.AppendLine("/// <param name=\"dataSet\">보고 참조와 개수가 일치하는 고정 DataSet 디렉터리입니다.</param>");
            b.AppendLine("/// <param name=\"report\">수신한 URCB 보고 스냅샷입니다. 보고에 포함되지 않은 항목은 기존 모델 값을 유지합니다.</param>");
            b.Append("public void ApplyReport(").Append(I).Append("Client.MmsDataSet dataSet, ").Append(I).AppendLine("Client.MmsReport report) {");
            b.AppendLine("if(dataSet==null || report==null || report.Count!=dataSet.Count || report.DataSetReference!=dataSet.Reference) throw new global::System.ArgumentException(\"Report/DataSet mismatch.\"); lock (_sync) {");
            b.AppendLine("for(int n=0;n<dataSet.Count;n++) { var r=dataSet.GetMember(n); int p=Find(r.Domain,r.Item); if(p>=0 && report.GetValue(n)!=null) Validate(p,report.GetValue(n)); }");
            b.AppendLine("for(int n=0;n<dataSet.Count;n++) { var r=dataSet.GetMember(n); int p=Find(r.Domain,r.Item); if(p>=0 && report.GetValue(n)!=null) Assign(p,report.GetValue(n)); } } }");
            b.AppendLine("/// <summary>클라이언트 모델에 선언한 원격 제어 수입니다.</summary>");
            b.Append("public int ControlCount { get { return ").Append(controls.Count).AppendLine("; } }");
            b.AppendLine("/// <summary>고정 제어 참조의 ctlModel을 조회하여 동기 제어 클라이언트를 생성합니다.</summary>");
            b.AppendLine("/// <param name=\"index\">0 이상 ControlCount 미만의 고정 제어 인덱스입니다.</param>");
            b.AppendLine("/// <param name=\"origin\">복사할 0~64바이트의 출처 식별자이며 인증 정보가 아닙니다.</param>");
            b.AppendLine("/// <param name=\"originCategory\">출처 범주(0~8)입니다.</param>");
            b.AppendLine("/// <returns>기존 클라이언트 연결을 공유하는 제어 객체입니다. 호출을 직렬화하십시오.</returns>");
            b.Append("public ").Append(I).AppendLine("Client.MmsControlClient CreateControl(int index, byte[] origin, int originCategory = 3) { switch(index) {");
            for (int n = 0; n < controls.Count; n++)
            {
                var c = controls[n];
                b.Append("case ").Append(n).Append(": return new ").Append(I).Append("Client.MmsControlClient(_client, ").Append(Literal(c.Domain)).Append(", ").Append(Literal(c.Item))
                    .Append(", origin, originCategory, (").Append(I).Append("MmsControlValueKind)").Append(c.Kind).AppendLine(");");
            }
            b.AppendLine("default: throw new global::System.ArgumentOutOfRangeException(\"index\"); } }");
        }
    }
}
