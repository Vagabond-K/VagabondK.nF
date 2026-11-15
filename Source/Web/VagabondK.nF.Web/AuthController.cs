using System;
using System.Net;
using nanoFramework.WebServer;

namespace VagabondK.nF.Web
{
    /// <summary>표준 상태 응답에 추가 필드를 병합하는 훅입니다(열린 객체 안에서 호출됨).</summary>
    public delegate void AuthStatusExtrasWriter(JsonWriter w);

    /// <summary>자격 정보 영속화 훅입니다. 성공 시 <see langword="null"/>, 실패 시 실패 사정을 반환합니다.</summary>
    public delegate string AuthCredentialPersist();

    /// <summary>모든 인증 요청 진입점에서 호출되는 관찰 훅입니다.</summary>
    public delegate void AuthRequestObserver(HttpListenerContext context);

    /// <summary>
    /// <c>/api/auth/*</c> 표준 인증 엔드포인트를 제공하는 컨트롤러입니다.
    /// </summary>
    /// <remarks>
    /// 사용: 컨트롤러 배열에 <c>typeof(AuthController)</c>를 등록하고 <see cref="Server"/>·
    /// <see cref="CsrfMarker"/>를 설정한 뒤(생성 후 <c>Start()</c> 전), 네트워크 주소가 정해질 때마다
    /// <see cref="LocalAddress"/>를 갱신하세요. 앱 고유의 상태 필드는 <see cref="StatusExtras"/>,
    /// 비밀번호·API 키 변경의 파일 저장은 <see cref="Persist"/>로 연결합니다.
    /// status·login·logout·token은 <see cref="PublicEndpointAttribute"/>라 미인증에서도 호출 가능하고,
    /// password·apikey는 서버 가드가 자동 차단합니다(fail-closed).
    /// 메시지 문자열은 static 필드이므로 앱 시작에서 원하는 언어로 바꿀 수 있습니다.
    /// </remarks>
    public class AuthController
    {
        /// <summary>세션 토큰 읽기·쿠키 설정에 사용할 서버입니다(필수 설정).</summary>
        public static GuardedWebServer Server;

        /// <summary>POST가 통과해야 할 CSRF 마커 헤더 이름입니다(상태 응답의 csrfHeader로 클라이언트에 전달).</summary>
        public static string CsrfMarker = "x-auth-csrf";

        /// <summary>CSRF Host 검사에 쓸 장치 자기 IP입니다(미설정 시 빈 문자열 = 모든 POST 차단).</summary>
        public static string LocalAddress = "";

        /// <summary>인증 폼 본문 읽기 상한(바이트)입니다.</summary>
        public static int BodyLimit = 2048;

        /// <summary>상태 응답에 병합할 앱 고유 필드 훅(<see langword="null"/> 가능).</summary>
        public static AuthStatusExtrasWriter StatusExtras;

        /// <summary>password·apikey 변경 후 호출되는 영속화 훅(<see langword="null"/>이면 메모리 변경만 적용).</summary>
        public static AuthCredentialPersist Persist;

        /// <summary>6개 엔드포인트 진입점에서 CSRF 검사 전 호출되는 훅(<see langword="null"/> 가능).
        /// 활동 시각 갱신·주소 재동기화 같은 앱 고유의 관측 작업에 연결합니다.</summary>
        public static AuthRequestObserver OnRequest;

        // ---------- 현지화 가능한 메시지({0} = 숫자 자리) ----------

