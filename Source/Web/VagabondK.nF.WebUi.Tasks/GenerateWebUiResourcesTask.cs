using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using VagabondK.nF.WebUi;

namespace VagabondK.nF.WebUi.Tasks
{
    /// <summary>
    /// *.nf.html 페이지들(첫 줄 @route:... 마커)과 컴포넌트를 머지·축소·gzip한 뒤 그
    /// 바이트를 nanoFramework 바이너리 리소스(.resx → .nanoresources → PE embed)로 내보내고,
    /// 장치측에서 ResourceUtility.GetObject로 부분 읽기 하는 WebPage 클래스와
    /// 모든 페이지/자산 라우트 핸들러(WebUiRoutes)를 생성합니다.
    /// </summary>
    public sealed class GenerateWebUiResourcesTask : Task
    {
        // part 하나의 최대 바이트. 리소스 항목 수와 부분 읽기 경계의 절충값.
        const int PartSize = 32768;

        // 공용 자산 ETag에 병합되는 빌드 스탬프: 자산 내용은 그대로여도 서버 로직이 바뀌면
        // 기존(오염) 캐시를 무효화해야 하므로 매 태스크 실행 새 값을 씁니다. bin 파일 내용에는
        // 들어가지 않으므로 WriteIfChanged의 결정성(재빌드 시 파일 불변)은 유지됩니다.
        readonly string _stamp = DateTime.UtcNow.Ticks.ToString("x8", CultureInfo.InvariantCulture);

        /// <summary>ResourceManager baseName(예: nanoGate.Management.WebUiResources).</summary>
        [Required]
        public string ResourceName { get; set; }

        /// <summary>코어 런타임 js(Assets\core.js) 파일 경로.</summary>
        [Required]
        public string WebUiCore { get; set; }

        /// <summary>컴포넌트 루트 세미콜론 목록(앞 루트가 뒤를 섀도잉). 페이지 .nf.html도 여기서 발견합니다.</summary>
        [Required]
        public string WebUiComponents { get; set; }

        /// <summary>선택: manifest.id 대체(단일 출처 강제).</summary>
        public string WebUiId { get; set; }

        /// <summary>생성 클래스의 네임스페이스.</summary>
        public string WebUiNamespace { get; set; }

        /// <summary>디자이너 클래스명(기본 WebPage). 라우터 클래스는 여기서 파생하지 않습니다.</summary>
        public string WebUiClassName { get; set; }

        /// <summary>선택: 모든 페이지가 공유하는 :root 토큰 css 파일 경로.</summary>
        public string WebUiTheme { get; set; }

        /// <summary>{{BRAND}} 치환문(기본 "WebUi").</summary>
        public string WebUiBrand { get; set; }

        /// <summary>"true" = css 자산 링크 분리. 테마+컴포넌트 css를 페이지 전체 유니온으로
        /// /style.css 단일 자산으로 빼고 모든 페이지가 같은 파일을 link합니다. 페이지 자기 css는 인라인 유지.</summary>
        public string WebUiLinkCss { get; set; }

        /// <summary>공용 i18n 런타임(Assets\i18n.js) 경로. /i18n.js 단일 gzip 자산으로 서빙되며
        /// 코어/로그인 js가 uiText를 직접 부르므로 필수 입력입니다(targets에 라이브러리 기본값).</summary>
        public string WebUiI18n { get; set; }

        /// <summary>resx/bin/cs 산출물을 쓰는 폴더(보통 obj\config\webui\).</summary>
        [Required]
        public string OutputDirectory { get; set; }

        /// <summary>생성된 .resx 파일(웹 자산 바이너리 part 포함).</summary>
        [Output]
        public ITaskItem ResxFile { get; set; }

        /// <summary>생성된 디자이너 .cs(WebPage 리소스 접근 클래스).</summary>
        [Output]
        public ITaskItem DesignerFile { get; set; }

        /// <summary>생성된 WebUiRoutes.g.cs — targets가 Compile에 주입합니다.</summary>
        [Output]
        public ITaskItem RoutesFile { get; set; }

