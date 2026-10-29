using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace VagabondK.nF.IEC61850.SCL
{
    /// <summary>PC 전용의 제한된 SCL 정적 모델 생성기입니다. 지원하지 않는 모델은 오류 위치를 알려 거절합니다.</summary>
    public static class SclGenerator
    {
        private const string SclNamespace = "http://www.iec.ch/61850/2003/SCL";
        /// <summary>단일 IED/AccessPoint/Server의 말단 데이터 항목·Enum·제어·DataSet·제한형 URCB를 C# 7.3 코드로 생성합니다.</summary>
        /// <param name="source">IEC SCL 네임스페이스를 사용하는 XML 문서 전체입니다. 최대 2097152자이며 DTD와 외부 리소스는 허용하지 않습니다.</param>
        /// <returns>정적 서버 모델과 세션 생성 진입점을 선언하는 C# 소스 문자열입니다. 파일을 직접 기록하지 않습니다.</returns>
        /// <exception cref="ArgumentNullException">XML 문자열이 null입니다.</exception>
        /// <exception cref="XmlException">XML 형식·크기 제한 또는 DTD 금지 조건을 위반합니다.</exception>
        /// <exception cref="FormatException">SCL 모델이 지원 범위를 벗어납니다. 모델 오류에는 가능한 경우 행 번호가 포함됩니다.</exception>
        public static string GenerateText(string source)
        {
            if (source == null) throw new ArgumentNullException("source");
            using (var text = new StringReader(source))
            using (var reader = XmlReader.Create(text, new XmlReaderSettings {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2097152 }))
            {
                var document = XDocument.Load(reader, LoadOptions.SetLineInfo);
                var root = document.Root;
                if (root == null || root.Name != XName.Get("SCL", SclNamespace)) throw new FormatException("Expected IEC SCL namespace.");
                return new Model(root).Generate();
            }
        }
        private static FormatException Error(XElement e, string message)
        { return new FormatException("SCL line " + ((IXmlLineInfo)e).LineNumber + ": " + message); }
        private static string Attr(XElement e, string name, string fallback = null)
        {
            string value = (string)e.Attribute(name) ?? fallback;
            if (value == null || value.Length == 0) throw Error(e, "Missing " + name);
            return value;
        }
        private static void Shape(XElement e, string attributes, string children)
        {
            foreach (var a in e.Attributes())
                if (!a.IsNamespaceDeclaration && a.Name != XNamespace.Xml + "space" &&
                    (a.Name.NamespaceName.Length != 0 || !(" " + attributes + " desc ").Contains(" " + a.Name.LocalName + " ")))
                    throw Error(e, "Unsupported attribute " + a.Name);
            foreach (var child in e.Elements())
                if (child.Name.NamespaceName != SclNamespace || !(" " + children + " ").Contains(" " + child.Name.LocalName + " "))
                    throw Error(child, "Unsupported element " + child.Name.LocalName);
        }
        private static XElement[] Children(XElement e, string name)
        { return e.Elements(XName.Get(name, SclNamespace)).ToArray(); }
        private static XElement One(XElement e, string name)
        {
            var elements = Children(e, name);
            if (elements.Length != 1) throw Error(e, "Expected exactly one " + name);
            return elements[0];
        }
        private static bool Flag(XElement e, string name, bool fallback = false)
        {
            string value = (string)e.Attribute(name);
            if (value == null) return fallback;
            if (value == "true" || value == "1") return true;
            if (value == "false" || value == "0") return false;
            throw Error(e, "Invalid Boolean " + name);
        }
        private static int Number(XElement e, string name, int fallback, int minimum, int maximum)
        {
            int value;
            string text = (string)e.Attribute(name);
            if (text == null) return fallback;
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) || value < minimum || value > maximum)
                throw Error(e, "Invalid range " + name);
            return value;
        }
        private static string Name(XElement e, string name, string fallback = null)
        {
            string value = Attr(e, name, fallback);
            if (value.Length > 64 || value.Any(c => !(c >= 'A' && c <= 'Z') && !(c >= 'a' && c <= 'z') &&
                !(c >= '0' && c <= '9') && c != '_')) throw Error(e, "Unsupported identifier " + name);
            return value;
        }
        private sealed class Leaf
        {
            internal string Domain, Item, Constructor;
            internal bool Reportable;
        }
        private sealed class Node
        {
            internal XElement Xml;
            internal string Domain, Device, Name;
        }
        private sealed class Model
        {
            private readonly XElement root;
            private readonly Dictionary<string, XElement> types = new Dictionary<string, XElement>(StringComparer.Ordinal);
            private readonly List<Leaf> leaves = new List<Leaf>();
            private readonly List<Node> nodes = new List<Node>();
            private readonly Dictionary<string, int> leafIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            private readonly Dictionary<string, string> initial = new Dictionary<string, string>(StringComparer.Ordinal);
            private readonly List<string> sets = new List<string>(), reports = new List<string>();
            private readonly List<string> controls = new List<string>();
            private readonly HashSet<string> controlNames = new HashSet<string>(StringComparer.Ordinal);
            private readonly HashSet<string> controlItems = new HashSet<string>(StringComparer.Ordinal);
            private int controlVariables;
            private readonly Dictionary<string, int> setIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            private readonly List<int[]> setMembers = new List<int[]>();
            private readonly HashSet<string> reportIds = new HashSet<string>(StringComparer.Ordinal);
            internal Model(XElement root) { this.root = root; }
            private XElement Type(XElement at, string name, string kind)
            {
                XElement value;
                if (!types.TryGetValue(kind + ":" + name, out value)) throw Error(at, "Missing " + kind + " " + name);
                return value;
            }
            internal string Generate()
            {
                Shape(root, "version revision release", "Header IED DataTypeTemplates");
                if (Children(root, "Header").Length > 1) throw Error(root, "Multiple Headers.");
                var templates = One(root, "DataTypeTemplates");
                Shape(templates, "", "LNodeType DOType DAType EnumType");
                foreach (var type in templates.Elements())
                {
                    string key = type.Name.LocalName + ":" + Name(type, "id");
                    if (types.ContainsKey(key)) throw Error(type, "Duplicate template " + key);
                    types.Add(key, type);
                }
                var ied = One(root, "IED"); Shape(ied, "name manufacturer type configVersion", "AccessPoint");
                string iedName = Name(ied, "name");
                var access = One(ied, "AccessPoint"); Shape(access, "name", "Server"); Name(access, "name");
                var server = One(access, "Server"); Shape(server, "", "LDevice");
                var devices = Children(server, "LDevice");
                if (devices.Length == 0) throw Error(server, "No logical devices.");
                var domains = new HashSet<string>(StringComparer.Ordinal);
                var instances = new HashSet<string>(StringComparer.Ordinal);
                foreach (var device in devices)
                {
                    Shape(device, "inst ldName", "LN0 LN");
                    string inst = Name(device, "inst"), domain = Name(device, "ldName", iedName + inst);
                    if (!domains.Add(domain) || !instances.Add(inst)) throw Error(device, "Duplicate logical device/domain.");
                    One(device, "LN0");
                    var nodeNames = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var xml in device.Elements())
                    {
                        Shape(xml, "prefix lnClass inst lnType", "DOI DataSet ReportControl");
                        string lnClass = Name(xml, "lnClass"), prefix = (string)xml.Attribute("prefix") ?? "",
                            lnInst = (string)xml.Attribute("inst") ?? "";
                        if (prefix.Length != 0) Name(xml, "prefix");
                        if (lnInst.Length != 0) Name(xml, "inst");
                        string ln = prefix + lnClass + lnInst;
                        if (xml.Name.LocalName == "LN0" && (ln != "LLN0")) throw Error(xml, "LN0 must be LLN0.");
                        if (ln.Length > 64 || !nodeNames.Add(ln)) throw Error(xml, "Duplicate/oversized logical node.");
                        nodes.Add(new Node { Xml = xml, Domain = domain, Device = inst, Name = ln });
                        foreach (var doi in Children(xml, "DOI")) Initial(doi, domain + "/" + ln, true);
                        var lnType = Type(xml, Name(xml, "lnType"), "LNodeType");
                        Shape(lnType, "id lnClass iedType", "DO");
                        if (Attr(lnType, "lnClass") != lnClass) throw Error(xml, "lnClass differs from template.");
                        foreach (var dataObject in Children(lnType, "DO"))
                        {
                            Shape(dataObject, "name type", "");
                            ExpandObject(Type(dataObject, Name(dataObject, "type"), "DOType"), domain, ln,
                                Name(dataObject, "name"), new HashSet<string>(StringComparer.Ordinal));
                        }
                    }
                }
                foreach (string key in initial.Keys)
                    if (!leafIndex.ContainsKey(key)) throw Error(root, "Initial value has no leaf: " + key);
                foreach (var node in nodes)
                    foreach (var set in Children(node.Xml, "DataSet")) DataSet(node, set);
                foreach (var node in nodes)
                    foreach (var report in Children(node.Xml, "ReportControl")) Report(node, report);
                if (leaves.Count + controlVariables == 0 || leaves.Count + reports.Count * 12 + controlVariables > 128)
                    throw Error(root, "Model exceeds 128 variables or is empty.");
                var output = new StringBuilder();
                output.Append("using VagabondK.nF.MMS;\nusing VagabondK.nF.MMS.Server;\nusing VagabondK.nF.IEC61850;\nusing VagabondK.nF.IEC61850.Server;\n\n");
                output.Append("/// <summary>SCL에서 확정한 변수·DataSet·제어·URCB를 보관하는 정적 서버 모델입니다.</summary>\n");
                output.Append("/// <remarks>공유 모델의 값 갱신과 모든 세션 처리를 직렬화하십시오. 인증이나 설비 동작을 제공하지 않습니다.</remarks>\n");
                output.Append("public sealed class GeneratedIec61850Model\n{\n");
                for (int i = 0; i < leaves.Count; i++)
                    output.Append("    /// <summary>").Append(leaves[i].Domain).Append("/").Append(leaves[i].Item)
                        .Append("의 초기값과 전송 형식을 보관하는 공유 서버 변수입니다.</summary>\n")
                        .Append("    public readonly MmsServerVariable V").Append(i).Append(";\n");
                output.Append("    /// <summary>SCL 말단 데이터 항목 순서로 정렬된 일반 변수 배열입니다. 세션 생성 이후 배열 항목을 변경하지 마십시오.</summary>\n");
                output.Append("    public readonly MmsServerVariable[] Variables;\n");
                output.Append("    /// <summary>일반 변수 인스턴스를 참조하는 고정 DataSet 배열입니다.</summary>\n    public readonly MmsServerDataSet[] DataSets;\n");
                output.Append("    /// <summary>세션의 ProcessReports 호출로 송신하는 비버퍼형 보고 제어 배열입니다.</summary>\n    public readonly MmsServerReportControl[] Reports;\n");
                output.Append("    /// <summary>실행 처리기와 허용 정책을 애플리케이션에서 연결할 공유 제어 배열입니다.</summary>\n    public readonly MmsServerControl[] Controls;\n");
                output.Append("    /// <summary>SCL 초기값으로 독립된 변수 버퍼와 공유 DataSet·보고·제어 객체를 생성합니다.</summary>\n");
                output.Append("    public GeneratedIec61850Model()\n    {\n");
                for (int i = 0; i < leaves.Count; i++)
                    output.Append("        V").Append(i).Append(" = ").Append(leaves[i].Constructor).Append(";\n");
                output.Append("        Variables = new MmsServerVariable[] { ").Append(string.Join(", ", Enumerable.Range(0, leaves.Count).Select(i => "V" + i))).Append(" };\n");
                output.Append("        DataSets = new MmsServerDataSet[] {\n");
                foreach (string set in sets) output.Append("            ").Append(set).Append(",\n");
                output.Append("        };\n        Reports = new MmsServerReportControl[] {\n");
                foreach (string report in reports) output.Append("            ").Append(report).Append(",\n");
                output.Append("        };\n        Controls = new MmsServerControl[] {\n");
                foreach (string control in controls) output.Append("            ").Append(control).Append(",\n");
                output.Append("        };\n    }\n");
                output.Append("    /// <summary>이 모델을 공유하는 서버 세션을 생성합니다. Association과 요청 처리는 호출자 책임입니다.</summary>\n");
                output.Append("    /// <param name=\"channel\">이미 연결된 호출자 소유 채널입니다. 세션은 채널을 해제하지 않습니다.</param>\n");
                output.Append("    /// <returns>모델 갱신 및 다른 세션의 처리와 직렬화하여 사용할 IEC 61850 서버 세션입니다.</returns>\n");
                output.Append("    public Iec61850ServerSession CreateSession(IMmsChannel channel)\n    {\n");
                output.Append("        return new Iec61850ServerSession(channel, Variables, controls: Controls, dataSets: DataSets, reports: Reports);\n    }\n}\n");
                return output.ToString();
            }
            private void Initial(XElement e, string parent, bool objectNode)
            {
                Shape(e, "name", objectNode ? "DAI SDI" : "DAI SDI Val");
                string path = parent + "$" + Name(e, "name");
                var values = Children(e, "Val");
                if (values.Length > 1) throw Error(e, "Multiple initial values.");
                if (values.Length == 1)
                {
                    Shape(values[0], "", "");
                    if (e.Name.LocalName != "DAI" || initial.ContainsKey(path)) throw Error(e, "Invalid initial value path.");
                    initial.Add(path, values[0].Value.Trim());
                }
                foreach (var child in e.Elements().Where(x => x.Name.LocalName != "Val")) Initial(child, path, false);
            }
            private void ExpandObject(XElement type, string domain, string ln, string path, HashSet<string> visiting)
            {
                string key = "DO:" + Attr(type, "id");
                if (path.Length > 64 || visiting.Count >= 16) throw Error(type, "Template nesting exceeds 16 or item path exceeds 64.");
                if (!visiting.Add(key)) throw Error(type, "Cyclic DOType.");
                Shape(type, "id cdc iedType", "DA SDO");
                if (Children(type, "DA").Any(e => (string)e.Attribute("fc") == "CO"))
                {
                    Control(type, domain, ln, path);
                    visiting.Remove(key); return;
                }
                foreach (var child in type.Elements())
                {
                    if (child.Name.LocalName == "SDO")
                    {
                        Shape(child, "name type", "");
                        ExpandObject(Type(child, Name(child, "type"), "DOType"), domain, ln, path + "$" + Name(child, "name"), visiting);
                    }
                    else Attribute(child, domain, ln, path, Attr(child, "fc"), visiting);
                }
                visiting.Remove(key);
            }
            private void Attribute(XElement attribute, string domain, string ln, string parent, string fc, HashSet<string> visiting,
                bool parentDataChange = false, bool parentQualityChange = false)
            {
                Shape(attribute, attribute.Name.LocalName == "DA" ? "name bType type fc dchg qchg dupd" : "name bType type", "Val");
                if (fc != "ST" && fc != "MX" && fc != "SP" && fc != "CF") throw Error(attribute, "Unsupported FC " + fc);
                if (Flag(attribute, "dupd")) throw Error(attribute, "data-update trigger is unsupported.");
                bool dataChange = attribute.Name.LocalName == "DA" ? Flag(attribute, "dchg") : parentDataChange;
                bool qualityChange = attribute.Name.LocalName == "DA" ? Flag(attribute, "qchg") : parentQualityChange;
                string path = parent + "$" + Name(attribute, "name"), kind = Attr(attribute, "bType");
                if (path.Length > 64 || visiting.Count >= 16) throw Error(attribute, "Template nesting exceeds 16 or item path exceeds 64.");
                if (kind == "Struct")
                {
                    if (Children(attribute, "Val").Length != 0) throw Error(attribute, "Structured initial value is unsupported.");
                    var type = Type(attribute, Name(attribute, "type"), "DAType"); Shape(type, "id iedType", "BDA");
                    string key = "DA:" + Attr(type, "id");
                    if (!visiting.Add(key)) throw Error(type, "Cyclic DAType.");
                    foreach (var child in Children(type, "BDA")) Attribute(child, domain, ln, path, fc, visiting, dataChange, qualityChange);
                    visiting.Remove(key); return;
                }
                string item = ln + "$" + fc + "$" + path;
                if (item.Length > 64) throw Error(attribute, "MMS item exceeds 64 characters.");
                string wireKey = domain + "/" + item, valueKey = domain + "/" + ln + "$" + path;
                if (leafIndex.ContainsKey(wireKey) || controlItems.Contains(wireKey)) throw Error(attribute, "Duplicate MMS leaf " + wireKey);
                string value = null;
                var defaults = Children(attribute, "Val");
                if (defaults.Length > 1) throw Error(attribute, "Multiple template initial values.");
                if (defaults.Length == 1) { Shape(defaults[0], "", ""); value = defaults[0].Value.Trim(); }
                string instance;
                if (initial.TryGetValue(valueKey, out instance)) { value = instance; initial.Remove(valueKey); }
                string args = "\"" + domain + "\", \"" + item + "\"", constructor;
                if (kind == "BOOLEAN")
                {
                    value = value ?? "false";
                    if (value != "true" && value != "false") throw Error(attribute, "Expected Boolean true/false.");
                    constructor = "new MmsServerVariable(" + args + ", " + value + ")";
                }
                else if (kind == "INT32" || kind == "Enum")
                {
                    int integer;
                    if (kind == "Enum") integer = Enumeration(attribute, value);
                    else if (!int.TryParse(value ?? "0", NumberStyles.Integer, CultureInfo.InvariantCulture, out integer))
                        throw Error(attribute, "Invalid INT32.");
                    constructor = "new MmsServerVariable(" + args + ", " + integer.ToString(CultureInfo.InvariantCulture) + ")";
                }
                else if (kind == "FLOAT32")
                {
                    float real;
                    if (!float.TryParse(value ?? "0", NumberStyles.Float, CultureInfo.InvariantCulture, out real) || float.IsInfinity(real) || float.IsNaN(real))
                        throw Error(attribute, "Invalid finite FLOAT32.");
                    constructor = "new MmsServerVariable(" + args + ", " + real.ToString("R", CultureInfo.InvariantCulture) + "f)";
                }
                else if (kind == "Dbpos")
                {
                    value = value ?? "intermediate-state";
                    string state = value == "intermediate-state" ? "Intermediate" : value == "off" ? "Off" :
                        value == "on" ? "On" : value == "bad-state" ? "Bad" : null;
                    if (state == null) throw Error(attribute, "Invalid Dbpos state.");
                    constructor = "Iec61850ServerData.CreateDoublePoint(" + args + ", IecDoublePoint." + state + ")";
                }
                else if (kind == "Quality" || kind == "Timestamp")
                {
                    if (value != null) throw Error(attribute, "Quality/Timestamp initial Val is unsupported; set it explicitly at runtime.");
                    constructor = "Iec61850ServerData.Create" + (kind == "Quality" ? "Quality" : "Timestamp") + "(" + args + ")";
                }
                else throw Error(attribute, "Unsupported bType " + kind);
                leafIndex.Add(wireKey, leaves.Count);
                leaves.Add(new Leaf { Domain = domain, Item = item, Constructor = constructor,
                    Reportable = kind == "Quality" ? qualityChange && !dataChange : dataChange && !qualityChange });
                if (leaves.Count > 128) throw Error(attribute, "More than 128 leaves.");
            }
            private int Enumeration(XElement attribute, string value)
            {
                var type = Type(attribute, Name(attribute, "type"), "EnumType");
                Shape(type, "id", "EnumVal");
                var labels = new Dictionary<string, int>(StringComparer.Ordinal);
                var ordinals = new HashSet<int>();
                foreach (var member in Children(type, "EnumVal"))
                {
                    Shape(member, "ord", "");
                    int ordinal;
                    if (!int.TryParse(Attr(member, "ord"), NumberStyles.Integer, CultureInfo.InvariantCulture, out ordinal))
                        throw Error(member, "Enum ordinal exceeds INT32.");
                    string label = member.Value.Trim();
                    if (label.Length == 0 || !labels.TryAdd(label, ordinal) || !ordinals.Add(ordinal))
                        throw Error(member, "Duplicate/empty Enum label or ordinal.");
                }
                if (labels.Count == 0) throw Error(type, "Empty EnumType.");
                int result;
                if (value != null && labels.TryGetValue(value, out result)) return result;
                if (int.TryParse(value ?? "0", NumberStyles.Integer, CultureInfo.InvariantCulture, out result) && ordinals.Contains(result))
                    return result;
                throw Error(attribute, "Initial Enum value must resolve to a declared ordinal.");
            }
            private string InitialValue(XElement attribute, string domain, string ln, string path)
            {
                var defaults = Children(attribute, "Val");
                if (defaults.Length > 1) throw Error(attribute, "Multiple initial values.");
                string value = null;
                if (defaults.Length == 1) { Shape(defaults[0], "", ""); value = defaults[0].Value.Trim(); }
                string key = domain + "/" + ln + "$" + path, instance;
                if (initial.TryGetValue(key, out instance)) { value = instance; initial.Remove(key); }
                return value;
            }
            private void Control(XElement type, string domain, string ln, string path)
            {
                string cdc = Attr(type, "cdc");
                if ((cdc != "SPC" && cdc != "DPC" && cdc != "APC") || path.Contains("$") || Children(type, "SDO").Length != 0)
                    throw Error(type, "Only unnested SPC/DPC/Float32 APC controls are supported.");
                var attributes = Children(type, "DA");
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var attribute in attributes)
                {
                    Shape(attribute, "name bType type fc dchg qchg dupd", "Val");
                    string attributeName = Name(attribute, "name");
                    if (Flag(attribute, "dupd") || ((Flag(attribute, "dchg") || Flag(attribute, "qchg")) &&
                        attributeName != "stVal" && attributeName != "mxVal" && attributeName != "q" && attributeName != "t"))
                        throw Error(attribute, "Unsupported control trigger metadata.");
                    if (!names.Add(attributeName)) throw Error(attribute, "Duplicate control DA.");
                }
                XElement model = attributes.SingleOrDefault(e => (string)e.Attribute("name") == "ctlModel");
                if (model == null || Attr(model, "fc") != "CF" || Attr(model, "bType") != "Enum")
                    throw Error(type, "Control requires CF Enum ctlModel.");
                int mode = Enumeration(model, InitialValue(model, domain, ln, path + "$ctlModel"));
                if (mode < 1 || mode > 4) throw Error(model, "Control model must be 1..4.");
                int timeout = 1000;
                foreach (var attribute in attributes)
                {
                    string name = Attr(attribute, "name"), fc = Attr(attribute, "fc"), kind = Attr(attribute, "bType");
                    if (name == "ctlModel") continue;
                    if (name == "sboTimeout" || name == "sboClass")
                    {
                        if ((mode != 2 && mode != 4) || fc != "CF") throw Error(attribute, "SBO configuration requires a selected model.");
                        string value = InitialValue(attribute, domain, ln, path + "$" + name);
                        if (name == "sboTimeout")
                        {
                            if (kind != "INT32U" || !int.TryParse(value ?? "1000", NumberStyles.None, CultureInfo.InvariantCulture, out timeout) ||
                                timeout < 1 || timeout > 60000) throw Error(attribute, "sboTimeout requires INT32U 1..60000.");
                        }
                        else if (kind != "Enum" || Enumeration(attribute, value) != 0)
                            throw Error(attribute, "Only operate-once sboClass is supported.");
                    }
                    else if (name == "SBO" && mode == 2 && fc == "CO" && kind == "VisString129")
                    {
                        if (InitialValue(attribute, domain, ln, path + "$" + name) != null) throw Error(attribute, "SBO Val is unsupported.");
                    }
                    else if (fc == "CO")
                    {
                        if (kind != "Struct" || (name != "Oper" && name != "Cancel" && !(name == "SBOw" && mode == 4)))
                            throw Error(attribute, "Unsupported control command.");
                        CommandType(attribute, cdc == "APC", name != "Cancel");
                    }
                    else if ((name == "stVal" && cdc != "APC" && fc == "ST" && kind == (cdc == "DPC" ? "Dbpos" : "BOOLEAN")) ||
                        (name == "mxVal" && cdc == "APC" && fc == "MX" && kind == "Struct"))
                    {
                        if (InitialValue(attribute, domain, ln, path + "$" + name) != null)
                            throw Error(attribute, "Control state initial values are supplied by the runtime profile.");
                        if (cdc == "APC") Fields(attribute, new[] { "f:FLOAT32" });
                    }
                    else if ((name == "q" && kind == "Quality") || (name == "t" && kind == "Timestamp"))
                        Attribute(attribute, domain, ln, path, fc, new HashSet<string>(StringComparer.Ordinal));
                    else throw Error(attribute, "Unsupported control DA " + name);
                }
                if (!names.Contains("Oper") || !names.Contains("Cancel") || !names.Contains(cdc == "APC" ? "mxVal" : "stVal") ||
                    (mode == 2 && !names.Contains("SBO")) || (mode == 4 && !names.Contains("SBOw")))
                    throw Error(type, "Incomplete control template.");
                string item = ln + "$CO$" + path;
                if (item.Length > 53 || !controlNames.Add(domain + "/" + item)) throw Error(type, "Duplicate/oversized control.");
                string configuration = ln + "$CF$" + path;
                var reserved = new List<string> { item, item + "$Oper", item + "$Cancel", configuration + "$ctlModel",
                    ln + (cdc == "APC" ? "$MX$" : "$ST$") + path + (cdc == "APC" ? "$mxVal" : "$stVal") };
                if (mode == 2 || mode == 4)
                    reserved.AddRange(new[] { item + (mode == 2 ? "$SBO" : "$SBOw"), configuration + "$sboTimeout", configuration + "$sboClass" });
                if (cdc == "APC") reserved.Add(ln + "$MX$" + path + "$mxVal$f");
                foreach (string name in reserved)
                    if (leafIndex.ContainsKey(domain + "/" + name) || !controlItems.Add(domain + "/" + name))
                        throw Error(type, "Duplicate runtime control variable.");
                controlVariables += (mode == 2 || mode == 4 ? 8 : 5) + (cdc == "APC" ? 1 : 0);
                controls.Add("new MmsServerControl(\"" + domain + "\", \"" + item + "\", (MmsControlModel)" + mode +
                    ", selectionTimeoutMilliseconds: " + timeout + ", valueKind: MmsControlValueKind." +
                    (cdc == "APC" ? "AnalogFloat32" : cdc == "DPC" ? "DoublePoint" : "Boolean") + ")");
            }
            private XElement[] Fields(XElement attribute, string[] expected)
            {
                if (Children(attribute, "Val").Length != 0) throw Error(attribute, "Command/structure Val is unsupported.");
                var type = Type(attribute, Name(attribute, "type"), "DAType"); Shape(type, "id iedType", "BDA");
                var fields = Children(type, "BDA");
                if (fields.Length != expected.Length) throw Error(type, "Unsupported control structure field count.");
                for (int i = 0; i < fields.Length; i++)
                {
                    Shape(fields[i], "name bType type", "");
                    if (Attr(fields[i], "name") + ":" + Attr(fields[i], "bType") != expected[i])
                        throw Error(fields[i], "Unsupported control structure field/order.");
                }
                return fields;
            }
            private void CommandType(XElement attribute, bool analog, bool operate)
            {
                var fields = Fields(attribute, operate ?
                    new[] { "ctlVal:" + (analog ? "Struct" : "BOOLEAN"), "origin:Struct", "ctlNum:INT8U", "T:Timestamp", "Test:BOOLEAN", "Check:Check" } :
                    new[] { "ctlVal:" + (analog ? "Struct" : "BOOLEAN"), "origin:Struct", "ctlNum:INT8U", "T:Timestamp", "Test:BOOLEAN" });
                if (analog) Fields(fields[0], new[] { "f:FLOAT32" });
                var origin = Fields(fields[1], new[] { "orCat:Enum", "orIdent:Octet64" });
                Enumeration(origin[0], "0");
            }
            private void DataSet(Node node, XElement e)
            {
                Shape(e, "name", "FCDA");
                string name = node.Name + "$" + Name(e, "name"), key = node.Domain + "/" + name;
                if (name.Length > 64 || setIndex.ContainsKey(key) || sets.Count == 8) throw Error(e, "Duplicate/oversized DataSet or more than 8.");
                var entries = Children(e, "FCDA");
                if (entries.Length < 1 || entries.Length > 16) throw Error(e, "DataSet requires 1..16 leaf members.");
                var members = new List<string>();
                var indices = new List<int>();
                foreach (var entry in entries)
                {
                    Shape(entry, "ldInst prefix lnClass lnInst doName daName fc", "");
                    string device = Name(entry, "ldInst", node.Device), prefix = (string)entry.Attribute("prefix") ?? "",
                        inst = (string)entry.Attribute("lnInst") ?? "";
                    if (prefix.Length != 0) Name(entry, "prefix");
                    if (inst.Length != 0) Name(entry, "lnInst");
                    string ln = prefix + Name(entry, "lnClass") + inst;
                    var target = nodes.SingleOrDefault(n => n.Device == device && n.Name == ln);
                    if (target == null) throw Error(entry, "Unknown FCDA logical node.");
                    string path = Attr(entry, "doName") + "." + Attr(entry, "daName");
                    foreach (string part in path.Split('.'))
                        if (part.Length == 0 || part.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')) throw Error(entry, "Unsupported FCDA path.");
                    int index;
                    string reference = target.Domain + "/" + ln + "$" + Attr(entry, "fc") + "$" + path.Replace('.', '$');
                    if (!leafIndex.TryGetValue(reference, out index)) throw Error(entry, "FCDA must resolve to supported leaf: " + reference);
                    members.Add("V" + index);
                    indices.Add(index);
                }
                setIndex.Add(key, sets.Count);
                setMembers.Add(indices.ToArray());
                sets.Add("new MmsServerDataSet(\"" + node.Domain + "\", \"" + name + "\", new MmsServerVariable[] { " + string.Join(", ", members) + " })");
            }
            private void Report(Node node, XElement e)
            {
                Shape(e, "name rptID datSet confRev buffered bufTime intgPd indexed", "TrgOps OptFields RptEnabled");
                string name = node.Name + "$RP$" + Name(e, "name"), id = Name(e, "rptID");
                int index;
                if (!setIndex.TryGetValue(node.Domain + "/" + node.Name + "$" + Name(e, "datSet"), out index)) throw Error(e, "Unknown RCB DataSet.");
                if (name.Length > 55 || !reportIds.Add(id) || reports.Count == 4) throw Error(e, "Duplicate/oversized RCB or more than 4.");
                if (Flag(e, "buffered") || Flag(e, "indexed", true) || Number(e, "bufTime", 0, 0, 0) != 0)
                    throw Error(e, "Only nonindexed URCB with BufTm=0 is supported.");
                foreach (int member in setMembers[index])
                    if (!leaves[member].Reportable) throw Error(e, "URCB members require explicit dchg (Quality: qchg) metadata.");
                int revision = Number(e, "confRev", 1, 1, int.MaxValue), period = Number(e, "intgPd", 0, 0, 60000);
                if (period != 0 && period < 100) throw Error(e, "Integrity period must be 0 or 100..60000ms.");
                var triggers = One(e, "TrgOps"); Shape(triggers, "dchg qchg dupd period gi", "");
                if (!Flag(triggers, "dchg") || !Flag(triggers, "qchg") || Flag(triggers, "dupd") ||
                    !Flag(triggers, "period") || !Flag(triggers, "gi")) throw Error(triggers, "Requires dchg/qchg/period/gi only.");
                var options = One(e, "OptFields");
                Shape(options, "seqNum timeStamp dataSet reasonCode dataRef entryID configRef bufOvfl", "");
                if (!Flag(options, "seqNum") || !Flag(options, "dataSet") || !Flag(options, "reasonCode") || !Flag(options, "configRef") ||
                    Flag(options, "timeStamp") || Flag(options, "dataRef") || Flag(options, "entryID") || Flag(options, "bufOvfl"))
                    throw Error(options, "Requires seqNum/reasonCode/dataSet/configRef only.");
                var enabled = Children(e, "RptEnabled");
                if (enabled.Length > 1) throw Error(e, "Multiple RptEnabled.");
                if (enabled.Length == 1)
                {
                    Shape(enabled[0], "max", "");
                    if (Number(enabled[0], "max", 1, 1, 1) != 1) throw Error(enabled[0], "Only one RCB owner.");
                }
                reports.Add("new MmsServerReportControl(\"" + node.Domain + "\", \"" + name + "\", \"" + id +
                    "\", DataSets[" + index + "], " + revision + "u, " + period + ")");
            }
        }
    }
}
