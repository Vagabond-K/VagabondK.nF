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
                    components: Get("webui_components"));
            });

            var input = files.Combine(options);
            context.RegisterSourceOutput(input, static (spc, pair) => Execute(spc, pair.Left, pair.Right));
        }

        static void Execute(SourceProductionContext spc, ImmutableArray<(string path, string text)> files, (string ns, string cls, string shell, string profile, string core, string components) opts)
        {
            string ns = opts.ns, cls = opts.cls;
            string shellPath = opts.shell, profilePath = opts.profile, corePath = opts.core;

            string shellText = null, profileText = null, coreText = null;
            string shellResolved = Resolve(shellPath), profileResolved = Resolve(profilePath), coreResolved = Resolve(corePath);
            // WebUiComponents는 세미콜론(또는 globalconfig용 '|' 인코딩)으로 여러 루트(프로젝트 로컬 + 공유 폴더)를 지정할 수 있다.
            string[] compRoots = opts.components.Split(new[] { ';', '|' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(Resolve).Where(r => r.Length > 0).ToArray();
            var componentFiles = new Dictionary<string, PageComposer.ComponentFiles>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                string p = Resolve(f.path);
                if (p == shellResolved) shellText = f.text;
                else if (p == profileResolved) profileText = f.text;
                else if (p == coreResolved) coreText = f.text;
                else if (compRoots.Length > 0 && IsUnderAny(p, compRoots, out string rel))
                {
                    // components\<name>\<name>.{html,js,css}: 루트 바로 아래 이름 폴더 하나, 파일명은 폴더명과 같아야 함.
                    string[] seg = rel.Split(Path.DirectorySeparatorChar);
                    if (seg.Length != 2) continue;
                    string name = seg[0];
                    if (Path.GetFileNameWithoutExtension(seg[1]) != name) continue;
                    PageComposer.ComponentFiles c;
                    if (!componentFiles.TryGetValue(name, out c)) { c = new PageComposer.ComponentFiles(); componentFiles[name] = c; }
                    string ext = Path.GetExtension(seg[1]).ToLowerInvariant();
                    if (ext == ".html") { c.HtmlPath = f.path; c.Html = PageComposer.ReadLines(f.text); }
                    else if (ext == ".js") c.Js = PageComposer.ReadLines(f.text);
                    else if (ext == ".css") c.Css = PageComposer.ReadLines(f.text);
                }
            }

            if (shellText == null) { spc.ReportDiagnostic(Diagnostic.Create(MissingInput, Location.None, shellPath + " (WebUiShell)")); return; }
            if (profileText == null) { spc.ReportDiagnostic(Diagnostic.Create(MissingInput, Location.None, profilePath + " (WebUiProfile)")); return; }
            if (coreText == null) { spc.ReportDiagnostic(Diagnostic.Create(MissingInput, Location.None, corePath + " (WebUiCore)")); return; }

            PageComposer.Result r;
            try
            {
                r = PageComposer.Compose(shellText, profileText, coreText, shellPath, profilePath, ns, cls, opts.components, componentFiles);
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

        static bool IsUnder(string path, string root)
        {
            return path.Length > root.Length && path[root.Length] == Path.DirectorySeparatorChar
                && path.StartsWith(root, Path.DirectorySeparatorChar == '/' ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        }

        static bool IsUnderAny(string path, string[] roots, out string rel)
        {
            foreach (string root in roots)
                if (IsUnder(path, root)) { rel = path.Substring(root.Length + 1); return true; }
            rel = null;
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
