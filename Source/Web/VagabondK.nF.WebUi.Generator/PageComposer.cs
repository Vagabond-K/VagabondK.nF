using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace VagabondK.nF.WebUi.Generator
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
        static readonly Regex SlotToken = new Regex(@"^<!--@slot:([A-Za-z0-9_-]+)-->$", RegexOptions.Compiled);
        static readonly string[] Legacy = { "PROFILE", "KEYS", "LABELS", "formValues", "fillForm", "profileDirty",
            "profileExport", "profileImport", "showValue", "profileAddr", "profileScale", "profileAccess",
            "profilePoints", "profileName", "profileByteOrder", "profileCacheCols" };
        static readonly string[] FeatureNames = { "points", "scale", "name", "byteOrder", "access", "addr", "cacheCols" };
        static readonly string[] Fmts = { "ms", "pin", "secret", "bool", "text" };

        Result res;

        PageComposer() { }

        static string[] ReadLines(string text)
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

        internal static Result Compose(string shellText, string profileText, string coreText, string shellPath, string profilePath, string ns, string cls)
        {
            var c = new PageComposer();
            return c.Run(shellText, profileText, coreText, shellPath, profilePath, ns, cls);
        }

        void Fail(string file, string message) { res.ErrorFile = file; res.ErrorMessage = message; }

        static string MarkerOf(string raw)
        {
            string line = raw.Trim();
            for (int i = 0; i < Markers.Length; i++) if (Markers[i] == line) return line;
            return null;
        }

        Result Run(string shellText, string profileText, string coreText, string shellPath, string profilePath, string ns, string cls)
        {
            res = new Result();
            var result = res;
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
            if (!ValidateManifest(manifest, profilePath)) return result;

            string views = string.Join("\n", sections["<!--@views-->"].ToArray());
            string script = string.Join("\n", sections["<!--@script-->"].ToArray());
            if (!ValidateCross(manifest, views, script, profilePath)) return result;

            // Parse the profile @slots section into name -> markup lines; validate against shell positions.
            var slotMap = new Dictionary<string, List<string>>();
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
            foreach (string k in slotMap.Keys)
                if (!shellSlots.Contains(k)) { Fail(profilePath, "profile fills slot \"" + k + "\" but the shell has no <!--@slot:" + k + "--> position for it"); return result; }

            var merged = new List<string>();
            foreach (string raw in shellLines)
            {
                string m = MarkerOf(raw);
                if (m == null)
                {
                    Match sm = SlotToken.Match(raw.Trim());
                    if (sm.Success) { List<string> body; if (slotMap.TryGetValue(sm.Groups[1].Value, out body)) merged.AddRange(body); continue; }
                    merged.Add(raw); continue;
                }
                if (m == "<!--@manifest-->") merged.Add("const MANIFEST = " + MiniJson.JsonOut(manifest) + ";");
                else if (m == "<!--@core-->") merged.AddRange(ReadLines(coreText));
                else merged.AddRange(sections[m]);
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
            code.Append("// <auto-generated>\n// Generated by VagabondK.nF.WebUi.Generator. Do not edit.\n// </auto-generated>\n");
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

        // ---------- manifest validation ----------
        bool ValidateManifest(object o, string profilePath)
        {
            var m = o as Dictionary<string, object>;
            if (m == null) { Fail(profilePath, "manifest must be a JSON object"); return false; }
            foreach (string k in m.Keys)
                if (k != "id" && k != "schema" && k != "form" && k != "features") { Fail(profilePath, "unknown manifest key \"" + k + "\""); return false; }
            string id;
            if (!m.ContainsKey("id") || (id = m["id"] as string) == null || !Regex.IsMatch(id, "^[a-z0-9][a-z0-9-]*$"))
            { Fail(profilePath, "manifest.id must be a lowercase id (^[a-z0-9][a-z0-9-]*$)"); return false; }
            double schema;
            if (!m.ContainsKey("schema") || !(m["schema"] is double) || (schema = (double)m["schema"]) < 1 || schema != Math.Floor(schema))
            { Fail(profilePath, "manifest.schema must be an integer >= 1 (ConfigurationSchemaVersion)"); return false; }
            var form = m.ContainsKey("form") ? m["form"] as List<object> : null;
            if (form == null || form.Count == 0) { Fail(profilePath, "manifest.form must be a non-empty array"); return false; }
            var names = new HashSet<string>();
            foreach (object item in form)
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
