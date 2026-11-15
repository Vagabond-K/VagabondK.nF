using System;
using System.Security.Cryptography;
using System.Text;

namespace VagabondK.nF.Web
{
    /// <summary>솔트+HMAC-SHA256 반복 해시로 관리자 비밀번호를 저장·검증합니다.</summary>
    /// <remarks>저장 형식은 <c>a1$&lt;saltHex&gt;$&lt;hashHex&gt;</c>입니다.
    /// 기기에는 비밀번호 원문이 아니라 이 문자열만 저장됩니다.</remarks>
    public static class PasswordHasher
    {
        /// <summary>하드웨어 RNG가 없는 임베디드 환경의 엔트로피를 모아 난수 바이트를 만드는 내부 도구입니다.</summary>
        internal static class Entropy
        {
            // 주의: 중첩 타입의 static 필드 초기자는 nanoCLR에서 정적 메서드 호출 경로로
            // .cctor가 보장되지 않아 null로 남는 경우가 확인되었습니다(장치 ArgumentNullException).
            // 초기자에 의존하지 말고 Bytes() 진입 시점에 방어 초기화합니다.
            private static object Sync;
            private static ulong counter;

            /// <summary>시드 혼합을 HMAC-SHA256으로 확장한 예측 어려운 바이트를 반환합니다.</summary>
            /// <remarks>엔트로피 원천은 <see cref="Guid.NewGuid"/>(플랫폼 RNG 기반)와 <see cref="Environment.TickCount64"/>입니다.
            /// 암호학적으로 강한 RNG는 아니지만 세션 토큰·솔트 생성 용도로 충분합니다.
            /// 해시 확장에는 반드시 정적 <see cref="HMACSHA256.HashData(byte[], byte[])"/>를 사용해야 합니다.
            /// nanoFramework의 <c>new HMACSHA256(key)</c> 인스턴스 경로는 일부 기기(ESP32)에서 항상 0을 반환하는 결함이 확인되었습니다.</remarks>
            public static byte[] Bytes(int count)
            {
                if (count <= 0) throw new ArgumentOutOfRangeException("count");
                byte[] output = new byte[count];
                int written = 0;
                if (Sync == null) Sync = new object();  // .cctor 미실행 방어(위 비고 참조)
                lock (Sync)
                {
                    byte[] seed = Guid.NewGuid().ToByteArray();
                    long tick = Environment.TickCount64;
                    seed[0] ^= (byte)tick;
                    seed[1] ^= (byte)(tick >> 8);
                    seed[2] ^= (byte)(tick >> 16);
                    seed[3] ^= (byte)(tick >> 24);
                    {
                        byte[] block = null;
                        int offset = 0;
                        while (written < count)
                        {
                            if (block == null || offset == block.Length)
                            {
                                block = HMACSHA256.HashData(seed, BitConverter.GetBytes(counter++));
                                offset = 0;
                            }
                            int take = count - written < block.Length - offset ? count - written : block.Length - offset;
                            Array.Copy(block, offset, output, written, take);
                            written += take;
                            offset += take;
                        }
                    }
                }
                return output;
            }
        }

        /// <summary>형식 버전 접두사입니다.</summary>
        private const string Prefix = "a1$";
        /// <summary>솔트 길이(바이트)입니다.</summary>
        private const int SaltBytes = 16;
        /// <summary>HMAC 반복 횟수입니다. ESP32급 기기에서 수백 밀리초 수준입니다.</summary>
        public const int Iterations = 1000;

        /// <summary>비밀번호로부터 저장용 해시 문자열을 만듭니다.</summary>
        /// <param name="password">평문 비밀번호입니다.</param>
        /// <returns><c>a1$솔트헥스$해시헥스</c> 형식 문자열입니다.</returns>
        /// <exception cref="ArgumentException"><paramref name="password"/>가 비어 있는 경우.</exception>
        public static string Create(string password)
        {
            if (password == null || password.Length == 0) throw new ArgumentException("Password is required.", "password");
            byte[] salt = Entropy.Bytes(SaltBytes);
            byte[] hash = Derive(password, salt);
            return Prefix + ToHex(salt) + "$" + ToHex(hash);
        }

        /// <summary>비밀번호가 저장된 해시 문자열과 일치하는지 상수 시간으로 비교합니다.</summary>
        /// <param name="password">평문 비밀번호입니다.</param>
        /// <param name="stored"><see cref="Create"/>가 만든 저장 문자열입니다.</param>
        /// <returns>일치하면 <see langword="true"/>입니다.</returns>
        public static bool Verify(string password, string stored)
        {
            if (password == null || stored == null) return false;
            if (stored.Length <= Prefix.Length || !stored.StartsWith(Prefix)) return false;
            int second = stored.IndexOf('$', Prefix.Length);
            if (second < 0) return false;
            byte[] salt;
            byte[] expected;
            try
            {
                salt = FromHex(stored.Substring(Prefix.Length, second - Prefix.Length));
                expected = FromHex(stored.Substring(second + 1));
            }
            catch (Exception)
            {
                return false;
            }
            if (salt == null || expected == null || salt.Length == 0 || expected.Length == 0) return false;
            return SecureEquals(Derive(password, salt), expected);
        }

        private static byte[] Derive(string password, byte[] salt)
        {
            byte[] current = Encoding.UTF8.GetBytes(password);
            for (int i = 0; i < Iterations; i++)
                current = HMACSHA256.HashData(salt, current);
            return current;
        }

        /// <summary>두 바이트 배열을 상수 시간 비교합니다.</summary>
        internal static bool SecureEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null) return false;
            int diff = a.Length ^ b.Length;
            int len = a.Length < b.Length ? a.Length : b.Length;
            for (int i = 0; i < len; i++)
                diff |= a[i] ^ b[i];
            return diff == 0;
        }

        /// <summary>두 문자열을 상수 시간 비교합니다.</summary>
        internal static bool SecureEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            int diff = a.Length ^ b.Length;
            int len = a.Length < b.Length ? a.Length : b.Length;
            for (int i = 0; i < len; i++)
                diff |= a[i] ^ b[i];
            return diff == 0;
        }

        /// <summary>바이트 배열을 소문자 16진수 문자열로 변환합니다.</summary>
        internal static string ToHex(byte[] data)
        {
            StringBuilder sb = new StringBuilder(data.Length * 2);
            for (int i = 0; i < data.Length; i++)
            {
                sb.Append(HexChar((byte)(data[i] >> 4)));
                sb.Append(HexChar((byte)(data[i] & 0x0F)));
            }
            return sb.ToString();
        }

        /// <summary>16진수 문자열을 바이트 배열로 변환합니다. 형식이 틀리면 <see langword="null"/>입니다.</summary>
        internal static byte[] FromHex(string hex)
        {
            if (hex == null || (hex.Length & 1) != 0) return null;
            byte[] data = new byte[hex.Length / 2];
            for (int i = 0; i < data.Length; i++)
            {
                int hi = HexValue(hex[2 * i]);
                int lo = HexValue(hex[2 * i + 1]);
                if (hi < 0 || lo < 0) return null;
                data[i] = (byte)((hi << 4) | lo);
            }
            return data;
        }

        private static char HexChar(byte nibble)
        {
            return nibble < 10 ? (char)('0' + nibble) : (char)('a' + nibble - 10);
        }

        private static int HexValue(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }
    }
}
