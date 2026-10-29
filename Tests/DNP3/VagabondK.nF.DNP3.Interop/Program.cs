using System;
using System.IO;
using System.Diagnostics;

namespace VagabondK.nF.DNP3.Interop
{
    // 사전 준비된 네이티브 OpenDNP3 도구와 양방향 상호운용을 검사합니다. 외부 실행 파일의 Decoder 정체성을 먼저 확인합니다.
    // patched-ieee 및 알려진 한계의 report 모드는 진단용이며 원본 스택의 무조건적 상호운용 성공으로 표시하지 않습니다.
    internal static class Program
    {
        internal static int KnownInteropLimitations;
        internal static bool PatchedIeee;

        private static void VerifyDecoder(string executable)
        {
            using var process = Process.Start(new ProcessStartInfo(executable, "--decoder-identity")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            try
            {
                var output = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                Check(process.WaitForExit(5000), "Decoder identity probe timed out.");
                Check(process.ExitCode == 0 && errors.GetAwaiter().GetResult().Length == 0 &&
                    output.GetAwaiter().GetResult().Trim() == (PatchedIeee ? "DECODER PATCHED_IEEE" : "DECODER ORIGINAL"),
                    "Decoder identity mismatch; never label a patched tool as original OpenDNP3.");
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                    process.WaitForExit();
                }
            }
        }

        internal static void Check(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }

        private static int Main(string[] args)
        {
            try
            {
                string master = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                        @"..\..\..\..\..\..\bin\protocol-tools\master-build\Release\VagabondKDnp3Master.exe"));
                int iterations = 14;
                uint seed = 20261023;
                bool reportKnownLimits = false;
                for (int i = 0; i < args.Length; i++)
                {
                    Check(i + 1 < args.Length, "Expected --master PATH, --stress COUNT, --seed UINT, --decoder original|patched-ieee or --known-limits fail|report.");
                    string option = args[i], value = args[++i];
                    if (option == "--master")
                        master = Path.GetFullPath(value);
                    else if (option == "--decoder")
                    {
                        Check(value == "original" || value == "patched-ieee", "Decoder policy must be original or patched-ieee.");
                        PatchedIeee = value == "patched-ieee";
                    }
                    else if (option == "--stress")
                    {
                        Check(int.TryParse(value, out iterations) && iterations >= 1 && iterations <= 1000000,
                            "Stress iterations must be 1..1000000.");
                    }
                    else if (option == "--seed")
                        Check(uint.TryParse(value, out seed) && seed != 0, "Seed must be a nonzero uint.");
                    else if (option == "--known-limits")
                    {
                        Check(value == "fail" || value == "report", "Known-limit policy must be fail or report.");
                        reportKnownLimits = value == "report";
                    }
                    else
                        throw new ArgumentException("Unknown validation option: " + option);
                }
                Check(File.Exists(master), "Build the independent master first: Build\\Build-Dnp3ValidationMaster.ps1");
                VerifyDecoder(master);
                VerifyDecoder(Path.Combine(Path.GetDirectoryName(master), "VagabondKDnp3Outstation.exe"));
                Console.WriteLine(PatchedIeee ? "DECODER: TEST-ONLY PATCHED IEEE; not unmodified OpenDNP3 interoperability." :
                    "DECODER: ORIGINAL OpenDNP3 / ser4cpp.");
                Console.WriteLine("PROTOCOL RUN: stress iterations=" + iterations + ", seed=" + seed);
                MalformedCases.Run(iterations, seed);
                NativeDnpCases.Run(master);
                if (KnownInteropLimitations > 0)
                {
                    Console.WriteLine("VERIFICATION INCOMPLETE: " + KnownInteropLimitations +
                        " known independent decoder limitations; no unconditional PASS. " +
                        (reportKnownLimits ? "Explicit diagnostic report mode." : "Strict mode exits with code 2."));
                    return reportKnownLimits ? 0 : 2;
                }
                Console.WriteLine(PatchedIeee ? "PATCHED-STACK VERIFICATION PASS: no mismatches; original-stack limitations remain documented." :
                    "PROTOCOL VERIFICATION PASS: no mismatches or known limitations in this run.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
        }
    }
}
