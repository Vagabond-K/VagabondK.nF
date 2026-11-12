using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using nanoFramework.WebServer;
using VagabondK.nF.WebAuth;

namespace VagabondK.nF.Web.Sample
{
    /// <summary>
    /// 샘플 장치의 설정 값. 웹에서 저장하면 파일에 영속화되어 재부팅 후에도 유지된다.
    /// </summary>
    public static class SampleConfig
    {
        public const string Path = "I:\\web-sample.cfg";

        // 검증 한계값의 단일 출처. /api/auth/status의 limits로 클라이언트에 전달된다.
        public const int NameMax = 30, PollMin = 200, PollMax = 60000, Schema = 1, ModeMax = 2;

        public static string DeviceName = "ESP32 샘플";
        public static int PollMs = 1000;
        public static bool Debug = false;
        public static int Mode = 1; // segmented 데모: 0=간이 1=표준 2=상세

        public static void Load()
        {
            try
            {
                if (!File.Exists(Path)) return;
                string[] lines = File.ReadAllText(Path).Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string key = line.Substring(0, eq), val = line.Substring(eq + 1);
                    if (key == "deviceName" && val.Length > 0 && val.Length <= NameMax) DeviceName = val;
                    else if (key == "pollMs") { int v; if (int.TryParse(val, out v) && v >= PollMin && v <= PollMax) PollMs = v; }
                    else if (key == "debug") Debug = val == "1";
                    else if (key == "mode") { int v; if (int.TryParse(val, out v) && v >= 0 && v <= ModeMax) Mode = v; }
                }
            }
            catch (Exception) { /* 기본값 유지 */ }
        }

        public static void Save()
        {
            string tmp = Path + ".tmp";
            string body = "deviceName=" + DeviceName + "\r\npollMs=" + PollMs.ToString() + "\r\ndebug=" + (Debug ? "1" : "0") + "\r\nmode=" + Mode.ToString() + "\r\n";
            if (File.Exists(tmp)) File.Delete(tmp);
            File.WriteAllText(tmp, body);
            if (File.Exists(Path)) File.Delete(Path);
            File.Move(tmp, Path);
        }

