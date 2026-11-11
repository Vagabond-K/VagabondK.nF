using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace VagabondK.nF.WebUi
{
    /// <summary>
    /// WebUi 셸/프로필 HTML을 빌드 타임에 머지·축소·gzip하여 WebPage 소스를 생성하는 증분 생성기.
    /// 입력: AdditionalFiles(shell.html, profile.html, core.js) + globalconfig(webui_* 키).
    /// </summary>
    [Generator(LanguageNames.CSharp)]
    public sealed class WebUiGenerator : IIncrementalGenerator
    {
        static readonly DiagnosticDescriptor Error = new DiagnosticDescriptor(
            "WUI001", "WebUi page generation failed", "WebUi: {0}", "WebUi", DiagnosticSeverity.Error, true);
        static readonly DiagnosticDescriptor MissingInput = new DiagnosticDescriptor(
            "WUI002", "WebUi input file missing", "WebUi: input file not found as AdditionalFiles: {0}", "WebUi", DiagnosticSeverity.Error, true);
        static readonly DiagnosticDescriptor Info = new DiagnosticDescriptor(
            "WUI000", "WebUi page summary", "{0}", "WebUi", DiagnosticSeverity.Info, true);

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var files = context.AdditionalTextsProvider
                .Select(static (t, ct) => (path: t.Path, text: t.GetText(ct) == null ? string.Empty : t.GetText(ct).ToString()))
                .Collect();

            var options = context.AnalyzerConfigOptionsProvider.Select(static (provider, ct) =>
            {
                string Get(string key)
                {
                    string v;
                    return provider.GlobalOptions.TryGetValue(key, out v) ? v : string.Empty;
                }
                return (
                    ns: Get("webui_namespace"), cls: Get("webui_class_name"),
                    shell: Get("webui_shell"), profile: Get("webui_profile"), core: Get("webui_core"),
                    components: Get("webui_components"), id: Get("webui_id"));
            });

            var input = files.Combine(options);
            context.RegisterSourceOutput(input, static (spc, pair) => Execute(spc, pair.Left, pair.Right));
        }

        static void Execute(SourceProductionContext spc, ImmutableArray<(string path, string text)> files, (string ns, string cls, string shell, string profile, string core, string components, string id) opts)
        {
            string ns = opts.ns, cls = opts.cls, id = opts.id;
            string shellPath = opts.shell, profilePath = opts.profile, corePath = opts.core;

            string shellText = null, profileText = null, coreText = null;
            string shellResolved = Resolve(shellPath), profileResolved = Resolve(profilePath), coreResolved = Resolve(corePath);
            // WebUiComponents는 세미콜론(또는 globalconfig용 '|' 인코딩)으로 여러 루트(프로젝트 로컬 + 공유 폴더)를 지정할 수 있다.
            string[] compRoots = opts.components.Split(new[] { ';', '|' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(Resolve).Where(r => r.Length > 0).ToArray();
            // 컴포넌트 인식: <name>.nf.html = 컴포넌트 정의(이름 = .nf 앞 stem. 폴더/깊이 자유).
            // 같은 이름의 <name>.nf.js/.nf.css가 같은 폴더에 있으면 한 벌. 일반 .html/.js/.css는
            // 루트 아래에 있어도 컴포넌트가 아니므로 무시(shell·profile은 명시 경로로 별도 처리).
            // 여러 루트에서 같은 이름이 나오면 앞 루트(프로젝트 로컬)가 뒤 루트(공유)를 섀도잉하고,
            // 같은 루트 안 이름 중복은 모호하므로 빌드 오류.
            var compEntries = new List<(int root, string dir, string stem, string ext, string path, string text)>();
            foreach (var f in files)
            {
                string p = Resolve(f.path);
                if (p == shellResolved) shellText = f.text;
                else if (p == profileResolved) profileText = f.text;
                else if (p == coreResolved) coreText = f.text;
                else if (compRoots.Length > 0 && IsUnderAny(p, compRoots, out string rel, out int rootIdx))
                {
                    string stem = ComponentStem(rel);
                    if (stem == null) continue;
                    string ext = Path.GetExtension(rel).ToLowerInvariant();
                    compEntries.Add((rootIdx, Path.GetDirectoryName(rel) ?? string.Empty, stem, ext, f.path, f.text));
                }
            }
            var componentFiles = new Dictionary<string, PageComposer.ComponentFiles>(StringComparer.OrdinalIgnoreCase);
            var owner = new Dictionary<string, (int root, string dir)>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in compEntries)
            {
                if (e.ext != ".html") continue;
                (int root, string dir) cur;
                if (owner.TryGetValue(e.stem, out cur))
                {
                    if (cur.root == e.root)
                    {
                        spc.ReportDiagnostic(Diagnostic.Create(Error, Location.None,
                            "duplicate component \"" + e.stem + "\" in the same root: " + componentFiles[e.stem].HtmlPath + " and " + e.path));
                        return;
                    }
                    if (e.root > cur.root) continue; // 앞 루트가 이미 이김(섀도잉)
                }
                componentFiles[e.stem] = new PageComposer.ComponentFiles { HtmlPath = e.path, Html = PageComposer.ReadLines(e.text) };
                owner[e.stem] = (e.root, e.dir);
            }
            foreach (var e in compEntries)
            {
                if (e.ext == ".html") continue;
                (int root, string dir) o;
                if (!owner.TryGetValue(e.stem, out o)) continue; // 같은 이름 html 없음 = 고아 파일, 무시
                if (o.root != e.root || !string.Equals(o.dir, e.dir, StringComparison.OrdinalIgnoreCase)) continue; // 같은 폴더만 짝
                PageComposer.ComponentFiles c = componentFiles[e.stem];
                if (e.ext == ".js") c.Js = PageComposer.ReadLines(e.text);
                else c.Css = PageComposer.ReadLines(e.text);
            }

            if (shellText == null) { spc.ReportDiagnostic(Diagnostic.Create(MissingInput, Location.None, shellPath + " (WebUiShell)")); return; }
            if (profileText == null) { spc.ReportDiagnostic(Diagnostic.Create(MissingInput, Location.None, profilePath + " (WebUiProfile)")); return; }
            if (coreText == null) { spc.ReportDiagnostic(Diagnostic.Create(MissingInput, Location.None, corePath + " (WebUiCore)")); return; }

            PageComposer.Result r;
            try
            {
                r = PageComposer.Compose(shellText, profileText, coreText, shellPath, profilePath, ns, cls, id, opts.components, componentFiles);
            }
            catch (Exception e)
            {
                spc.ReportDiagnostic(Diagnostic.Create(Error, Location.None, e.Message));
                return;
            }

            if (r.Source == null)
            {
                spc.ReportDiagnostic(Diagnostic.Create(Error, FileLocation(r.ErrorFile), r.ErrorMessage));
                return;
            }
            spc.ReportDiagnostic(Diagnostic.Create(Info, Location.None, r.Info));
            spc.AddSource("WebPage.g.cs", SourceText.From(r.Source, Encoding.UTF8));
        }

        // <name>.nf.{html,js,css} → name. 그 외(일반 .html 등)는 null = 컴포넌트 아님.
        static string ComponentStem(string rel)
        {
            string lower = rel.ToLowerInvariant();
            if (lower.EndsWith(".nf.html", StringComparison.Ordinal)) return rel.Substring(0, rel.Length - 8);
            if (lower.EndsWith(".nf.css", StringComparison.Ordinal)) return rel.Substring(0, rel.Length - 7);
            if (lower.EndsWith(".nf.js", StringComparison.Ordinal)) return rel.Substring(0, rel.Length - 6);
            return null;
        }

        static bool IsUnder(string path, string root)
        {
            return path.Length > root.Length && path[root.Length] == Path.DirectorySeparatorChar
                && path.StartsWith(root, Path.DirectorySeparatorChar == '/' ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        }

        static bool IsUnderAny(string path, string[] roots, out string rel, out int rootIdx)
        {
            for (int i = 0; i < roots.Length; i++)
                if (IsUnder(path, roots[i])) { rel = path.Substring(roots[i].Length + 1); rootIdx = i; return true; }
            rel = null;
            rootIdx = -1;
            return false;
        }

        static string Resolve(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch { return path; }
        }

        static Location FileLocation(string path)
        {
            if (string.IsNullOrEmpty(path)) return Location.None;
            try
            {
                string full = Path.GetFullPath(path);
                return Location.Create(full, new TextSpan(0, 0), new LinePositionSpan(default(LinePosition), default(LinePosition)));
            }
            catch { return Location.None; }
        }
    }
}