        /// <summary>로그인 잠금 응답(429) 문구. {0} = 남은 초.</summary>
        public static string MsgLocked = "login locked, retry in {0}s.";
        /// <summary>비밀번호 오류 응답(401) 문구. {0} = 남은 시도 횟수.</summary>
        public static string MsgBadPassword = "wrong password, {0} attempts left.";
        /// <summary>인증 비활성 상태에서 로그인을 시도한 응답(400) 문구.</summary>
        public static string MsgDisabled = "authentication is disabled.";
        /// <summary>password 필드가 없을 때(400).</summary>
        public static string MsgNoPasswordField = "password field missing.";
        /// <summary>apikey 필드가 없을 때(400).</summary>
        public static string MsgNoApiKeyField = "apikey field missing.";
        /// <summary>비밀번호 길이 규칙 위반(400).</summary>
        public static string MsgPasswordLength = "password must be 8-63 characters.";
        /// <summary>비밀번호·API 키 문자셋 위반(400).</summary>
        public static string MsgCharset = "only letters, digits and symbols are allowed.";
        /// <summary>API 키 길이 위반(400).</summary>
        public static string MsgApiKeyLength = "apikey must be 64 characters or fewer.";
        /// <summary>grant_type 위반(400).</summary>
        public static string MsgGrantType = "grant_type must be password or refresh_token.";
        /// <summary>리프레시 토큰 교환 실패(401).</summary>
        public static string MsgBadRefresh = "refresh token is invalid, sign in with the password again.";
        /// <summary>영속화 실패(500) 접두 문구. {0} = 훅이 준 사유.</summary>
        public static string MsgPersistFailed = "failed to save credentials: {0}";

        /// <summary>비밀번호 최소 길이(규칙 훅 미제공 시 적용).</summary>
        public static int PasswordMinLength = 8;
        /// <summary>비밀번호 최대 길이.</summary>
        public static int PasswordMaxLength = 63;
        /// <summary>API 키 최대 길이(0이면 길이 검사 없음).</summary>
        public static int ApiKeyMaxLength = 64;

        /// <summary>GET /api/auth/status — 인증 활성화·로그인·잠금 상태와 CSRF 헤더 이름을 알립니다.</summary>
        [Route("api/auth/status")]
        [Method("GET")]
        [PublicEndpoint]
        public void Status(WebServerEventArgs e)
        {
            Notify(e.Context);
            GuardedWebServer server = Server;
            if (server == null) { NotWired(e.Context.Response); return; }

            LoginGuard g = server.Auth;
            bool enabled = g != null && g.IsEnabled;
            bool logged;
            bool lockedOut = false;
            int remaining = LoginGuard.MaxFailedAttempts;
            int lockSec = 0;

            if (g == null)
            {
                logged = true;
            }
            else
            {
                AccessDecision d = g.CheckAccess(
                    server.ReadSessionToken(e.Context.Request),
                    e.Context.Request.Headers[server.ApiKeyHeaderName]);
                logged = d == AccessDecision.Open || d == AccessDecision.Allowed;
                lockedOut = g.IsLockedOut;
                remaining = g.RemainingAttempts;
                lockSec = g.LockoutSecondsRemaining;
            }

            JsonWriter w = new JsonWriter();
            w.BeginObject().Prop("ok", true)
             .Prop("authEnabled", enabled)
             .Prop("loggedIn", logged)
             .Prop("lockedOut", lockedOut)
             .Prop("remaining", remaining)
             .Prop("lockSec", lockSec)
             .Prop("csrfHeader", CsrfMarker);
            AuthStatusExtrasWriter extras = StatusExtras;
            if (extras != null) extras(w);
            w.EndObject();
            WebApi.Json(e.Context.Response, 200, w.ToString());
        }

        /// <summary>POST /api/auth/login — password 폼 필드로 로그인, 성공 시 세션 쿠키를 설정합니다.</summary>
        [Route("api/auth/login")]
        [Method("POST")]
        [PublicEndpoint]
        public void Login(WebServerEventArgs e)
        {
            Notify(e.Context);
            if (CsrfGuard.Check(e.Context, CsrfMarker, LocalAddress)) return;

            GuardedWebServer server = Server;
            if (server == null) { NotWired(e.Context.Response); return; }

            LoginGuard g = server.Auth;
            HttpListenerResponse response = e.Context.Response;
            if (g == null || !g.IsEnabled)
            {
                WebApi.Error(response, 400, MsgDisabled);
                return;
            }

            string password = FormReader.Read(e.Context.Request, BodyLimit, "password");
            string token;
            LoginResult r = g.TryLogin(password ?? string.Empty, out token);
            switch (r)
            {
                case LoginResult.Success:
                    server.SetSessionCookie(response, token);
                    WebApi.Json(response, 200, WebApi.OkJson);
                    return;
                case LoginResult.LockedOut:
                    WebApi.Error(response, 429, Format(MsgLocked, g.LockoutSecondsRemaining));
                    return;
                case LoginResult.Disabled:
                    WebApi.Error(response, 400, MsgDisabled);
                    return;
                default:
                    server.SetSessionCookie(response, null);
                    WebApi.Error(response, 401, Format(MsgBadPassword, g.RemainingAttempts));
                    return;
            }
        }