        public override bool Execute()
        {
            string coreText = ReadIfAny(WebUiCore);
            if (coreText == null) { Log.LogError("WebUi: core file not found: " + WebUiCore); return false; }
            // 공용 i18n은 선택이 아닙니다: 코어/로그인이 uiText를 부르므로 없으면 페이지가 깨집니다.
            string i18nText = ReadIfAny(WebUiI18n);
            if (i18nText == null) { Log.LogError("WebUi: i18n file not found: " + WebUiI18n); return false; }

            string[] compRoots = (WebUiComponents ?? string.Empty)
                .Split(new[] { ';', '|' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(r => r.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .Where(Directory.Exists)
                .ToArray();

            // 페이지(.nf.html + 첫 줄 @route 마커)와 컴포넌트를 한 번의 스캔으로 수집·섀도잉합니다.
            Dictionary<string, PageComposer.ComponentFiles> components;
            List<PageEntry> pages;
            if (!CollectComponents(compRoots, out components, out pages))
                return false;
            if (pages.Count == 0)
            {
                Log.LogError("WebUi: no page with an @route:/path first-line marker was found under $(WebUiComponents) roots");
                return false;
            }
            // 페이지 순서 = 리소스/라우터 인덱스: "/"가 맨 앞(designer가 0번을 기본 페이지로 별칭),
            // 나머지는 경로 오름차순. 정렬 후 인접 비교로 중복 경로를 잡습니다.
            pages.Sort((a, b) =>
            {
                bool ar = a.Route.Path == "/", br = b.Route.Path == "/";
                if (ar != br) return ar ? -1 : 1;
                return string.CompareOrdinal(a.Route.Path, b.Route.Path);
            });
            for (int i = 1; i < pages.Count; i++)
                if (pages[i].Route.Path == pages[i - 1].Route.Path)
                {
                    Log.LogError("WebUi: duplicate route \"{0}\" in {1} and {2}", pages[i].Route.Path, pages[i - 1].File, pages[i].File);
                    return false;
                }

            // 다국어 사전 수집: 컴포넌트 루트의 texts*.resx를 병합(원본 파일, 브라우저 서빙 원문 아님).
            // 베이스 texts.resx의 이름 있는 항목은 en 원문으로 마크업/주입 폴백이라 /texts.js에 들어가지 않고
            // 사전 값만 uiText 폴백 주입(enFallbacks)에 씁니다.
            Dictionary<string, string> enFallbacks;
            string texts = CollectTexts(compRoots, out enFallbacks);

            bool linkCss = string.Equals(WebUiLinkCss, "true", StringComparison.OrdinalIgnoreCase);
            // 공유 테마는 선택: 미지정이면 빈 문자열(각 페이지 own css의 기본 팔레트 그대로).
            string theme = string.Empty;
            if (!string.IsNullOrWhiteSpace(WebUiTheme))
            {
                try { theme = File.ReadAllText(WebUiTheme); }
                catch { Log.LogError("WebUi: cannot read WebUiTheme: {0}", WebUiTheme); return false; }
            }

            try
            {
                Directory.CreateDirectory(OutputDirectory);

                // 페이지별 Compose + part(p<page>_<part>) 저장. 리소스 id는 전 항목 공통
                // HashSet으로 관리해 16비트 해시 충돌 시 키에 접미를 붙여 재계산합니다.
                // (BuildTasks의 EnsureResourcesIds는 충돌 시 오류만 내고 회피하지 않습니다.)
                var used = new HashSet<short>();
                var resxKeys = new List<string>();
                var partIds = new List<short>();
                var pageLengths = new List<int>();
                var pageStarts = new List<int>();
                var pageETags = new List<uint>();
                var results = new List<PageComposer.Result>(pages.Count);
                var cssUnion = new List<string>();
                var cssSeen = new HashSet<string>(StringComparer.Ordinal);
                string loginPath = string.Empty;
                int totalBytes = 0;
                for (int pi = 0; pi < pages.Count; pi++)
                {
                    PageEntry pg = pages[pi];
                    // login 페이지: 마크업/스크립트가 부르는 키만 사전에서 골라 부분집합을 본문에
                    // 인라인합니다(자산 왕복 없음 → 첫 페인트부터 모국어, 깜빡임 구조 제거).
                    // 부분집합을 못 만들면 링크 방식(/texts.js·/i18n.js)으로 후퇴합니다.
                    string inlineTexts = null;
                    if (pg.Route.IsLogin)
                    {
                        HashSet<string> tk = PageComposer.ExtractTextKeys(string.Join("\n", pg.Html));
                        if (pg.Js != null)
                            tk.UnionWith(PageComposer.ExtractTextKeys(string.Join("\n", pg.Js)));
                        string subset = CollectTextsSubset(compRoots, tk);
                        if (subset != null)
                            inlineTexts = subset + "\n" + i18nText;
                    }

                    PageComposer.Result r;
                    try
                    {
                        r = PageComposer.Compose(pg.Html, coreText, pg.File, WebUiNamespace, WebUiClassName,
                            WebUiId, WebUiComponents, components, linkCss, theme, texts, i18nText, enFallbacks,
                            WebUiBrand, pg.Css, pg.Js, inlineTexts, pg.Route.IsLogin);
                    }
                    catch (Exception e)
                    {
                        Log.LogError("WebUi: " + e.Message);
                        return false;
                    }
                    if (r.Gzip == null)
                    {
                        Log.LogError("WebUi page \"" + pg.Route.Path + "\": " + (r.ErrorMessage ?? "(unknown)")
                            + (string.IsNullOrEmpty(r.ErrorFile) ? "" : " [" + r.ErrorFile + "]"));
                        return false;
                    }
                    // en 사전(texts.resx)에 없어 폴백이 주입되지 않은 uiText 키 — 번역 사정 범위를 넘은 호출.
                    WarnMisses(r.EnMisses);

                    if (pg.Route.IsLogin && loginPath.Length == 0) loginPath = pg.Route.Path;
                    results.Add(r);
                    if (linkCss && r.CssAssetLines != null)
                        foreach (string block in PageComposer.SplitCssBlocks(PageComposer.Minify(r.CssAssetLines).Split('\n')))
                            if (cssSeen.Add(block)) cssUnion.Add(block);

                    byte[] gzip = r.Gzip;
                    totalBytes += gzip.Length;
                    int partCount = (gzip.Length + PartSize - 1) / PartSize;
                    if (partCount == 0) partCount = 1;
                    pageStarts.Add(partIds.Count);
                    pageLengths.Add(gzip.Length);
                    pageETags.Add(r.Hash);
                    for (int i = 0; i < partCount; i++)
                    {
                        string key = "p" + pi.ToString(CultureInfo.InvariantCulture) + "_" + i.ToString(CultureInfo.InvariantCulture);
                        short id = GenerateIdFromResourceName(key);
                        while (!used.Add(id))
                        {
                            key += "z";
                            id = GenerateIdFromResourceName(key);
                        }
                        resxKeys.Add(key);
                        partIds.Add(id);

                        string binName = "page_" + key + ".bin";
                        int offset = i * PartSize;
                        int length = Math.Min(PartSize, gzip.Length - offset);
                        var buf = new byte[length];
                        Array.Copy(gzip, offset, buf, 0, length);
                        WriteIfChanged(Path.Combine(OutputDirectory, binName), buf);
                    }
                }
                PageComposer.Result r0 = results[0]; // 사전·i18n gzip은 페이지마다 동일 — 0번 결과에서 취합니다.

                // css 자산 링크 분리(WebUiLinkCss=true): 전체 페이지가 참조한 테마+컴포넌트 css의
                // 유니온을 한 번 gzip해 단일 항목 c0(/style.css)으로 저장. 모든 페이지가 같은 파일을 link합니다.
                string cssKey = null;
                int cssLength = -1;
                short cssId = 0;
                uint cssHash = 0;
                if (linkCss && cssUnion.Count > 0)
                {
                    byte[] cssGzip;
                    PageComposer.GzipAndHash(PageComposer.Minify(cssUnion), out cssGzip, out cssHash);
                    if (cssGzip.Length > 65534)
                    {
                        Log.LogError("WebUi: style.css is {0} B gzip, over the 65534 B single-resource limit", cssGzip.Length);
                        return false;
                    }
                    cssKey = "c0";
                    cssId = GenerateIdFromResourceName(cssKey);
                    while (!used.Add(cssId))
                    {
                        cssKey += "z";
                        cssId = GenerateIdFromResourceName(cssKey);
                    }
                    WriteIfChanged(Path.Combine(OutputDirectory, "page_" + cssKey + ".bin"), cssGzip);
                    cssLength = cssGzip.Length;
                    r0.Info += " Css " + cssLength.ToString(CultureInfo.InvariantCulture) + " B gzip.";
                }

                // 다국어 사전 자산: /texts.js gzip 단일 항목 tx(모든 페이지 공유).
                string textsKey = null;
                int textsLength = -1;
                short textsId = 0;
                uint textsHash = 0;
                if (r0.TextsGzip != null)
                {
                    if (r0.TextsGzip.Length > 65534)
                    {
                        Log.LogError("WebUi: texts.js is {0} B gzip, over the 65534 B single-resource limit", r0.TextsGzip.Length);
                        return false;
                    }
                    textsKey = "tx";
                    textsId = GenerateIdFromResourceName(textsKey);
                    while (!used.Add(textsId))
                    {
                        textsKey += "z";
                        textsId = GenerateIdFromResourceName(textsKey);
                    }
                    WriteIfChanged(Path.Combine(OutputDirectory, "page_" + textsKey + ".bin"), r0.TextsGzip);
                    textsLength = r0.TextsGzip.Length;
                    textsHash = r0.TextsHash;
                    r0.Info += " Texts " + textsLength.ToString(CultureInfo.InvariantCulture) + " B gzip.";
                }

                // 공용 i18n 자산: /i18n.js gzip 단일 항목 ij(모든 페이지 공유).
                string i18nKey = null;
                int i18nLength = -1;
                short i18nId = 0;
                uint i18nHash = 0;
                if (r0.I18nGzip != null)
                {
                    if (r0.I18nGzip.Length > 65534)
                    {
                        Log.LogError("WebUi: i18n.js is {0} B gzip, over the 65534 B single-resource limit", r0.I18nGzip.Length);
                        return false;
                    }
                    i18nKey = "ij";
                    i18nId = GenerateIdFromResourceName(i18nKey);
                    while (!used.Add(i18nId))
                    {
                        i18nKey += "z";
                        i18nId = GenerateIdFromResourceName(i18nKey);
                    }
                    WriteIfChanged(Path.Combine(OutputDirectory, "page_" + i18nKey + ".bin"), r0.I18nGzip);
                    i18nLength = r0.I18nGzip.Length;
                    i18nHash = r0.I18nHash;
                    r0.Info += " I18n " + i18nLength.ToString(CultureInfo.InvariantCulture) + " B gzip.";
                }

                // 페이지 라우팅은 .nf.html 첫 줄의 @route 마커가 유일하게 결정합니다(별도 로그인 저장 단계 없음).

                string resxPath = Path.Combine(OutputDirectory, "WebUiResources.resx");
                WriteResx(resxPath, resxKeys.ToArray(), cssKey, textsKey, i18nKey);
                string designerPath = Path.Combine(OutputDirectory, "WebPage.resources.cs");
                WriteDesigner(designerPath, pageLengths, pageStarts, pageETags, partIds,
                    cssLength, cssId, cssHash, textsLength, textsId, textsHash, i18nLength, i18nId, i18nHash);
                string routesPath = Path.Combine(OutputDirectory, "WebUiRoutes.g.cs");
                WriteRoutes(routesPath, pages, loginPath, cssKey != null, textsKey != null, i18nKey != null);

                ResxFile = new TaskItem(resxPath);
                DesignerFile = new TaskItem(designerPath);
                RoutesFile = new TaskItem(routesPath);
                Log.LogMessage(MessageImportance.Normal,
                    "WebUi resources: {0} pages, {1} B gzip total, {2} parts. {3}",
                    pages.Count, totalBytes.ToString(CultureInfo.InvariantCulture), partIds.Count, r0.Info);
            }
            catch (Exception e)
            {
                Log.LogError("WebUi resource generation failed: " + e.Message);
                return false;
            }
            return true;
        }

        static string ReadIfAny(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            try { return File.ReadAllText(path); }
            catch { return null; }
        }

        // 컴포넌트/페이지 수집: <name>.nf.{html,js,css}를 루트별로 스캔, 앞 루트가 같은
        // 이름을 섀도잉, 같은 루트 내 중복은 오류, js/css는 html과 같은 루트·같은 폴더일 때만 짝.
        // .html은 첫 줄 @route 마커로 페이지/컴포넌트가 갈립니다(마커 있으면 페이지, none이면
        // 섀도잉 전용 — 어떤 결과에도 들어가지 않습니다).
        bool CollectComponents(string[] roots, out Dictionary<string, PageComposer.ComponentFiles> componentFiles,
            out List<PageEntry> pages)
        {
            componentFiles = new Dictionary<string, PageComposer.ComponentFiles>(StringComparer.OrdinalIgnoreCase);
            pages = new List<PageEntry>();
            if (roots.Length == 0) return true;

            var entries = new List<Entry>();
            for (int rootIdx = 0; rootIdx < roots.Length; rootIdx++)
            {
                string root = roots[rootIdx];
                foreach (string file in EnumerateSorted(root))
                {
                    string rel = file.Substring(root.Length + 1);
                    string stem = ComponentStem(rel);
                    if (stem == null) continue;
                    string ext = Path.GetExtension(rel).ToLowerInvariant();
                    var en = new Entry
                    {
                        Root = rootIdx,
                        Dir = Path.GetDirectoryName(rel) ?? string.Empty,
                        Stem = stem,
                        Ext = ext,
                        Path = file
                    };
                    if (ext == ".html")
                    {
                        string err;
                        en.Route = PageComposer.ParseRoute(PageComposer.ReadLines(File.ReadAllText(file)), out err);
                        if (err != null)
                        {
                            Log.LogError("WebUi: {0}", err);
                            return false;
                        }
                    }
                    entries.Add(en);
                }
            }

            // stem 승자표: 페이지든 컴포넌터든 이름 하나에 승리자 하나. entries는 루트
            // 오름차순으로.scanned되므로 첫 승자가 곧 앞 루트이며, 뒤(큰 루트)는 계속 겁니다.
            var owner = new Dictionary<string, OwnerInfo>(StringComparer.OrdinalIgnoreCase);
            var pageByStem = new Dictionary<string, PageEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in entries)
            {
                if (e.Ext != ".html") continue;
                OwnerInfo cur;
                if (owner.TryGetValue(e.Stem, out cur))
                {
                    if (cur.RootIdx == e.Root)
                    {
                        Log.LogError("WebUi: duplicate \"{0}\" in the same root: {1} and {2}", e.Stem, cur.File, e.Path);
                        return false;
                    }
                    continue; // 앞 루트가 이미 이김(섀도잉)
                }
                var o = new OwnerInfo { RootIdx = e.Root, Dir = e.Dir, File = e.Path };
                owner[e.Stem] = o;
                if (e.Route != null)
                {
                    o.IsPage = true;
                    if (e.Route.IsNone)
                    {
                        o.Shadowed = true; // route:none — 같은 이름의 뒤 루트 파일만 지우는 용도
                        continue;
                    }
                    var pg = new PageEntry { File = e.Path, RootIdx = e.Root, Dir = e.Dir, Route = e.Route };
                    o.IsPage = true;
                    pageByStem[e.Stem] = pg;
                    pages.Add(pg);
                    // pg.Html은 pairing 전 원본 라인을 재사용(@route/@instance 마커 줄은 PageComposer.Run이 출력에서 제거).
                    pg.Html = PageComposer.ReadLines(File.ReadAllText(e.Path));
                }
                else
                    componentFiles[e.Stem] = new PageComposer.ComponentFiles
                    {
                        HtmlPath = e.Path,
                        Html = PageComposer.ReadLines(File.ReadAllText(e.Path))
                    };
            }

            foreach (var e in entries)
            {
                if (e.Ext == ".html") continue;
                OwnerInfo o;
                if (!owner.TryGetValue(e.Stem, out o)) continue; // 같은 이름 html 없음 = 고아 파일, 무시
                if (o.Shadowed) continue;                          // none 승자면 짝도 버립니다
                if (o.RootIdx != e.Root || !string.Equals(o.Dir, e.Dir, StringComparison.OrdinalIgnoreCase)) continue; // 같은 폴더만 짝
                string[] lines = PageComposer.ReadLines(File.ReadAllText(e.Path));
                if (o.IsPage)
                {
                    PageEntry pg = pageByStem[e.Stem];
                    if (e.Ext == ".js") pg.Js = lines;
                    else pg.Css = lines;
                }
                else
                {
                    PageComposer.ComponentFiles c = componentFiles[e.Stem];
                    if (e.Ext == ".js") c.Js = lines;
                    else c.Css = lines;
                }
            }
            return true;
        }

        // stem 승자의 메타(루트/폴더/종류). 페이지면 pageByStem에서 본문을 찾습니다.
        sealed class OwnerInfo
        {
            public int RootIdx;
            public string Dir;
            public string File;
            public bool IsPage;
            public bool Shadowed; // route:none 승자
        }

        // 파일시스템 열거 순서에 의존하지 않기 위해 정렬(빌드 재현성).
        static IEnumerable<string> EnumerateSorted(string root)
        {
            var files = Directory.GetFiles(root, "*.nf.*", SearchOption.AllDirectories);
            Array.Sort(files, StringComparer.Ordinal);
            return files;
        }

        // 다국어 사전 수집: 컴포넌트 루트별 texts.resx(베이스 en) + texts.<컬처>.resx를
        // 런타임 JS 문자열 하나로 병합합니다. resx는 빌드 도구 원본 파일이며 PE에 임베드되지 않습니다.
        // texts.resx의 이름 있는 항목 = en 원문: /texts.js에는 들어가지 않고 uiText(key)
        // 호출부 폴백 주입(enFallbacks)에만 씁니다.
        // static.NNN 이름 항목 = 정적 영역 원문(value = 영어 원문 그 자체). 각 컬처 resx의
        // 같은 이름과 짝지어 t.static.<컬처>(영어→현지어)로 방출 — static.nf.js의
        // localizeStatic()이 사용합니다. uiText 키는 접두사 규약(gate./nf./login.)이라
        // static.과 충돌하지 않습니다.
        string CollectTexts(string[] roots, out Dictionary<string, string> enFallbacks)
        {
            var merged = new StringBuilder();
            enFallbacks = new Dictionary<string, string>(StringComparer.Ordinal);
            // 루트를 역순으로 돕니다(성분 루트 배열은 프로젝트→공유→라이브러리 순).
            // 병합 결과가 Object.assign 순차 적용이므로 나중에 적용되는 프로젝트 키가
            // 라이브러리 키를 키 단위로 덮어씁니다.
            for (int i = roots.Length - 1; i >= 0; i--)
            {
                string root = roots[i];
                if (!Directory.Exists(root)) continue;

                // 이 루트의 베이스(en): 이름 있는 항목은 폴백 사정, static.NNN은 정적 원문 표.
                var staticOrig = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (string f in Directory.GetFiles(root, "texts.resx", SearchOption.AllDirectories)
                             .OrderBy(x => x, StringComparer.Ordinal))
                    foreach (var e in ReadResxDictionary(f))
                        if (e.Key.StartsWith("static.", StringComparison.Ordinal)) staticOrig[e.Key] = e.Value;
                        else enFallbacks[e.Key] = e.Value;

                foreach (string f in Directory.GetFiles(root, "texts.*.resx", SearchOption.AllDirectories)
                             .Where(x => !Path.GetFileName(x).Equals("texts.resx", StringComparison.OrdinalIgnoreCase))
                             .OrderBy(x => x, StringComparer.Ordinal))
                {
                    var m = Regex.Match(Path.GetFileName(f), @"^texts\.(.+)\.resx$", RegexOptions.IgnoreCase);
                    if (!m.Success) continue;
                    string lang = m.Groups[1].Value;
                    var named = new List<KeyValuePair<string, string>>();
                    var stat = new List<KeyValuePair<string, string>>();
                    foreach (var e in ReadResxDictionary(f))
                        (e.Key.StartsWith("static.", StringComparison.Ordinal) ? stat : named).Add(e);

                    merged.Append("// ---- texts.").Append(lang).Append(".resx ----\n");
                    merged.Append("(function (w) {\nvar t = (w.__wuiTx = w.__wuiTx || {});\n");
                    AppendAssign(merged, "t[\"" + lang + "\"]", named);
                    if (stat.Count > 0)
                    {
                        merged.Append("Object.assign(t[\"static\"] = t[\"static\"] || {}, {});\n");
                        var pairs = new List<KeyValuePair<string, string>>();
                        foreach (var e in stat)
                        {
                            string en;
                            if (!staticOrig.TryGetValue(e.Key, out en))
                            {
                                Log.LogWarning("WebUi: {0} has static entry '{1}' with no original in this root's texts.resx (skipped)", Path.GetFileName(f), e.Key);
                                continue;
                            }
                            pairs.Add(new KeyValuePair<string, string>(en, e.Value));
                        }
                        AppendAssign(merged, "t[\"static\"][\"" + lang + "\"]", pairs);
                    }
                    merged.Append("})(window);\n");
                }
            }
            return merged.ToString();
        }

        // 공용 사전 부분집합: 로그인 스크립트가 부르는 키만 texts.<lang>.resx에서 골라 냅니다.
        // 병합 순서는 CollectTexts와 동일(루트 역순 → 뒤(상위 우선) 프로젝트 키가 덮어씀).
        // 히트 0건이면 null(링크 방식으로 후퇴). static.*는 로그인 마크업에 없으므로 제외.
        string CollectTextsSubset(string[] roots, HashSet<string> keys)
        {
            if (keys == null || keys.Count == 0) return null;
            var merged = new StringBuilder();
            for (int i = roots.Length - 1; i >= 0; i--)
            {
                string root = roots[i];
                if (!Directory.Exists(root)) continue;
                foreach (string f in Directory.GetFiles(root, "texts.*.resx", SearchOption.AllDirectories)
                             .Where(x => !Path.GetFileName(x).Equals("texts.resx", StringComparison.OrdinalIgnoreCase))
                             .OrderBy(x => x, StringComparer.Ordinal))
                {
                    var m = Regex.Match(Path.GetFileName(f), @"^texts\.(.+)\.resx$", RegexOptions.IgnoreCase);
                    if (!m.Success) continue;
                    string lang = m.Groups[1].Value;
                    var named = new List<KeyValuePair<string, string>>();
                    foreach (var e in ReadResxDictionary(f))
                        if (!e.Key.StartsWith("static.", StringComparison.Ordinal) && keys.Contains(e.Key)) named.Add(e);
                    if (named.Count == 0) continue;
                    merged.Append("// login subset: texts.").Append(lang).Append(".resx\n");
                    merged.Append("(function (w) {\nvar t = (w.__wuiTx = w.__wuiTx || {});\n");
                    AppendAssign(merged, "t[\"" + lang + "\"]", named);
                    merged.Append("})(window);\n");
                }
            }
            return merged.Length == 0 ? null : merged.ToString();
        }

        static void AppendAssign(StringBuilder sb, string target, List<KeyValuePair<string, string>> entries)
        {
            sb.Append("Object.assign(").Append(target).Append(" = ").Append(target).Append(" || {}, {\n");
            for (int n = 0; n < entries.Count; n++)
            {
                sb.Append('\'').Append(JsEscape(entries[n].Key)).Append("': '")
                  .Append(JsEscape(entries[n].Value)).Append('\'');
                sb.Append(n + 1 < entries.Count ? ",\n" : "\n");
            }
            sb.Append("});\n");
        }

        static string JsEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '\'': sb.Append("\\'"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\u2028': sb.Append("\\u2028"); break;
                    case '\u2029': sb.Append("\\u2029"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        // resx → 이름/값 (문서 순서). 값은 순수 텍스트만 약속합니다(이진/ResXFileRef 금지).
        static IEnumerable<KeyValuePair<string, string>> ReadResxDictionary(string file)
        {
            var doc = XDocument.Load(file);
            XNamespace ns = doc.Root.Name.Namespace;
            foreach (var d in doc.Root.Elements(ns + "data"))
            {
                string name = (string)d.Attribute("name");
                var v = d.Element(ns + "value");
                if (string.IsNullOrEmpty(name) || v == null) continue;
                yield return new KeyValuePair<string, string>(name, v.Value);
            }
        }

        void WarnMisses(List<string> misses)
        {
            if (misses == null || misses.Count == 0) return;
            Log.LogWarning("WebUi: {0} uiText key(s) have no en fallback in texts.resx (runtime dictionary only): {1}",
                misses.Count, string.Join(", ", misses.ToArray()));
        }

        static string ComponentStem(string rel)
        {
            string lower = rel.ToLowerInvariant();
            if (lower.EndsWith(".nf.html", StringComparison.Ordinal)) return rel.Substring(0, rel.Length - 8);
            if (lower.EndsWith(".nf.css", StringComparison.Ordinal)) return rel.Substring(0, rel.Length - 7);
            if (lower.EndsWith(".nf.js", StringComparison.Ordinal)) return rel.Substring(0, rel.Length - 6);
            return null;
        }

        static void WriteResx(string path, string[] keys, string cssKey, string textsKey, string i18nKey)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<root>\n");
            sb.Append("  <resheader name=\"resmimetype\"><value>text/microsoft-resx</value></resheader>\n");
            sb.Append("  <resheader name=\"version\"><value>2.0</value></resheader>\n");
            sb.Append("  <resheader name=\"reader\"><value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>\n");
            sb.Append("  <resheader name=\"writer\"><value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>\n");
            foreach (string key in keys)
                sb.Append("  <data name=\"").Append(key)
                  .Append("\" type=\"System.Resources.ResXFileRef, System.Windows.Forms\"><value>page_")
                  .Append(key).Append(".bin;System.Byte[], mscorlib</value></data>\n");
            if (cssKey != null)
                sb.Append("  <data name=\"").Append(cssKey)
                  .Append("\" type=\"System.Resources.ResXFileRef, System.Windows.Forms\"><value>page_")
                  .Append(cssKey).Append(".bin;System.Byte[], mscorlib</value></data>\n");
            if (textsKey != null)
                sb.Append("  <data name=\"").Append(textsKey)
                  .Append("\" type=\"System.Resources.ResXFileRef, System.Windows.Forms\"><value>page_")
                  .Append(textsKey).Append(".bin;System.Byte[], mscorlib</value></data>\n");
            if (i18nKey != null)
                sb.Append("  <data name=\"").Append(i18nKey)
                  .Append("\" type=\"System.Resources.ResXFileRef, System.Windows.Forms\"><value>page_")
                  .Append(i18nKey).Append(".bin;System.Byte[], mscorlib</value></data>\n");
            sb.Append("</root>\n");
            WriteIfChanged(path, Encoding.UTF8.GetBytes(sb.ToString()));
        }

        // designer 신형상: 페이지 테이블(PageCount/PageLengths/PagePartStart/PageETags +
        // flat PartIds)과 gzip 자산(css/tx/ij). 0번 페이지는 구형 별칭(Length/PartCount/ETag/Read)을
        // 유지해 nanoGate 등 단일 페이지 코드의 이관을 돕습니다. 페이지 서빙은 TryServePage.
        void WriteDesigner(string path, List<int> lengths, List<int> starts, List<uint> etags, List<short> ids,
            int cssLength, short cssId, uint cssHash,
            int textsLength, short textsId, uint textsHash, int i18nLength, short i18nId, uint i18nHash)
        {
            var sb = new StringBuilder();
            sb.Append("// <auto-generated>\n// Generated by VagabondK.nF.WebUi.Tasks. Do not edit.\n// </auto-generated>\n");
            sb.Append("namespace ").Append(WebUiNamespace).Append('\n').Append("{\n");
            sb.Append("    internal static class ").Append(WebUiClassName).Append('\n').Append("    {\n");
            sb.Append("        internal const int PageCount = ").Append(lengths.Count).Append(";\n");
            sb.Append("        internal static readonly int[] PageLengths = new int[] { ");
            for (int i = 0; i < lengths.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(lengths[i].ToString(CultureInfo.InvariantCulture));
            }
            sb.Append(" };\n");
            sb.Append("        internal static readonly int[] PagePartStart = new int[] { ");
            for (int i = 0; i < starts.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(starts[i].ToString(CultureInfo.InvariantCulture));
            }
            sb.Append(" };\n");
            sb.Append("        internal static readonly string[] PageETags = new string[] { ");
            for (int i = 0; i < etags.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append("\"\\\"").Append(etags[i].ToString("x8", CultureInfo.InvariantCulture)).Append("\\\"\"");
            }
            sb.Append(" };\n\n");
            // 0번 페이지 구형 별칭(단일 페이지 프로젝트의 마이그레이션 브리지).
            sb.Append("        internal const int Length = ").Append(lengths[0]).Append(";\n");
            sb.Append("        internal const int PartCount = ").Append(starts.Count > 1 ? starts[1] - starts[0] : ids.Count).Append(";\n");
            sb.Append("        internal const string ETag = \"\\\"").Append(etags[0].ToString("x8", CultureInfo.InvariantCulture)).Append("\\\"\";\n\n");
            sb.Append("        const int PartSize = ").Append(PartSize).Append(";\n");
            sb.Append("        static readonly short[] PartIds = new short[] { ");
            for (int i = 0; i < ids.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append("((short)").Append(((short)ids[i]).ToString(CultureInfo.InvariantCulture)).Append(")");
            }
            sb.Append(" };\n");
            sb.Append("        static System.Resources.ResourceManager _rm;\n\n");
            sb.Append("        // 페이지 gzip의 [offset, offset+count) 구간을 리소스에서 직접 읽습니다.\n");
            sb.Append("        // part 경계를 넘지 않는 범위만 반환하므로 호출자는 반환 길이만큼 진행하면 됩니다.\n");
            sb.Append("        internal static byte[] ReadPage(int page, int offset, int count)\n        {\n");
            sb.Append("            System.Resources.ResourceManager rm = _rm;\n");
            sb.Append("            if (rm == null)\n");
            sb.Append("                _rm = rm = new System.Resources.ResourceManager(\"").Append(ResourceName)
              .Append("\", typeof(").Append(WebUiClassName).Append(").Assembly);\n");
            sb.Append("            int part = offset / PartSize;\n");
            sb.Append("            int within = offset - part * PartSize;\n");
            sb.Append("            int last = PageLengths[page] - offset;\n");
            sb.Append("            if (count > last) count = last;\n");
            sb.Append("            if (count > PartSize - within) count = PartSize - within;\n");
            sb.Append("            return (byte[])nanoFramework.Runtime.Native.ResourceUtility.GetObject(rm, (WebUiPartId)PartIds[PagePartStart[page] + part], within, count);\n");
            sb.Append("        }\n\n");
            sb.Append("        internal static byte[] Read(int offset, int count)\n        {\n");
            sb.Append("            return ReadPage(0, offset, count);\n        }\n\n");
            // 페이지 HTML 서빙: noStore(login)는 ETag 없이 no-store, 아니면 no-cache+ETag/304.
            sb.Append("        internal static bool TryServePage(int page, System.Net.HttpListenerContext ctx, bool noStore)\n        {\n");
            sb.Append("            System.Net.HttpListenerResponse res = ctx.Response;\n");
            sb.Append("            if (!noStore && PageETags[page] == ctx.Request.Headers[\"if-none-match\"])\n            {\n");
            sb.Append("                res.ContentLength64 = 0;\n");
            sb.Append("                res.KeepAlive = false;\n");
            sb.Append("                res.StatusCode = 304;\n");
            sb.Append("                res.Close();\n                return true;\n            }\n");
            sb.Append("            res.Headers.Add(\"Cache-Control\", noStore ? \"no-store\" : \"no-cache\");\n");
            sb.Append("            if (!noStore) res.Headers.Add(\"ETag\", PageETags[page]);\n");
            sb.Append("            res.Headers.Add(\"Content-Encoding\", \"gzip\");\n");
            sb.Append("            res.Headers.Add(\"X-Content-Type-Options\", \"nosniff\");\n");
            sb.Append("            res.Headers.Add(\"X-Frame-Options\", \"DENY\");\n");
            sb.Append("            res.ContentType = \"text/html; charset=utf-8\";\n");
            sb.Append("            int length = PageLengths[page];\n");
            sb.Append("            res.ContentLength64 = length;\n");
            sb.Append("            System.IO.Stream o = res.OutputStream;\n");
            sb.Append("            int pos = 0;\n");
            sb.Append("            try\n            {\n");
            sb.Append("                while (pos < length)\n                {\n");
            sb.Append("                    byte[] buf = ReadPage(page, pos, 4096);\n");
            sb.Append("                    if (buf == null || buf.Length == 0) break;\n");
            sb.Append("                    o.Write(buf, 0, buf.Length);\n");
            sb.Append("                    pos += buf.Length;\n                }\n            }\n            catch { }\n");
            sb.Append("            res.Close();\n            return true;\n        }\n\n");

            // ===== css 자산 링크 분리(WebUiLinkCss=true) =====  CssLength가 0이면 미분리.
            sb.Append("        internal const int CssLength = ").Append(cssLength < 0 ? 0 : cssLength).Append(";\n");
            sb.Append("        internal const short CssId = ").Append(cssLength < 0 ? 0 : (int)cssId).Append(";\n");
            sb.Append("        internal const string CssETag = \"\\\"").Append(cssHash.ToString("x8", CultureInfo.InvariantCulture)).Append('-').Append(_stamp).Append("\\\"\";\n");
            sb.Append("        internal const int TextsLength = ").Append(textsLength < 0 ? 0 : textsLength).Append(";\n");
            sb.Append("        internal const short TextsId = ").Append(textsLength < 0 ? 0 : (int)textsId).Append(";\n");
            sb.Append("        internal const string TextsETag = \"\\\"").Append(textsHash.ToString("x8", CultureInfo.InvariantCulture)).Append('-').Append(_stamp).Append("\\\"\";\n");
            sb.Append("        internal const int I18nLength = ").Append(i18nLength < 0 ? 0 : i18nLength).Append(";\n");
            sb.Append("        internal const short I18nId = ").Append(i18nLength < 0 ? 0 : (int)i18nId).Append(";\n");
            sb.Append("        internal const string I18nETag = \"\\\"").Append(i18nHash.ToString("x8", CultureInfo.InvariantCulture)).Append('-').Append(_stamp).Append("\\\"\";\n\n");
            sb.Append("        static byte[] ReadAsset(short id, int offset, int count)\n        {\n");
            sb.Append("            System.Resources.ResourceManager rm = _rm;\n");
            sb.Append("            if (rm == null)\n");
            sb.Append("                _rm = rm = new System.Resources.ResourceManager(\"").Append(ResourceName)
              .Append("\", typeof(").Append(WebUiClassName).Append(").Assembly);\n");
            sb.Append("            return (byte[])nanoFramework.Runtime.Native.ResourceUtility.GetObject(rm, (WebUiPartId)id, offset, count);\n");
            sb.Append("        }\n\n");
            sb.Append("        // gzip 자산을 4KB 청크로 스트리밍합니다. 라우트 핸들러에서 [PublicEndpoint]로 노출해야 합니다.\n");
            sb.Append("        static bool ServeCss(System.Net.HttpListenerContext ctx, short id, int length, string etag, string contentType)\n        {\n");
            sb.Append("            System.Net.HttpListenerResponse res = ctx.Response;\n");
            sb.Append("            if (etag == ctx.Request.Headers[\"if-none-match\"])\n            {\n");
            sb.Append("                res.ContentLength64 = 0;\n");
            sb.Append("                res.KeepAlive = false;\n");
            sb.Append("                res.StatusCode = 304;\n");
            sb.Append("                res.Close();\n                return true;\n            }\n");
            sb.Append("            res.Headers.Add(\"Cache-Control\", \"no-cache\");\n");
            sb.Append("            res.Headers.Add(\"ETag\", etag);\n");
            sb.Append("            res.Headers.Add(\"Content-Encoding\", \"gzip\");\n");
            sb.Append("            res.Headers.Add(\"X-Content-Type-Options\", \"nosniff\");\n");
            sb.Append("            res.ContentType = contentType;\n");
            sb.Append("            res.ContentLength64 = length;\n");
            sb.Append("            System.IO.Stream o = res.OutputStream;\n");
            sb.Append("            int pos = 0;\n");
            sb.Append("            try\n            {\n");
            sb.Append("                while (pos < length)\n                {\n");
            sb.Append("                    byte[] buf = ReadAsset(id, pos, 4096);\n");
            sb.Append("                    if (buf == null || buf.Length == 0) break;\n");
            sb.Append("                    o.Write(buf, 0, buf.Length);\n");
            sb.Append("                    pos += buf.Length;\n                }\n            }\n            catch { }\n");
            sb.Append("            res.Close();\n            return true;\n        }\n\n");
            sb.Append("        internal static bool TryServeStyle(System.Net.HttpListenerContext ctx)\n        {\n");
            sb.Append("            return CssLength != 0 && ServeCss(ctx, CssId, CssLength, CssETag, \"text/css\");\n        }\n\n");
            sb.Append("        internal static bool TryServeTexts(System.Net.HttpListenerContext ctx)\n        {\n");
            sb.Append("            return TextsLength != 0 && ServeCss(ctx, TextsId, TextsLength, TextsETag, \"application/javascript\");\n        }\n\n");
            sb.Append("        internal static bool TryServeI18n(System.Net.HttpListenerContext ctx)\n        {\n");
            sb.Append("            return I18nLength != 0 && ServeCss(ctx, I18nId, I18nLength, I18nETag, \"application/javascript\");\n        }\n");
            sb.Append("    }\n\n");
            // nanoCLR은 리소스 조회 시 박싱된 값의 기초형이 I2인 enum만 인정합니다(fresx 장치 실증).
            sb.Append("    enum WebUiPartId : short\n    {\n        None = 0\n    }\n}\n");
            WriteIfChanged(path, Encoding.UTF8.GetBytes(sb.ToString()));
        }

        // 앱 연결 라우터(WebUiRoutes.g.cs) 생성: gzip 자산 [PublicEndpoint] 라우트 +
        // 페이지별 GET 핸들러 하나. 앱은 controllers 배열에 typeof(WebUiRoutes)를 넣고
        // server.LoginPagePath = WebUiRoutes.LoginPath; 만 지정하면 됩니다.
        void WriteRoutes(string path, List<PageEntry> pages, string loginPath, bool hasStyle, bool hasTexts, bool hasI18n)
        {
            var sb = new StringBuilder();
            sb.Append("// <auto-generated>\n// Generated by VagabondK.nF.WebUi.Tasks. Do not edit.\n// </auto-generated>\n");
            sb.Append("using nanoFramework.WebServer;\n");
            sb.Append("using VagabondK.nF.Web;\n\n");
            sb.Append("namespace ").Append(WebUiNamespace).Append('\n').Append("{\n");
            sb.Append("    public static class WebUiRoutes\n    {\n");
            sb.Append("        // 로그인 페이지 경로('' = 없음). GuardedWebServer.LoginPagePath에 대입할 것.\n");
            sb.Append("        public const string LoginPath = \"").Append(loginPath).Append("\";\n\n");

            if (hasStyle)
                AppendAssetRoute(sb, "StyleAsset", "/style.css", WebUiClassName + ".TryServeStyle(e.Context)");
            if (hasTexts)
                AppendAssetRoute(sb, "TextsAsset", "/texts.js", WebUiClassName + ".TryServeTexts(e.Context)");
            if (hasI18n)
                AppendAssetRoute(sb, "I18nAsset", "/i18n.js", WebUiClassName + ".TryServeI18n(e.Context)");

            for (int i = 0; i < pages.Count; i++)
            {
                PageEntry pg = pages[i];
                sb.Append("        [Route(\"").Append(pg.Route.Path).Append("\")]\n");
                sb.Append("        [Method(\"GET\")]\n");
                if (pg.Route.IsPublic)
                    sb.Append("        [PublicEndpoint]\n");
                sb.Append("        public static void Page").Append(i.ToString(CultureInfo.InvariantCulture))
                  .Append("(WebServerEventArgs e)\n        {\n");
                // login 페이지는 인증 상태와 무관한 응답이라 캐시 금지(no-store)로 서빙.
                sb.Append("            ").Append(WebUiClassName).Append(".TryServePage(")
                  .Append(i.ToString(CultureInfo.InvariantCulture)).Append(", e.Context, ")
                  .Append(pg.Route.IsLogin ? "true" : "false").Append(");\n        }\n\n");
            }
            sb.Append("    }\n}\n");
            WriteIfChanged(path, Encoding.UTF8.GetBytes(sb.ToString()));
        }

        static void AppendAssetRoute(StringBuilder sb, string name, string url, string serveCall)
        {
            sb.Append("        [Route(\"").Append(url).Append("\")]\n");
            sb.Append("        [Method(\"GET\")]\n");
            sb.Append("        [PublicEndpoint]\n");
            sb.Append("        public static void ").Append(name).Append("(WebServerEventArgs e)\n        {\n");
            sb.Append("            if (!").Append(serveCall).Append(")\n");
            sb.Append("                WebServer.OutputHttpCode(e.Context.Response, System.Net.HttpStatusCode.NotFound);\n");
            sb.Append("        }\n\n");
        }

        // 재빌드 시 내용이 그대로면 타임스탬프를 보존해 컴파일 캐시가 깨지지 않게 합니다.
        static void WriteIfChanged(string path, byte[] content)
        {
            if (File.Exists(path))
            {
                byte[] old = File.ReadAllBytes(path);
                if (old.Length == content.Length)
                {
                    bool same = true;
                    for (int i = 0; i < old.Length; i++)
                        if (old[i] != content[i]) { same = false; break; }
                    if (same) return;
                }
            }
            File.WriteAllBytes(path, content);
        }

        // nanoFramework.Tools.BuildTasks.ProcessResourceFiles.GenerateIdFromResourceName과
        // 동일한 해시(nf-interpreter의 bsearch 키가 됩니다). 어셈블리 복제 없이 재현합니다.
        static short GenerateIdFromResourceName(string name)
        {
            int a = 352654597, b = a;
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if ((i & 1) == 0) a = ((a << 5) + a) ^ c;
                else b = ((b << 5) + b) ^ c;
            }
            int h = a + b * 1566083941;
            unchecked { return (short)((short)(h >> 16) ^ (short)h); }
        }

        sealed class Entry
        {
            public int Root;
            public string Dir;
            public string Stem;
            public string Ext;
            public string Path;
            public PageComposer.PageRoute Route; // .html만: @route 마커(없으면 null = 컴포넌트)
        }

        // 라우팅 페이지 하나(.nf.html + @route 마커 승자). own css/js는 페어링 결과.
        sealed class PageEntry
        {
            public int RootIdx;
            public string Dir;
            public string File;
            public PageComposer.PageRoute Route;
            public string[] Html;
            public string[] Css;
            public string[] Js;
        }
    }
}
