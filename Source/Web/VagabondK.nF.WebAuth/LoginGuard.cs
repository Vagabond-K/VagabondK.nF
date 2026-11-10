using System;

namespace VagabondK.nF.WebAuth
{
    /// <summary>요청이 인증을 필요로 하는지, 통과하는지 거부되는지를 나타냅니다.</summary>
    public enum AccessDecision
    {
        /// <summary>인증이 꺼져 있어 항상 통과합니다(비밀번호 미설정 기본 상태).</summary>
        Open,
        /// <summary>유효한 세션 또는 API 키로 통과합니다.</summary>
        Allowed,
        /// <summary>자격이 없어 거부합니다(HTTP 401).</summary>
        Denied,
        /// <summary>로그인 실패 잠금 상태라 거부합니다(HTTP 429).</summary>
        LockedOut
    }

    /// <summary>로그인 시도 결과를 나타냅니다.</summary>
    public enum LoginResult
    {
        /// <summary>성공하여 세션 토큰을 발급했습니다.</summary>
        Success,
        /// <summary>비밀번호가 일치하지 않습니다.</summary>
        BadPassword,
        /// <summary>실패 잠금 대기 중입니다.</summary>
        LockedOut,
        /// <summary>인증 기능이 꺼져 있어 로그인이 필요 없습니다.</summary>
        Disabled
    }

    /// <summary>
    /// 단일 관리자 비밀번호 + 메모리 세션 + 정적 API 키 + 실패 잠금을 묶은 웹 인증 파사드입니다.
    /// </summary>
    /// <remarks>
    /// 저장하는 것은 <see cref="PasswordHash"/>(해시 문자열)과 <see cref="ApiKey"/>뿐이며
    /// 영속화(파일 저장)는 호출자(nanoGate) 책임입니다. 재부팅 시 세션은 사라지지만
    /// 해시와 키는 호출자가 다시 로드하면 그대로 유효합니다.
    /// </remarks>
    public sealed class LoginGuard
    {
        private readonly object Sync = new object();
        private readonly SessionStore sessions;
        private readonly TokenStore tokens;

        private int failedAttempts;
        private long lockUntilTick;

        /// <summary>잠금 해제 전 허용되는 연속 로그인 실패 횟수입니다.</summary>
        public const int MaxFailedAttempts = 5;
        /// <summary>임계 실패 후 로그인 시도를 거부하는 기간(60초)입니다.</summary>
        public const long LockoutMilliseconds = 60L * 1000L;

        /// <summary>기본 세션 저장소(8개·30분)로 파사드를 만듭니다.</summary>
        public LoginGuard()
        {
            sessions = new SessionStore();
            tokens = new TokenStore();
        }

        /// <summary>지정 세션 저장소로 파사드를 만듭니다.</summary>
        /// <param name="sessions">사용할 세션 저장소입니다.</param>
        /// <exception cref="ArgumentNullException"><paramref name="sessions"/>가 <see langword="null"/>인 경우.</exception>
        public LoginGuard(SessionStore sessions)
        {
            if (sessions == null) throw new ArgumentNullException("sessions");
            this.sessions = sessions;
            tokens = new TokenStore();
        }

        /// <summary>저장된 관리자 비밀번호 해시 문자열입니다. 비어 있으면(<see langword="null"/> 또는 "") 인증이 꺼집니다.</summary>
        /// <remarks>설정 값을 바꾸면 기존 세션이 모두 회수되고 실패 카운터가 초기화됩니다.</remarks>
        public string PasswordHash
        {
            get { return passwordHash; }
            set
            {
                lock (Sync)
                {
                    passwordHash = value;
                    sessions.RevokeAll();
                    tokens.RevokeAll();
                    failedAttempts = 0;
                    lockUntilTick = 0;
                }
            }
        }
        private string passwordHash;

        /// <summary>자동화·기계 간 호출용 정적 API 키입니다. 비어 있으면 사용 안 함입니다.</summary>
        public string ApiKey
        {
            get { return apiKey; }
            set { lock (Sync) apiKey = value; }
        }
        private string apiKey;

        /// <summary>세션 저장소입니다(로그아웃·상태 조회용).</summary>
        public SessionStore Sessions
        {
            get { return sessions; }
        }

        /// <summary>리프레시 토큰 저장소입니다(<see cref="TryLogin(string, out string, out string)"/>·<see cref="TryRefresh"/>용).</summary>
        public TokenStore Tokens
        {
            get { return tokens; }
        }

        /// <summary>관리자 비밀번호 또는 API 키가 설정되어 인증이 활성화된 상태인지 나타냅니다.</summary>
        public bool IsEnabled
        {
            get { lock (Sync) return (passwordHash != null && passwordHash.Length > 0) || (apiKey != null && apiKey.Length > 0); }
        }

        /// <summary>현재 로그인 시도가 실패 잠금 상태인지 나타냅니다.</summary>
        public bool IsLockedOut
        {
            get { lock (Sync) return Environment.TickCount64 < lockUntilTick; }
        }

