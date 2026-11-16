using System;
using System.Threading;
using VagabondK.nF.Web;

namespace VagabondK.nF.Web.PackageSmoke
{
    /// <summary>
    /// VagabondK.nF.Web 패키지만으로 컴파일·배치가 되는지 확인하는 스모크 앱입니다.
    /// 실제 네트워크 동작은 검사하지 않으며, NuGet 설치 산출물의 참조 무결성만 검증합니다.
    /// </summary>
    public class Program
    {
        public static void Main()
        {
            LoginGuard guard = new LoginGuard();
            guard.SetPassword("smoke-password");
            string sessionToken;
            LoginResult login = guard.TryLogin("smoke-password", out sessionToken);
            AccessDecision decision = guard.CheckAccess(sessionToken, null);
            int remaining = guard.RemainingAttempts;

            GuardedWebServer server = new GuardedWebServer(80, new Type[] { typeof(AuthController) }, guard);
            server.SessionCookieName = "smokeauth";
            server.SessionHeaderName = "x-smoke-session";
            server.ApiKeyHeaderName = "x-smoke-key";
            server.LoginPagePath = "/login";
            LoginGuard bound = server.Auth;

            AuthController.Server = server;
            AuthController.BodyLimit = 1024;

            string hash = PasswordHasher.Create("smoke-password");
            bool verified = PasswordHasher.Verify("smoke-password", hash);

            SessionStore sessions = new SessionStore();
            TokenStore tokens = new TokenStore();

            JsonWriter json = new JsonWriter(64);
            json.BeginObject();
            json.Prop("ok", true);
            json.EndObject();

            // FormReader는 요청 객체가 필요해 타입 참조로만 검증합니다.
            Type form = typeof(FormReader);

            Thread.Sleep(1);
        }
    }
}