        /// <summary>POST /api/auth/logout — 현재 세션을 회수하고 쿠키를 지웁니다.</summary>
        [Route("api/auth/logout")]
        [Method("POST")]
        [PublicEndpoint]
        public void Logout(WebServerEventArgs e)
        {
            Notify(e.Context);
            if (CsrfGuard.Check(e.Context, CsrfMarker, LocalAddress)) return;

            GuardedWebServer server = Server;
            if (server == null) { NotWired(e.Context.Response); return; }

            if (server.Auth != null)
                server.Auth.Sessions.Revoke(server.ReadSessionToken(e.Context.Request));
            server.SetSessionCookie(e.Context.Response, null);
            WebApi.Json(e.Context.Response, 200, WebApi.OkJson);
        }

        /// <summary>
        /// POST /api/auth/token — 자동화용 토큰 엔드포인트(OAuth식).
        /// grant_type=password로 액세스+리프레시 쌍을, grant_type=refresh_token으로 회전 교환을 받습니다.
        /// </summary>
        [Route("api/auth/token")]
        [Method("POST")]
        [PublicEndpoint]
        public void Token(WebServerEventArgs e)
        {
            Notify(e.Context);
            if (CsrfGuard.Check(e.Context, CsrfMarker, LocalAddress)) return;

            GuardedWebServer server = Server;
            if (server == null) { NotWired(e.Context.Response); return; }

            LoginGuard g = server.Auth;
            HttpListenerResponse response = e.Context.Response;
            if (g == null || !g.IsEnabled)
            {
                WebApi.Error(response, 400, MsgDisabled);
                return;
            }

            FormReader f = new FormReader(e.Context.Request, BodyLimit);
            string grant = f.Get("grant_type");
            string access, refresh;

            if (grant == "password")
            {
                LoginResult r = g.TryLogin(f.Get("password") ?? string.Empty, out access, out refresh);
                if (r == LoginResult.Success)
                {
                    TokenJson(response, g, access, refresh);
                    return;
                }
                if (r == LoginResult.LockedOut)
                {
                    WebApi.Error(response, 429, Format(MsgLocked, g.LockoutSecondsRemaining));
                    return;
                }
                if (r == LoginResult.Disabled)
                {
                    WebApi.Error(response, 400, MsgDisabled);
                    return;
                }
                WebApi.Error(response, 401, Format(MsgBadPassword, g.RemainingAttempts));
                return;
            }

            if (grant == "refresh_token")
            {
                if (g.TryRefresh(f.Get("refresh_token"), out access, out refresh))
                {
                    TokenJson(response, g, access, refresh);
                    return;
                }
                WebApi.Error(response, 401, MsgBadRefresh);
                return;
            }

            WebApi.Error(response, 400, MsgGrantType);
        }