        /// <summary>신규 비밀번호로 해시를 만들어 설정합니다(세션 전부 회수). 비어 있으면 비밀번호 인증만 해제합니다.</summary>
        /// <param name="newPassword">설정할 평문 비밀번호입니다. 비어 있으면 인증을 해제합니다.</param>
        public void SetPassword(string newPassword)
        {
            PasswordHash = string.IsNullOrEmpty(newPassword) ? null : PasswordHasher.Create(newPassword);
        }

        /// <summary>관리자 비밀번호로 로그인을 시도하고 성공 시 세션 토큰을 발급합니다.</summary>
        /// <param name="password">평문 비밀번호입니다.</param>
        /// <param name="sessionToken">성공 시 발급된 토큰, 실패 시 <see langword="null"/>입니다.</param>
        /// <returns>결과 열거값입니다.</returns>
        public LoginResult TryLogin(string password, out string sessionToken)
        {
            sessionToken = null;
            lock (Sync)
            {
                if (passwordHash == null || passwordHash.Length == 0)
                    return LoginResult.Disabled;
                if (Environment.TickCount64 < lockUntilTick)
                    return LoginResult.LockedOut;

                if (PasswordHasher.Verify(password ?? string.Empty, passwordHash))
                {
                    failedAttempts = 0;
                    lockUntilTick = 0;
                    sessionToken = sessions.Issue();
                    return LoginResult.Success;
                }

                failedAttempts++;
                if (failedAttempts >= MaxFailedAttempts)
                {
                    lockUntilTick = Environment.TickCount64 + LockoutMilliseconds;
                    failedAttempts = 0;
                }
                return LoginResult.BadPassword;
            }
        }

        /// <summary>비밀번호 로그인을 시도하고 성공 시 세션(액세스) 토큰과 리프레시 토큰을 함께 발급합니다.</summary>
        /// <param name="password">평문 비밀번호입니다.</param>
        /// <param name="sessionToken">성공 시 발급된 액세스 토큰, 실패 시 <see langword="null"/>입니다.</param>
        /// <param name="refreshToken">성공 시 발급된 리프레시 토큰, 실패 시 <see langword="null"/>입니다.</param>
        /// <returns>결과 열거값입니다.</returns>
        public LoginResult TryLogin(string password, out string sessionToken, out string refreshToken)
        {
            LoginResult result = TryLogin(password, out sessionToken);
            refreshToken = result == LoginResult.Success ? tokens.Issue() : null;
            return result;
        }

        /// <summary>리프레시 토큰을 새 액세스+리프레시 쌍으로 교환합니다(회전: 이전 리프레시 즉시 폐기).</summary>
        /// <remarks>리프레시 토큰은 128비트 엔트로피라 무차별 대입 대상이 되지 않으며, 잠금 상태와 무관하게 교환됩니다.</remarks>
        /// <param name="refreshToken">제출된 리프레시 토큰입니다.</param>
        /// <param name="sessionToken">성공 시 새 액세스 토큰입니다.</param>
        /// <param name="newRefreshToken">성공 시 회전된 새 리프레시 토큰입니다.</param>
        /// <returns>유효한 리프레시 토큰이었으면 <see langword="true"/>입니다.</returns>
        public bool TryRefresh(string refreshToken, out string sessionToken, out string newRefreshToken)
        {
            string rotated;
            if (!tokens.TryExchange(refreshToken, out rotated))
            {
                sessionToken = null;
                newRefreshToken = null;
                return false;
            }

            sessionToken = sessions.Issue();
            newRefreshToken = rotated;
            return true;
        }

        /// <summary>
        /// 요청 헤더 정보로 접근을 판정합니다.
        /// </summary>
        /// <param name="sessionToken">쿠키 또는 세션 헤더에서 추출한 토큰(<see langword="null"/> 가능)입니다.</param>
        /// <param name="providedApiKey">API 키 헤더 값(<see langword="null"/> 가능)입니다.</param>
        /// <returns>접근 판정입니다. 인증 미설정 시 <see cref="AccessDecision.Open"/>입니다.</returns>
        public AccessDecision CheckAccess(string sessionToken, string providedApiKey)
        {
            lock (Sync)
            {
                bool hasPassword = passwordHash != null && passwordHash.Length > 0;
                bool hasApiKey = apiKey != null && apiKey.Length > 0;
                if (!hasPassword && !hasApiKey)
                    return AccessDecision.Open;
                if (sessions.Validate(sessionToken))
                    return AccessDecision.Allowed;
                if (hasApiKey && PasswordHasher.SecureEquals(providedApiKey, apiKey))
                    return AccessDecision.Allowed;
                if (Environment.TickCount64 < lockUntilTick)
                    return AccessDecision.LockedOut;
                return AccessDecision.Denied;
            }
        }

        /// <summary>남은 실패 허용 횟수를 반환합니다(잠금 카운트다운 표시용).</summary>
        public int RemainingAttempts
        {
            get { lock (Sync) return MaxFailedAttempts - failedAttempts; }
        }

        /// <summary>실패 잠금 해제까지 남은 초입니다(0이면 잠금이 아님).</summary>
        public int LockoutSecondsRemaining
        {
            get
            {
                lock (Sync)
                {
                    long left = lockUntilTick - Environment.TickCount64;
                    return left <= 0 ? 0 : (int)((left + 999) / 1000);
                }
            }
        }
    }
}
