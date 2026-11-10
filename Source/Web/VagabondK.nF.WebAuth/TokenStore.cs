using System;
using System.Security.Cryptography;
using System.Text;

namespace VagabondK.nF.WebAuth
{
    /// <summary>
    /// 메모리 기반 리프레시 토큰 저장소입니다. 평문이 아닌 HMAC 해시로 보관하며,
    /// 교환(rotate)마다 이전 토큰이 즉시 폐기됩니다. 재부팅 시 모든 토큰이 사라집니다.
    /// </summary>
    /// <remarks>
    /// 만료 계산은 <see cref="Environment.TickCount64"/> 기반이라 재부팅 후에도
    /// 과거 토큰이 되살아나지 않도록 저장소를 RAM-only로 유지합니다.
    /// </remarks>
    public sealed class TokenStore
    {
        /// <summary>기본 리프레시 토큰 유효 시간(30일)입니다.</summary>
        public const long DefaultLifetimeMilliseconds = 30L * 24L * 60L * 60L * 1000L;
        /// <summary>기본 최대 동시 리프레시 토큰 수입니다.</summary>
        public const int DefaultMaxTokens = 4;

        private readonly object Sync = new object();
        private readonly byte[] storeKey;
        private string[] hashes;
        private long[] expiresAt;
        private int count;

        /// <summary>기본값(4개·30일)으로 저장소를 만듭니다.</summary>
        public TokenStore()
            : this(DefaultMaxTokens, DefaultLifetimeMilliseconds)
        {
        }

        /// <summary>동시 토큰 수와 유효 시간을 지정해 저장소를 만듭니다.</summary>
        /// <param name="maxTokens">유지할 최대 토큰 수입니다. 초과하면 가장 오래된 토큰이 밀려납니다.</param>
        /// <param name="lifetimeMilliseconds">발급 후 토큰이 유효한 기간(밀리초)입니다.</param>
        /// <exception cref="ArgumentOutOfRangeException">값이 0 이하인 경우.</exception>
        public TokenStore(int maxTokens, long lifetimeMilliseconds)
        {
            if (maxTokens <= 0) throw new ArgumentOutOfRangeException("maxTokens");
            if (lifetimeMilliseconds <= 0) throw new ArgumentOutOfRangeException("lifetimeMilliseconds");

            // 부팅마다 새 랜덤 키: RAM 덤프에 표가 노출되어도 토큰 자체는 복원되지 않습니다.
            storeKey = PasswordHasher.Entropy.Bytes(32);
            hashes = new string[maxTokens];
            expiresAt = new long[maxTokens];
            LifetimeMilliseconds = lifetimeMilliseconds;
        }

        /// <summary>발급 후 토큰이 유효한 기간(밀리초)입니다.</summary>
        public long LifetimeMilliseconds { get; set; }

        /// <summary>만료되지 않은 현재 토큰 수입니다.</summary>
        public int Count
        {
            get
            {
                lock (Sync)
                {
                    Prune();
                    return count;
                }
            }
        }

        /// <summary>새 리프레시 토큰을 발급합니다. 자리가 없으면 가장 오래된 토큰부터 회수합니다.</summary>
        /// <returns>16바이트(32헥스) 토큰 문자열입니다. 표에는 HMAC 해시만 남습니다.</returns>
        public string Issue()
        {
            lock (Sync)
            {
                Prune();
                if (count == hashes.Length)
                    RemoveAt(0);

                string token = PasswordHasher.ToHex(PasswordHasher.Entropy.Bytes(16));
                hashes[count] = Hash(token);
                expiresAt[count] = Environment.TickCount64 + LifetimeMilliseconds;
                count++;
                return token;
            }
        }

        /// <summary>토큰을 검증하고 회전합니다: 성공 시 이전 토큰은 즉시 폐기되고 새 토큰이 발급됩니다.</summary>
        /// <param name="token">제출된 리프레시 토큰입니다. <see langword="null"/>이면 실패입니다.</param>
        /// <param name="newToken">성공 시 회전된 새 토큰, 실패 시 <see langword="null"/>입니다.</param>
        /// <returns>유효하고 회전되었으면 <see langword="true"/>입니다.</returns>
        public bool TryExchange(string token, out string newToken)
        {
            newToken = null;
            if (token == null || token.Length == 0)
                return false;

            lock (Sync)
            {
                Prune();
                string candidate = Hash(token);
                for (int i = 0; i < count; i++)
                {
                    if (PasswordHasher.SecureEquals(hashes[i], candidate))
                    {
                        RemoveAt(i);
                        newToken = Issue();
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>토큰을 회수합니다. 제출된 평문 토큰으로 비교합니다.</summary>
        /// <param name="token">회수할 토큰입니다.</param>
        public void Revoke(string token)
        {
            if (token == null || token.Length == 0)
                return;

            lock (Sync)
            {
                string candidate = Hash(token);
                for (int i = 0; i < count; i++)
                {
                    if (PasswordHasher.SecureEquals(hashes[i], candidate))
                    {
                        RemoveAt(i);
                        return;
                    }
                }
            }
        }

        /// <summary>모든 토큰을 회수합니다(비밀번호 변경·해제 시).</summary>
        public void RevokeAll()
        {
            lock (Sync)
            {
                for (int i = 0; i < count; i++)
                    hashes[i] = null;
                count = 0;
            }
        }

        private string Hash(string token)
        {
            return PasswordHasher.ToHex(HMACSHA256.HashData(storeKey, Encoding.UTF8.GetBytes(token)));
        }

        private void Prune()
        {
            long now = Environment.TickCount64;
            for (int i = count - 1; i >= 0; i--)
                if (now >= expiresAt[i])
                    RemoveAt(i);
        }

        private void RemoveAt(int index)
        {
            for (int i = index; i < count - 1; i++)
            {
                hashes[i] = hashes[i + 1];
                expiresAt[i] = expiresAt[i + 1];
            }
            hashes[--count] = null;
        }
    }
}
