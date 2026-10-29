using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

// 사전 준비된 원본 OpenDNP3 Outstation을 실행하여 보드 Master의 제어 순서·UTC 동기화·완료 표식을 검사합니다.
// COM6 또는 지정 TCP 주소의 격리된 테스트 장치만 사용하며 최종 완료 읽기가 끝날 시간을 두고 피어를 종료합니다.
internal static class IndependentOutstation
{
    internal static int Run(string target)
    {
        Process process = null;
        try
        {
            string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\.."));
            string executable = Path.Combine(root, @"bin\protocol-tools\master-build\Release\VagabondKDnp3Outstation.exe");
            IndependentMaster.RequireOriginal(executable);
            process = Process.Start(new ProcessStartInfo(executable, target == "COM6" ?
                "COM6 physical" : "20000 physical " + target)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            var errors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            int operations = 0;
            bool time = false;
            while (true)
            {
                string line = process.StandardOutput.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult();
                if (line == null)
                    throw new IOException("OpenDNP3 exited before MCU completion.");
                Console.WriteLine("OpenDNP3: " + line);
                if (line == "READY")
                    Console.WriteLine("READY: unmodified OpenDNP3 native " + target + " Outstation");
                if (line.StartsWith("PHYSICAL OPERATIONS ", StringComparison.Ordinal))
                {
                    int current = int.Parse(line.Substring("PHYSICAL OPERATIONS ".Length), CultureInfo.InvariantCulture);
                    if (current != operations + 1)
                        throw new InvalidOperationException("Independent operation sequence.");
                    operations = current;
                }
                if (line.StartsWith("TIME ", StringComparison.Ordinal))
                {
                    long utc = long.Parse(line.Substring(5), CultureInfo.InvariantCulture);
                    if (utc < 1700000001234 || utc > 1700000031234)
                        throw new InvalidOperationException("Independent UTC sync value.");
                    time = true;
                }
                if (line != "PHYSICAL COMPLETE")
                    continue;
                if (operations != VagabondK.nF.DNP3.SerialHost.SerialValidation.ExpectedOperations || !time)
                    throw new InvalidOperationException("Independent control/time totals.");
                // MCU가 완료 값을 다시 읽을 때까지 피어를 실행 상태로 둡니다.
                Thread.Sleep(5000);
                process.StandardInput.WriteLine("quit");
                process.StandardInput.Flush();
                if (!process.WaitForExit(5000))
                    throw new TimeoutException("OpenDNP3 did not shut down.");
                string error = errors.GetAwaiter().GetResult();
                if (process.ExitCode != 0)
                    throw new IOException("OpenDNP3 failed: " + error);
                Console.WriteLine("INDEPENDENT PHYSICAL OUTSTATION PASS: " + target +
                    "; unmodified OpenDNP3; " + operations +
                    " operations, UTC sync, completion marker. MCU console must separately confirm all assertions.");
                return 0;
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("INDEPENDENT PHYSICAL OUTSTATION FAIL: " + error);
            return 1;
        }
        finally
        {
            if (process != null)
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                    process.WaitForExit();
                }
                process.Dispose();
            }
        }
    }
}
