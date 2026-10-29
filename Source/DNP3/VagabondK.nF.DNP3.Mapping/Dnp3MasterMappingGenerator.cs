using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace VagabondK.nF.DNP3.Mapping.Generator
{
    /// <summary>Master 수신 모델의 형식·저장소를 검사하고 Fragment 반영 어댑터를 생성합니다.</summary>
    public sealed partial class Dnp3MappingGenerator
    {
        private static readonly DiagnosticDescriptor Receive = Error("VKDN006", "DNP3 Master 수신 처리기");
        private static readonly DiagnosticDescriptor Layout = Error("VKDN007", "nanoFramework 모델 레이아웃");

        private static bool Writable(ISymbol symbol)
        {
            if (symbol.IsStatic) return false;
            if (symbol is IFieldSymbol f) return !f.IsConst && !f.IsReadOnly;
            return symbol is IPropertySymbol p && !p.IsIndexer && p.ExplicitInterfaceImplementations.Length == 0 &&
                !p.ReturnsByRef && !p.ReturnsByRefReadonly && p.SetMethod != null && !p.SetMethod.IsInitOnly;
        }

        private static void GenerateMaster(SourceProductionContext output, INamedTypeSymbol model, string hint)
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
            if (Attribute(model, "Dnp3Map") != null || model.Name == "Dnp3MasterBinding" || model.GetMembers("Dnp3MasterBinding").Length != 0)
                report(Shape, model, "Master/Outstation 모델을 분리하고 생성 이름 Dnp3MasterBinding을 사용하지 마십시오.");
            var fields = model.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsConst).ToArray();
            var methods = model.GetMembers().OfType<IMethodSymbol>().ToArray();
            if (fields.Count(f => !f.IsStatic) > byte.MaxValue || fields.Count(f => f.IsStatic) > byte.MaxValue ||
                methods.Count(m => !m.IsStatic) > byte.MaxValue || methods.Count(m => m.IsStatic) > byte.MaxValue)
                report(Layout, model, "nanoFramework 형식의 인스턴스/정적 필드 및 메서드는 각각 255개 이하이어야 합니다. 자동 속성의 backing field와 접근자도 포함합니다. 배열 기반 속성 또는 별도 모델로 저장소를 분리하십시오.");
            var points = new List<Point>();
            var storage = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            var callbacks = model.GetMembers().Where(s => Attribute(s, "Dnp3Received") != null).ToArray();
            if (callbacks.Length != 1 || !ReceiveMethod(callbacks[0]))
                report(Receive, model, "정확히 하나의 [Dnp3Received] void Method(Dnp3Measurement[], Dnp3ResponseInfo)가 필요합니다.");
            foreach (var symbol in model.GetMembers())
            {
                if (Attribute(symbol, "Dnp3Control") != null)
                    report(Control, symbol, "Dnp3Control은 Outstation 모델에만 지정합니다. Master 명령은 기존 Command API를 사용하십시오.");
                var pointAttributes = symbol.GetAttributes().Where(IsPoint).ToArray();
                if (pointAttributes.Length > 1)
                    report(Member, symbol, "한 멤버에는 DNP3 Point 특성을 하나만 지정하십시오.");
                var a = pointAttributes.FirstOrDefault();
                if (a == null) continue;
                if (!Writable(symbol))
                {
                    report(Member, symbol, "쓰기 가능한 인스턴스 필드 또는 속성이 필요합니다.");
                    continue;
                }
                if (!PointAddress(a, out int kind, out ushort index) ||
                    a.NamedArguments.Any(n => n.Value.Kind == TypedConstantKind.Error || n.Key != "Flags" && n.Key != "Timestamp"))
                {
                    report(Options, symbol, "유효한 종류/UInt16 인덱스와 Flags/Timestamp만 지정합니다. Event 정책/Variation은 Outstation 옵션입니다.");
                    continue;
                }
                var type = TypeOf(symbol).SpecialType;
                bool valid = kind == 4 || kind == 6 ? type == SpecialType.System_Double || type == SpecialType.System_Single :
                    kind == 0 || kind == 5 ? type == SpecialType.System_Boolean :
                    kind == 1 ? type == SpecialType.System_Byte : type == SpecialType.System_UInt32;
                if (!valid)
                {
                    report(Member, symbol, "Binary 계열은 bool, DoubleBitBinary는 byte, Counter 계열은 uint, Analog 계열은 float/double이어야 합니다.");
                    continue;
                }
                if (!storage.Add(symbol)) report(Duplicate, symbol, "Point의 값·품질·시각 저장 멤버를 공유할 수 없습니다.");
                string flags = WriteMetadata(model, a, "Flags", SpecialType.System_Byte, storage, symbol, report);
                string time = WriteMetadata(model, a, "Timestamp", SpecialType.System_Int64, storage, symbol, report);
                points.Add(new Point { Symbol = symbol, Kind = kind, Index = index, Flags = flags, Time = time });
            }
            if (points.Count < 1 || points.Count > 512) report(Member, model, "1~512개 Point가 필요합니다.");
            points.Sort((a, b) => a.Kind != b.Kind ? a.Kind.CompareTo(b.Kind) : a.Index.CompareTo(b.Index));
            for (int i = 1; i < points.Count; i++)
                if (points[i].Kind == points[i - 1].Kind && points[i].Index == points[i - 1].Index)
                    report(Duplicate, points[i].Symbol, "동일 종류와 인덱스가 중복됩니다.");
            if (failed) return;
            var b = new StringBuilder("// <auto-generated/>\n");
            if (!model.ContainingNamespace.IsGlobalNamespace) b.Append("namespace ").Append(model.ContainingNamespace.ToDisplayString()).AppendLine(" {");
            b.Append("partial class @").Append(model.Name).AppendLine(" {");
            b.AppendLine("/// <summary>수신 순서대로 현재값을 반영하고 원본 Fragment 전체를 앱에 전달합니다.</summary>");
            b.Append("public sealed class Dnp3MasterBinding : ").Append(R).AppendLine("IDnp3MasterHandler {");
            b.Append("private readonly ").Append(model.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).AppendLine(" _model;");
            b.AppendLine("private readonly object _sync;");
            b.AppendLine("/// <summary>초기 모델을 읽거나 변경하지 않고 수신 어댑터를 생성합니다.</summary>");
            b.AppendLine("/// <param name=\"model\">앱 소유 모델입니다.</param>");
            b.AppendLine("/// <param name=\"syncRoot\">앱에서도 모델 접근에 사용하는 공유 잠금 객체입니다.</param>");
            b.AppendLine("/// <remarks>모델과 잠금 객체는 호출자 소유이며, 앱의 모델 읽기와 쓰기에도 같은 잠금을 사용해야 합니다.</remarks>");
            b.AppendLine("/// <exception cref=\"global::System.ArgumentNullException\">모델 또는 잠금 객체가 null입니다.</exception>");
            b.Append("public Dnp3MasterBinding(").Append(model.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).AppendLine(" model, object syncRoot) {");
            b.AppendLine("if (model == null) throw new global::System.ArgumentNullException(\"model\");");
            b.AppendLine("if (syncRoot == null) throw new global::System.ArgumentNullException(\"syncRoot\");");
            b.AppendLine("_model = model; _sync = syncRoot; }");
            b.AppendLine("private static int Slot(" + R + "Dnp3Measurement item) { switch (item.Type) {");
            foreach (var group in points.GroupBy(p => p.Kind))
            {
                b.Append("case ").Append(R).Append("Dnp3PointType.").Append(Types[group.Key]).AppendLine(": {");
                var members = group.ToArray();
                MasterSlotTree(b, points, members, 0, members.Length);
                b.AppendLine("}");
            }
            b.AppendLine("} return -1; }");
            b.AppendLine("/// <inheritdoc/>");
            b.AppendLine("/// <remarks>같은 잠금 안에서 매핑 대상 값 전체를 먼저 검사한 뒤 수신 순서대로 반영하고, 미등록 Point와 중복 Point를 포함한 모든 측정값을 한 번에 전달합니다. 같은 Point가 반복되면 모델에는 마지막 수신값이 남습니다. setter나 수신 콜백에서 발생한 예외는 전파하며 이미 반영한 값은 되돌리지 않습니다.</remarks>");
            b.AppendLine("/// <exception cref=\"global::System.ArgumentNullException\">측정값 배열이 null입니다.</exception>");
            b.AppendLine("/// <exception cref=\"global::System.ArgumentException\">매핑된 측정값의 범위, 시각 또는 대상 실수 형식으로의 표현이 유효하지 않습니다.</exception>");
            b.Append("public void OnMeasurements(").Append(R).Append("Dnp3Measurement[] measurements, ").Append(R).AppendLine("Dnp3ResponseInfo response) {");
            b.AppendLine("if (measurements == null) throw new global::System.ArgumentNullException(\"measurements\");");
            b.AppendLine("lock (_sync) {");
            b.AppendLine("for (int i = 0; i < measurements.Length; i++) { var item = measurements[i]; int slot = Slot(item); if (slot >= 0) Validate(item, slot); }");
            b.AppendLine("for (int i = 0; i < measurements.Length; i++) { var item = measurements[i]; int slot = Slot(item); if (slot >= 0) Apply(item, slot); }");
            b.Append("_model.@").Append(callbacks[0].Name).AppendLine("(measurements, response); } }");
            // nanoCLR의 switch 테이블은 항목 수를 8비트로 저장합니다.
            const int chunkSize = 32;
            int chunks = (points.Count + chunkSize - 1) / chunkSize;
            foreach (string method in new[] { "Validate", "Apply" })
            {
                b.Append("private ").Append(method == "Validate" ? "static " : "").Append("void ").Append(method)
                    .Append("(").Append(R).AppendLine("Dnp3Measurement item, int slot) { switch (slot / 32) {");
                for (int chunk = 0; chunk < chunks; chunk++)
                    b.Append("case ").Append(chunk).Append(": ").Append(method).Append(chunk).AppendLine("(item, slot); break;");
                b.AppendLine("default: throw new global::System.ArgumentOutOfRangeException(\"slot\"); } }");
            }
            for (int chunk = 0; chunk < chunks; chunk++)
            {
                var members = points.Skip(chunk * chunkSize).Take(chunkSize).ToArray();
                b.Append("private static void Validate").Append(chunk).Append("(").Append(R)
                    .AppendLine("Dnp3Measurement item, int slot) { var value = item.Value; switch (slot) {");
                foreach (var group in members.GroupBy(p => new { p.Kind, Type = TypeOf(p.Symbol).SpecialType }))
                {
                    var p = group.First();
                    foreach (var point in group) b.Append("case ").Append(points.IndexOf(point)).AppendLine(":");
                    b.AppendLine("{");
                    b.AppendLine("if (value.Timestamp < 0 || value.Timestamp > 0xffffffffffffL) throw new global::System.ArgumentOutOfRangeException(\"measurements\");");
                    if (p.Kind == 4 || p.Kind == 6)
                    {
                        b.AppendLine("double number = value.AsAnalog();");
                        b.Append("if (double.IsNaN(number) || double.IsInfinity(number)");
                        if (TypeOf(p.Symbol).SpecialType == SpecialType.System_Single)
                            b.Append(" || number > float.MaxValue || number < -float.MaxValue");
                        b.AppendLine(") throw new global::System.ArgumentException(\"Unrepresentable DNP3 analog value.\");");
                    }
                    else
                        b.Append("if (value.Raw > ").Append(p.Kind == 0 || p.Kind == 5 ? "1UL" : p.Kind == 1 ? "3UL" : "uint.MaxValue")
                            .AppendLine(") throw new global::System.ArgumentException(\"DNP3 point value range.\");");
                    b.AppendLine("break; }");
                }
                b.AppendLine("} }");
                b.Append("private void Apply").Append(chunk).Append("(").Append(R)
                    .AppendLine("Dnp3Measurement item, int slot) { switch (slot) {");
                foreach (var p in members)
                {
                    string value = p.Kind == 4 || p.Kind == 6 ? (TypeOf(p.Symbol).SpecialType == SpecialType.System_Single ? "(float)" : "") + "item.Value.AsAnalog()" :
                        p.Kind == 0 || p.Kind == 5 ? "item.Value.Raw != 0" : p.Kind == 1 ? "(byte)item.Value.Raw" : "(uint)item.Value.Raw";
                    b.Append("case ").Append(points.IndexOf(p)).Append(": _model.@").Append(p.Symbol.Name).Append(" = ").Append(value).AppendLine(";");
                    if (p.Flags != null) b.Append("_model.@").Append(p.Flags).AppendLine(" = item.Value.Flags;");
                    if (p.Time != null) b.Append("_model.@").Append(p.Time).AppendLine(" = item.Value.Timestamp;");
                    b.AppendLine("break;");
                }
                b.AppendLine("} }");
            }
            b.AppendLine("} }");
            if (!model.ContainingNamespace.IsGlobalNamespace) b.AppendLine("}");
            output.AddSource(hint, SourceText.From(SyntaxFactory.ParseCompilationUnit(b.ToString()).NormalizeWhitespace().ToFullString() + "\n", Encoding.UTF8));
        }

        private static void MasterSlotTree(StringBuilder b, List<Point> points, Point[] members, int start, int count)
        {
            if (count == 0) { b.AppendLine("return -1;"); return; }
            int middle = start + count / 2;
            var point = members[middle];
            b.Append("if (item.Index == ").Append(point.Index).Append(") return ").Append(points.IndexOf(point)).AppendLine(";");
            if (count == 1) { b.AppendLine("return -1;"); return; }
            b.Append("if (item.Index < ").Append(point.Index).AppendLine(") {");
            MasterSlotTree(b, points, members, start, middle - start);
            b.AppendLine("} else {");
            MasterSlotTree(b, points, members, middle + 1, start + count - middle - 1);
            b.AppendLine("}");
        }

        private static string WriteMetadata(INamedTypeSymbol model, AttributeData attribute, string key, SpecialType type,
            HashSet<ISymbol> storage, ISymbol point, Action<DiagnosticDescriptor, ISymbol, string> report)
        {
            if (!attribute.NamedArguments.Any(n => n.Key == key)) return null;
            string name = Option<string>(attribute, key, null);
            var members = name == null ? new ISymbol[0] : model.GetMembers(name).ToArray();
            if (members.Length != 1 || !Writable(members[0]) || TypeOf(members[0]).SpecialType != type)
                report(Options, point, key + "는 같은 모델의 쓰기 가능한 " + (key == "Flags" ? "byte" : "long") + " 멤버 이름이어야 합니다.");
            else if (!storage.Add(members[0])) report(Duplicate, point, "Point의 값·품질·시각 저장 멤버를 공유할 수 없습니다.");
            return name;
        }

        private static bool ReceiveMethod(ISymbol symbol)
        {
            return symbol is IMethodSymbol m && !m.IsStatic && !m.IsGenericMethod && !m.IsAsync && !m.ReturnsByRef &&
                m.MethodKind == MethodKind.Ordinary && m.ExplicitInterfaceImplementations.Length == 0 && m.ReturnsVoid &&
                (!m.IsPartialDefinition || m.PartialImplementationPart != null) &&
                !m.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "System.Diagnostics.ConditionalAttribute") &&
                m.Parameters.Length == 2 && m.Parameters.All(p => p.RefKind == RefKind.None) &&
                m.Parameters[0].Type is IArrayTypeSymbol a && a.Rank == 1 && a.ElementType.ToDisplayString() == "VagabondK.nF.DNP3.Dnp3Measurement" &&
                m.Parameters[1].Type.ToDisplayString() == "VagabondK.nF.DNP3.Dnp3ResponseInfo";
        }

        private const string MasterAttributes = @"
namespace VagabondK.nF.DNP3.Mapping {
    /// <summary>Master 수신의 모델 갱신 및 원본 Fragment 전달 어댑터를 생성합니다.</summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Class, AllowMultiple=false, Inherited=false)]
    internal sealed class Dnp3MasterMapAttribute : global::System.Attribute { }
    /// <summary>모델 갱신 후 원본 측정값 배열과 응답 정보를 받는 void 메서드입니다.</summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Method, AllowMultiple=false, Inherited=false)]
    internal sealed class Dnp3ReceivedAttribute : global::System.Attribute { }
}";
    }
}
