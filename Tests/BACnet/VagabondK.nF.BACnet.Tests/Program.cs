using System;

namespace VagabondK.nF.BACnet.Tests
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

        internal static byte[] Bacnet(byte[] apdu)
        {
            var frame = new byte[apdu.Length + 6];
            frame[0] = 0x81;
            frame[1] = 10;
            frame[2] = (byte)(frame.Length >> 8);
            frame[3] = (byte)frame.Length;
            frame[4] = 1;
            frame[5] = 4;
            Array.Copy(apdu, 0, frame, 6, apdu.Length);
            return frame;
        }
    }

    /// <summary>BACnet 장치와 IP 세션 단위 테스트 진입점입니다. 외부 장비 없이 메모리와 루프백 UDP만 사용합니다.</summary>
    /// <remarks>soak 인수가 있으면 <c>SoakRunner</c>가 정상 교환·프레이밍·IP 세션 검사를 반복하고, 없으면 각 검사를 한 번 실행합니다.</remarks>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                if (SoakRunner.TryRun(args, "BACnet", BacnetIpCases.SoakNormal,
                    BacnetDeviceCases.Framing, BacnetIpCases.Run)) return 0;
                if (args.Length != 0)
                    throw new ArgumentException("No options are supported.");
                BacnetDeviceCases.Run();
                BacnetIpCases.Run();
                Console.WriteLine("PROTOCOL TESTS PASS: " + Cases.Checks + " assertions.");
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
