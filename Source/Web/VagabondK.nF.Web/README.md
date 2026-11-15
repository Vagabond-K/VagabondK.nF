# VagabondK.nF.Web

[Web 그룹](../README.md) · [전체 프로젝트](../../../README.md)

단일 관리자 비밀번호 기반의 임베디드 웹 인증 파사드입니다.
평문 비밀번호를 저장하지 않고, 솔트+HMAC-SHA256 반복 해시와 메모리 세션 토큰을 사용합니다.

`nanoFramework.WebServer`를 직접 참조하므로 인증·응답·CSRF 계층까지 한 어셈블리로
관리 화면을 완성할 수 있습니다(참조: `nanoFramework.WebServer`, `nanoFramework.System.Net.Http`(Server)).

## 구성 요소

| 형식 | 역할 |
|---|---|
| `PasswordHasher` | `a1$솔트$해시` 형식 생성·검증(상수 시간 비교) |
| `SessionStore` | 최대 8개·30분 RAM 세션 토큰 발급/검증/회수 |
| `TokenStore` | 최대 4개·30일 RAM 리프레시 토큰(HMAC 해지로만 저장, 1회용 회전) |
| `LoginGuard` | 위 셋 + 정적 API 키 + 5회 실패 시 60초 잠금을 묶은 파사드 |
| `[PublicEndpoint]` | 인증 ON 시에도 통과시킬 핸들러 표시 마커(기본은 전부 보호) |
| `GuardedWebServer` | `WebServer` 파생 클래스. 라우팅 전에 `LoginGuard`를 연결해 fail-closed 인증을 적용 |
| `WebApi` | JSON/텍스트 응답·본문 읽기·JSON 이스케이프·보안 헤더 헬퍼 (`WebApi.OkJson` = `{"ok":true}` 상수) |
| `JsonWriter` | 리플렉션 없는 수동 JSON 생성기. 따옴표·이스케이프·쉼표·중첩을 대신 관리 |
| `FormReader` | urlencoded 본문을 1회만 읽어 필드를 여러 개 파싱(이중 읽기 행업 방지) |
| `CsrfGuard` | 커스텀 헤더 + Host 검사로 크로스사이트 POST·DNS rebinding 차단 |
| `AuthController` | `/api/auth/*` 표준 인증 엔드포인트 6종(status·login·logout·token·password·apikey). 훅과 메시지 필드로 앱에 맞춤 |
| `CredentialFile` | `LoginGuard` 해시·API 키를 `h=`/`k=` 파일에 원자적(load/save) 영속화 |

## 동작 원칙

- **자격 증명(비밀번호·API 키) 미설정 = 완전 개방**: 둘 다 비어 있으면 `CheckAccess`는 항상 `Open`을 반환합니다.
  기존 자동화·스크립트를 깨뜨리지 않는 기본값입니다. 하나라도 설정되면 모든 엔드포인트가 보호됩니다(fail-closed).
- **재부팅 = 세션 소멸**: 토큰은 RAM에만 있어 기기가 재시작하면 로그인이 필요합니다.
  해시와 API 키는 앱이 파일을 로드하면 그대로 유효합니다.
- **영속화는 선택 사항**: 라이브러리는 자동으로 파일을 쓰지 않습니다.
  원하면 `CredentialFile.Load/Save`로 해시·API 키를 한 줄에 파일과 동기화할 수 있습니다.
- **선언적 접근 제어**: `[PublicEndpoint]`는 마커이며, 해석은 `GuardedWebServer`가
  `InvokeRoute` 오버라이드에서 매칭된 핸들러의 특성을 읽어 적용합니다.
  특성이 없는 새 엔드포인트는 자동으로 보호됩니다(fail-closed).
  거부 시 `401 unauthorized`(잠금 시 `429`)를 응답합니다.
- **클라이언트 단절 흡수**: 응답 도중 브라우저가 연결을 끊으면(fetch 취소·타임아웃)
  쓰기에서 `SocketException`/`IOException`이 날 수 있습니다. `GuardedWebServer.InvokeRoute`
  와 `WebApi` 쓰기 경로가 이 계열만 잡아 연결을 닫고, 나머지 예외는 그대로 전파합니다.
  덕분에 VS 디버거 첨 중에도 요청 스레드가 앱 전체를 멈추지 않습니다.
- **토큰 전달 경로**: `GuardedWebServer.ReadSessionToken`은 세션 헤더 →
  `Authorization: Bearer` → 세션 쿠키 순으로 찾습니다. 헤더·쿠키 이름은 속성으로
  바꿀 수 있습니다(기본값 `x-auth-session` / `x-auth-key` / `auth`).
  `CsrfGuard.Check(context, markerHeader, localAddress)`의 마커 헤더 이름도 호스트가
  결정하며, 차단 문구는 `CsrfGuard.BlockedMessage`로 현지화할 수 있습니다.

## 해시 구현 주의 (nanoFramework)