        public static string Json()
        {
            return "{\"deviceName\":\"" + WebApi.JsonEscape(DeviceName) + "\",\"pollMs\":\"" + PollMs.ToString() +
                   "\",\"debug\":\"" + (Debug ? "1" : "0") + "\",\"mode\":\"" + Mode.ToString() + "\"}";
        }
    }

    /// <summary>
    /// 로그인 보호 상태(비밀번호 해시 + API 키)를 파일에 영속화한다.
    /// 최초 실행 시 기본 비밀번호 "sample1234"로 초기화한다 — 실사용 전 반드시 변경.
    /// </summary>
    public static class SampleAuth
    {
        private const string FilePath = "I:\\web-sample.auth";
        private const string DefaultPassword = "sample1234";

        public static readonly LoginGuard Guard = new LoginGuard();

        public static void Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    string[] lines = File.ReadAllText(FilePath).Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string line = lines[i].Trim();
                        if (line.StartsWith("h=")) Guard.PasswordHash = line.Substring(2);
                        else if (line.StartsWith("k=")) Guard.ApiKey = line.Substring(2);
                    }
                    if (Guard.PasswordHash != null && Guard.PasswordHash.Length > 0) return;
                }
                Guard.SetPassword(DefaultPassword);
                Save();
            }
            catch (Exception)
            {
                Guard.SetPassword(DefaultPassword);
            }
        }

        private static void Save()
        {
            string tmp = FilePath + ".tmp";
            string body = "h=" + Guard.PasswordHash + "\r\nk=" + (Guard.ApiKey == null ? "" : Guard.ApiKey) + "\r\n";
            if (File.Exists(tmp)) File.Delete(tmp);
            File.WriteAllText(tmp, body);
            if (File.Exists(FilePath)) File.Delete(FilePath);
            File.Move(tmp, FilePath);
        }

        /// <summary>설정 저장 시 함께 호출되어 비밀번호 변경을 반영한다(샘플에서는 미사용).</summary>
        public static void ChangePassword(string newPassword)
        {
            Guard.SetPassword(newPassword);
            Save();
        }
    }

    /// <summary>
    /// 라우트 핸들러. [PublicEndpoint]가 없는 엔드포인트는 GuardedWebServer가 자동 401 차단(fail-closed).
    /// POST는 CsrfGuard로 마커 헤더(x-web-sample) + Origin 지역 주소 검사를 함께 통과해야 한다.
    /// </summary>
    public class SampleController
    {
        public const string CsrfMarker = "x-web-sample";

        public static GuardedWebServer Server;
        public static string LocalAddress = "";

        private static readonly long BootTick = Environment.TickCount64;
        private static int requests;

        // ---------- 페이지 ----------

        [Route("/")]
        [Method("GET")]
        [PublicEndpoint]
        public void Page(WebServerEventArgs e)
        {
            HttpListenerRequest req = e.Context.Request;
            HttpListenerResponse res = e.Context.Response;
            res.Headers.Add("Cache-Control", "no-cache");
            res.Headers.Add("ETag", WebPage.ETag);
            res.Headers.Add("X-Content-Type-Options", "nosniff");
            res.Headers.Add("X-Frame-Options", "DENY");
            if (req.Headers["if-none-match"] == WebPage.ETag)
            {
                WebServer.OutputHttpCode(res, HttpStatusCode.NotModified);
                return;
            }
            // WebPage.g.cs는 gzip된 바이트이므로 Content-Encoding을 반드시 붙인다.
            res.Headers.Add("Content-Encoding", "gzip");
            res.StatusCode = 200;
            res.ContentType = "text/html; charset=utf-8";
            res.ContentLength64 = WebPage.Length;
            Stream o = res.OutputStream;
            for (int i = 0; i < WebPage.PartCount; i++)
            {
                byte[] part = Convert.FromBase64String(WebPage.GetPart(i));
                o.Write(part, 0, part.Length);
            }
            res.Close();
        }

        // ---------- 인증 ----------

        [Route("api/auth/status")]
        [Method("GET")]
        [PublicEndpoint]
        public void AuthStatus(WebServerEventArgs e)
        {
            LoginGuard g = SampleAuth.Guard;
            bool logged = !g.IsEnabled || g.Sessions.Validate(Server.ReadSessionToken(e.Context.Request));
            WebApi.Json(e.Context.Response, 200,
                "{\"ok\":true,\"authEnabled\":" + (g.IsEnabled ? "true" : "false") +
                ",\"loggedIn\":" + (logged ? "true" : "false") +
                ",\"lockedOut\":" + (g.IsLockedOut ? "true" : "false") +
                ",\"remaining\":" + g.RemainingAttempts.ToString() +
                ",\"lockSec\":" + g.LockoutSecondsRemaining.ToString() +
                ",\"csrfHeader\":\"" + CsrfMarker + "\"" +
                ",\"schemaVersion\":" + SampleConfig.Schema.ToString() +
                ",\"limits\":{\"nameMax\":" + SampleConfig.NameMax.ToString() +
                ",\"pollMin\":" + SampleConfig.PollMin.ToString() +
                ",\"pollMax\":" + SampleConfig.PollMax.ToString() + "}}");
        }

        [Route("api/auth/login")]
        [Method("POST")]
        [PublicEndpoint]
        public void Login(WebServerEventArgs e)
        {
            if (CsrfGuard.Check(e.Context, CsrfMarker, LocalAddress)) return;
            string password = FormReader.Read(e.Context.Request, 2048, "password");
            string token;
            LoginResult r = SampleAuth.Guard.TryLogin(password, out token);
            if (r == LoginResult.Success)
            {
                Server.SetSessionCookie(e.Context.Response, token);
                WebApi.Json(e.Context.Response, 200, "{\"ok\":true}");
                return;
            }
            if (r == LoginResult.LockedOut)
            {
                WebApi.Error(e.Context.Response, 429, "시도 횟수를 초과했습니다. " +
                    SampleAuth.Guard.LockoutSecondsRemaining.ToString() + "초 후 다시 시도하세요.");
                return;
            }
            if (r == LoginResult.Disabled)
            {
                WebApi.Error(e.Context.Response, 400, "인증이 꺼져 있습니다.");
                return;
            }
            Server.SetSessionCookie(e.Context.Response, null);
            WebApi.Error(e.Context.Response, 401, "비밀번호가 올바르지 않습니다. 남은 시도 " +
                SampleAuth.Guard.RemainingAttempts.ToString() + "회.");
        }

        [Route("api/auth/logout")]
        [Method("POST")]
        [PublicEndpoint]
        public void Logout(WebServerEventArgs e)
        {
            if (CsrfGuard.Check(e.Context, CsrfMarker, LocalAddress)) return;
            SampleAuth.Guard.Sessions.Revoke(Server.ReadSessionToken(e.Context.Request));
            Server.SetSessionCookie(e.Context.Response, null);
            WebApi.Json(e.Context.Response, 200, "{\"ok\":true}");
        }

        // ---------- 상태/설정 (인증 필요) ----------

        [Route("api/status")]
        [Method("GET")]
        public void Status(WebServerEventArgs e)
        {
            requests++;
            long uptimeSec = (Environment.TickCount64 - BootTick) / 1000;
            if (SampleConfig.Debug)
                Debug.WriteLine("status request #" + requests.ToString() + " uptime=" + uptimeSec.ToString());
            WebApi.Json(e.Context.Response, 200,
                "{\"ok\":true,\"uptime\":" + uptimeSec.ToString() +
                ",\"heapFree\":" + nanoFramework.Runtime.Native.GC.Run(false).ToString() +
                ",\"ip\":\"" + LocalAddress + "\"" +
                ",\"requests\":" + requests.ToString() +
                ",\"authEnabled\":" + (SampleAuth.Guard.IsEnabled ? "true" : "false") +
                // 서버 자기서술(Phase 2 설계): capabilities는 manifest.features의 최종 고향, settingsKeys는
                // 이 서버가 실제로 받는 설정 키(폼 검증 상실 방어). manifest 소멸 로드맵의 additive groundwork.
                ",\"capabilities\":{\"points\":false}" +
                ",\"settingsKeys\":[\"deviceName\",\"pollMs\",\"debug\",\"mode\"]" +
                "," + SampleConfig.Json().Substring(1)); // deviceName/pollMs/debug 병합
        }

        [Route("api/settings")]
        [Method("GET")]
        public void SettingsGet(WebServerEventArgs e)
        {
            WebApi.Json(e.Context.Response, 200,
                "{\"ok\":true,\"schemaVersion\":" + SampleConfig.Schema.ToString() +
                ",\"gatewayProfile\":\"web-sample\"," + SampleConfig.Json().Substring(1));
        }

        [Route("api/settings")]
        [Method("POST")]
        public void SettingsPost(WebServerEventArgs e)
        {
            if (CsrfGuard.Check(e.Context, CsrfMarker, LocalAddress)) return;
            FormReader f = new FormReader(e.Context.Request, 4096);
            string name = f.Get("deviceName"), poll = f.Get("pollMs"), dbg = f.Get("debug"), mode = f.Get("mode");

            int pollMs;
            if (name == null || name.Length < 1 || name.Length > SampleConfig.NameMax) { WebApi.Error(e.Context.Response, 400, "deviceName 오류"); return; }
            if (poll == null || !int.TryParse(poll, out pollMs) || pollMs < SampleConfig.PollMin || pollMs > SampleConfig.PollMax) { WebApi.Error(e.Context.Response, 400, "pollMs 오류"); return; }
            if (dbg != "0" && dbg != "1") { WebApi.Error(e.Context.Response, 400, "debug 오류"); return; }
            int modeV;
            if (mode == null || !int.TryParse(mode, out modeV) || modeV < 0 || modeV > SampleConfig.ModeMax) { WebApi.Error(e.Context.Response, 400, "mode 오류"); return; }

            SampleConfig.DeviceName = name;
            SampleConfig.PollMs = pollMs;
            SampleConfig.Debug = dbg == "1";
            SampleConfig.Mode = modeV;
            SampleConfig.Save();
            WebApi.Json(e.Context.Response, 200, "{\"ok\":true}");
        }

        // firmware 컴포넌트용 수신 데모 엔드포인트: 청크를 누적 계수만 한다(실제 플래싱은 부트로터 영역).
        static int fwBytes;

        [Route("api/firmware")]
        [Method("POST")]
        public void FirmwarePost(WebServerEventArgs e)
        {
            if (CsrfGuard.Check(e.Context, CsrfMarker, LocalAddress)) return;
            FormReader f = new FormReader(e.Context.Request, 8192);
            string seqS = f.Get("seq"), totS = f.Get("total"), data = f.Get("data");
            int seq, total;
            if (data == null || seqS == null || totS == null ||
                !int.TryParse(seqS, out seq) || !int.TryParse(totS, out total) || seq < 0 || seq >= total)
            { WebApi.Error(e.Context.Response, 400, "firmware 필드 오류"); return; }
            if (seq == 0) fwBytes = 0;
            fwBytes += data.Length * 3 / 4; // base64 → 원바이트 근사
            WebApi.Json(e.Context.Response, 200,
                "{\"ok\":true,\"seq\":" + seq.ToString() + ",\"received\":" + fwBytes.ToString() + "}");
        }
    }
}
