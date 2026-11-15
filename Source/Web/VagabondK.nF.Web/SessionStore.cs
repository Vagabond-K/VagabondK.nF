using System;

namespace VagabondK.nF.Web
{
    /// <summary>메모리 기반 세션 토큰 저장소입니다. 재부팅 시 모든 세션이 사라집니다.</summary>
    public sealed class SessionStore
    {
        /// <summary>기본 세션 유효 시간(30분)입니다.</summary>
        public const long DefaultLifetimeMilliseconds = 30L * 60L * 1000L;
        /// <summary>기본 최대 동시 세션 수입니다.</summary>
        public const int DefaultMaxSessions = 8;

        private readonly object Sync = new object();
        private string[] tokens;
        private long[] expiresAt;
        private int count;

        /// <summary>최대 <see cref="DefaultMaxSessions"/>개, 기본 30분 유효로 저장소를 만듭니다.</summary>
        public SessionStore()
            : this(DefaultMaxSessions, DefaultLifetimeMilliseconds)
        {
        }

        /// <summary>동시 세션 수와 유효 시간을 지정해 저장소를 만듭니다.</summary>
        /// <param name="maxSessions">유지할 최대 세션 수입니다. 초과하면 가장 오래된 세션이 밀려납니다.</param>
        /// <param name="lifetimeMilliseconds">발급 후 세션이 유효한 기간(밀리초)입니다.</param>
        /// <exception cref="ArgumentOutOfRangeException">값이 0 이하인 경우.</exception>
        public SessionStore(int maxSessions, long lifetimeMilliseconds)
        {
            if (maxSessions <= 0) throw new ArgumentOutOfRangeException("maxSessions");
            if (lifetimeMilliseconds <= 0) throw new ArgumentOutOfRangeException("lifetimeMilliseconds");
            tokens = new string[maxSessions];
            expiresAt = new long[maxSessions];
            LifetimeMilliseconds = lifetimeMilliseconds;
        }

        /// <summary>발급 후 세션이 유효한 기간(밀리초)입니다.</summary>
        public long LifetimeMilliseconds { get; set; }

        /// <summary>만료되지 않은 현재 세션 수입니다.</summary>
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

        /// <summary>새 세션 토큰을 발급합니다. 자리가 없으면 가장 오래된 세션부터 회수합니다.</summary>
        /// <returns>16바이트(32헥스) 토큰 문자열입니다.</returns>
        public string Issue()
        {
            lock (Sync)
            {
                Prune();
                if (count == tokens.Length)
                    RemoveAt(0);
                string token = PasswordHasher.ToHex(PasswordHasher.Entropy.Bytes(16));
                tokens[count] = token;
                expiresAt[count] = Environment.TickCount64 + LifetimeMilliseconds;
                count++;
                return token;
            }
        }

        /// <summary>토큰이 유효(존재+미만료)한지 확인합니다. 만료된 토큰은 정리됩니다.</summary>
        /// <param name="token">확인할 토큰입니다. <see langword="null"/>이면 실패입니다.</param>
        /// <returns>유효하면 <see langword="true"/>입니다.</returns>
        public bool Validate(string token)
        {
            if (token == null || token.Length == 0) return false;
            lock (Sync)
            {
                Prune();
                for (int i = 0; i < count; i++)
                    if (PasswordHasher.SecureEquals(tokens[i], token))
                        return true;
                return false;
            }
        }

        /// <summary>토큰을 회수합니다(로그아웃).</summary>
        /// <param name="token">회수할 토큰입니다.</param>
        public void Revoke(string token)
        {
            if (token == null) return;
            lock (Sync)
            {
                for (int i = 0; i < count; i++)
                    if (PasswordHasher.SecureEquals(tokens[i], token))
                    {
                        RemoveAt(i);
                        return;
                    }
            }
        }

        /// <summary>모든 세션을 회수합니다(비밀번호 변경·해제 시).</summary>
        public void RevokeAll()
        {
            lock (Sync)
            {
                for (int i = 0; i < count; i++)
                    tokens[i] = null;
                count = 0;
            }
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
                tokens[i] = tokens[i + 1];
                expiresAt[i] = expiresAt[i + 1];
            }
            tokens[--count] = null;
        }
    }
}
