using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace VagabondK.nF.Modbus.Mapping.Generator
{
    /// <summary>Modbus 매핑 선언에서 MCU용 직접 접근 코드를 생성합니다.</summary>
    [Generator(LanguageNames.CSharp)]
    public sealed class ModbusMappingGenerator : IIncrementalGenerator
    {
        private const string Prefix = "VagabondK.nF.Modbus.Mapping.";
        private const string Data = "global::VagabondK.nF.Modbus.";
        private const string Client = "global::VagabondK.nF.Modbus.Client.ModbusClient";
        private const string Store = "global::VagabondK.nF.Modbus.Server.IModbusDataStore";
        private static readonly string[] AreaAttributes = { "Coil", "DiscreteInput", "HoldingRegister", "InputRegister" };
        private static readonly DiagnosticDescriptor Shape = Error("VKNF001", "매핑 클래스 선언");
        private static readonly DiagnosticDescriptor Member = Error("VKNF002", "매핑 멤버 선언");
        private static readonly DiagnosticDescriptor Range = Error("VKNF003", "매핑 주소 및 옵션");
        private static readonly DiagnosticDescriptor Overlap = Error("VKNF004", "중복 매핑 주소");
        private static readonly DiagnosticDescriptor Collision = Error("VKNF005", "생성 멤버 이름 충돌");
        private static readonly DiagnosticDescriptor Budget = new DiagnosticDescriptor(
            "VKNF006", "매핑 버퍼 예산 초과", "{0}", "ModbusMapping", DiagnosticSeverity.Warning, true);

        private static DiagnosticDescriptor Error(string id, string title)
        {
            return new DiagnosticDescriptor(id, title, "{0}", "ModbusMapping", DiagnosticSeverity.Error, true);
        }

        /// <summary>매핑 선언의 검색과 코드 생성을 컴파일러에 등록합니다.</summary>
        /// <param name="context">컴파일러가 제공하는 Generator 초기화 컨텍스트입니다.</param>
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            context.RegisterPostInitializationOutput(c => c.AddSource("ModbusMappingAttributes.g.cs",
                SourceText.From(VagabondK.nF.Mapping.Generator.MappingAttributeSource.ForBuild(Attributes), Encoding.UTF8)));
            var models = context.SyntaxProvider.ForAttributeWithMetadataName(Prefix + "ModbusMapAttribute",
                (node, _) => node is TypeDeclarationSyntax,
                (syntax, _) => (INamedTypeSymbol)syntax.TargetSymbol);
            context.RegisterSourceOutput(models.Collect(), (output, symbols) =>
            {
                var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
                int index = 0;
                foreach (var symbol in symbols.OrderBy(s => s.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), StringComparer.Ordinal))
                    if (seen.Add(symbol))
                        Generate(output, symbol, "Map" + (index++).ToString("D4", CultureInfo.InvariantCulture) + ".Modbus.g.cs");
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
                model.IsGenericType || model.ContainingType != null ||
                model.BaseType?.SpecialType != SpecialType.System_Object ||
                model.DeclaringSyntaxReferences.Any(r => !(r.GetSyntax() is ClassDeclarationSyntax c) ||
                    !c.Modifiers.Any(SyntaxKind.PartialKeyword)))
            {
                report(Shape, model, "최상위 비제네릭 partial class를 사용하십시오. 상속·static·abstract 클래스는 지원하지 않습니다.");
                return;
            }
            var map = model.GetAttributes().First(a => a.AttributeClass?.ToDisplayString() == Prefix + "ModbusMapAttribute");
            int role = Argument(map, 0);
            if (role < 0 || role > 1)
                report(Range, model, "ModbusMapRole은 Client 또는 Server여야 합니다.");
            int maxRegisters = Named(map, "MaxReadRegisters", 125);
            int maxBits = Named(map, "MaxReadBits", 2000);
            int budget = Named(map, "BufferBudgetBytes", 0);
            if (maxRegisters < 1 || maxRegisters > 125 || maxBits < 1 || maxBits > 2000 || budget < 0 ||
                (role == 1 && map.NamedArguments.Any(a => a.Key == "MaxReadRegisters" || a.Key == "MaxReadBits")))
                report(Range, model, "Client의 MaxReadRegisters는 1~125, MaxReadBits는 1~2000이며 BufferBudgetBytes는 0 이상이어야 합니다.");
            var points = new List<Point>();
            foreach (var symbol in model.GetMembers())
            {
                var attributes = symbol.GetAttributes().Where(IsPoint).ToArray();
                if (attributes.Length == 0)
                    continue;
                if (attributes.Length != 1)
                {
                    report(Member, symbol, "한 멤버에는 Modbus 매핑 특성을 하나만 지정하십시오.");
                    continue;
                }
                var attribute = attributes[0];
                var property = symbol as IPropertySymbol;
                var field = symbol as IFieldSymbol;
                var type = property?.Type ?? field?.Type;
                bool writable = Named(attribute, "Writable", 0) != 0;
                bool needsSetter = role == 0 || writable;
                if (symbol.IsStatic || (property == null && field == null) ||
                    (property != null && (property.IsIndexer || property.ExplicitInterfaceImplementations.Length != 0 ||
                        property.ReturnsByRef || property.ReturnsByRefReadonly ||
                        property.GetMethod == null || (needsSetter && (property.SetMethod == null || property.SetMethod.IsInitOnly)))) ||
                    (field != null && (field.IsConst || (needsSetter && field.IsReadOnly))))
                {
                    report(Member, symbol, "인스턴스 필드 또는 읽기 가능한 일반 속성이 필요합니다. Client 수신 및 원격 쓰기 대상은 대입 가능해야 합니다.");
                    continue;
                }
                bool shorthand = attribute.AttributeClass.Name != "ModbusPointAttribute";
                int area = shorthand ? Array.IndexOf(AreaAttributes, attribute.AttributeClass.Name.Replace("Attribute", "")) : Argument(attribute, 0);
                int address = Argument(attribute, shorthand ? 0 : 1);
                int words = Named(attribute, "WordOrder", 0);
                int bytes = Named(attribute, "ByteOrder", 0);
                string readGroup = "";
                foreach (var pair in attribute.NamedArguments)
                    if (pair.Key == "ReadGroup")
                    {
                        readGroup = pair.Value.Value as string;
                        if (pair.Value.Kind == TypedConstantKind.Error || readGroup == null ||
                            (readGroup.Length > 0 && !ValidGroup(readGroup)) || role != 0)
                            report(Range, symbol, "ReadGroup은 Client 전용이며 빈 문자열 또는 영문자로 시작하는 영문자·숫자·밑줄 이름이어야 합니다.");
                    }
                int width;
                string format = Format(type.SpecialType, out width);
                if (area < 0 || area > 3 || address < 0 || address > 65535 ||
                    words < 0 || words > 1 || bytes < 0 || bytes > 1)
                {
                    report(Range, symbol, "영역, 0 기반 주소(0~65535) 및 Byte/Word 순서를 확인하십시오.");
                    continue;
                }
                if (format == null || (area < 2) != (type.SpecialType == SpecialType.System_Boolean))
                {
                    report(Member, symbol, "Coil/DiscreteInput은 bool만, Register는 16·32·64비트 정수와 float/double만 지원합니다.");
                    continue;
                }
                if (address + width > 65536 || (writable && (area == 1 || area == 3)) ||
                    (area < 2 && (words != 0 || bytes != 0)) || (width == 1 && words != 0))
                {
                    report(Range, symbol, "주소 끝, 읽기 전용 영역의 Writable, 해당 형식에 의미 없는 순서 옵션을 확인하십시오.");
                    continue;
                }
                points.Add(new Point(symbol.Name, format, area, address, width, words, bytes, writable, readGroup, symbol));
                if (role == 0 && area >= 2 && width > maxRegisters)
                    report(Range, symbol, "MaxReadRegisters는 각 매핑 값의 Register 수 이상이어야 합니다. 값을 나누어 읽지 않습니다.");
            }
            if (points.Count == 0)
                report(Member, model, "최소 하나의 ModbusPoint 멤버가 필요합니다.");
            points = points.OrderBy(p => p.Area).ThenBy(p => p.Address).ToList();
            for (int i = 1; i < points.Count; i++)
                if (points[i - 1].Area == points[i].Area && points[i - 1].End > points[i].Address)
                    report(Overlap, points[i].Symbol, "동일 영역에서 주소 범위가 겹칩니다: " + points[i - 1].Name);
            var reserved = role == 0
                ? new HashSet<string> { "ReadModbus", "__modbusRegisters", "__modbusBits" }
                : new HashSet<string> { "ModbusStore" };
            if (role == 0)
            {
                foreach (var p in points.Where(p => p.Writable))
                    reserved.Add("WriteModbus" + p.Name);
                foreach (var group in points.Select(p => p.ReadGroup).Where(g => !string.IsNullOrEmpty(g)).Distinct())
                    reserved.Add("ReadModbusGroup" + group);
            }
            if (reserved.Contains(model.Name))
                report(Collision, model, "클래스 이름이 생성 멤버 이름과 충돌합니다: " + model.Name);
            foreach (var symbol in model.GetMembers().Where(s => reserved.Contains(s.Name)))
                report(Collision, symbol, "생성기가 사용하는 멤버 이름입니다: " + symbol.Name);
            if (failed)
                return;
            var code = new StringBuilder("// <auto-generated/>\n");
            var requests = role == 0 ? Requests(points, maxRegisters, maxBits) : new List<List<Point>>();
            int registers = role == 0 ? RequestSize(requests, false) : points.Where(p => p.Area >= 2).Select(p => p.Width).DefaultIfEmpty(0).Max();
            int bits = role == 0 ? RequestSize(requests, true) : 0;
            int payload = registers * 2 + bits;
            code.Append("// Modbus mapping: role=").Append(role == 0 ? "Client" : "Server")
                .Append(", points=").Append(points.Count).Append(", readRequests=").Append(requests.Count)
                .Append(", registerBufferLength=").Append(registers).Append(", bitBufferLength=").Append(bits)
                .Append(", bufferPayloadBytes=").Append(payload).AppendLine(".");
            code.AppendLine("// Buffer payload excludes object/array headers, alignment, model, transport and protocol buffers.");
            if (budget > 0 && payload > budget)
                output.ReportDiagnostic(Diagnostic.Create(Budget, model.Locations.FirstOrDefault(),
                    "매핑 버퍼 원소의 합계는 " + payload + " Byte로 BufferBudgetBytes " + budget +
                    "를 초과합니다. 배열 헤더·정렬 및 통신 계층의 메모리는 별도입니다."));
            string ns = model.ContainingNamespace.IsGlobalNamespace ? null : model.ContainingNamespace.ToDisplayString();
            if (ns != null)
                code.Append("namespace ").Append(ns).AppendLine(" {");
            code.Append("partial class @").Append(model.Name).AppendLine(" {");
            if (role == 0)
                EmitClient(code, points, requests, maxRegisters, maxBits);
            else
                EmitStore(code, model, points);
            code.AppendLine("}");
            if (ns != null)
                code.AppendLine("}");
            output.AddSource(hint, SourceText.From(
                SyntaxFactory.ParseCompilationUnit(code.ToString()).NormalizeWhitespace().ToFullString() + "\n", Encoding.UTF8));
        }

        private static bool IsPoint(AttributeData attribute)
        {
            string name = attribute.AttributeClass?.ToDisplayString();
            return name == Prefix + "ModbusPointAttribute" || AreaAttributes.Any(a => name == Prefix + a + "Attribute");
        }

        private static int Argument(AttributeData attribute, int index)
        {
            if (attribute.ConstructorArguments.Length <= index || attribute.ConstructorArguments[index].Kind == TypedConstantKind.Error)
                return -1;
            return Convert.ToInt32(attribute.ConstructorArguments[index].Value, CultureInfo.InvariantCulture);
        }

        private static int Named(AttributeData attribute, string name, int fallback)
        {
            foreach (var pair in attribute.NamedArguments)
                if (pair.Key == name)
                    return pair.Value.Kind == TypedConstantKind.Error ? -1 : Convert.ToInt32(pair.Value.Value, CultureInfo.InvariantCulture);
            return fallback;
        }

        private static bool ValidGroup(string name)
        {
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool letter = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
                if (!letter && (i == 0 || (c != '_' && (c < '0' || c > '9'))))
                    return false;
            }
            return true;
        }

        private static string Format(SpecialType type, out int width)
        {
            width = 1;
            switch (type)
            {
                case SpecialType.System_Boolean: return "Boolean";
                case SpecialType.System_Int16: return "Int16";
                case SpecialType.System_UInt16: return "UInt16";
                case SpecialType.System_Int32: width = 2; return "Int32";
                case SpecialType.System_UInt32: width = 2; return "UInt32";
                case SpecialType.System_Single: width = 2; return "Single";
                case SpecialType.System_Int64: width = 4; return "Int64";
                case SpecialType.System_UInt64: width = 4; return "UInt64";
                case SpecialType.System_Double: width = 4; return "Double";
                default: return null;
            }
        }

        private static string Orders(Point p)
        {
            return (p.Width == 1 ? "" : ", " + Data + "RegisterWordOrder." + (p.Words == 0 ? "HighWordFirst" : "LowWordFirst")) +
                ", " + Data + "RegisterByteOrder." + (p.Bytes == 0 ? "HighByteFirst" : "LowByteFirst");
        }

        private static string Read(Point p, string buffer, string offset)
        {
            return Data + "RegisterConvert.Read" + p.Format + "(" + buffer + ", " + offset + Orders(p) + ")";
        }

        private static string Write(Point p, string buffer, string offset, string value)
        {
            return Data + "RegisterConvert.Write" + p.Format + "(" + buffer + ", " + offset + ", " + value + Orders(p) + ");";
        }

        private static List<List<Point>> Requests(List<Point> points, int maxRegisters, int maxBits)
        {
            var groups = new List<List<Point>>();
            foreach (var point in points)
            {
                var last = groups.LastOrDefault();
                if (last == null || last[0].Area != point.Area || last.Last().End != point.Address ||
                    point.End - last[0].Address > (point.Area < 2 ? maxBits : maxRegisters))
                    groups.Add(new List<Point> { point });
                else
                    last.Add(point);
            }
            return groups;
        }

        private static int RequestSize(List<List<Point>> requests, bool bits)
        {
            return requests.Where(g => (g[0].Area < 2) == bits)
                .Select(g => g.Last().End - g[0].Address).DefaultIfEmpty(0).Max();
        }

        private static void EmitClient(StringBuilder b, List<Point> points, List<List<Point>> groups, int maxRegisters, int maxBits)
        {
            int registers = RequestSize(groups, false);
            int bits = RequestSize(groups, true);
            if (registers > 0)
                b.Append("private readonly ushort[] __modbusRegisters = new ushort[").Append(registers).AppendLine("];");
            if (bits > 0)
                b.Append("private readonly bool[] __modbusBits = new bool[").Append(bits).AppendLine("];");
            EmitClientRead(b, groups, "ReadModbus");
            foreach (var group in points.Select(p => p.ReadGroup).Where(g => g.Length > 0).Distinct().OrderBy(g => g, StringComparer.Ordinal))
            {
                // 그룹에 속하지 않는 Point를 제외해도 필요한 버퍼가 커지지 않도록 전체 읽기의 요청 경계를 유지합니다.
                var selected = groups.SelectMany(request =>
                    Requests(request.Where(p => p.ReadGroup == group).ToList(), maxRegisters, maxBits)).ToList();
                EmitClientRead(b, selected, "ReadModbusGroup" + group);
            }
            foreach (var point in points.Where(p => p.Writable))
            {
                b.AppendLine("/// <summary>현재 멤버 값을 명시적으로 송신합니다.</summary>");
                b.AppendLine("/// <param name=\"client\">호출자가 관리하는 Client입니다.</param>");
                b.AppendLine("/// <param name=\"unitId\">대상 Unit ID입니다. 직렬 통신의 0은 응답 없는 브로드캐스트 주소입니다.</param>");
                b.AppendLine("/// <remarks>공유 변환 버퍼를 사용하므로 읽기·쓰기와 모델 접근을 호출자가 직렬화해야 합니다. 모델의 현재값을 보내며 원격 쓰기 성공 여부를 모델에 별도로 기록하지 않습니다. 통신이나 getter에서 발생한 예외는 그대로 전달됩니다.</remarks>");
                b.AppendLine("/// <exception cref=\"global::System.ArgumentNullException\">Client가 null입니다.</exception>");
                b.Append("public void WriteModbus").Append(point.Name).Append("(").Append(Client).AppendLine(" client, byte unitId) {");
                b.AppendLine("if (client == null) throw new global::System.ArgumentNullException(\"client\");");
                if (point.Area == 0)
                    b.Append("client.WriteSingleCoil(unitId, ").Append(point.Address).Append(", this.@").Append(point.Name).AppendLine(");");
                else
                {
                    b.AppendLine(Write(point, "__modbusRegisters", "0", "this.@" + point.Name));
                    if (point.Width == 1)
                        b.Append("client.WriteSingleRegister(unitId, ").Append(point.Address).AppendLine(", __modbusRegisters[0]);");
                    else
                        b.Append("client.WriteMultipleRegisters(unitId, ").Append(point.Address).Append(", ").Append(point.Width).AppendLine(", __modbusRegisters, 0);");
                }
                b.AppendLine("}");
            }
        }

        private static void EmitClientRead(StringBuilder b, List<List<Point>> groups, string method)
        {
            b.AppendLine("/// <summary>대상 멤버의 인접 주소를 읽어 반영합니다. 호출자는 전체 읽기·그룹 읽기·쓰기와 모델 접근을 직렬화해야 합니다.</summary>");
            b.AppendLine("/// <remarks>실패 전에 완료한 요청의 값은 유지됩니다. Setter의 예외는 그대로 전달합니다.</remarks>");
            b.AppendLine("/// <param name=\"client\">호출자가 관리하는 Client입니다.</param>");
            b.AppendLine("/// <param name=\"unitId\">대상 Unit ID입니다. Serial은 1~247이며 TCP는 0~255입니다.</param>");
            b.AppendLine("/// <exception cref=\"global::System.ArgumentNullException\">Client가 null입니다.</exception>");
            b.Append("public void ").Append(method).Append("(").Append(Client).AppendLine(" client, byte unitId) {");
            b.AppendLine("if (client == null) throw new global::System.ArgumentNullException(\"client\");");
            string[] methods = { "ReadCoils", "ReadDiscreteInputs", "ReadHoldingRegisters", "ReadInputRegisters" };
            foreach (var group in groups)
            {
                Point first = group[0];
                string buffer = first.Area < 2 ? "__modbusBits" : "__modbusRegisters";
                b.Append("client.").Append(methods[first.Area]).Append("(unitId, ").Append(first.Address)
                    .Append(", ").Append(group.Last().End - first.Address).Append(", ").Append(buffer).AppendLine(", 0);");
                foreach (var point in group)
                    b.Append("this.@").Append(point.Name).Append(" = ").Append(point.Area < 2
                        ? buffer + "[" + (point.Address - first.Address) + "]"
                        : Read(point, buffer, (point.Address - first.Address).ToString(CultureInfo.InvariantCulture))).AppendLine(";");
            }
            b.AppendLine("}");
        }

        private static void EmitStore(StringBuilder b, INamedTypeSymbol model, List<Point> points)
        {
            int width = points.Where(p => p.Area >= 2).Select(p => p.Width).DefaultIfEmpty(0).Max();
            b.AppendLine("/// <summary>모델에 직접 접근하는 Server 저장소입니다. 앱도 같은 잠금으로 모델에 접근해야 합니다.</summary>");
            b.Append("public sealed class ModbusStore : ").Append(Store).AppendLine(", global::VagabondK.nF.Modbus.Server.IModbusDataStoreValidator {");
            b.Append("private readonly ").Append(model.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).AppendLine(" _model;");
            b.AppendLine("private readonly object _sync;");
            if (width > 0)
                b.Append("private readonly ushort[] _scratch = new ushort[").Append(width).AppendLine("];");
            b.AppendLine("/// <summary>모델과 애플리케이션이 공유할 잠금 객체를 설정합니다.</summary>");
            b.AppendLine("/// <param name=\"model\">수명을 호출자가 관리하는 모델입니다.</param>");
            b.AppendLine("/// <param name=\"syncRoot\">모델 접근에 공통으로 사용할 잠금 객체입니다.</param>");
            b.AppendLine("/// <exception cref=\"global::System.ArgumentNullException\">모델 또는 잠금 객체가 null입니다.</exception>");
            b.Append("public ModbusStore(").Append(model.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).AppendLine(" model, object syncRoot) {");
            b.AppendLine("if (model == null) throw new global::System.ArgumentNullException(\"model\");");
            b.AppendLine("if (syncRoot == null) throw new global::System.ArgumentNullException(\"syncRoot\");");
            b.AppendLine("_model = model; _sync = syncRoot; }");
            b.AppendLine("private static void AddressError() { throw new global::VagabondK.nF.Modbus.ModbusException(global::VagabondK.nF.Modbus.ExceptionCode.IllegalDataAddress); }");
            for (int area = 0; area < 4; area++)
                EmitRange(b, points.Where(p => p.Area == area).ToList(), area);
            b.AppendLine("/// <inheritdoc/>");
            b.AppendLine("public bool IsValidRange(global::VagabondK.nF.Modbus.FunctionCode function, ushort address, ushort count) {");
            b.AppendLine("switch ((byte)function) {");
            b.AppendLine("case 1: return Range0(address, count, false); case 2: return Range1(address, count, false);");
            b.AppendLine("case 3: return Range2(address, count, false); case 4: return Range3(address, count, false);");
            b.AppendLine("case 5: return count == 1 && Range0(address, count, true); case 15: return Range0(address, count, true);");
            b.AppendLine("case 6: return count == 1 && Range2(address, count, true); case 16: return Range2(address, count, true);");
            b.AppendLine("default: return false; } }");
            EmitStoreRead(b, points, true);
            EmitStoreRead(b, points, false);
            EmitStoreWrite(b, points.Where(p => p.Area == 0 && p.Writable).ToList(), true);
            EmitStoreWrite(b, points.Where(p => p.Area == 2 && p.Writable).ToList(), false);
            b.AppendLine("}");
        }

        private static void EmitRange(StringBuilder b, List<Point> points, int area)
        {
            b.Append("private static bool Range").Append(area).AppendLine("(int address, int count, bool write) {");
            b.AppendLine("int end = address + count; if (count < 1 || end > 65536) return false;");
            b.AppendLine("if (write) {");
            EmitIntervals(b, points.Where(p => p.Writable).ToList(), true);
            b.AppendLine("} else {");
            EmitIntervals(b, points, false);
            b.AppendLine("} return false; }");
        }

        private static void EmitIntervals(StringBuilder b, List<Point> points, bool write)
        {
            var intervals = new List<List<Point>>();
            foreach (var point in points)
            {
                var last = intervals.LastOrDefault();
                if (last == null || last.Last().End != point.Address)
                    intervals.Add(new List<Point> { point });
                else
                    last.Add(point);
            }
            foreach (var interval in intervals)
            {
                b.Append("if (address >= ").Append(interval[0].Address).Append(" && end <= ").Append(interval.Last().End).AppendLine(") {");
                if (write)
                {
                    foreach (var point in interval.Where(p => p.Width > 1))
                        b.Append("if ((address > ").Append(point.Address).Append(" && address < ").Append(point.End)
                            .Append(") || (end > ").Append(point.Address).Append(" && end < ").Append(point.End).AppendLine(")) return false;");
                }
                b.AppendLine("return true; }");
            }
        }

        private static void EmitStoreRead(StringBuilder b, List<Point> points, bool bits)
        {
            b.AppendLine("/// <inheritdoc/>");
            b.Append("public void ").Append(bits ? "ReadBits(bool discreteInputs" : "ReadRegisters(bool inputRegisters")
                .Append(", ushort address, ushort count, ").Append(bits ? "bool" : "ushort").AppendLine("[] values) {");
            b.AppendLine("if (values == null || values.Length < count) throw new global::System.ArgumentException(\"values\");");
            b.AppendLine("lock (_sync) {");
            string selector = bits ? "discreteInputs" : "inputRegisters";
            int baseArea = bits ? 0 : 2;
            b.Append("if (!(").Append(selector).Append(" ? Range").Append(baseArea + 1).Append("(address, count, false) : Range")
                .Append(baseArea).AppendLine("(address, count, false))) AddressError();");
            b.AppendLine("int end = (int)address + count;");
            if (!bits && points.Any(p => p.Area >= 2))
                b.AppendLine("int start, stop, i;");
            foreach (var point in points.Where(p => (p.Area < 2) == bits))
            {
                b.Append("if (").Append(point.Area == baseArea ? "!" : "").Append(selector)
                    .Append(" && address < ").Append(point.End).Append(" && end > ").Append(point.Address).AppendLine(") {");
                if (bits)
                    b.Append("values[").Append(point.Address).Append(" - address] = _model.@").Append(point.Name).AppendLine(";");
                else
                {
                    b.AppendLine(Write(point, "_scratch", "0", "_model.@" + point.Name));
                    b.Append("start = address > ").Append(point.Address).Append(" ? address : ").Append(point.Address).AppendLine(";");
                    b.Append("stop = end < ").Append(point.End).Append(" ? end : ").Append(point.End).AppendLine(";");
                    b.Append("for (i = start; i < stop; i++) values[i - address] = _scratch[i - ").Append(point.Address).AppendLine("];");
                }
                b.AppendLine("}");
            }
            b.AppendLine("} }");
        }

        private static void EmitStoreWrite(StringBuilder b, List<Point> points, bool bits)
        {
            b.AppendLine("/// <inheritdoc/>");
            b.AppendLine("/// <remarks>전체 주소 범위를 먼저 검증합니다. 호출 중에는 setter를 포함한 앱 코드에서 입력 배열을 변경하지 않아야 합니다. setter에서 예외가 발생해도 앞선 변경은 유지됩니다.</remarks>");
            b.Append("public void ").Append(bits ? "WriteCoils" : "WriteHoldingRegisters")
                .Append("(ushort address, ushort count, ").Append(bits ? "bool" : "ushort").AppendLine("[] values) {");
            b.AppendLine("if (values == null || values.Length < count) throw new global::System.ArgumentException(\"values\");");
            b.Append("lock (_sync) { if (!Range").Append(bits ? 0 : 2).AppendLine("(address, count, true)) AddressError();");
            if (points.Count > 0)
                b.AppendLine("int end = (int)address + count;");
            foreach (var point in points)
            {
                string offset = "(" + point.Address + " - address)";
                b.Append("if (address <= ").Append(point.Address).Append(" && end >= ").Append(point.End).Append(") _model.@")
                    .Append(point.Name).Append(" = ").Append(bits ? "values[" + offset + "]" : Read(point, "values", offset)).AppendLine(";");
            }
            b.AppendLine("} }");
        }

        private sealed class Point
        {
            internal Point(string name, string format, int area, int address, int width,
                int words, int bytes, bool writable, string readGroup, ISymbol symbol)
            {
                Name = name; Format = format; Area = area; Address = address;
                Width = width; Words = words; Bytes = bytes; Writable = writable; ReadGroup = readGroup; Symbol = symbol;
            }

            internal string Name { get; }
            internal string Format { get; }
            internal int Area { get; }
            internal int Address { get; }
            internal int End => Address + Width;
            internal int Width { get; }
            internal int Words { get; }
            internal int Bytes { get; }
            internal bool Writable { get; }
            internal string ReadGroup { get; }
            internal ISymbol Symbol { get; }
        }

        private static readonly string Attributes = @"// <auto-generated/>
