using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace VagabondK.nF.WebUi
{
    /// <summary>
    /// 셸+프로필 페이지를 머지/축소/gzip/해시하여 WebPage 소스 텍스트로 만드는 순수 로직.
    /// VagabondK.nF.WebUi.targets의 인라인 RoslynCodeTask 로직에서 그대로 이식했다.
    /// </summary>
    internal sealed class PageComposer
    {
        internal sealed class Result
        {
            public string Source;      // 생성 실패 시 null
            public string ErrorFile;   // 실패 원본 파일 경로
            public string ErrorMessage;
            public string Info;        // 크기 요약 메시지
        }

        static readonly string[] Markers = { "<!--@manifest-->", "<!--@core-->", "<!--@style-->", "<!--@views-->", "<!--@script-->" };
        const string SlotsMarker = "<!--@slots-->";
        const string InstanceMarker = "<!--@instance-->";
        static readonly Regex SlotToken = new Regex(@"^<!--@slot:([A-Za-z0-9_-]+)-->$", RegexOptions.Compiled);
        static readonly Regex UseToken = new Regex(@"^<!--@use:([A-Za-z0-9_-]+)-->$", RegexOptions.Compiled);
        // <name/> 문법: 비-void 요소의 자기종결은 HTML 금기 표기라 줄 전체가 마커다(Phase 2 로드맵).
        static readonly Regex SelfCloseTag = new Regex(@"^<([a-z][a-z0-9-]*)\s*/>$", RegexOptions.Compiled);
        static readonly HashSet<string> VoidNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "br", "hr", "img", "input", "link", "meta", "base", "area", "embed", "col", "param", "source", "track", "wbr" };
        // 폼 컨트롤과 data-label/data-fmt 속성 스캔용(마크업 → manifest.form 자동 유도).
        static readonly Regex ControlTag = new Regex(@"<(?:input|select|textarea)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>&lt;name&gt;.html과 같은 폴더의 &lt;name&gt;.{js,css} 한 벌. Html은 필수, 나머지는 선택.</summary>
        internal sealed class ComponentFiles
        {
            public string HtmlPath;
            public string[] Html;
            public string[] Js = new string[0];
            public string[] Css = new string[0];
        }

        string componentsRoot;
        Dictionary<string, ComponentFiles> components;
        List<string> useOrder;
        HashSet<string> useDone, useVisiting, compSlots;
        Dictionary<string, List<string>> slotMap;
        static readonly string[] Legacy = { "PROFILE", "KEYS", "LABELS", "formValues", "fillForm", "profileDirty",
            "profileExport", "profileImport", "showValue", "profileAddr", "profileScale", "profileAccess",
            "profilePoints", "profileName", "profileByteOrder", "profileCacheCols" };
        static readonly string[] FeatureNames = { "points", "scale", "name", "byteOrder", "access", "addr", "cacheCols" };
        static readonly string[] Fmts = { "ms", "pin", "secret", "bool", "text" };

        Result res;

        PageComposer() { }

        internal static string[] ReadLines(string text)
        {
            // File.ReadAllLines와 동일하게: 줄바꿈으로 자르고 마지막 빈 원소는 버린다.
            if (text.Length == 0) return new string[0];
            string[] lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            if (lines.Length > 0 && lines[lines.Length - 1].Length == 0)
            {
                var trimmed = new string[lines.Length - 1];
                Array.Copy(lines, trimmed, trimmed.Length);
                return trimmed;
            }
            return lines;
        }

        internal static Result Compose(string shellText, string profileText, string coreText, string shellPath, string profilePath, string ns, string cls,
            string id, string componentsRoot, Dictionary<string, ComponentFiles> components)
        {
            var c = new PageComposer();
            return c.Run(shellText, profileText, coreText, shellPath, profilePath, ns, cls, id, componentsRoot, components);
        }

        void Fail(string file, string message) { res.ErrorFile = file; res.ErrorMessage = message; }

        static string MarkerOf(string raw)
        {
            string line = raw.Trim();
            for (int i = 0; i < Markers.Length; i++) if (Markers[i] == line) return line;
            return null;
        }

        Result Run(string shellText, string profileText, string coreText, string shellPath, string profilePath, string ns, string cls,
            string id, string componentsRoot, Dictionary<string, ComponentFiles> components)
        {
            res = new Result();
            var result = res;
            this.componentsRoot = (componentsRoot ?? string.Empty).Replace('|', ';');
            this.components = components ?? new Dictionary<string, ComponentFiles>(StringComparer.Ordinal);
            this.compSlots = new HashSet<string>(StringComparer.Ordinal);
            string[] shellLines = ReadLines(shellText);
            var shellAt = new Dictionary<string, int>();
            var shellSlots = new HashSet<string>();
            for (int i = 0; i < shellLines.Length; i++)
            {
                string m = MarkerOf(shellLines[i]);
                if (m == null)
                {
                    Match sm = SlotToken.Match(shellLines[i].Trim());
                    if (sm.Success) shellSlots.Add(sm.Groups[1].Value);
                    continue;
                }
                if (shellAt.ContainsKey(m)) { Fail(shellPath, "duplicate marker " + m); return result; }
                shellAt[m] = i;
            }
            foreach (string m in Markers)
                if (!shellAt.ContainsKey(m)) { Fail(shellPath, "missing marker " + m); return result; }
            if (shellAt["<!--@core-->"] < shellAt["<!--@manifest-->"]) { Fail(shellPath, "@core marker must come after @manifest"); return result; }

            var sections = new Dictionary<string, List<string>>();
            var slotSection = new List<string>();
            bool inSlots = false;
            string current = null;
            foreach (string raw in ReadLines(profileText))
            {
                string sline = raw.Trim();
                if (sline == SlotsMarker) { if (inSlots) { Fail(profilePath, "duplicate @slots marker"); return result; } inSlots = true; current = null; continue; }
                string m = MarkerOf(raw);
                if (m != null)
                {
                    inSlots = false;
                    if (m == "<!--@core-->") { Fail(profilePath, "@core is provided by the library; remove it from the profile"); return result; }
                    if (sections.ContainsKey(m)) { Fail(profilePath, "duplicate marker " + m); return result; }
                    current = m;
                    sections[m] = new List<string>();
                    continue;
                }
                if (inSlots) { slotSection.Add(raw); continue; }
                if (current == null) { if (raw.Trim().Length > 0) { Fail(profilePath, "content before the first marker"); return result; } continue; }
                sections[current].Add(raw);
            }
            foreach (string m in Markers)
                if (m != "<!--@core-->" && !sections.ContainsKey(m)) { Fail(profilePath, "missing marker " + m); return result; }

            var parts = new List<string>();
            foreach (string raw in sections["<!--@manifest-->"]) { string t = raw.Trim(); if (t.Length > 0) parts.Add(t); }
            object manifest;
            try { manifest = new MiniJson().Parse(string.Join(" ", parts.ToArray())); }
            catch (Exception e) { Fail(profilePath, "manifest JSON: " + e.Message); return result; }
            // WebUiId(msbuild 속성)는 manifest.id를 대체한다: 주입 시 선두로 옮기고, 값이 다르면 오류(단일 출처 강제).
            if (!string.IsNullOrEmpty(id))
            {
                var md = manifest as Dictionary<string, object>;
                if (md != null)
                {
                    object cur;
                    if (md.TryGetValue("id", out cur) && (cur as string) != id)
                    { Fail(profilePath, "manifest.id conflicts with WebUiId \"" + id + "\"; keep one source"); return result; }
                    var nd = new Dictionary<string, object>();
                    nd["id"] = id;
                    foreach (string k in md.Keys) if (k != "id") nd[k] = md[k];
                    manifest = nd;
                }
            }
            if (!ValidateManifest(manifest, profilePath)) return result;

            // Parse the profile @slots section into name -> markup lines; validate against shell positions.
            slotMap = new Dictionary<string, List<string>>();
            string curSlot = null;
            foreach (string raw in slotSection)
            {
                Match sm = SlotToken.Match(raw.Trim());
                if (sm.Success)
                {
                    curSlot = sm.Groups[1].Value;
                    if (slotMap.ContainsKey(curSlot)) { Fail(profilePath, "duplicate slot definition \"" + curSlot + "\""); return result; }
                    slotMap[curSlot] = new List<string>();
                    continue;
                }
                if (curSlot == null) { if (raw.Trim().Length > 0) { Fail(profilePath, "slot content before the first @slot:NAME token in @slots"); return result; } continue; }
                slotMap[curSlot].Add(raw);
            }
            // 슬롯 위치 검증은 아래 @use 확장 루프 이후에 수행한다(컴포넌트 안의 슬롯 토큰도 위치가 될 수 있으므로).

            // @use 컴포넌트 발견·확장: useOrder는 서브→부모 순서(자식이 먼저 나열되어 CSS 우선순위가 올바르게 겹침).
            useOrder = new List<string>();
            useDone = new HashSet<string>(StringComparer.Ordinal);
            useVisiting = new HashSet<string>(StringComparer.Ordinal);
            if (!DiscoverUses(shellLines, shellPath)) return result;
            if (!DiscoverUses(sections["<!--@views-->"], profilePath)) return result;
            foreach (List<string> body in slotMap.Values)
                if (!DiscoverUses(body, profilePath)) return result;
            var expanded = new Dictionary<string, string[]>(StringComparer.Ordinal);
            var compCss = new List<string>();
            var compJs = new List<string>();
            var templates = new List<string>();
            var instBody = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (string name in useOrder)
            {
                ComponentFiles c = components[name];
                string[] body = ExpandComponent(c.Html, expanded);
                if (IsInstance(c.Html))
                {
                    // 인스턴스 컴포넌트: html은 <template>으로 보내고 @use 자리는 <wui-name> 태그가 된다.
                    // js/css는 커스텀 엘리먼트 정의에 포장되어 인스턴스마다 shadow root 안에서 실행되므로
                    // id가 인스턴스별로 격리되고 여러 위치에서 안전하게 재사용할 수 있다.
                    string tag = "wui-" + name.ToLowerInvariant();
                    instBody[name] = body;
                    expanded[name] = new string[] { "<" + tag + "></" + tag + ">" };
                    templates.Add("<template id=\"wui-t-" + name + "\">");
                    templates.AddRange(body);
                    templates.Add("</template>");
                    string jsArg = c.Js.Length > 0
                        ? "function (root, document, $) {\n" + string.Join("\n", c.Js) + "\n}"
                        : "null";
                    string cssArg = c.Css.Length > 0 ? ", " + MiniJson.JsonOut(string.Join("\n", c.Css)) : "";
                    compJs.Add("WUI.def(" + MiniJson.JsonOut(name) + ", " + jsArg + cssArg + ");");
                }
                else
                {
                    expanded[name] = body;
                    compCss.AddRange(c.Css);
                    compJs.AddRange(c.Js);
                }
            }
            foreach (string k in slotMap.Keys)
                if (!shellSlots.Contains(k) && !compSlots.Contains(k)) { Fail(profilePath, "profile fills slot \"" + k + "\" but neither the shell nor a component has a <!--@slot:" + k + "--> position for it"); return result; }

            var allViews = new List<string>(ExpandTokens(sections["<!--@views-->"], expanded));
            var allScript = new List<string>(sections["<!--@script-->"]);
            foreach (string name in useOrder)
            {
                string[] b;
                allViews.AddRange(instBody.TryGetValue(name, out b) ? b : expanded[name]);
                allScript.AddRange(components[name].Js);
            }
            string views = string.Join("\n", allViews.ToArray());
            string script = string.Join("\n", allScript.ToArray());
            if (!DeriveForm(manifest, allViews, profilePath)) return result;
            if (!ValidateCross(manifest, views, script, profilePath)) return result;

            var merged = new List<string>();
            foreach (string raw in shellLines)
            {
                string m = MarkerOf(raw);
                if (m == null)
                {
                    Match sm = SlotToken.Match(raw.Trim());
                    if (sm.Success)
                    {
                        List<string> body;
                        if (slotMap.TryGetValue(sm.Groups[1].Value, out body)) merged.AddRange(ExpandTokens(body, expanded));
                        continue;
                    }
                    string uref = UseRef(raw.Trim());
                    if (uref != null) { merged.AddRange(expanded[uref]); continue; }
                    merged.Add(raw); continue;
                }
                if (m == "<!--@manifest-->") merged.Add("const MANIFEST = " + MiniJson.JsonOut(manifest) + ";");
                else if (m == "<!--@core-->") merged.AddRange(ReadLines(coreText));
                else if (m == "<!--@style-->") { merged.AddRange(compCss); merged.AddRange(sections[m]); }
                else if (m == "<!--@script-->") { merged.AddRange(compJs); merged.AddRange(sections[m]); }
                else merged.AddRange(ExpandTokens(sections[m], expanded));
            }
            if (templates.Count > 0)
            {
                int at = merged.FindIndex(l => l.Trim() == "<script>");
                if (at < 0) at = merged.Count;
                merged.InsertRange(at, templates);
            }

            var page = new StringBuilder();
            foreach (string raw in merged)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("//") || (line.StartsWith("<!--") && line.EndsWith("-->"))) continue;
                if (page.Length > 0) page.Append('\n');
                page.Append(line);
            }

            byte[] html = new UTF8Encoding(false).GetBytes(page.ToString());
            byte[] gzip;
            using (var memory = new MemoryStream())
            {
                using (var compressor = new GZipStream(memory, CompressionLevel.Optimal, true))
                    compressor.Write(html, 0, html.Length);
                gzip = memory.ToArray();
            }
            uint hash = 2166136261;
            unchecked { foreach (byte value in gzip) { hash ^= value; hash *= 16777619; } }

            const int PartBytes = 768;
            int partCount = (gzip.Length + PartBytes - 1) / PartBytes;
            if (string.IsNullOrEmpty(ns)) ns = "VagabondK.nF.WebUi";
            if (string.IsNullOrEmpty(cls)) cls = "WebPage";
            var code = new StringBuilder();
            code.Append("// <auto-generated>\n// Generated by VagabondK.nF.WebUi. Do not edit.\n// </auto-generated>\n");
            code.Append("namespace ").Append(ns).Append("\n{\n    internal static class ").Append(cls).Append("\n    {\n");
            code.Append("        internal const int Length = ").Append(gzip.Length).Append(";\n");
            code.Append("        internal const int PartCount = ").Append(partCount).Append(";\n");
            code.Append("        internal const string ETag = \"\\\"").Append(hash.ToString("x8")).Append("\\\"\";\n\n");
            code.Append("        internal static string GetPart(int index)\n        {\n            switch (index)\n            {\n");
            for (int i = 0; i < partCount; i++)
            {
                int offset = i * PartBytes;
                code.Append("                case ").Append(i).Append(": return \"")
                    .Append(Convert.ToBase64String(gzip, offset, Math.Min(PartBytes, gzip.Length - offset))).Append("\";\n");
            }
            code.Append("                default: return string.Empty;\n            }\n        }\n    }\n}\n");

            result.Source = code.ToString();
            result.Info = "WebUi page (" + ((string)((Dictionary<string, object>)manifest)["id"]) + "): " +
                html.Length.ToString(CultureInfo.InvariantCulture) + " B minified, " + gzip.Length.ToString(CultureInfo.InvariantCulture) + " B gzip, " +
                partCount.ToString(CultureInfo.InvariantCulture) + " parts.";
            return result;
        }

        // ---------- @use components ----------
        static bool IsInstance(string[] lines)
        {
            foreach (string raw in lines)
                if (raw.Trim() == InstanceMarker) return true;
            return false;
        }

        // 컴포넌트 참조 판정: 줄 전체가 <!--@use:name-->이거나 <name/>(비-void)면 이름 반환.
        // void 요소(<br/> 등)는 HTML 그대로이므로 null(=참조 아님).
        static string UseRef(string trimmed)
        {
            Match um = UseToken.Match(trimmed);
            if (um.Success) return um.Groups[1].Value;
            Match sc = SelfCloseTag.Match(trimmed);
            if (sc.Success && !VoidNames.Contains(sc.Groups[1].Value)) return sc.Groups[1].Value;
            return null;
        }

        bool DiscoverUses(IEnumerable<string> lines, string origin)
        {
            foreach (string raw in lines)
            {
                string name = UseRef(raw.Trim());
                if (name == null) continue;
                if (useDone.Contains(name)) continue;
                if (useVisiting.Contains(name)) { Fail(origin, "circular component reference: \"" + name + "\""); return false; }
                ComponentFiles c;
                if (!components.TryGetValue(name, out c) || c.Html == null)
                {
                    Fail(origin, "component \"" + name + "\" not found (from @use or <" + name + "/>): expected " + name + ".nf.html (any folder) under " +
                        (componentsRoot.Length == 0 ? "(WebUiComponents is not set)" : componentsRoot));
                    return false;
                }
                useVisiting.Add(name);
                if (!DiscoverUses(c.Html, c.HtmlPath)) return false;
                useVisiting.Remove(name);
                useDone.Add(name);
                useOrder.Add(name);
            }
            return true;
        }

        static List<string> ExpandTokens(IEnumerable<string> lines, Dictionary<string, string[]> expanded)
        {
            var outLines = new List<string>();
            foreach (string raw in lines)
            {
                string uref = UseRef(raw.Trim());
                string[] e;
                if (uref != null && expanded.TryGetValue(uref, out e)) outLines.AddRange(e);
                else outLines.Add(raw);
            }
            return outLines;
        }

        // 컴포넌트 html 한 벌을 최종 줄로 확장: @use는 expanded에서, @slot은 slotMap에서 대체(없으면 빈 줄 제거처럼 사라짐).
        string[] ExpandComponent(string[] html, Dictionary<string, string[]> expanded)
        {
            var outLines = new List<string>();
            foreach (string raw in html)
            {
                string t = raw.Trim();
                string uref = UseRef(t);
                if (uref != null)
                {
                    string[] e;
                    if (expanded.TryGetValue(uref, out e)) outLines.AddRange(e);
                    continue;
                }
                Match sm = SlotToken.Match(t);
                if (sm.Success)
                {
                    compSlots.Add(sm.Groups[1].Value);
                    List<string> body;
                    if (slotMap.TryGetValue(sm.Groups[1].Value, out body)) outLines.AddRange(ExpandTokens(body, expanded));
                    continue;
                }
                outLines.Add(raw);
            }
            return outLines.ToArray();
        }

        // ---------- form 자동 유도 (Phase 1: manifest 소멸 로드맵의 첫 단계) ----------
        // 마크업의 data-label/data-fmt 속성이 manifest.form의 새 고향이다.
        // 컨트롤 태그(input/select/textarea)에 두 속성 중 하나라도 있으면 form 항목으로 유도하고,
        // 수동 manifest.form과 이름이 충돌하면 빌드 오류(단일 출처 강제). 수동 항목은 유도 전에
        // 이름 집합으로 제외하므로 기존 프로젝트(속성 없음)는 동작이 그대로다.
        bool DeriveForm(object manifest, List<string> viewLines, string origin)
        {
            var m = (Dictionary<string, object>)manifest;
            var manual = m.ContainsKey("form") ? (List<object>)m["form"] : null;
            var manualNames = new HashSet<string>(StringComparer.Ordinal);
            if (manual != null)
                foreach (object it in manual) manualNames.Add((string)((Dictionary<string, object>)it)["name"]);

            var derived = new List<object>();
            var seen = new Dictionary<string, string>(StringComparer.Ordinal); // name → label+fmt 서명(중복·불일치 검사)
            foreach (string raw in viewLines)
            {
                foreach (Match tm in ControlTag.Matches(raw))
                {
                    string tag = tm.Value;
                    string label = Attr(tag, "data-label"), fmt = Attr(tag, "data-fmt");
                    if (label == null && fmt == null) continue;
                    string name = Attr(tag, "name");
                    if (name == null) { Fail(origin, "control with data-label/data-fmt has no name: " + tag); return false; }
                    if (fmt != null)
                    {
                        bool ok = false;
                        foreach (string x in Fmts) if (x == fmt) ok = true;
                        if (!ok) { Fail(origin, "data-fmt \"" + fmt + "\" on \"" + name + "\" must be one of ms/pin/secret/bool/text"); return false; }
                    }
                    if (manualNames.Contains(name))
                    { Fail(origin, "form field \"" + name + "\" is defined in both manifest.form and markup data-label/data-fmt; keep one source"); return false; }
                    string effLabel = label ?? name;
                    if (effLabel.Length == 0) { Fail(origin, "data-label on \"" + name + "\" is empty"); return false; }
                    string sig = effLabel + "\n" + (fmt ?? string.Empty);
                    string prev;
                    if (seen.TryGetValue(name, out prev))
                    {
                        // 같은 컴포넌트가 인라인+검증용 병합으로 두 번 보이는 경우(동일 서명)는 정상. 불일치만 오류.
                        if (prev != sig) { Fail(origin, "form field \"" + name + "\" has conflicting data-label/data-fmt in markup"); return false; }
                        continue;
                    }
                    seen[name] = sig;
                    var e = new Dictionary<string, object>();
                    e["name"] = name;
                    e["label"] = effLabel;
                    if (fmt != null) e["fmt"] = fmt;
                    derived.Add(e);
                }
            }

            if (manual == null && derived.Count == 0)
            { Fail(origin, "no form fields: declare manifest.form or add data-label/data-fmt to form controls"); return false; }
            if (derived.Count > 0)
            {
                var final = manual != null ? new List<object>(manual) : new List<object>();
                final.AddRange(derived);
                m["form"] = final;
            }
            return true;
        }

        static string Attr(string tag, string name)
        {
            // 속성은 태그명 직후나 다른 속성 뒤 공백으로 시작하므로 (?:^|\s)로 data-name 같은 부분일치를 막는다.
            Match x = Regex.Match(tag, "(?:^|\\s)" + name + "=\"([^\"]*)\"");
            return x.Success ? x.Groups[1].Value : null;
        }

        // ---------- manifest validation ----------
        bool ValidateManifest(object o, string profilePath)
        {
            var m = o as Dictionary<string, object>;
            if (m == null) { Fail(profilePath, "manifest must be a JSON object"); return false; }
            foreach (string k in m.Keys)
                if (k != "id" && k != "schema" && k != "form" && k != "features") { Fail(profilePath, "unknown manifest key \"" + k + "\""); return false; }
            string id;
            if (!m.ContainsKey("id") || (id = m["id"] as string) == null || !Regex.IsMatch(id, "^[a-z0-9][a-z0-9-]*$"))
            { Fail(profilePath, "manifest.id must be a lowercase id (^[a-z0-9][a-z0-9-]*$) or set the WebUiId project property"); return false; }
            double schema;
            if (!m.ContainsKey("schema") || !(m["schema"] is double) || (schema = (double)m["schema"]) < 1 || schema != Math.Floor(schema))
            { Fail(profilePath, "manifest.schema must be an integer >= 1 (ConfigurationSchemaVersion)"); return false; }
            var form = m.ContainsKey("form") ? m["form"] as List<object> : null;
            if (m.ContainsKey("form") && (form == null || form.Count == 0)) { Fail(profilePath, "manifest.form, if present, must be a non-empty array (or omit it to derive from data-label/data-fmt)"); return false; }
            var names = new HashSet<string>();
            foreach (object item in form ?? new List<object>())
            {
                var f = item as Dictionary<string, object>;
                if (f == null) { Fail(profilePath, "form entry must be an object"); return false; }
                foreach (string k in f.Keys)
                    if (k != "name" && k != "label" && k != "fmt") { Fail(profilePath, "unknown form key \"" + k + "\""); return false; }
                string name = f.ContainsKey("name") ? f["name"] as string : null;
                if (name == null || !Regex.IsMatch(name, "^[A-Za-z][A-Za-z0-9_]*$")) { Fail(profilePath, "form.name invalid: " + MiniJson.JsonOut(f.ContainsKey("name") ? f["name"] : null)); return false; }
                if (!names.Add(name)) { Fail(profilePath, "duplicate form name \"" + name + "\""); return false; }
                string label = f.ContainsKey("label") ? f["label"] as string : null;
                if (string.IsNullOrEmpty(label)) { Fail(profilePath, "form \"" + name + "\" needs a non-empty label"); return false; }
                if (f.ContainsKey("fmt"))
                {
                    string fmt = f["fmt"] as string;
                    bool ok = false;
                    foreach (string x in Fmts) if (x == fmt) ok = true;
                    if (!ok) { Fail(profilePath, "form \"" + name + "\" fmt must be one of ms/pin/secret/bool/text"); return false; }
                }
            }
            if (m.ContainsKey("features"))
            {
                var feat = m["features"] as Dictionary<string, object>;
                if (feat == null) { Fail(profilePath, "manifest.features must be an object"); return false; }
                foreach (string k in feat.Keys)
                {
                    bool known = false;
                    foreach (string x in FeatureNames) if (x == k) known = true;
                    if (!known) { Fail(profilePath, "unknown feature \"" + k + "\" (allowed: " + string.Join(",", FeatureNames) + ")"); return false; }
                }
                foreach (string flag in new string[] { "points", "scale", "name", "byteOrder" })
                    if (feat.ContainsKey(flag) && !(feat[flag] is bool)) { Fail(profilePath, "feature \"" + flag + "\" must be a boolean"); return false; }
                if (feat.ContainsKey("access"))
                {
                    var acc = feat["access"] as List<object>;
                    if (acc == null || acc.Count == 0) { Fail(profilePath, "feature access must be a non-empty array"); return false; }
                    foreach (object a in acc)
                    {
                        string s = a as string;
                        if (s != "RO" && s != "WO" && s != "RW") { Fail(profilePath, "feature access values must be RO/WO/RW"); return false; }
                    }
                }
                if (feat.ContainsKey("addr"))
                {
                    var addr = feat["addr"] as Dictionary<string, object>;
                    if (addr == null) { Fail(profilePath, "feature addr must be an object"); return false; }
                    foreach (string k in addr.Keys)
                        if (k != "s" && k != "t") { Fail(profilePath, "feature addr sides must be s/t"); return false; }
                    foreach (string side in new string[] { "s", "t" })
                    {
                        if (!addr.ContainsKey(side)) continue;
                        var sd = addr[side] as Dictionary<string, object>;
                        if (sd == null) { Fail(profilePath, "feature addr." + side + " must be an object"); return false; }
                        foreach (string k in sd.Keys)
                            if (k != "show" && k != "spaces" && k != "d" && k != "a") { Fail(profilePath, "unknown addr." + side + " key \"" + k + "\""); return false; }
                        if (sd.ContainsKey("show"))
                        {
                            var show = sd["show"] as List<object>;
                            if (show == null) { Fail(profilePath, "addr." + side + ".show must be an array"); return false; }
                            foreach (object x in show)
                            {
                                string s = x as string;
                                if (s != "p" && s != "d" && s != "s" && s != "a" && s != "t") { Fail(profilePath, "addr." + side + ".show values must be p/d/s/a/t"); return false; }
                            }
                        }
                        if (sd.ContainsKey("spaces"))
                        {
                            var sp = sd["spaces"] as List<object>;
                            if (sp == null || sp.Count == 0) { Fail(profilePath, "addr." + side + ".spaces must be a non-empty array"); return false; }
                            foreach (object x in sp) if (!(x is string)) { Fail(profilePath, "addr." + side + ".spaces must be strings"); return false; }
                        }
                        foreach (string rng in new string[] { "d", "a" })
                        {
                            if (!sd.ContainsKey(rng)) continue;
                            var rd = sd[rng] as Dictionary<string, object>;
                            if (rd == null) { Fail(profilePath, "addr." + side + "." + rng + " must be an object"); return false; }
                            foreach (string k in rd.Keys)
                                if (k != "min" && k != "max" && k != "label") { Fail(profilePath, "unknown addr." + side + "." + rng + " key \"" + k + "\""); return false; }
                            double dummy;
                            if (!rd.ContainsKey("min") || !(rd["min"] is double) || !rd.ContainsKey("max") || !(rd["max"] is double))
                            { Fail(profilePath, "addr." + side + "." + rng + " needs numeric min/max"); return false; }
                            dummy = (double)rd["min"] + (double)rd["max"];
                            if (dummy >= 0 || dummy < 0) { }
                        }
                    }
                }
                if (feat.ContainsKey("cacheCols"))
                {
                    var cc = feat["cacheCols"] as Dictionary<string, object>;
                    if (cc == null) { Fail(profilePath, "feature cacheCols must be an object"); return false; }
                    foreach (string k in cc.Keys)
                        if (k != "head") { Fail(profilePath, "cacheCols may only hold \"head\"; the row function lives in profileCacheRow"); return false; }
                    var head = cc.ContainsKey("head") ? cc["head"] as List<object> : null;
                    if (head == null || head.Count == 0) { Fail(profilePath, "cacheCols.head must be a non-empty array"); return false; }
                    foreach (object x in head) if (!(x is string)) { Fail(profilePath, "cacheCols.head must be strings"); return false; }
                }
            }
            return true;
        }

        bool HasDef(string script, string name)
        {
            return Regex.IsMatch(script, @"(^|[^A-Za-z0-9_$.])(function|const|let|var)\s+" + name + @"(\s|=|\()");
        }

        bool ValidateCross(object manifest, string views, string script, string profilePath)
        {
            var m = (Dictionary<string, object>)manifest;
            foreach (string name in Legacy)
                if (HasDef(script, name)) { Fail(profilePath, "\"" + name + "\" is now provided by the shell/WebUi; remove it from the profile"); return false; }
            foreach (string hook in new string[] { "profileRender", "profileTick", "checkSettings", "syncForm" })
                if (!HasDef(script, hook)) { Fail(profilePath, "profile script must define " + hook); return false; }
            var feat = m.ContainsKey("features") ? m["features"] as Dictionary<string, object> : null;
            bool points = feat == null || !feat.ContainsKey("points") || (bool)feat["points"];
            if (points && !HasDef(script, "profilePoint")) { Fail(profilePath, "profile script must define profilePoint (or set features.points=false)"); return false; }
            if (feat != null && feat.ContainsKey("cacheCols") && !HasDef(script, "profileCacheRow")
                && Regex.IsMatch(script, @"component\s*\(\s*['""]cacheRow['""]") == false)
            { Fail(profilePath, "features.cacheCols requires a profileCacheRow(p) function or a WebUi.component(\"cacheRow\", ...) in the profile script"); return false; }
            var form = (List<object>)m["form"];
            foreach (object item in form)
            {
                string name = (string)((Dictionary<string, object>)item)["name"];
                if (views.IndexOf("name=\"" + name + "\"", StringComparison.Ordinal) < 0)
                { Fail(profilePath, "form field \"" + name + "\" has no matching name=\"" + name + "\" control in the @views section"); return false; }
            }
            return true;
        }
    }
}
