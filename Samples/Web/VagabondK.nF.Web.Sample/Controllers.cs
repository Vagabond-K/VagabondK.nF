using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using nanoFramework.WebServer;
using VagabondK.nF.Web;

namespace VagabondK.nF.Web.Sample
{
    /// <summary>
    /// 샘플 장치의 설정 값. 웹에서 저장하면 파일에 영속화되어 재부팅 후에도 유지됩니다.
    /// </summary>
    public static class SampleConfig
    {
        public const string Path = "I:\\web-sample.cfg";

        // 검증 한계값의 단일 출처. /api/auth/status의 limits로 클라이언트에 전달됩니다.
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

        /// <summary>열려 있는 객체 안에 설정 4필드를 병합합니다(JsonWriter 컨벤션: 값은 문자열).</summary>
        public static void WriteProps(JsonWriter w)
        {
            w.Prop("deviceName", DeviceName)
             .Prop("pollMs", PollMs.ToString())
             .Prop("debug", Debug ? "1" : "0")
             .Prop("mode", Mode.ToString());
        }

        public static string Json()
        {
            JsonWriter w = new JsonWriter();
            w.BeginObject();
            WriteProps(w);
            w.EndObject();
            return w.ToString();
        }
    }

    /// <summary>
    /// 로그인 보호 상태(비밀번호 해시 + API 키)를 CredentialFile로 영속화합니다.
    /// 최초 실행 시 기본 비밀번호 "sample1234"로 초기화합니다 — 실사용 전 반드시 변경.
    /// 엔드포인트 자체는 라이브러리 AuthController가 제공하므로 여기엔 파일 I/O와 훅만 남습니다.
    /// </summary>
    public static class SampleAuth
    {
        private const string FilePath = "I:\\web-sample.auth";
        private const string DefaultPassword = "sample1234";

        public static readonly LoginGuard Guard = new LoginGuard();

        public static void Load()
        {
            if (!CredentialFile.Load(Guard, FilePath))
            {
                Guard.SetPassword(DefaultPassword);
                CredentialFile.Save(Guard, FilePath);
            }
        }

        /// <summary>AuthController.Persist 훅: password/apikey 변경 시 호출되어 파일로 저장합니다.</summary>
        public static string Save()
        {
            return CredentialFile.Save(Guard, FilePath);
        }

        /// <summary>AuthController.StatusExtras 훅: 상태 응답에 샘플 고유 필드를 병합합니다.</summary>
        public static void WriteStatusExtras(JsonWriter w)
        {
            w.Prop("schemaVersion", SampleConfig.Schema)
             .Prop("limits").BeginObject()
             .Prop("nameMax", SampleConfig.NameMax)
             .Prop("pollMin", SampleConfig.PollMin)
             .Prop("pollMax", SampleConfig.PollMax)
             .EndObject();
        }
    }

    /// <summary>
    /// 라우트 핸들러. [PublicEndpoint]가 없는 엔드포인트는 GuardedWebServer가 자동 401 차단(fail-closed).
    /// POST는 CsrfGuard로 마커 헤더(x-web-sample) + Origin 지역 주소 검사를 함께 통과해야 합니다.
    /// /api/auth/*는 같은 규칙으로 라이브러리 AuthController가 처리합니다(Program에서 배선).
    /// </summary>
    public class SampleController
    {
        public const string CsrfMarker = "x-web-sample";

        // CSRF Host 검사 주소는 인증 컨트롤러와 단일 출처를 씁니다(Program 루프가 갱신).
        public static string LocalAddress
        {
            get { return AuthController.LocalAddress; }
            set { AuthController.LocalAddress = value; }
        }

        private static readonly long BootTick = Environment.TickCount64;
        private static int requests;

        // 페이지/자산 라우트는 생성된 WebUiRoutes가 전담합니다(WebUi\*.nf.html의 첫 줄
        // @route 마커 → Task가 WebUiRoutes.g.cs 생성). 미인증 html 요청의 /login 302는
        // GuardedWebServer.LoginPagePath가 처리하고, 이 파일에는 API 핸들러만 남습니다.
        // 인증 3엔드포인트(status/login/logout)는 라이브러리 AuthController로 통합되어 여기엔 없습니다.

        // ---------- 상태/설정 (인증 필요) ----------

        [Route("api/status")]
        [Method("GET")]
        public void Status(WebServerEventArgs e)
        {
            requests++;
            long uptimeSec = (Environment.TickCount64 - BootTick) / 1000;
            if (SampleConfig.Debug)
                Debug.WriteLine("status request #" + requests.ToString() + " uptime=" + uptimeSec.ToString());
            // 서버 자기서술(Phase 2 설계): capabilities는 manifest.features의 최종 고향, settingsKeys는
            // 이 서버가 실제로 받는 설정 키(폼 검증 상실 방어). manifest 소멸 로드맵의 additive groundwork.
            JsonWriter w = new JsonWriter();
            w.BeginObject().Prop("ok", true)
             .Prop("uptime", uptimeSec)
             .Prop("heapFree", nanoFramework.Runtime.Native.GC.Run(false))
             .Prop("ip", LocalAddress)
             .Prop("requests", requests)
             .Prop("authEnabled", SampleAuth.Guard.IsEnabled)
             .PropRaw("capabilities", "{\"points\":false}")
             .Prop("ap", false)
             .PropArr("settingsKeys", new string[] { "deviceName", "pollMs", "debug", "mode" });
            SampleConfig.WriteProps(w); // deviceName/pollMs/debug/mode 병합
            w.EndObject();
            WebApi.Json(e.Context.Response, 200, w.ToString());
        }