namespace VagabondK.nF.Modbus.Mapping
{
    /// <summary>생성할 Modbus 역할입니다.</summary>
    internal enum ModbusMapRole {
        /// <summary>원격 장치를 읽고 명시적으로 쓰는 메서드를 생성합니다.</summary>
        Client,
        /// <summary>로컬 모델을 원격 요청에 노출하는 저장소를 생성합니다.</summary>
        Server
    }
    /// <summary>매핑할 Modbus 데이터 영역입니다.</summary>
    internal enum ModbusArea {
        /// <summary>읽기와 쓰기가 가능한 1비트 출력 영역입니다.</summary>
        Coil,
        /// <summary>읽기 전용 1비트 입력 영역입니다.</summary>
        DiscreteInput,
        /// <summary>읽기와 쓰기가 가능한 16비트 Register 영역입니다.</summary>
        HoldingRegister,
        /// <summary>읽기 전용 16비트 Register 영역입니다.</summary>
        InputRegister
    }
    /// <summary>Register의 상위 또는 하위 Byte 우선 순서입니다.</summary>
    internal enum ModbusByteOrder {
        /// <summary>Register의 상위 Byte를 먼저 해석합니다. 기본 순서입니다.</summary>
        HighByteFirst,
        /// <summary>Register의 상위·하위 Byte를 교환하여 해석합니다.</summary>
        LowByteFirst
    }
    /// <summary>다중 Register의 상위 또는 하위 Word 우선 순서입니다.</summary>
    internal enum ModbusWordOrder {
        /// <summary>낮은 주소부터 높은 자리의 Word를 배치합니다. 기본 순서입니다.</summary>
        HighWordFirst,
        /// <summary>낮은 주소부터 낮은 자리의 Word를 배치하며 64비트는 네 Word의 순서를 뒤집습니다.</summary>
        LowWordFirst
    }

