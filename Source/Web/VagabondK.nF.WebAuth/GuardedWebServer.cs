using System;
using System.Net;
using System.Reflection;
using nanoFramework.WebServer;

namespace VagabondK.nF.WebAuth
{
    /// <summary>
    /// <see cref="LoginGuard"/>를 nanoFramework.WebServer 라우팅에 연결하는 인증 서버입니다.
    /// </summary>
    /// <remarks>
    /// 문서화된 확장점(<see cref="WebServer.InvokeRoute"/>)을 오버라이드해 핸들러 실행
    /// 전에 <see cref="PublicEndpointAttribute"/> 표시와 접근 권한을 검사하므로
    /// <b>fail-closed</b>입니다 — 표시가 없는 새 엔드포인트는 자동으로 보호됩니다.
    /// 세션 토큰은 세션 헤더 → Authorization: Bearer → 쿠키 순으로 찾습니다.
    /// 헤더·쿠키 이름은 속성으로 재설정할 수 있습니다(생성 후 <see cref="WebServer.Start"/>
    /// 호출 전에 설정하세요).
    /// </remarks>
    /// <example>
    /// <code>
    /// GuardedWebServer server = new GuardedWebServer(80, new Type[] { typeof(MyController) }, guard);
    /// server.SessionCookieName = "ngauth";
    /// server.Start();
    /// </code>
    /// </example>
    public class GuardedWebServer : WebServer
    {
        private LoginGuard auth;

        /// <summary>세션 쿠키 이름입니다.</summary>
        public string SessionCookieName
        {
            get { return sessionCookieName; }
            set { sessionCookieName = value; }
        }
        private string sessionCookieName = "auth";

        /// <summary>세션 토큰을 담는 요청 헤더 이름입니다(쿠키 대용, 자동화용).</summary>
        public string SessionHeaderName
        {
            get { return sessionHeaderName; }
            set { sessionHeaderName = value; }
        }
        private string sessionHeaderName = "x-auth-session";

        /// <summary>정적 API 키를 담는 요청 헤더 이름입니다.</summary>
        public string ApiKeyHeaderName
        {
            get { return apiKeyHeaderName; }
            set { apiKeyHeaderName = value; }
        }
        private string apiKeyHeaderName = "x-auth-key";

        /// <summary>Authorization 헤더의 토큰 접두사입니다.</summary>
        public const string BearerPrefix = "Bearer ";

        /// <summary>보호할 컨트롤러들과 가드를 연결해 서버를 만듭니다.</summary>
        /// <param name="port">청취 TCP 포트입니다.</param>
        /// <param name="controllers">[Route]/[Method] 컨트롤러 타입들입니다.</param>
        /// <param name="auth">접근 판정에 쓸 <see cref="LoginGuard"/>입니다. <see langword="null"/>이면 열려 있습니다.</param>
        public GuardedWebServer(int port, Type[] controllers, LoginGuard auth)
            : base(port, HttpProtocol.Http, IPAddress.Any, controllers)
        {
            this.auth = auth;
        }

        /// <summary>접근 판정에 사용할 가드입니다(런타임 교체 가능).</summary>
        public LoginGuard Auth
        {
            get { return auth; }
            set { auth = value; }
        }

        /// <summary>핸들러 실행 전 인증을 검사합니다. 거부 시 401/429 JSON을 쓰고 통과하지 않습니다.</summary>
        protected override void InvokeRoute(CallbackRoutes route, HttpListenerContext context)
        {
            LoginGuard guard = auth;
            if (guard != null && guard.IsEnabled && !IsPublic(route.Callback))
            {
                AccessDecision decision = guard.CheckAccess(
                    ReadSessionToken(context.Request),
                    context.Request.Headers[apiKeyHeaderName]);

                if (decision == AccessDecision.Denied)
                {
                    WebApi.Error(context.Response, 401, "unauthorized");
                    return;
                }
                if (decision == AccessDecision.LockedOut)
                {
                    WebApi.Error(context.Response, 429, "login locked, wait and retry");
                    return;
                }
            }

            base.InvokeRoute(route, context);
        }

        /// <summary>요청에서 세션 토큰을 찾습니다: 세션 헤더 → Bearer → 쿠키 순입니다.</summary>
        public string ReadSessionToken(HttpListenerRequest request)
        {
            string token = request.Headers[sessionHeaderName];
            if (token != null && token.Length > 0)
                return token.Trim();

            string authorization = request.Headers["Authorization"];
            if (authorization != null && authorization.StartsWith(BearerPrefix) &&
                authorization.Length > BearerPrefix.Length)
                return authorization.Substring(BearerPrefix.Length).Trim();

            return FindCookie(request.Headers["cookie"]);
        }

        /// <summary>로그인 상태 쿠키를 설정(<see langword="token"/> 지정)·제거(null)합니다.</summary>
        public void SetSessionCookie(HttpListenerResponse response, string token)
        {
            response.Headers.Add("Set-Cookie", token == null
                ? sessionCookieName + "=; Path=/; Max-Age=0"
                : sessionCookieName + "=" + token + "; Path=/; HttpOnly; SameSite=Strict");
        }

        // 핸들러 메서드의 메타데이터에서 마커 어트리뷰트를 찾습니다.
        // WebServer 패키지 자체가 컨트롤러 등록에 쓰는 것과 같은 리플렉션 API를 사용합니다.
        private static bool IsPublic(MethodInfo method)
        {
            object[] attributes = method.GetCustomAttributes(true);
            for (int i = 0; i < attributes.Length; i++)
            {
                if (attributes[i].GetType() == typeof(PublicEndpointAttribute))
                    return true;
            }

            return false;
        }

        private string FindCookie(string header)
        {
            if (header == null)
                return null;

            int at = header.IndexOf(SessionCookieName + "=");
            if (at < 0)
                return null;

            int start = at + SessionCookieName.Length + 1;
            int end = header.IndexOf(';', start);
            return end < 0 ? header.Substring(start) : header.Substring(start, end - start);
        }
    }
}