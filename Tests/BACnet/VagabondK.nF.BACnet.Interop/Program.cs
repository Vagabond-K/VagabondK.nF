using System;

namespace VagabondK.nF.BACnet.Interop
{
    /// <summary>독립 BACnet 스택(System.IO.BACnet 4.0.0)으로 장치 구현을 교차 검증하는 상호 운용 테스트 진입점입니다.</summary>
    /// <remarks>
    /// 기본 실행은 루프백 UDP에서 <see cref="BacnetCases"/>(정상/오류/스트레스)와 <see cref="MalformedCases"/>(잘린·변조 프레임)를 수행합니다.
    /// <c>--stress N</c>, <c>--seed S</c>로 반복 횟수와 난수 시드를 바꿀 수 있습니다.
    /// <c>--bacnet-hardware &lt;보드 IPv4&gt; &lt;반복&gt;</c>은 BACnet IPHost 샘플을 실행 중인 보드를 UDP 47808로 직접 검사합니다(같은 LAN, 방화벽 허용 필요).
    /// </remarks>
    internal static class Program
    {
        internal static void Check(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }

        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 3 && args[0] == "--bacnet-hardware")
                {
                    BacnetHardwareCases.Run(args[1], int.Parse(args[2]));
                    return 0;
                }
                int iterations = 14;
                uint seed = 20261024;
                for (int i = 0; i < args.Length; i++)
                {
                    Check(i + 1 < args.Length, "Expected --stress COUNT, --seed UINT or --bacnet-hardware IP COUNT.");
                    string option = args[i], value = args[++i];
                    if (option == "--stress")
                        Check(int.TryParse(value, out iterations) && iterations >= 1 && iterations <= 1000000,
                            "Stress iterations must be 1..1000000.");
                    else if (option == "--seed")
                        Check(uint.TryParse(value, out seed) && seed != 0, "Seed must be a nonzero uint.");
                    else
                        throw new ArgumentException("Unknown validation option: " + option);
                }
                Console.WriteLine("PROTOCOL RUN: stress iterations=" + iterations + ", seed=" + seed);
                BacnetCases.Run(iterations, seed);
                MalformedCases.Run(iterations, seed);
                Console.WriteLine("PROTOCOL VERIFICATION PASS: no mismatches in this run.");
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
