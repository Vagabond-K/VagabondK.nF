using System;

using static VagabondK.nF.DNP3.Interop.Program;

namespace VagabondK.nF.DNP3.Interop
{
    // 고정 난수 시드로 Application 문법을 변조하고 이어서 프레임 한 비트를 손상시켜 응답 크기 제한과 CRC 거부를 확인합니다.
    internal static class MalformedCases
    {
        public static void Run(int iterations, uint seed)
        {
            var database = new Dnp3Database(new[] {
                new Dnp3PointDefinition(Dnp3PointType.Analog, 0, Dnp3Value.Analog(250)),
                new Dnp3PointDefinition(Dnp3PointType.Binary, 0, Dnp3Value.Bits(1))
            });
            var dnp = new Dnp3Station(database, 1, 2);
            var random = new Random(unchecked((int)seed));
            int count = Math.Max(2000, iterations * 16), rejected = 0;
            long now = 1000;
            for (int i = 0; i < count; i++)
            {
                var payload = new byte[random.Next(3, 251)];
                random.NextBytes(payload);
                payload[0] = (byte)(0xc0 | (i & 63));
                payload[1] = (byte)(0xc0 | (i & 15));
                byte[] frame = Frame(payload);
                now += 10;
                try
                {
                    byte[] response = dnp.Process(frame, now);
                    Check(response == null || response.Length <= 292, "DNP3 grammar fuzz response bounds: seed=" + seed + ", i=" + i);
                }
                catch (Dnp3ProtocolException) { }
                frame[random.Next(frame.Length)] ^= (byte)(1 << random.Next(8));
                try
                {
                    byte[] corrupt = dnp.Process(frame, now);
                    Check(corrupt == null, "Single-bit-corrupted DNP3 frame was answered: seed=" + seed + ", i=" + i);
                }
                catch (Dnp3ProtocolException) { }
                rejected++;
                try
                {
                    byte[] poll = dnp.Poll(now);
                    Check(poll == null || poll.Length <= 292, "DNP3 poll bounds after fuzz: seed=" + seed + ", i=" + i);
                }
                catch (TimeoutException)
                {
                    dnp.ResetConnection();
                }
            }
            Console.WriteLine("MALFORMED INPUT PASS: " +
                count + " valid-CRC DNP3 grammar payloads plus " + rejected + " CRC-corrupt frames; seed=" + seed + ". In-process fixtures, not conformance.");
        }

        private static byte[] Frame(byte[] payload)
        {
            var frame = new byte[10 + payload.Length + 2 * ((payload.Length + 15) / 16)];
            frame[0] = 5;
            frame[1] = 0x64;
            frame[2] = (byte)(payload.Length + 5);
            frame[3] = 0xc4;
            frame[4] = 1;
            frame[6] = 2;
            Crc(frame, 0, 8);
            for (int offset = 0, p = 10; offset < payload.Length;)
            {
                int count = Math.Min(16, payload.Length - offset);
                Array.Copy(payload, offset, frame, p, count);
                Crc(frame, p, count);
                offset += count;
                p += count + 2;
            }
            return frame;
        }

        private static void Crc(byte[] bytes, int offset, int length)
        {
            ushort crc = Dnp3Link.Crc(bytes, offset, length);
            bytes[offset + length] = (byte)crc;
            bytes[offset + length + 1] = (byte)(crc >> 8);
        }
    }
}
