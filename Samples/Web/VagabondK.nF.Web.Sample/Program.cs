using System;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using VagabondK.nF.Web;

namespace VagabondK.nF.Web.Sample
{
    /// <summary>
    /// VagabondK.nF.WebUi(빌드타임 페이지 생성) + VagabondK.nF.Web(로그인/API 인증) 조합 샘플.
    /// 포트 80에서 GuardedWebServer를 띄우고, 인증 없는 API 요청은 서버가 401로 차단합니다(fail-closed).
    /// </summary>
    public class Program
    {
        /// <summary>서버 기동 → 네트워크 대기 → 30초마다 주소 갱신/가비지 컬렉션의 메인 루프.</summary>
        public static void Main()
        {
            SampleConfig.Load();
            SampleAuth.Load();

            GuardedWebServer server = new GuardedWebServer(80, new Type[] { typeof(SampleController), typeof(AuthController), typeof(WebUiRoutes) }, SampleAuth.Guard);
            server.SessionCookieName = "wsauth";
            server.SessionHeaderName = "x-web-sample-session";
            server.ApiKeyHeaderName = "x-web-sample-key";
            // 미인증 브라우저 요청을 /login으로 302 안내(경로는 페이지 마커에서 생성됨).
            server.LoginPagePath = WebUiRoutes.LoginPath;

            // 인증 엔드포인트(api/auth/*)는 라이브러리 AuthController가 담당합니다.
            AuthController.Server = server;
            AuthController.CsrfMarker = SampleController.CsrfMarker;
            AuthController.StatusExtras = new AuthStatusExtrasWriter(SampleAuth.WriteStatusExtras);
            AuthController.Persist = new AuthCredentialPersist(SampleAuth.Save);
            AuthController.MsgLocked = "시도 횟수를 초과했습니다. {0}초 후 다시 시도하세요.";
            AuthController.MsgBadPassword = "비밀번호가 올바르지 않습니다. 남은 시도 {0}회.";
            AuthController.MsgDisabled = "인증이 꺼져 있습니다.";

            server.Start();

            WaitForNetwork();

            while (true)
            {
                string ip = DetectAddress();
                if (ip.Length > 0) AuthController.LocalAddress = ip;
                nanoFramework.Runtime.Native.GC.Run(false);
                Thread.Sleep(30000);
            }
        }

        /// <summary>
        /// NIC에 IPv4 주소가 잡힐 때까지 최대 90초 폴링합니다. 실패해도 예외를 던지지 않습니다 —
        /// Wi-Fi 재연결이 90초보다 오래 걸리는 경우(플래시 후 재부팅 등) 앱이 죽으면 안 되고,
        /// 본 루프가 30초마다 계속 재시도합니다.
        /// </summary>
        private static void WaitForNetwork()
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < deadline)
            {
                string ip = DetectAddress();
                if (ip.Length > 0)
                {
                    AuthController.LocalAddress = ip;
                    Debug.WriteLine("network up: " + ip);
                    return;
                }
                Thread.Sleep(500);
            }
            Debug.WriteLine("network not up yet; main loop will keep retrying");
        }

        // AP 인터페이스는 꺼져 있어도 저장된 주소를 보고하므로 STA 주소만 반환합니다(게이트웨이와 동일 규칙).
        private static string DetectAddress()
        {
            NetworkInterface[] nics = NetworkInterface.GetAllNetworkInterfaces();
            for (int i = 0; i < nics.Length; i++)
            {
                if (nics[i].NetworkInterfaceType == NetworkInterfaceType.WirelessAP)
                    continue;
                string ip = nics[i].IPv4Address;
                if (ip != null && ip.Length > 0 && ip != "0.0.0.0" && ip != "127.0.0.1")
                    return ip;
            }
            return "";
        }
    }
}
