using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
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
                    shell: Get("webui_shell"), profile: Get("webui_profile"), core: Get("webui_core"));
            });

            var input = files.Combine(options);
            context.RegisterSourceOutput(input, static (spc, pair) => Execute(spc, pair.Left, pair.Right));
        }

        static void Execute(SourceProductionContext spc, ImmutableArray<(string path, string text)> files, (string ns, string cls, string shell, string profile, string core) opts)
        {
            string ns = opts.ns, cls = opts.cls;
            string shellPath = opts.shell, profilePath = opts.profile, corePath = opts.core;

            string shellText = null, profileText = null, coreText = null;
            string shellResolved = Resolve(shellPath), profileResolved = Resolve(profilePath), coreResolved = Resolve(corePath);
            foreach (var f in files)
            {
                string p = Resolve(f.path);
                if (p == shellResolved) shellText = f.text;
                else if (p == profileResolved) profileText = f.text;
                else if (p == coreResolved) coreText = f.text;
            }

            if (shellText == null) { spc.ReportDiagnostic(Diagnostic.Create(MissingInput, Location.None, shellPath + " (WebUiShell)")); return; }
            if (profileText == null) { spc.ReportDiagnostic(Diagnostic.Create(MissingInput, Location.None, profilePath + " (WebUiProfile)")); return; }
            if (coreText == null) { spc.ReportDiagnostic(Diagnostic.Create(MissingInput, Location.None, corePath + " (WebUiCore)")); return; }

            PageComposer.Result r;
            try
            {
                r = PageComposer.Compose(shellText, profileText, coreText, shellPath, profilePath, ns, cls);
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
