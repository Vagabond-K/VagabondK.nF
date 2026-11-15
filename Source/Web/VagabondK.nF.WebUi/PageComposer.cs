using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VagabondK.nF.WebUi
{
    /// <summary>
    /// 셸+프로필 페이지를 머지/축소/gzip/해시하여 WebPage 소스 텍스트로 만드는 순수 로직.
    /// VagabondK.nF.WebUi.targets의 인라인 RoslynCodeTask 로직에서 그대로 이식했습니다.
    /// </summary>
    internal sealed class PageComposer
    {
        internal sealed class Result
        {
            public string ErrorFile;   // 실패 원본 파일 경로
            public string ErrorMessage;
            public string Info;        // 크기 요약 메시지
            public byte[] Gzip;        // 축약 페이지의 gzip 바이트(리소스 파츠로 분할 저장)
            public uint Hash;          // html 원문 FNV-1a(ETag)
            // linkCss=true일 때 /style.css 자산으로 합칠 테마+컴포넌트 css 조각(페이지 자기 css는
            // 제외 — 항상 페이지 HTML에 인라인). Task가 여러 페이지의 것을 유니온해 자산 하나로 만듭니다.
            public List<string> CssAssetLines;
            public byte[] TextsGzip;   // /texts.js: 다국어 사전(번역 언어만 — en은 텍스트에 주입되므로 사전에서 제외)
            public uint TextsHash;
            public byte[] I18nGzip;    // /i18n.js: 공용 i18n 런타임(코어·로그인 공유 — uiText 복제 방지)
            public uint I18nHash;
            public List<string> EnMisses; // en 사전에 없어 폴백이 주입되지 않은 uiText 키(빌드 경고용)
        }

        // 주입 마커: <nf-core/> 같은 요소 표기 단독 줄(슬래시 생략 <nf-core>도 동일).
        // 구 <!--@x--> 주석 표기는 script/style 안에서 VS TS1206 경고를 내므로 폐기되고,
        // 태그 밖 단독 줄로 옮겼습니다. 발견 시 빌드 실패로 마이그레이션을 안내합니다.
        static readonly Regex NfMarkerRe = new Regex(@"^<nf-(core|style|views|script)/?>$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly string[] Markers = { "<nf-core/>", "<nf-style/>", "<nf-views/>", "<nf-script/>" };
        static readonly Regex LegacyMarker = new Regex(@"<!--@(manifest|core|style|views|script|instance|route|slot|use)", RegexOptions.Compiled);
        const string MarkerMigrationMsg = "comment markers were removed: put <nf-core/>/<nf-style/>/<nf-views/>/<nf-script/> alone on their own lines outside script/style blocks, and write routes/instances as @route:/path opts / @instance [form] (see README)";
        // @instance: 단독 줄에서만 성립. @instance 또는 @instance form(form=폼 연관 커스텀 엘리먼트).
        static readonly Regex InstanceRe = new Regex(@"^@instance((?:\s+[a-zA-Z0-9_-]+)*)\s*$", RegexOptions.Compiled);
        // 라우트 마커: *.nf.html 파일의 첫 줄에만 성립. @route:/path login public 형식.
        // 옵션 쉼표/공백 구분: login(가드 LoginPagePath 지정 + 텍스트 부분집합 인라인 + no-store),
        // public([PublicEndpoint]로 익명 개방). none은 공유 페이지 섀도잉용(라우트 생성에서 제외).
        // 첫 줄이 아니면 라우트 마커가 아니다(본문의 @instance 등과의 혼동 방지).
        static readonly Regex RouteRe = new Regex(@"^@route:(\S+)(?:\s+([A-Za-z0-9_ ,\-]+))?\s*$", RegexOptions.Compiled);

        internal sealed class PageRoute
        {
            public string Path;
            public bool IsLogin;
            public bool IsPublic;
            public bool IsNone;
        }

        // 첫 줄에서 라우트 마커를 판정합니다. 반환 null = 마커 없음(= 컴포넌트).
        // err != null = 마커는 있는데 형식 오류(빌드 오류로 알릴 것).
        internal static PageRoute ParseRoute(string[] lines, out string err)
        {
            err = null;
            if (lines.Length == 0) return null;
            Match m = RouteRe.Match(lines[0].Trim());
            if (!m.Success) return null;
            var pr = new PageRoute();
            string p = m.Groups[1].Value;
            if (p.Equals("none", StringComparison.OrdinalIgnoreCase)) { pr.IsNone = true; return pr; }
            if (p.Length == 0 || p[0] != '/') p = "/" + p;
            while (p.Length > 1 && p[p.Length - 1] == '/') p = p.Substring(0, p.Length - 1);
            pr.Path = p;
            if (m.Groups[2].Success)
            {
                string[] opts = m.Groups[2].Value.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < opts.Length; i++)
                {
                    if (opts[i].Equals("login", StringComparison.OrdinalIgnoreCase)) pr.IsLogin = true;
                    else if (opts[i].Equals("public", StringComparison.OrdinalIgnoreCase)) pr.IsPublic = true;
                    else { err = "unknown @route option \"" + opts[i] + "\" (allowed: login, public, or path \"none\")"; return pr; }
                }
            }
            return pr;
        }
        // 빌드타임 슬롯은 폐기되었습니다(Phase 2): 섀도잉 가능한 <name/> 컴포넌트로 대체됩니다.
        const string SlotRemovedMsg = "@slot syntax was removed: expose a shadowable name.nf.html component and reference it as <name/> instead";
        // @use 마커는 제거되었습니다: 참조는 <name/>(줄 전체) 또는 인스턴스의 <wui-name> 태그만 인정합니다.
        // 오래된 마커가 조용히 무력화되지 않도록 발견 시 빌드 실패로 알립니다.
        static readonly Regex UseMarkerGone = new Regex(@"<!--@use:", RegexOptions.Compiled);
        // <wui-name ...> 여는 태그 = 인스턴스 컴포넌트 참조(본문 보유 컴포넌트의 마크업 방식).
        static readonly Regex WuiTag = new Regex(@"<wui-([a-z][a-z0-9-]*)(?=[\s/>])", RegexOptions.Compiled);
        // <name/> 문법: 비-void 요소의 자기종결은 HTML 금기 표기라 줄 전체가 마커입니다(Phase 2 로드맵).
        // 속성 허용(@instance 컴포넌트의 파라미터): <name a="1" b/> → 그룹2가 속성 문자열.
        static readonly Regex SelfCloseTag = new Regex(@"^<([a-z][a-z0-9-]*)((?:\s+[a-zA-Z0-9_-]+(?:=""[^""]*"")?)*)\s*/>$", RegexOptions.Compiled);
        // 가드: 위와 같은 태그가 줄 전체에 없으면(인라인) 참조로 인식 못 하고 원문 통과→조용한 파손.
        // known component 이름을 인라인에서 만나면 빌드 실패로 알립니다.
        static readonly Regex InlineRef = new Regex(@"<([a-z][a-z0-9-]*)((?:\s+[a-zA-Z0-9_-]+(?:=""[^""]*"")?)*)\s*/>", RegexOptions.Compiled);
        // 여러 줄로 들여쓰기된 자기종결 참조의 시작 줄: <name 또는 <name attr="v" …로 끝나고 >가 없는 줄.
        // MergeRefs가 이런 블록을 한 줄로 합쳐 SelfCloseTag 규칙(<name attrs/>)에 태웁니다(들여쓰기 가독성 보존).
        static readonly Regex MultiOpen = new Regex(@"^<([a-z][a-z0-9-]*)((?:\s+[a-zA-Z0-9_-]+(?:=""[^""]*"")?)*)$", RegexOptions.Compiled);
        // 가드: <wui-name .../> 자기종결 표기. 커스텀 엘리먼트는 HTML에서 자기종결이 무시되어
        // 브라우저가 여는 태그로 해석하고 뒤 형제를 shadow 안으로 삼켜버리므로 짝 표기(<wui-name ...></wui-name>)만 허용합니다.
        static readonly Regex WuiSelfClose = new Regex(@"(<wui-[a-z][a-z0-9-]*)((?:\s+[a-zA-Z0-9_-]+(?:=""[^""]*"")?)*)\s*/>", RegexOptions.Compiled);
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
            // File.ReadAllLines와 동일하게: 줄바꿈으로 자르고 마지막 빈 원소는 버립니다.
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

        // WUI(Web Components) 메커니즘은 인스턴스 컴포넌트에만 필요합니다.
        // 인스턴스가 없으면 센티널 이후를 버려 페이지 바이트(nF 문자열 힙)를 아낍니다.
        internal static System.Collections.Generic.IEnumerable<string> CoreLines(string coreText, bool includeWui)
        {
            foreach (string l in ReadLines(coreText))
            {
                if (!includeWui && l.StartsWith("// ---- WUI:", StringComparison.Ordinal)) yield break;
                yield return l;
            }
        }

        internal static Result Compose(string[] pageLines, string coreText, string pagePath, string ns, string cls,
            string id, string componentsRoot, Dictionary<string, ComponentFiles> components, bool linkCss, string themeText, string textsText, string i18nText,
            Dictionary<string, string> enFallbacks, string brand, string[] ownCss, string[] ownJs, string inlineTexts, bool isLogin)
        {
            var c = new PageComposer();
            return c.Run(pageLines, coreText, pagePath, ns, cls, id, componentsRoot, components, linkCss, themeText, textsText, i18nText, enFallbacks, brand, ownCss, ownJs, inlineTexts, isLogin);
        }

        void Fail(string file, string message) { res.ErrorFile = file; res.ErrorMessage = message; }

        static string MarkerOf(string raw)
        {
            Match m = NfMarkerRe.Match(raw.Trim());
            return m.Success ? "<nf-" + m.Groups[1].Value.ToLowerInvariant() + "/>" : null;
        }

        Result Run(string[] shellLines, string coreText, string pagePath, string ns, string cls,
            string id, string componentsRoot, Dictionary<string, ComponentFiles> components, bool linkCss, string themeText, string textsText, string i18nText,
            Dictionary<string, string> enFallbacks, string brand, string[] ownCss, string[] ownJs, string inlineTexts, bool isLogin)
        {
            res = new Result();
            res.EnMisses = new List<string>();
            var result = res;
            // 공유 테마(WebUiTheme): :root 토큰 한 꾸러미. 링크 모드면 /style.css 자산에
            // 컴포넌트 css와 함께 들어가 모든 페이지가 같은 파일을 link하고,
            // 인라인 모드면 각 페이지의 @style 자리에 같은 텍스트로 인라인됩니다.
            var themeLines = MinifyLines(themeText ?? string.Empty);
            this.componentsRoot = (componentsRoot ?? string.Empty).Replace('|', ';');
            this.components = components ?? new Dictionary<string, ComponentFiles>(StringComparer.Ordinal);
            // 페이지 첫 줄 라우트 마커(@route)는 판정 전용이므로 여기서 제거한다(Task는 원본 줄을 그대로 넘김).
            // <nf-views>/<nf-core>는 선택, <nf-style>/<nf-script>는 필수 마커.
            if (shellLines.Length > 0 && RouteRe.IsMatch(shellLines[0].Trim()))
            {
                var stripped = new string[shellLines.Length - 1];
                Array.Copy(shellLines, 1, stripped, 0, stripped.Length);
                shellLines = stripped;
            }
            // manifest는 페이지가 선언하지 않습니다: id는 WebUiId 속성, form은 마크업 유도(Phase 1.5 완결).
            string contentPath = pagePath;
            var shellAt = new Dictionary<string, int>();
            for (int i = 0; i < shellLines.Length; i++)
            {
                string m = MarkerOf(shellLines[i]);
                if (m == null)
                {
                    if (LegacyMarker.IsMatch(shellLines[i])) { Fail(pagePath, MarkerMigrationMsg); return result; }
                    continue;
                }
                if (shellAt.ContainsKey(m)) { Fail(pagePath, "duplicate marker " + m); return result; }
                shellAt[m] = i;
            }
            foreach (string m in Markers)
                if (!shellAt.ContainsKey(m) && m != "<nf-views/>" && m != "<nf-core/>") { Fail(pagePath, "missing marker " + m); return result; }

            var manifestObj = new Dictionary<string, object>();
            if (!string.IsNullOrEmpty(id)) manifestObj["id"] = id;
            object manifest = manifestObj;

            var sections = new Dictionary<string, List<string>>();
            foreach (string m in Markers) if (m != "<nf-core/>") sections[m] = new List<string>();

            // 컴포넌트 발견·확장: useOrder는 서브→부모 순서(자식이 먼저 나열되어 CSS 우선순위가 올바르게 겹침).
            useOrder = new List<string>();
            useDone = new HashSet<string>(StringComparer.Ordinal);
            useVisiting = new HashSet<string>(StringComparer.Ordinal);
            if (!DiscoverUses(shellLines, pagePath)) return result;
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
                    // 인스턴스 컴포넌트: html은 <template>으로 보내고 참조 자리(<name/> 또는 태그 자체)는 <wui-name>이 됩니다.
                    // js/css는 커스텀 엘리먼트 정의에 포장되어 인스턴스마다 shadow root 안에서 실행되므로
                    // id가 인스턴스별로 격리되고 여러 위치에서 안전하게 재사용할 수 있습니다.
                    string tag = "wui-" + name.ToLowerInvariant();
                    instTag[name] = tag;
                    instBody[name] = body;
                    expanded[name] = new string[] { "<" + tag + "></" + tag + ">" };
                    templates.Add("<template id=\"wui-t-" + name + "\">");
                    templates.AddRange(body);
                    templates.Add("</template>");
                    // js/css 조각은 한 줄 문자열로 합쳐지므로 최종 Trim이 끝부분만 건드립니다.
                    // 여기서 미리 각 줄을 Trim해 들여쓰기 공백을 제거합니다(JS에서 줄 앞 공백은 무의미).
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
            // 루트 페이지의 자기 마크업 자체가 뷰입니다: 폼 유도/교차 검증을 스캔에 포함합니다.
            allViews.AddRange(ExpandTokens(shellLines, expanded));
            var allScript = new List<string>(sections["<nf-script/>"]);
            foreach (string name in useOrder)
            {
                string[] b;
                allViews.AddRange(instBody.TryGetValue(name, out b) ? b : expanded[name]);
                allScript.AddRange(components[name].Js);
            }
            string views = string.Join("\n", allViews.ToArray());
            string script = string.Join("\n", allScript.ToArray());
            if (isLogin)
            {
                // 로그인 페이지: core/MANIFEST 없는 고정 템플릿이라 profilePoint 훅과 폼 유도
                // 검증을 면제합니다(비밀번호 입력은 login.nf.js가 직접 처리).
                manifestObj["form"] = new List<object>();
            }
            else
            {
                if (!DeriveForm(manifest, allViews, contentPath)) return result;
                if (!ValidateCross(manifest, views, script, contentPath)) return result;
            }

            var merged = new List<string>();
            bool textsDone = false;
            // 판정(DiscoverUses)과 같은 줄 보기를 봐야 하므로 여러 줄 참조도 여기서 병합됩니다.
            foreach (string raw in MergeRefs(shellLines))
            {
                string m = MarkerOf(raw);
                if (m == null)
                {
                    // @instance 줄은 판정 전용 마커: 출력에서 사라집니다(구 주석형은 Minify가 지웠던 자리).
                    if (InstanceRe.IsMatch(raw.Trim())) continue;
                    string uref = UseRef(raw.Trim());
                    if (uref != null) { merged.AddRange(ExpandRef(uref, raw.Trim(), expanded) ?? expanded[uref]); continue; }
                    merged.Add(raw); continue;
                }
                if (m == "<nf-core/>")
                {
                    // 다국어 사전+공용 i18n 런타임은 core.js보다 먼저 실행되어야 합니다(uiText 정의).
                    // 마커가 태그 밖에 있으므로 주입 블록의 <script> 태그를 우리가 생성합니다.
                    TextsBlock(merged, ref textsDone, inlineTexts, textsText, i18nText);
                    merged.Add("<script>");
                    merged.Add("const MANIFEST = " + MiniJson.JsonOut(manifest) + ";");
                    merged.AddRange(CoreLines(coreText, templates.Count > 0));
                    merged.Add("</script>");
                }
                else if (m == "<nf-style/>")
                {
                    // 페이지 자기 css가 테마/컴포넌트보다 먼저(자기 파일이 라이브러리를 덮습니다).
                    // 링크 모드: 테마+컴포넌트 CSS는 /style.css 자산으로 외부화하고 <link> 한 줄로 부릅니다.
                    var css = new List<string>();
                    if (ownCss != null) css.AddRange(ownCss);
                    if (!linkCss)
                    {
                        css.AddRange(themeLines);
                        css.AddRange(compCss);
                    }
                    css.AddRange(ExpandTokens(sections[m], expanded));
                    if (css.Count > 0)
                    {
                        merged.Add("<style>");
                        merged.AddRange(css);
                        merged.Add("</style>");
                    }
                    if (linkCss && (themeLines.Count > 0 || compCss.Count > 0))
                        merged.Add("<link rel=\"stylesheet\" href=\"/style.css\">");
                }
                else if (m == "<nf-script/>")
                {
                    // @core가 없는 페이지(로그인 등)는 텍스트 블록 자리가 여기로 이동합니다.
                    TextsBlock(merged, ref textsDone, inlineTexts, textsText, i18nText);
                    merged.Add("<script>");
                    merged.AddRange(compJs);
                    if (ownJs != null) merged.AddRange(ownJs);
                    merged.AddRange(sections[m]);
                    merged.Add("</script>");
                }
                // <nf-views/>: 뷰 조각은 이미 유도 스캔에 포함되므로 출력 생성 없이 소멸합니다.
            }
            if (templates.Count > 0)
            {
                int at = merged.FindIndex(l => l.Trim() == "<script>");
                if (at < 0) at = merged.Count;
                merged.InsertRange(at, templates);
            }

            string minified = Minify(merged).Replace("{{BRAND}}", string.IsNullOrEmpty(brand) ? "WebUi" : brand);
            byte[] html = new UTF8Encoding(false).GetBytes(InjectFallbacks(minified, enFallbacks, res.EnMisses));
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
            result.Gzip = gzip;
            result.Hash = hash;

            // 링크 모드: 테마+컴포넌트 CSS 조각을 전달합니다 — Task가 페이지 전체에서 유니온해
            // /style.css 단일 gzip 자산 하나로 만듭니다(페이지 자기 css는 인라인 유지, 자산화하지 않음).
            if (linkCss && (themeLines.Count > 0 || compCss.Count > 0))
            {
                result.CssAssetLines = new List<string>(themeLines);
                result.CssAssetLines.AddRange(compCss);
            }

            // 다국어 사전 자산: texts*.resx에서 병합된 JS를 /texts.js gzip로 생성합니다(메인·로그인 공유).
            if (!string.IsNullOrEmpty(textsText))
            {
                byte[] txGzip; uint txHash;
                GzipAndHash(Minify(ReadLines(textsText)), out txGzip, out txHash);
                result.TextsGzip = txGzip;
                result.TextsHash = txHash;
            }

            // 공용 i18n 런타임 자산: Assets\i18n.js를 /i18n.js gzip로 생성합니다(메인·로그인 공유).
            if (!string.IsNullOrEmpty(i18nText))
            {
                byte[] ijGzip; uint ijHash;
                GzipAndHash(Minify(ReadLines(i18nText)), out ijGzip, out ijHash);
                result.I18nGzip = ijGzip;
                result.I18nHash = ijHash;
            }

            string pid;
            var md = manifest as Dictionary<string, object>;
            object idv;
            pid = (md != null && md.TryGetValue("id", out idv)) ? idv as string : null;
            result.Info = "WebUi page" + (string.IsNullOrEmpty(pid) ? string.Empty : " (" + pid + ")") + ": " +
                html.Length.ToString(CultureInfo.InvariantCulture) + " B minified, " + gzip.Length.ToString(CultureInfo.InvariantCulture) + " B gzip.";
            return result;
        }

        // 다국어 사전과 공용 i18n 런타임을 독립 <script> 태그로.emit합니다. 페이지당 한 번
        // 실행(<nf-core/> 자리가 우선, 없으면 <nf-script/> 자리): 사전은 코어/페이지 js의
        // uiText 정의보다 먼저 실행되어야 합니다. inlineTexts(로그인 키 부분집합)가 있으면
        // 링크 왕복 없이 인라인 블록으로 넣습니다 — 첫 페인트부터 모국어라 영어 깜빡임이 사라집니다.
        static void TextsBlock(List<string> merged, ref bool done, string inlineTexts, string textsText, string i18nText)
        {
            if (done) return;
            if (inlineTexts == null && string.IsNullOrEmpty(textsText) && string.IsNullOrEmpty(i18nText)) return;
            done = true;
            if (inlineTexts != null)
            {
                merged.Add("<script>");
                merged.AddRange(MinifyLines(inlineTexts));
                merged.Add("</script>");
            }
            else
            {
                if (!string.IsNullOrEmpty(textsText)) merged.Add("<script src=\"/texts.js\"></script>");
                if (!string.IsNullOrEmpty(i18nText)) merged.Add("<script src=\"/i18n.js\"></script>");
            }
        }

        // 최종 축소: 줄 앞뒤 공백 제거 + 온줄 주석 제거. 소스는 들여쓰기 자유롭게 써도
        // 결과에서는 공백·주석이 사라집니다(nF 문자열 힙 절약). 여러 줄로 퍼진 <!-- -->와
        // /* */ 주석도 상태 추적 제거; script 안의 <!-- 는 JS 레거시 한 줄 주석으로 다룹니다.
        // ===== 빌드 타임 en 폴백 주입 =====
        // 호출부는 uiText('key'[, params])만 쓰고 두 번째 인자(폴백 문자열)를 작성하지 않습니다.
        // 주입기가 텍스트 전체에서 uiText('key' 후속이 ',' 또는 ')'인 지점을 찾아
        // en 사전(texts.resx의 이름 있는 항목) 값을 복원합니다 → uiText('key','en',...).
        // 런타임 uiText(key, en, params) 서명과는 무관하게 소스 형식 그대로 되돌리는 것입니다.
        static readonly Regex UiTextCall = new Regex(@"uiText\(\s*('(?:[^'\\]|\\.)*'|""(?:[^""\\]|\\.)*"")\s*([,)])", RegexOptions.Compiled);

        // 스크립트가 부르는 uiText 키 집합(로그인 페이지 인라인 사전 부분집합 추출용).
        internal static HashSet<string> ExtractTextKeys(string js)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in UiTextCall.Matches(js)) set.Add(Unquote(m.Groups[1].Value));
            return set;
        }

        static string InjectFallbacks(string text, Dictionary<string, string> en, List<string> misses)
        {
            if (en == null || en.Count == 0 || string.IsNullOrEmpty(text)) return text;
            return UiTextCall.Replace(text, m =>
            {
                string key = Unquote(m.Groups[1].Value);
                string fb;
                if (!en.TryGetValue(key, out fb))
                {
                    if (misses != null && !misses.Contains(key)) misses.Add(key);
                    return m.Value;
                }
                return "uiText('" + EscapeJs(key) + "', '" + EscapeJs(fb) + "'" + m.Groups[2].Value;
            });
        }

        static string Unquote(string literal)
        {
            char q = literal[0];
            return literal.Substring(1, literal.Length - 2).Replace("\\" + q, q.ToString()).Replace("\\\\", "\\");
        }

        static string EscapeJs(string s)
        {
            return s.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\r", "").Replace("\n", "\\n");
        }

        internal static string Minify(IEnumerable<string> lines)
        {
            var page = new StringBuilder();
            bool inScript = false, skipHtmlComment = false, skipBlockComment = false;
            foreach (string raw in lines)
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
            return page.ToString();
        }

        static List<string> MinifyLines(string text)
        {
            return new List<string>(Minify(ReadLines(text)).Split('\n'));
        }

        // css 줄 목록을 중괄호 균형으로 최상위 "블록" 단위로 분할합니다(linkCss 유니온의 중복 제거 단위).
        // 줄 단위로 중복을 제거하면 안 됩니다: 다줄 규칙의 닫는 `}` 줄은 문자열이 전부 같아도
        // HashSet이 첫 `}`만 남기고 모두 삼키며, 이후 모든 다줄 규칙이 닫는 대괄호를 잃고 깨집니다.
        internal static IEnumerable<string> SplitCssBlocks(IEnumerable<string> lines)
        {
            var block = new StringBuilder();
            int depth = 0;
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (block.Length > 0) block.Append('\n');
                block.Append(line);
                for (int i = 0; i < line.Length; i++)
                {
                    char ch = line[i];
                    if (ch == '{') depth++;
                    else if (ch == '}') depth--;
                }
                if (depth <= 0)
                {
                    yield return block.ToString();
                    block.Clear();
                    depth = 0;
                }
            }
            if (block.Length > 0) yield return block.ToString();
        }

        // 텍스트를 UTF-8 축약 바이트로 만들어 gzip하고, gzip 전 텍스트 바이트 기준
        // FNV-1a 해시를 생성합니다(ETag 결정성 규칙은 페이지와 동일: deflate 출력은 무시).
        internal static void GzipAndHash(string text, out byte[] gzip, out uint hash)
        {
            byte[] raw = new UTF8Encoding(false).GetBytes(text);
            using (var memory = new MemoryStream())
            {
                using (var compressor = new GZipStream(memory, CompressionLevel.Optimal, true))
                    compressor.Write(raw, 0, raw.Length);
                gzip = memory.ToArray();
            }
            hash = 2166136261;
            unchecked { foreach (byte value in raw) { hash ^= value; hash *= 16777619; } }
        }

        // ---------- component references ----------
        static bool IsSlotToken(string raw) { return raw.Trim().StartsWith("<!--@slot", StringComparison.Ordinal); }

        // 마커는 단독 줄에서만 성립(bare @instance): 태그와 같은 줄 표기는 인정하지 않는다.
        static bool IsInstance(string[] lines)
        {
            foreach (string raw in lines)
                if (InstanceRe.IsMatch(raw.Trim())) return true;
            return false;
        }

        // @instance form 인수 여부: 폼 연관 커스텀 엘리먼트로 등록(WUI.def의 opts.form).
        static bool InstanceForm(string[] lines)
        {
            foreach (string raw in lines)
            {
                Match m = InstanceRe.Match(raw.Trim());
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
        // wui- 접두 이름은 제외: <wui-name .../>는 자기종결 태그 금지 대상(아래 가드)이며,
        // <wui-name> 여는 태그 참조는 WuiTag 루프가 이미 처리합니다(이 함수가 가로채면 이름에
        // "wui-"가 남아 가짜 컴포넌트 조회 실패로 이어집니다).
        static string UseRef(string trimmed)
        {
            Match sc = SelfCloseTag.Match(trimmed);
            if (!sc.Success || VoidNames.Contains(sc.Groups[1].Value)) return null;
            if (sc.Groups[1].Value.StartsWith("wui-", StringComparison.Ordinal)) return null;
            // <nf-core/> 같은 마커 요소는 주입 자리 표시자이지 컴포넌트 참조가 아니다(DiscoverUses 오탐 방지).
            if (sc.Groups[1].Value.StartsWith("nf-", StringComparison.Ordinal)) return null;
            return sc.Groups[1].Value;
        }

        // <name attr="v"/>의 속성 부분(없거나 참조가 아니면 빈 문자열). 속성은 @instance만 허용.
        static string UseArgs(string trimmed)
        {
            Match sc = SelfCloseTag.Match(trimmed);
            return sc.Success && !VoidNames.Contains(sc.Groups[1].Value) ? sc.Groups[2].Value.Trim() : "";
        }

        // 여러 줄 자기종결 참조를 한 줄로 합칩니다: <name\n attr="v"\n/> → <name attr="v" />.
        // 대상은 known 컴포넌트 이름의 시작 줄만(일반 HTML의 여러 줄 태그는 건드리지 않음),
        // 블록 끝이 '/>'로 끝나고 합친 결과가 SelfCloseTag에 맞을 때만 치환합니다.
        // 판정(DiscoverUses)과 확장(ExpandTokens/ExpandComponent)이 동일한 줄 보기를 봐야 하므로
        // 세 경로가 모두 이 함수를 먼저 통과합니다.
        string[] MergeRefs(IEnumerable<string> lines)
        {
            var src = lines as string[] ?? lines.ToArray();
            var outL = new List<string>(src.Length);
            for (int i = 0; i < src.Length; i++)
            {
                string t = src[i].Trim();
                Match m = MultiOpen.Match(t);
                if (!m.Success || VoidNames.Contains(m.Groups[1].Value) || !components.ContainsKey(m.Groups[1].Value))
                {
                    outL.Add(src[i]);
                    continue;
                }
                string joined = t;
                bool closed = false;
                int j = i + 1;
                for (; j < src.Length && j - i <= 40; j++)
                {
                    string u = src[j].Trim();
                    if (u.EndsWith("/>", StringComparison.Ordinal)) { joined += " " + u; closed = true; break; }
                    if (u.IndexOf('>') >= 0 || u.IndexOf('<') >= 0) break; // 일반 요소 시작/자식 등장 → 병합 포기
                    joined += " " + u;
                }
                if (closed && SelfCloseTag.IsMatch(joined.Trim()))
                {
                    outL.Add(joined);
                    i = j; // 합쳐진 블록 전체를 소비(다음 줄부터 계속)
                    continue;
                }
                // 병합 포기: 시작~직전 줄은 원문으로 두고, break가 발생한 줄은 다음 순회 재정복(손실 방지)
                for (int k = i; k < j; k++) outL.Add(src[k]);
                i = j - 1;
            }
            return outL.ToArray();
        }

        bool DiscoverUses(IEnumerable<string> lines, string origin)
        {
            foreach (string raw in MergeRefs(lines))
            {
                if (IsSlotToken(raw)) { Fail(origin, SlotRemovedMsg); return false; }
                Match wsc = WuiSelfClose.Match(raw);
                if (wsc.Success)
                { Fail(origin, "<wui-name .../> cannot be self-closed in HTML: write " + wsc.Groups[1].Value + wsc.Groups[2].Value + "></" + wsc.Groups[1].Value.Substring(1) + ">"); return false; }
                if (UseMarkerGone.IsMatch(raw))
                { Fail(origin, "@use markers were removed: reference a component as <name/> (alone on its line) or use its <wui-name> tag directly"); return false; }
                if (LegacyMarker.IsMatch(raw)) { Fail(origin, MarkerMigrationMsg); return false; }
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
                // <wui-name> 여는 태그 자체가 인스턴스 참조: 위치와 무관하게 컴포넌트를 번들에 포함합니다.
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

        // 참조 줄을 최종 조각으로: instance 사용 위치는 마크업의 속성을 <wui-name ...>로 옮겨 담습니다.
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
            foreach (string raw in MergeRefs(lines))
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
            foreach (string raw in MergeRefs(html))
            {
                string trimmed = raw.Trim();
                if (InstanceRe.IsMatch(trimmed)) continue; // @instance 마커는 판정 전용, 출력에서 제거.
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
        // 마크업의 data-label/data-fmt 속성이 manifest.form의 새 위치입니다.
        // 컨트롤 태그(input/select/textarea)에 두 속성 중 하나라도 있으면 form 항목으로 유도하고,
        // 수동 manifest.form과 이름이 충돌하면 빌드 오류(단일 출처 강제). 수동 항목은 유도 전에
        // 이름 집합으로 제외하므로 기존 프로젝트(속성 없음)는 동작이 그대로입니다.
        bool DeriveForm(object manifest, List<string> viewLines, string origin)
        {
            var m = (Dictionary<string, object>)manifest;
            var manual = m.ContainsKey("form") ? (List<object>)m["form"] : null;
            var manualNames = new HashSet<string>(StringComparer.Ordinal);
            if (manual != null)
                foreach (object it in manual) manualNames.Add((string)((Dictionary<string, object>)it)["name"]);

            var derived = new List<object>();
            var seen = new Dictionary<string, string>(StringComparer.Ordinal); // name → label+fmt 서명(중복·불일치 검사)
            // 여러 줄로 나뉜 태그(prettier 서식)도 놓치지 않습니다: 줄 단위 대신 전체를 이어서 스캔합니다.
            // (ControlTag의 [^>]*는 개행도 삼키므로 이을 수 있습니다. 참조 조각은 ExpandRef가 이미 단일 줄로 만듭니다.)
            foreach (Match tm in ControlTag.Matches(string.Join("\n", viewLines.ToArray())))
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

            if (manual == null && derived.Count == 0)
            {
                // 표시 전용 페이지(<form> 마크업 자체가 없음)는 합법. form 마크업이 있는데 필드가 0개일 때만 누락으로 봅니다.
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
            // 속성은 태그명 직후나 다른 속성 뒤 공백으로 시작하므로 (?:^|\s)로 data-name 같은 부분일치를 막습니다.
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

        bool ValidateCross(object manifest, string views, string script, string contentPath)
        {
            var m = (Dictionary<string, object>)manifest;
            foreach (string name in Legacy)
                if (HasDef(script, name)) { Fail(contentPath, "\"" + name + "\" is now provided by core.js; remove it from the page"); return false; }
            // features(manifest)는 폐기되어 capabilities가 유일한 출처입니다: profilePoint 등 훅의
            // 필수 여부는 런타임에 CAPS가 결정하므로, 빌드 시 검사는 Legacy 중복 정의와 form 대조만 남깁니다.
            // <form/> 컴포넌트는 MANIFEST.form으로 컨트롤을 런타임에 렌더합니다: 마크업에 name=가 없는 것이 정상이라 생략합니다.
            if (!useOrder.Contains("form"))
            {
                var form = (List<object>)m["form"];
                foreach (object item in form)
                {
                    string name = (string)((Dictionary<string, object>)item)["name"];
                    if (views.IndexOf("name=\"" + name + "\"", StringComparison.Ordinal) < 0)
                    { Fail(contentPath, "form field \"" + name + "\" has no matching name=\"" + name + "\" control in the page markup"); return false; }
                }
            }
            return true;
        }
    }
}