`PasswordHasher`는 반드시 **정적** `HMACSHA256.HashData(key, data)`만 사용합니다.
ESP32 nanoFramework에서 `new HMACSHA256(key)` + `ComputeHash` 인스턴스 경로는
키 스케줄링 결함으로 **항상 0 출력**이 되는 것을 실기기에서 확인했습니다
(솔트가 전부 0이 되어 로그인이 불가능해지는 증상으로 나타남).
순수 관리형 SHA-256 구현도 검증했으나 1000반복당 약 35초로 실사용 불가,
정적 경로는 0.3초 수준입니다.

## 토큰 발급 (Access/Refresh)

`LoginGuard.TryLogin(password, out access, out refresh)` 오버로드와 `TryRefresh(...)`로
OAuth의 password/refresh_token grant와 동일한 흐름을 만들 수 있습니다.

- **액세스 토큰**은 `SessionStore` 세션(30분)과 동일 객체라 쿠키·세션 헤더와
  함께 `Authorization: Bearer <token>`으로도 인정됩니다(파싱은 GuardedWebServer가 담당).
- **리프레시 토큰**은 128비트 랜덤 값이며 저장소에는 HMAC-SHA256 해지만 남습니다.
  교환(`TryRefresh`)마다 이전 토큰이 즉시 폐기되는 **회전(rotation)** 방식입니다.
- 비밀번호가 바뀌면 `PasswordHash` setter가 세션과 리프레시 토큰을 **전량 회수**합니다.
- 리프레시 토큰도 RAM 전용이라 재부팅하면 전부 무효화됩니다.

## 엔트로피 주의

nanoFramework에는 하드웨어 난수 생성기가 노출되어 있지 않습니다.
`SessionStore`·`TokenStore`·솔트는 `Guid.NewGuid`+`Environment.TickCount64`를 HMAC-SHA256으로 확장해 만듭니다.
LAN 내 평문 HTTP용 인증에는 충분하지만, 인터넷 노출 환경에서는 TLS와 함께 사용하십시오.

## 검증

PC 미러(`.csproj`)로 `PasswordHasher.Create/Verify` 왕복과 `LoginGuard` 상태 전이를 확인할 수 있습니다.

## 사용 예

```csharp
LoginGuard guard = new LoginGuard();
guard.Password = PasswordHasher.Create("s3cret!pass");

Type[] controllers = { typeof(MyController) };
GuardedWebServer server = new GuardedWebServer(80, controllers, guard)
{
    SessionCookieName = "mysess"
};
server.CommandReceived += (s, e) => { /* 미라우팅 경로 처리 */ };
server.Start();

// 컨트롤러 핸들러 안에서
protected void Handle(WebServerEventArgs e)
{
    if (CsrfGuard.Check(e.Context, "x-mymarker", Ethernet.GetLocalIP()))
        return;

    string pw = FormReader.Read(e.Context.Request, 768, "password");
    string token;
    if (guard.TryLogin(pw, out token) == LoginResult.Success)
    {
        server.SetSessionCookie(e.Context.Response, token);
        WebApi.Json(e.Context.Response, 200, WebApi.OkJson);
    }
    else
    {
        WebApi.Error(e.Context.Response, 401, "wrong password");
    }
}
```

## JSON 응답 만들기 (JsonWriter)

JSON을 직접 만드는 일은 항상 번거롭다. `"{\"ok\":true,\"name\":\"" + WebApi.JsonEscape(s) + "\"}"` 식의
문자열 연결은 따옴표와 이스케이프를 수작업으로 맞추다 한 군데만 틀려도 응답 전체가 망가지고, 여러 조각을
`Substring(1)`로 잘어 붙이는 우회까지 부르기 쉽다. 자동 직렬화기는 nanoFramework의 리플렉션·힙 제약 때문에
의도적으로 제공하지 않는다. 대신 리플렉션 없는 수동 생성기 `JsonWriter`가 그 몫을 대신한다. 키와 값을
순서대로 적어 넣으면 따옴표·이스케이프·쉼표·중첩을 스스로 관리한다.

```csharp
JsonWriter w = new JsonWriter();
w.BeginObject()
 .Prop("ok", true)
 .Prop("uptime", uptimeSec)                 // 숫자
 .Prop("authEnabled", guard.IsEnabled)       // bool
 .Prop("name", deviceName)                   // 문자열(자동 이스케이프)
 .Prop("limits").BeginObject()               // 중첩 객체
   .Prop("nameMax", 30)
 .EndObject()
 .PropArr("settingsKeys", new string[]{ "deviceName", "pollMs" })  // 문자열 배열
 .PropRaw("capabilities", "{\"points\":false}")  // 이미 JSON인 조각은 그대로 병합
.EndObject();
WebApi.Json(e.Context.Response, 200, w.ToString());
```