        /// <summary>
        /// POST /api/auth/password — 관리자 비밀번호를 교체합니다(빈 값이면 비밀번호 인증만 해제).
        /// 교체 성공 후 새 세션 쿠키를 발급해 현재 브라우저는 로그인 상태를 유지합니다.
        /// </summary>
        [Route("api/auth/password")]
        [Method("POST")]
        public void Password(WebServerEventArgs e)
        {
            Notify(e.Context);
            if (CsrfGuard.Check(e.Context, CsrfMarker, LocalAddress)) return;

            GuardedWebServer server = Server;
            if (server == null) { NotWired(e.Context.Response); return; }
            LoginGuard g = server.Auth;
            HttpListenerResponse response = e.Context.Response;

            string password = FormReader.Read(e.Context.Request, BodyLimit, "password");
            if (password == null)
            {
                WebApi.Error(response, 400, MsgNoPasswordField);
                return;
            }
            if (password.Length > 0)
            {
                if (password.Length < PasswordMinLength || password.Length > PasswordMaxLength)
                {
                    WebApi.Error(response, 400, MsgPasswordLength);
                    return;
                }
                if (!IsPrintableAscii(password))
                {
                    WebApi.Error(response, 400, MsgCharset);
                    return;
                }
            }

            g.SetPassword(password);
            if (!PersistOr500(response)) return;

            // SetPassword가 전 세션을 회수하므로, 현재 브라우저에는 새 세션을 발급해 유지합니다.
            server.SetSessionCookie(response, password.Length > 0 ? g.Sessions.Issue() : null);
            WebApi.Json(response, 200, WebApi.OkJson);
        }

        /// <summary>POST /api/auth/apikey — 정적 API 키를 교체합니다(빈 값이면 사용 해제).</summary>
        [Route("api/auth/apikey")]
        [Method("POST")]
        public void ApiKey(WebServerEventArgs e)
        {
            Notify(e.Context);
            if (CsrfGuard.Check(e.Context, CsrfMarker, LocalAddress)) return;

            GuardedWebServer server = Server;
            if (server == null) { NotWired(e.Context.Response); return; }
            LoginGuard g = server.Auth;
            HttpListenerResponse response = e.Context.Response;

            string key = FormReader.Read(e.Context.Request, BodyLimit, "apikey");
            if (key == null)
            {
                WebApi.Error(response, 400, MsgNoApiKeyField);
                return;
            }
            if (ApiKeyMaxLength > 0 && key.Length > ApiKeyMaxLength)
            {
                WebApi.Error(response, 400, MsgApiKeyLength);
                return;
            }
            if (key.Length > 0 && !IsPrintableAscii(key))
            {
                WebApi.Error(response, 400, MsgCharset);
                return;
            }

            g.ApiKey = key;
            if (!PersistOr500(response)) return;
            WebApi.Json(response, 200, WebApi.OkJson);
        }

        private static void TokenJson(HttpListenerResponse response, LoginGuard guard, string access, string refresh)
        {
            JsonWriter w = new JsonWriter();
            w.BeginObject().Prop("ok", true)
             .Prop("token_type", "Bearer")
             .Prop("access_token", access)
             .Prop("refresh_token", refresh)
             .Prop("expires_in", (int)(guard.Sessions.LifetimeMilliseconds / 1000L))
             .EndObject();
            WebApi.Json(response, 200, w.ToString());
        }

        private static bool PersistOr500(HttpListenerResponse response)
        {
            AuthCredentialPersist persist = Persist;
            if (persist == null) return true;
            string error = persist();
            if (error == null) return true;
            WebApi.Error(response, 500, Substitute(MsgPersistFailed, error));
            return false;
        }

        private static bool IsPrintableAscii(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] < ' ' || value[i] > '~') return false;
            }
            return true;
        }

        private static string Format(string message, int number)
        {
            return Substitute(message, number.ToString());
        }

        // nanoFramework의 String.Replace(string,string)은 미지원이라 IndexOf/Substring으로 대체합니다.
        private static string Substitute(string message, string value)
        {
            int i = message.IndexOf("{0}");
            if (i < 0) return message;
            return message.Substring(0, i) + value + message.Substring(i + 3);
        }

        private static void Notify(HttpListenerContext context)
        {
            AuthRequestObserver observer = OnRequest;
            if (observer != null) observer(context);
        }

        private static void NotWired(HttpListenerResponse response)
        {
            WebApi.Error(response, 500, "AuthController.Server is not set.");
        }
    }
}
