using System;

namespace VagabondK.nF.DNP3.Tests
{
    public static class Cases
    {
        public static int Checks;

        public static void Check(bool condition)
        {
            Checks++;
            if (!condition)
                throw new InvalidOperationException("Protocol assertion " + Checks);
        }

    }

    // 기본 실행은 스택·로컬 TCP·가상 Serial을 검사합니다. --dnp3-serial은 Serial 검사만, --soak는 장시간 회귀를 수행합니다.
    // 물리 COM 포트를 열지 않으므로 이 결과를 보드/RS485 타이밍 검증으로 해석하지 않습니다.
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                if (SoakRunner.TryRun(args, "DNP3", Dnp3TcpCases.SoakNormal,
                    Dnp3TcpCases.SoakMalformed, Dnp3TcpCases.SoakLifecycle)) return 0;
                if (args.Length > 1 || (args.Length == 1 && args[0] != "--dnp3-serial"))
                    throw new ArgumentException("Supported option: --dnp3-serial");
                if (args.Length == 0)
                {
                    Dnp3StackCases.Run();
                    Dnp3TcpCases.Run();
                }
                Dnp3SerialCases.Run();
                Dnp3SerialTimingCases.Run();
                Console.WriteLine("PROTOCOL TESTS PASS: " + Cases.Checks + " assertions; no physical port opened.");
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