- `Prop(name, 값)` — 키·값 한 쌍. 값 타입에 따라 `Str`/`Num`/`Bool`이 자동 선택된다.
- `Prop(name)`만 부르고 `BeginObject/BeginArray/Str/Num/Bool/Raw`로 값을 잇는 체이닝도 된다(중첩·배열용).
- `PropArr(name, items)` — 문자열 배열 한 번에.
- `PropRaw(name, json)` — 다른 곳에서 만든 유효 JSON 조각 병합. 이스케이프 없이 그대로 들어간다.
- `Null()`은 `null`, 빈 배열/객체는 `BeginArray().EndArray()`처럼 바로 닫으면 된다.
- 값이 아니라 구조가 고정된 단순 응답은 `WebApi.OkJson` 상수(`{"ok":true}`)를 그대로 쓴다.

주의: `Prop(name, long)`과 `Prop(name, int)`가 모두 있어 정수 인자는 타입에 맞게 자동 선택되지만,
`uptimeSec`처럼 `long` 값은 `Num`/`Prop`에 그대로 넣으면 JSON 숫자로 나간다. `MaxDepth`(8)를 넘는
중첩은 `InvalidOperationException`을 던진다.

## 인증 엔드포인트 열기 (AuthController)

로그인 화면(`Pages\login.nf.*`)이 이미 `/api/auth/status`·`/api/auth/login`을 호출하므로,
 컨트롤러 배열에 `typeof(AuthController)`를 넣고 정적 필드만 배선하면 인증 API가 완성된다.
수동으로 핸들러를 쓰던 샘플/게이트웨이의 중복 코드는 이 한 클래스로 대체할 수 있다.

```csharp
GuardedWebServer server = new GuardedWebServer(80,
    new Type[] { typeof(MyController), typeof(AuthController), typeof(WebUiRoutes) }, guard);
server.Start();

AuthController.Server = server;
AuthController.CsrfMarker = "x-mymarker";               // status 응답의 csrfHeader로 전달됨
AuthController.StatusExtras = new AuthStatusExtrasWriter(MyApp.WriteExtras); // 상태에 앱 필드 병합(선택)
AuthController.Persist = new AuthCredentialPersist(() => { CredentialFile.Save(guard, Path); return null; });
AuthController.OnRequest = new AuthRequestObserver(MyApp.Observe); // 모든 인증 요청 관찰(선택)
AuthController.MsgBadPassword = "비밀번호가 올바르지 않습니다. 남은 시도 {0}회."; // 현지화(선택)

// 네트워크가 주소를 잡을 때마다:
AuthController.LocalAddress = ip;                       // CSRF Host 검사에 필수
```

| 엔드포인트 | 인증 | 설명 |
|---|---|---|
| `GET /api/auth/status` | 불필요 | `authEnabled/loggedIn/lockedOut/remaining/lockSec/csrfHeader` + `StatusExtras` 병합 필드 |
| `POST /api/auth/login` | 불필요(CSRF) | `password` 폼 필드 → 세션 쿠키 설정. 실패 401/429 |
| `POST /api/auth/logout` | 불필요(CSRF) | 세션 회수 + 쿠키 삭제 |
| `POST /api/auth/token` | 불필요(CSRF) | `grant_type=password|refresh_token` → Bearer/access/refresh JSON(회전 포함) |
| `POST /api/auth/password` | **필요** | 비밀번호 교체(공백이면 해제). 성공 시 현재 브라우저는 새 세션으로 로그인 유지 |
| `POST /api/auth/apikey` | **필요** | 정적 API 키 교체(공백이면 해제) |

- `password`·`apikey` 변경은 `Persist` 훅을 부르고, 훅이 오류 문자열을 반환하면 `500`으로 응답한다(메모리만 바뀜).
- 6개 엔드포인트 진입점에서 항상 `OnRequest` 훅이 먼저 불린다(CSRF 검사 이전). 유휴 타이머 갱신·`LocalAddress` 재동기화 같은 앱 고유의 관측 작업에 연결한다.
- 메시지는 정적 필드라 앱 시작에서 언어별로 바꿀 수 있다(기본값 영어). `{0}`에는 남은 초/시도 횟수가 들어간다.
- `Server`·`LocalAddress` 미설정 시 각각 `500`(배선 오류 알림)·전 POST 차단으로 드러나 조용히 열리지 않는다.

## CredentialFile

`h=<해시>` / `k=<키>` 두 줄 형식 파일로 `LoginGuard` 자격 정보를 옮긴다. 저장은 임시의존 이름(`path.tmp`)에
 쓴 뒤 기존 파일을 지우고 rename하는 순서라, 전원 단절이 중간에 일어나도 반쪽(잘린) 파일이 남지 않는다.
 단, 정전 타이밍에 따라 파일이 잠시 없는 상태가 생길 수 있으며 이 경우 다음 저장이 새 파일로 만들어 기본값으로 돌아간다.

```csharp
if (!CredentialFile.Load(guard, "I:\\device.auth"))
{
    guard.SetPassword("first-run-default");   // 최초 실행 초기화
    CredentialFile.Save(guard, "I:\\device.auth");
}
```

`Save`는 성공 시 `null`, 실패 시 사유를 돌려주어 `AuthController.Persist` 훅에 바로 연결할 수 있다.