    /// <summary>빌드 시점에 Modbus 직접 접근 코드를 생성합니다. 런타임 바인딩은 생성하지 않습니다.</summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    internal sealed class ModbusMapAttribute : global::System.Attribute
    {
        /// <summary>Client 또는 Server 중 생성할 역할을 지정합니다.</summary>
        /// <param name=""role"">원격 접근 메서드를 생성할 Client 또는 로컬 저장소를 생성할 Server입니다.</param>
        public ModbusMapAttribute(ModbusMapRole role) { }
        /// <summary>Client의 요청당 Register 상한입니다. 기본값은 125이며 값 하나를 분할하지 않습니다.</summary>
        public int MaxReadRegisters { get; set; }
        /// <summary>Client의 요청당 Bit 상한입니다. 기본값은 2000입니다.</summary>
        public int MaxReadBits { get; set; }
        /// <summary>매핑 배열 원소의 크기 합계에 적용할 예산(바이트)입니다. 0이면 검사하지 않으며 초과 시 빌드 경고가 발생합니다.</summary>
        public int BufferBudgetBytes { get; set; }
    }

    /// <summary>0 기반 Modbus 주소에 필드 또는 속성을 매핑합니다.</summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Property | global::System.AttributeTargets.Field,
        AllowMultiple = false, Inherited = false)]
    internal class ModbusPointAttribute : global::System.Attribute
    {
        /// <summary>영역과 0 기반 시작 주소를 지정합니다.</summary>
        /// <param name=""area"">bool은 Coil/DiscreteInput, 숫자는 HoldingRegister/InputRegister에 매핑합니다.</param>
        /// <param name=""address"">0~65535의 시작 주소입니다. 다중 Register 값의 끝 주소도 65535 이하여야 합니다.</param>
        public ModbusPointAttribute(ModbusArea area, int address) { }
        /// <summary>명시적 Client 송신 또는 Server 원격 쓰기를 허용합니다. 기본값은 false입니다.</summary>
        public bool Writable { get; set; }
        /// <summary>Client의 선택 읽기 그룹입니다. 미지정하거나 빈 문자열이면 전체 읽기에만 포함됩니다. 영문자로 시작하는 영문자·숫자·밑줄 이름을 지정하면 ReadModbusGroup 메서드를 추가합니다.</summary>
        public string ReadGroup { get; set; }
        /// <summary>Register 내 Byte 순서입니다. 기본값은 HighByteFirst입니다.</summary>
        public ModbusByteOrder ByteOrder { get; set; }
        /// <summary>다중 Register의 Word 순서입니다. 기본값은 HighWordFirst입니다.</summary>
        public ModbusWordOrder WordOrder { get; set; }
    }
}" + AreaAttributeDeclarations();

        private static string AreaAttributeDeclarations()
        {
            var b = new StringBuilder("\nnamespace VagabondK.nF.Modbus.Mapping {\n");
            foreach (string area in AreaAttributes)
            {
                b.Append("/// <summary>").Append(area).AppendLine(" 영역의 0 기반 주소를 지정합니다. 기존 ModbusPoint와 같은 코드를 생성합니다.</summary>");
                b.AppendLine("[global::System.AttributeUsage(global::System.AttributeTargets.Property | global::System.AttributeTargets.Field, AllowMultiple=false, Inherited=false)]");
                b.Append("internal sealed class ").Append(area).AppendLine("Attribute : ModbusPointAttribute {");
                b.AppendLine("/// <summary>영역 내 시작 주소를 지정합니다.</summary>");
                b.AppendLine("/// <param name=\"address\">0~65535의 시작 주소입니다.</param>");
                b.Append("public ").Append(area).Append("Attribute(int address) : base(ModbusArea.").Append(area).AppendLine(", address) { } }");
            }
            return b.AppendLine("}").ToString();
        }
    }
}
