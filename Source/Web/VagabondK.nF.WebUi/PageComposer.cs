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
        const string ManifestMarker = "<!--@manifest-->";
        const string InstanceMarker = "<!--@instance-->";
        // @instance 인수 허용: <!--@instance--> 또는 <!--@instance form-->(form=폼 연관 커스텀 엘리먼트).
        static readonly Regex InstanceRe = new Regex(@"<!--@instance((?:\s+[a-zA-Z0-9_-]+)*)\s*-->", RegexOptions.Compiled);
        // 빌드타임 슬롯은 폐기되었다(Phase 2): 섀도잉 가능한 <name/> 컴포넌트로 대체.
        const string SlotRemovedMsg = "@slot syntax was removed: expose a shadowable name.nf.html component and reference it as <name/> instead";
        // @use 마커는 제거되었다: 참조는 <name/>(줄 전체) 또는 인스턴스의 <wui-name> 태그만 인정한다.
        // 오래된 마커가 조용히 무력화되지 않도록 발견 시 빌드 실패로 알린다.
        static readonly Regex UseMarkerGone = new Regex(@"<!--@use:", RegexOptions.Compiled);
        // <wui-name ...> 여는 태그 = 인스턴스 컴포넌트 참조(본문 보유 컴포넌트의 마크업 방식).
        static readonly Regex WuiTag = new Regex(@"<wui-([a-z][a-z0-9-]*)(?=[\s/>])", RegexOptions.Compiled);
        // <name/> 문법: 비-void 요소의 자기종결은 HTML 금기 표기라 줄 전체가 마커다(Phase 2 로드맵).
        // 속성 허용(@instance 컴포넌트의 파라미터): <name a="1" b/> → 그룹2가 속성 문자열.
        static readonly Regex SelfCloseTag = new Regex(@"^<([a-z][a-z0-9-]*)((?:\s+[a-zA-Z0-9_-]+(?:=""[^""]*"")?)*)\s*/>$", RegexOptions.Compiled);
        // 가드: 위와 같은 태그가 줄 전체에 없으면(인라인) 참조로 인식 못 하고 원문 통과→조용한 파손.
        // known component 이름을 인라인에서 만나면 빌드 실패로 알린다.
        static readonly Regex InlineRef = new Regex(@"<([a-z][a-z0-9-]*)((?:\s+[a-zA-Z0-9_-]+(?:=""[^""]*"")?)*)\s*/>", RegexOptions.Compiled);
        static readonly HashSet<string> VoidNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "br", "hr", "img", "input", "link", "meta", "base", "area", "embed", "col", "param", "source", "track", "wbr" };
        // 폼 컨트롤과 data-label/data-fmt 속성 스캔용(마크업 → manifest.form 자동 유도).
        // wui-number/wui-password/wui-search는 폼 연관(@instance form) 인스턴스 참조가 확장된 형태.
        static readonly Regex ControlTag = new Regex(@"<(?:input|select|textarea|wui-number|wui-password|wui-search)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
        HashSet<string> useDone, useVisiting;
        // @instance 컴포넌트의 name → 커스텀 엘리먼트 태그(useSite 속성 보존 생성용).
        Dictionary<string, string> instTag = new Dictionary<string, string>(StringComparer.Ordinal);
        static readonly string[] Legacy = { "PROFILE", "KEYS", "LABELS", "formValues", "fillForm", "profileDirty",
            "profileExport", "profileImport", "showValue", "profileAddr", "profileScale", "profileAccess",
            "profilePoints", "profileName", "profileByteOrder", "profileCacheCols" };
        static readonly string[] FeatureNames = { "points", "scale", "name", "byteOrder", "access", "addr", "cacheCols" };
        static readonly string[] Fmts = { "ms", "pin", "secret", "bool", "text", "seg", "switch" };

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

        // WUI(Web Components) 메커니즘은 인스턴스 컴포넌트에만 필요하다.
        // 인스턴스가 없으면 센티널 이후를 버려 페이지 바이트(nF 문자열 힙)를 아낀다.
        internal static System.Collections.Generic.IEnumerable<string> CoreLines(string coreText, bool includeWui)
        {
            foreach (string l in ReadLines(coreText))
            {
                if (!includeWui && l.StartsWith("// ---- WUI:", StringComparison.Ordinal)) yield break;
                yield return l;
            }
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
            string[] shellLines = ReadLines(shellText);
            // 페이지 모드(WebUiPage): 프로필 없는 단일 루트 페이지. @manifest/@views 마커는 선택이고,
            // @manifest 줄에는 같은 줄에 JSON을 적을 수 있다(마이그레이션 기간의 features 임시 보관처).
            bool pageMode = string.IsNullOrEmpty(profileText) || profileText.Trim().Length == 0;
            string contentPath = pageMode ? shellPath : profilePath;
            string inlineManifest = null;
            var shellAt = new Dictionary<string, int>();
            for (int i = 0; i < shellLines.Length; i++)
            {
                string m = MarkerOf(shellLines[i]);
                if (m == null && pageMode)
                {
                    string t = shellLines[i].Trim();
                    if (t.StartsWith(ManifestMarker, StringComparison.Ordinal))
                    {
                        m = ManifestMarker;
                        inlineManifest = t.Substring(ManifestMarker.Length).Trim();
                    }
                }
                if (m == null) continue;
                if (shellAt.ContainsKey(m)) { Fail(shellPath, "duplicate marker " + m); return result; }
                shellAt[m] = i;
            }
            foreach (string m in Markers)
                if (!shellAt.ContainsKey(m) && !(pageMode && (m == ManifestMarker || m == "<!--@views-->"))) { Fail(shellPath, "missing marker " + m); return result; }
            if (shellAt.ContainsKey(ManifestMarker) && shellAt["<!--@core-->"] < shellAt[ManifestMarker]) { Fail(shellPath, "@core marker must come after @manifest"); return result; }

            var sections = new Dictionary<string, List<string>>();
            if (pageMode)
            {
                foreach (string m in Markers) if (m != "<!--@core-->") sections[m] = new List<string>();
                if (!string.IsNullOrEmpty(inlineManifest)) sections[ManifestMarker].Add(inlineManifest);
            }
            else
            {
            string current = null;
            foreach (string raw in ReadLines(profileText))
            {
                if (IsSlotToken(raw)) { Fail(profilePath, SlotRemovedMsg); return result; }
                string m = MarkerOf(raw);
                if (m != null)
                {
                    if (m == "<!--@core-->") { Fail(profilePath, "@core is provided by the library; remove it from the profile"); return result; }
                    if (sections.ContainsKey(m)) { Fail(profilePath, "duplicate marker " + m); return result; }
                    current = m;
                    sections[m] = new List<string>();
                    continue;
                }
                if (current == null) { if (raw.Trim().Length > 0) { Fail(profilePath, "content before the first marker"); return result; } continue; }
                sections[current].Add(raw);
            }
            foreach (string m in Markers)
                if (m != "<!--@core-->" && !sections.ContainsKey(m)) { Fail(profilePath, "missing marker " + m); return result; }
            }

            var parts = new List<string>();
            foreach (string raw in sections[ManifestMarker]) { string t = raw.Trim(); if (t.Length > 0) parts.Add(t); }
            string manifestJson = string.Join(" ", parts.ToArray());
            // 페이지 모드에서 @manifest 마커는 선택이다(README 약속): 마커가 없으면 빈 manifest로 본다.
            if (pageMode && manifestJson.Trim().Length == 0) manifestJson = "{}";
            object manifest;
            try { manifest = new MiniJson().Parse(manifestJson); }
            catch (Exception e) { Fail(contentPath, "manifest JSON: " + e.Message); return result; }
            if (pageMode)
            {
                // 페이지 모드에서 schema는 선택(레거시 필드): 없으면 1로 간주.
                var pm = manifest as Dictionary<string, object>;
                if (pm != null && !pm.ContainsKey("schema")) pm["schema"] = 1.0;
            }
            // WebUiId(msbuild 속성)는 manifest.id를 대체한다: 주입 시 선두로 옮기고, 값이 다르면 오류(단일 출처 강제).
            if (!string.IsNullOrEmpty(id))
            {
                var md = manifest as Dictionary<string, object>;
                if (md != null)
                {
                    object cur;
                    if (md.TryGetValue("id", out cur) && (cur as string) != id)
                    { Fail(contentPath, "manifest.id conflicts with WebUiId \"" + id + "\"; keep one source"); return result; }
                    var nd = new Dictionary<string, object>();
                    nd["id"] = id;
                    foreach (string k in md.Keys) if (k != "id") nd[k] = md[k];
                    manifest = nd;
                }
            }
            if (!ValidateManifest(manifest, contentPath)) return result;

            // 컴포넌트 발견·확장: useOrder는 서브→부모 순서(자식이 먼저 나열되어 CSS 우선순위가 올바르게 겹침).
            useOrder = new List<string>();
            useDone = new HashSet<string>(StringComparer.Ordinal);
            useVisiting = new HashSet<string>(StringComparer.Ordinal);
            if (!DiscoverUses(shellLines, shellPath)) return result;
            if (!pageMode && !DiscoverUses(sections["<!--@views-->"], profilePath)) return result;
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
                    // 인스턴스 컴포넌트: html은 <template>으로 보내고 참조 자리(<name/> 또는 태그 자체)는 <wui-name>이 된다.
                    // js/css는 커스텀 엘리먼트 정의에 포장되어 인스턴스마다 shadow root 안에서 실행되므로
                    // id가 인스턴스별로 격리되고 여러 위치에서 안전하게 재사용할 수 있다.
                    string tag = "wui-" + name.ToLowerInvariant();
                    instTag[name] = tag;
                    instBody[name] = body;
                    expanded[name] = new string[] { "<" + tag + "></" + tag + ">" };
                    templates.Add("<template id=\"wui-t-" + name + "\">");
                    templates.AddRange(body);
                    templates.Add("</template>");
                    // js/css 조각은 한 줄 문자열로 합쳐지므로 최종 Trim이 끝부분만 건드린다.
                    // 여기서 미리 각 줄을 Trim해 들여쓰기 공백을 없앤다(JS에서 줄 앞 공백은 무의미).
                    string jsArg = c.Js.Length > 0
                        ? "function (root, document, $) {\n" + string.Join("\n", TrimLines(c.Js)) + "\n}"
                        : "null";
                    bool formAssoc = InstanceForm(c.Html);
                    string cssArg = c.Css.Length > 0 ? ", " + MiniJson.JsonOut(string.Join("\n", TrimLines(c.Css)))
                        : (formAssoc ? ", null" : "");
                    compJs.Add("WUI.def(" + MiniJson.JsonOut(name) + ", " + jsArg + cssArg + (formAssoc ? ", {form:1}" : "") + ");");
                }
                else
                {
                    expanded[name] = body;
                    compCss.AddRange(c.Css);
                    compJs.AddRange(c.Js);
                }
            }

            var allViews = new List<string>();
            // 페이지 모드의 루트 페이지는 자기 마크업 자체가 뷰다: 폼 유도/교차 검증을 스캔에 포함한다.
            if (pageMode) allViews.AddRange(ExpandTokens(shellLines, expanded));
            allViews.AddRange(ExpandTokens(sections["<!--@views-->"], expanded));
            var allScript = new List<string>(sections["<!--@script-->"]);
            foreach (string name in useOrder)
            {
                string[] b;
                allViews.AddRange(instBody.TryGetValue(name, out b) ? b : expanded[name]);
                allScript.AddRange(components[name].Js);
            }
            string views = string.Join("\n", allViews.ToArray());
            string script = string.Join("\n", allScript.ToArray());
            if (!DeriveForm(manifest, allViews, contentPath)) return result;
            if (!ValidateCross(manifest, views, script, contentPath, pageMode)) return result;

            var merged = new List<string>();
            foreach (string raw in shellLines)
            {
                string m = MarkerOf(raw);
                if (m == null && pageMode && raw.TrimStart().StartsWith(ManifestMarker, StringComparison.Ordinal)) m = ManifestMarker;
                if (m == null)
                {
                    string uref = UseRef(raw.Trim());
                    if (uref != null) { merged.AddRange(ExpandRef(uref, raw.Trim(), expanded) ?? expanded[uref]); continue; }
                    merged.Add(raw); continue;
                }
                if (m == "<!--@manifest-->") merged.Add("const MANIFEST = " + MiniJson.JsonOut(manifest) + ";");
                else if (m == "<!--@core-->") merged.AddRange(CoreLines(coreText, templates.Count > 0));
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

            // 최종 축소: 줄 앞뒤 공백 제거 + 온줄 주석 제거. 소스는 들여쓰기 자유롭게 써도
            // 결과에서는 공백·주석이 사라진다(nF 문자열 힙 절약). 여러 줄로 퍼진 <!-- -->와
            // /* */ 주석도 상태 추적 제거; script 안의 <!-- 는 JS 레거시 온줄 주석으로 다룬다.
            var page = new StringBuilder();
            bool inScript = false, skipHtmlComment = false, skipBlockComment = false;
            foreach (string raw in merged)
            {
                string line = raw.Trim();
                if (skipHtmlComment)
                {
                    int end = line.IndexOf("-->", StringComparison.Ordinal);
                    if (end < 0) continue;
                    skipHtmlComment = false;
                    line = line.Substring(end + 3).Trim();
                    if (line.Length == 0) continue;
                }
                if (skipBlockComment)
                {
                    int end = line.IndexOf("*/", StringComparison.Ordinal);
                    if (end < 0) continue;
                    skipBlockComment = false;
                    line = line.Substring(end + 2).Trim();
                    if (line.Length == 0) continue;
                }
                if (line.Length == 0 || line.StartsWith("//")) continue;
                if (line.StartsWith("<!--"))
                {
                    if (inScript) continue;
                    int end = line.IndexOf("-->", StringComparison.Ordinal);
                    if (end < 0) { skipHtmlComment = true; continue; }
                    line = line.Substring(end + 3).Trim();
                    if (line.Length == 0) continue;
                }
                else if (line.StartsWith("/*"))
                {
                    int end = line.IndexOf("*/", StringComparison.Ordinal);
                    if (end < 0) { skipBlockComment = true; continue; }
                    line = line.Substring(end + 2).Trim();
                    if (line.Length == 0) continue;
                }
                if (line.StartsWith("<script") && line.EndsWith(">")) inScript = true;
                else if (line.StartsWith("</script")) inScript = false;
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
            // 해시는 gzip이 아닌 html 원문에 대해 계산: deflate 출력은 호스트 런타임에
            // 따라 바이트가 달라질 수 있어 ETag가 비결정적이 됨 (FNV-1a over html)
            uint hash = 2166136261;
            unchecked { foreach (byte value in html) { hash ^= value; hash *= 16777619; } }

            // Pack7: 7비트 팩킹(문자 8개 = 바이트 7개). nanoFramework 문자열 힙은 어셈블리당
            // 64KB(UTF-8, 16비트 인덱스) 제한이라 Base64(1.333B/B)보다 ~13% 작다.
            // part당 UTF-8 바이트 ≤ 1024(문자열 길이 제한)를 평균+3시그마에서 만족하려면 830B.
            const int PartBytes = 830;
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
                    .Append(EncodePack7(gzip, offset, Math.Min(PartBytes, gzip.Length - offset))).Append("\";\n");
            }
            code.Append("                default: return string.Empty;\n            }\n        }\n    }\n}\n");

            result.Source = code.ToString();
            result.Info = "WebUi page (" + ((string)((Dictionary<string, object>)manifest)["id"]) + "): " +
                html.Length.ToString(CultureInfo.InvariantCulture) + " B minified, " + gzip.Length.ToString(CultureInfo.InvariantCulture) + " B gzip, " +
                partCount.ToString(CultureInfo.InvariantCulture) + " parts.";
            return result;
        }

        // Pack7: 7비트 값(0~127)을 C# 문자열 리터럴에 안전한 문자로 매핑.
        // 122개는 UTF-8 1바이트(컨트롤 0x01~0x1F 중 개문자 제외 + printable에서 " \ 제외 + 0x7F),
        // 나머지 6개(값 122~127)만 2바이트(0xC0~0xC5). 개행(0x0A,0x0C,0x0D,0x85)·인용부호·역슬래시 회피.
        // 디코더(CharToValue)는 장치 측(PageController, Sample Controllers)에 짝으로 존재한다.
        static char ValueToChar(int v)
        {
            if (v < 9) return (char)(v + 1);
            if (v == 9) return (char)0x0B;
            if (v < 30) return (char)(v + 4);   // 10..29 → 0x0E..0x21
            if (v < 87) return (char)(v + 5);   // 30..86 → 0x23..0x5B (" 건너뜀)
            if (v < 121) return (char)(v + 6);  // 87..120 → 0x5D..0x7E (\ 건너뜀)
            if (v == 121) return (char)0x7F;
            return (char)(v + 70);              // 122..127 → 0xC0..0xC5 (2바이트)
        }

        static string EncodePack7(byte[] data, int offset, int count)
        {
            var sb = new StringBuilder((count * 8 + 6) / 7);
            int bitBuf = 0, bitCount = 0;
            for (int i = offset; i < offset + count; i++)
            {
                bitBuf = (bitBuf << 8) | data[i];
                bitCount += 8;
                while (bitCount >= 7)
                {
                    bitCount -= 7;
                    sb.Append(ValueToChar((bitBuf >> bitCount) & 0x7F));
                }
            }
            if (bitCount > 0)
                sb.Append(ValueToChar((bitBuf << (7 - bitCount)) & 0x7F));
            return sb.ToString();
        }

        // ---------- component references ----------
        static bool IsSlotToken(string raw) { return raw.Trim().StartsWith("<!--@slot", StringComparison.Ordinal); }

        // 마커는 html 어디든 허용(단독 줄뿐 아니라 태그와 같은 줄도 인식). 줄 전체가 마커일 때와
        // 동일하게 주석으로 출력에 남으므로 동작 차이가 없다.
        static bool IsInstance(string[] lines)
        {
            foreach (string raw in lines)
                if (InstanceRe.IsMatch(raw)) return true;
            return false;
        }

        // <!--@instance form--> 인수 여부: 폼 연관 커스텀 엘리먼트로 등록(WUI.def의 opts.form).
        static bool InstanceForm(string[] lines)
        {
            foreach (string raw in lines)
            {
                Match m = InstanceRe.Match(raw);
                if (m.Success && m.Groups[1].Value.IndexOf("form", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        // 문자열로 합쳐질 조각들의 줄 앞뒤 공백 제거(최종 Trim은 합쳐진 한 줄의 끝만 건드림).
        static string[] TrimLines(string[] lines)
        {
            var r = new string[lines.Length];
            for (int i = 0; i < lines.Length; i++) r[i] = lines[i].Trim();
            return r;
        }

        // 컴포넌트 참조 판정: 줄 전체가 <name/>(비-void)면 이름 반환.
        // void 요소(<br/> 등)는 HTML 그대로이므로 null(=참조 아님).
        static string UseRef(string trimmed)
        {
            Match sc = SelfCloseTag.Match(trimmed);
            if (sc.Success && !VoidNames.Contains(sc.Groups[1].Value)) return sc.Groups[1].Value;
            return null;
        }

        // <name attr="v"/>의 속성 부분(없거나 참조가 아니면 빈 문자열). 속성은 @instance만 허용.
        static string UseArgs(string trimmed)
        {
            Match sc = SelfCloseTag.Match(trimmed);
            return sc.Success && !VoidNames.Contains(sc.Groups[1].Value) ? sc.Groups[2].Value.Trim() : "";
        }

        bool DiscoverUses(IEnumerable<string> lines, string origin)
        {
            foreach (string raw in lines)
            {
                if (IsSlotToken(raw)) { Fail(origin, SlotRemovedMsg); return false; }
                if (UseMarkerGone.IsMatch(raw))
                { Fail(origin, "@use markers were removed: reference a component as <name/> (alone on its line) or use its <wui-name> tag directly"); return false; }
                string trimmed = raw.Trim();
                string name = UseRef(trimmed);
                if (name != null)
                {
                    if (!Include(name, UseArgs(trimmed), origin)) return false;
                    continue;
                }
                foreach (Match im in InlineRef.Matches(raw))
                    if (!VoidNames.Contains(im.Groups[1].Value) && components.ContainsKey(im.Groups[1].Value))
                    { Fail(origin, "component reference <" + im.Groups[1].Value + "/> must be alone on its line (inline use is emitted as raw HTML and never expands)"); return false; }
                // <wui-name> 여는 태그 자체가 인스턴스 참조: 위치와 무관하게 컴포넌트를 번들에 포함한다.
                foreach (Match wm in WuiTag.Matches(raw))
                {
                    ComponentFiles c;
                    if (components.TryGetValue(wm.Groups[1].Value, out c) && c.Html != null && IsInstance(c.Html) &&
                        !Include(wm.Groups[1].Value, "", origin)) return false;
                }
            }
            return true;
        }

        // 이름으로 컴포넌트 포함: 순환 검사 후 재귀 진입, useOrder는 서브→부모 순서로 축적.
        bool Include(string name, string args, string origin)
        {
            if (useDone.Contains(name)) return true;
            if (useVisiting.Contains(name)) { Fail(origin, "circular component reference: \"" + name + "\""); return false; }
            ComponentFiles c;
            if (!components.TryGetValue(name, out c) || c.Html == null)
            {
                Fail(origin, "component \"" + name + "\" not found (from <" + name + "/> or <wui-" + name + ">): expected " + name + ".nf.html (any folder) under " +
                    (componentsRoot.Length == 0 ? "(WebUiComponents is not set)" : componentsRoot));
                return false;
            }
            if (args.Length > 0 && !IsInstance(c.Html))
            {
                Fail(origin, "attributes on <" + name + "/> are only supported on @instance components: add <!--@instance--> anywhere in " + c.HtmlPath);
                return false;
            }
            useVisiting.Add(name);
            if (!DiscoverUses(c.Html, c.HtmlPath)) return false;
            useVisiting.Remove(name);
            useDone.Add(name);
            useOrder.Add(name);
            return true;
        }

        // 참조 줄을 최종 조각으로: instance 사용 위치는 마크업의 속성을 <wui-name ...>로 옮겨 담는다.
        string[] ExpandRef(string uref, string trimmed, Dictionary<string, string[]> expanded)
        {
            string[] e;
            if (!expanded.TryGetValue(uref, out e)) return null;
            string tag;
            if (instTag.TryGetValue(uref, out tag))
            {
                string args = UseArgs(trimmed);
                if (args.Length > 0) return new[] { "<" + tag + " " + args + "></" + tag + ">" };
            }
            return e;
        }

        List<string> ExpandTokens(IEnumerable<string> lines, Dictionary<string, string[]> expanded)
        {
            var outLines = new List<string>();
            foreach (string raw in lines)
            {
                string trimmed = raw.Trim();
                string uref = UseRef(trimmed);
                string[] e = uref != null ? ExpandRef(uref, trimmed, expanded) : null;
                if (e != null) outLines.AddRange(e);
                else outLines.Add(raw);
            }
            return outLines;
        }

        // 컴포넌트 html 한 벌을 최종 줄로 확장: <name/> 참조는 expanded에서 대체.
        string[] ExpandComponent(string[] html, Dictionary<string, string[]> expanded)
        {
            var outLines = new List<string>();
            foreach (string raw in html)
            {
                string trimmed = raw.Trim();
                string uref = UseRef(trimmed);
                if (uref != null)
                {
                    string[] e = ExpandRef(uref, trimmed, expanded);
                    if (e != null) { outLines.AddRange(e); continue; }
                    continue; // 미해결 참조는 기존처럼 조용히 드롭(발견은 DiscoverUses가 이미 검증).
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
            {
                // 표시 전용 페이지(<form> 마크업 자체가 없음)는 합법. form 마크업이 있는데 필드가 0개일 때만 누락으로 본다.
                string markup = string.Join("\n", viewLines.ToArray());
                if (markup.IndexOf("<form", StringComparison.OrdinalIgnoreCase) >= 0)
                { Fail(origin, "no form fields: declare manifest.form or add data-label/data-fmt to form controls"); return false; }
                m["form"] = new List<object>();
                return true;
            }
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
                    if (k != "name" && k != "label" && k != "fmt" && k != "options") { Fail(profilePath, "unknown form key \"" + k + "\""); return false; }
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
                    if (!ok) { Fail(profilePath, "form \"" + name + "\" fmt must be one of ms/pin/secret/bool/text/seg/switch"); return false; }
                    if (fmt == "seg")
                    {
                        var opts = f.ContainsKey("options") ? f["options"] as List<object> : null;
                        if (opts == null || opts.Count == 0) { Fail(profilePath, "form \"" + name + "\" fmt \"seg\" needs a non-empty options array"); return false; }
                        foreach (object ov in opts)
                        {
                            if (ov is string) continue;
                            var oo = ov as Dictionary<string, object>;
                            if (oo == null || !(oo.ContainsKey("value") || oo.ContainsKey("label"))) { Fail(profilePath, "form \"" + name + "\" options entries must be strings or {value,label} objects"); return false; }
                        }
                    }
                    else if (f.ContainsKey("options")) { Fail(profilePath, "form \"" + name + "\" has options but fmt is not \"seg\""); return false; }
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

        bool ValidateCross(object manifest, string views, string script, string profilePath, bool pageMode)
        {
            var m = (Dictionary<string, object>)manifest;
            foreach (string name in Legacy)
                if (HasDef(script, name)) { Fail(profilePath, "\"" + name + "\" is now provided by the shell/WebUi; remove it from the profile"); return false; }
            // 레거시 훅 계약은 셸+프로필 모드 전용. 페이지 모드는 bindForm/wui:status/wui:tick 이벤트 계약으로 대체된다.
            if (!pageMode)
                foreach (string hook in new string[] { "profileRender", "profileTick", "checkSettings", "syncForm" })
                    if (!HasDef(script, hook)) { Fail(profilePath, "profile script must define " + hook); return false; }
            var feat = m.ContainsKey("features") ? m["features"] as Dictionary<string, object> : null;
            bool points = feat == null || !feat.ContainsKey("points") || (bool)feat["points"];
            // 페이지 모드에서는 페이지 자기 스크립트가 views(전체 페이지 텍스트)에 속한다: 훅 검사도 거기서 찾는다.
            string hookSrc = pageMode ? views + "\n" + script : script;
            if (points && !HasDef(hookSrc, "profilePoint")) { Fail(profilePath, "profile script must define profilePoint (or set features.points=false)"); return false; }
            if (feat != null && feat.ContainsKey("cacheCols") && !HasDef(hookSrc, "profileCacheRow")
                && Regex.IsMatch(hookSrc, @"component\s*\(\s*['""]cacheRow['""]") == false)
            { Fail(profilePath, "features.cacheCols requires a profileCacheRow(p) function or a WebUi.component(\"cacheRow\", ...) in the profile script"); return false; }
            // <form/> 컴포넌트는 MANIFEST.form으로 컨트롤을 런타임에 렌더한다: 마크업에 name=가 없는 것이 정상이라 생략.
            if (!useOrder.Contains("form"))
            {
                var form = (List<object>)m["form"];
                foreach (object item in form)
                {
                    string name = (string)((Dictionary<string, object>)item)["name"];
                    if (views.IndexOf("name=\"" + name + "\"", StringComparison.Ordinal) < 0)
                    { Fail(profilePath, "form field \"" + name + "\" has no matching name=\"" + name + "\" control in the @views section"); return false; }
                }
            }
            return true;
        }
    }
}