        [Route("api/settings")]
        [Method("GET")]
        public void SettingsGet(WebServerEventArgs e)
        {
            JsonWriter w = new JsonWriter();
            w.BeginObject().Prop("ok", true)
             .Prop("schemaVersion", SampleConfig.Schema)
             .Prop("gatewayProfile", "web-sample");
            SampleConfig.WriteProps(w);
            w.EndObject();
            WebApi.Json(e.Context.Response, 200, w.ToString());
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
            WebApi.Json(e.Context.Response, 200, WebApi.OkJson);
        }

        // firmware 컴포넌트용 수신 데모 엔드포인트: 청크를 누적 계수만 합니다(실제 플래싱은 부트로터 영역).
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
            JsonWriter w = new JsonWriter();
            w.BeginObject().Prop("ok", true).Prop("seq", seq).Prop("received", fwBytes).EndObject();
            WebApi.Json(e.Context.Response, 200, w.ToString());
        }

        // reboot 컴포넌트용 데모 엔드포인트: 확인만 반환하고 실제 재부팅은 하지 않습니다
                // (실전 프로젝트에서는 Microsoft.Devices.Software.Reboot를 호출합니다).
        [Route("api/reboot")]
        [Method("POST")]
        public void RebootPost(WebServerEventArgs e)
        {
            if (CsrfGuard.Check(e.Context, CsrfMarker, LocalAddress)) return;
            WebApi.Json(e.Context.Response, 200, WebApi.OkJson);
        }

        // ---------- 11차 컴포넌트 데모 (log-tail / register-view / wifi-form) ----------

        // log-tail용: 호출마다 새 줄 2개를 생성해 보냅니다(클라이언트가 꼬리 유지).
        static int logSeq;

        static string Tstr(long sec)
        {
            if (sec < 0) sec = 0;
            string r = ((sec / 3600) % 24).ToString(); if (r.Length < 2) r = "0" + r;
            string t = ((sec / 60) % 60).ToString(); if (t.Length < 2) t = "0" + t;
            r += ":" + t; t = (sec % 60).ToString(); if (t.Length < 2) t = "0" + t;
            return r + ":" + t;
        }

        [Route("api/log")]
        [Method("GET")]
        public void LogGet(WebServerEventArgs e)
        {
            long up = (Environment.TickCount64 - BootTick) / 1000;
            string a = "[" + Tstr(up - 2) + "] polling #" + (logSeq + 1).ToString() + " ok";
            string b = "[" + Tstr(up) + "] polling #" + (logSeq + 2).ToString() +
                (logSeq % 10 == 8 ? " warn: slow response" : " ok");
            logSeq += 2;
            JsonWriter w = new JsonWriter();
            w.BeginObject().Prop("ok", true).Prop("lines").BeginArray().Str(a).Str(b).EndArray().EndObject();
            WebApi.Json(e.Context.Response, 200, w.ToString());
        }

        // register-view용: 가동 시간의 결정적 함수라 폴링마다 값이 갱신됩니다.
        [Route("api/registers")]
        [Method("GET")]
        public void RegistersGet(WebServerEventArgs e)
        {
            long up = (Environment.TickCount64 - BootTick) / 1000;
            JsonWriter w = new JsonWriter(160);
            w.BeginObject().Prop("ok", true).Prop("values").BeginArray();
            for (int i = 0; i < 16; i++)
                w.Num((up * 13 + i * 257) & 0xFFFF);
            w.EndArray().EndObject();
            WebApi.Json(e.Context.Response, 200, w.ToString());
        }

        // wifi-form용: 네트워크 설정 데모(실전에서는 무선 라디오 API 호출 자리).
        static string NetSsid = "nanoNet", NetPass = "";

        [Route("api/network")]
        [Method("GET")]
        public void NetworkGet(WebServerEventArgs e)
        {
            JsonWriter w = new JsonWriter();
            w.BeginObject().Prop("ok", true).Prop("ssid", NetSsid).EndObject();
            WebApi.Json(e.Context.Response, 200, w.ToString());
        }

        [Route("api/network")]
        [Method("POST")]
        public void NetworkPost(WebServerEventArgs e)
        {
            if (CsrfGuard.Check(e.Context, CsrfMarker, LocalAddress)) return;
            FormReader f = new FormReader(e.Context.Request, 8192);
            string ssid = f.Get("ssid"), pass = f.Get("pass");
            if (ssid == null || ssid.Length == 0 || ssid.Length > 32)
            { WebApi.Error(e.Context.Response, 400, "ssid는 1~32자"); return; }
            NetSsid = ssid;
            if (pass != null && pass.Length > 0) NetPass = pass;
            WebApi.Json(e.Context.Response, 200, WebApi.OkJson);
        }
    }
}
