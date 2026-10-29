using System;

using static VagabondK.nF.BACnet.Interop.Program;

namespace VagabondK.nF.BACnet.Interop
{
    /// <summary>잘린 쓰기 요청과 난수 변조 프레임이 쓰기 ACK나 설정 변경 없이 거부되는지 검사합니다.</summary>
    internal static class MalformedCases
    {
        public static void Run(int iterations, uint seed)
        {
            var store = Fixtures.CreateBacnet(2);
            var bacnet = new BacnetDevice(store, 1, 0, "Fuzz");
            byte[] write = {
                0x81, 10, 0, 24, 1, 4, 0, 5, 8, 15, 0x0c, 0, 0x80, 0, 1, 0x19, 85,
                0x3e, 0x44, 0x43, 0xc8, 0, 0, 0x3f
            };
            uint initial = store.Get(Fixtures.Limit);
            for (int length = 0; length < write.Length; length++)
            {
                var truncated = new byte[length];
                Array.Copy(write, truncated, length);
                if (length >= 4)
                    truncated[3] = (byte)length;
                try
                {
                    byte[] reply = bacnet.Process(truncated);
                    Check(reply != null && reply[6] != 0x20, "Truncated BACnet write was acknowledged at length=" + length);
                }
                catch (BacnetProtocolException) { }
                store.Commit();
                Check(store.Get(Fixtures.Limit) == initial, "Truncated BACnet write mutated settings at length=" + length);
            }
            var random = new Random(unchecked((int)seed));
            int count = Math.Max(2000, iterations * 16);
            for (int i = 0; i < count; i++)
            {
                var bytes = new byte[random.Next(0, 514)];
                random.NextBytes(bytes);
                try
                {
                    bacnet.Process(bytes);
                }
                catch (BacnetProtocolException) { }
                if (bytes.Length >= 8)
                {
                    bytes[0] = 0x81;
                    bytes[1] = 10;
                    bytes[2] = (byte)(bytes.Length >> 8);
                    bytes[3] = (byte)bytes.Length;
                    bytes[4] = 1;
                    bytes[5] = 4;
                    bytes[6] = 0;
                    bytes[7] = 5;
                    try
                    {
                        bacnet.Process(bytes);
                    }
                    catch (BacnetProtocolException) { }
                }
            }
            Console.WriteLine("MALFORMED INPUT PASS: " + write.Length + " truncated BACnet writes with unchanged settings; " +
                count + " random/grammar BACnet payloads; seed=" + seed + ". In-process fixtures, not conformance.");
        }
    }
}
