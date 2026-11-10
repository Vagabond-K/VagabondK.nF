# VagabondK.nF.WebAuth

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
| `WebApi` | JSON/텍스트 응답·본문 읽기·JSON 이스케이프·보안 헤더 헬퍼 |
| `FormReader` | urlencoded 본문을 1회만 읽어 필드를 여러 개 파싱(이중 읽기 행업 방지) |
| `CsrfGuard` | 커스텀 헤더 + Host 검사로 크로스사이트 POST·DNS rebinding 차단 |

## 동작 원칙

- **자격 증명(비밀번호·API 키) 미설정 = 완전 개방**: 둘 다 비어 있으면 `CheckAccess`는 항상 `Open`을 반환합니다.
  기존 자동화·스크립트를 깨뜨리지 않는 기본값입니다. 하나라도 설정되면 모든 엔드포인트가 보호됩니다(fail-closed).
- **재부팅 = 세션 소멸**: 토큰은 RAM에만 있어 기기가 재시작하면 로그인이 필요합니다.
  해시와 API 키는 앱이 파일을 로드하면 그대로 유효합니다.
- **영속화는 앱 책임**: 이 라이브러리는 파일 저장·로드를 하지 않습니다.
- **선언적 접근 제어**: `[PublicEndpoint]`는 마커이며, 해석은 `GuardedWebServer`가
  `InvokeRoute` 오버라이드에서 매칭된 핸들러의 특성을 읽어 적용합니다.
  특성이 없는 새 엔드포인트는 자동으로 보호됩니다(fail-closed).
  거부 시 `401 unauthorized`(잠금 시 `429`)를 응답합니다.
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
