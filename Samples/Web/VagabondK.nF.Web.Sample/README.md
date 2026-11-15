# VagabondK.nF.Web.Sample — WebUi + Web 조합 샘플

[VagabondK.nF.WebUi](../../../Source/Web/VagabondK.nF.WebUi/README.md)(빌드타임 웹 페이지 생성)와
[VagabondK.nF.Web](../../../Source/Web/VagabondK.nF.Web/README.md)(로그인·API 키·CSRF·세션 인증)를
함께 쓰는 **가장 작은 참조 샘플**이다. 게이트웨이 없이도 단독으로 동작하며,
"내 장치에 웹 관리 화면을 붙이려면 이 파일을 이렇게 고치면 된다"는 출발점을 제공한다.

## 구성

```
VagabondK.nF.Web.Sample\
├── VagabondK.nF.Web.Sample.nfproj   WebUi targets를 import하는 최소 nfproj(WebUiId/Brand/Theme/LinkCss 설정)
├── Program.cs                        네트워크 대기 → GuardedWebServer(80) 시작 + AuthController 배선
├── Controllers.cs                    라우트 핸들러(페이지/자산/상태/설정) + 설정·인증 파일 영속화
└── WebUi\
    ├── index.nf.html                 루트 페이지(페이지 모드): manifest + 탭 + 조립 + 폴러만 남긴 얇은 셸
    ├── theme.css                     라이브러리 기본 테마(청록)를 slate 팔레트로 덮는 :root 토큰 css
    │                                 (WebUiTheme 지정 → 로그인·메인이 /style.css 하나로 공유)
    ├── heartbeat.nf.{html,js,css}    @instance 컴포넌트: 상태·설정 탭에서 재사용(태그마다 shadow root),
    │                                 문구는 uiText로 가져오는 다국어 예시
    ├── panel.nf.{html,js}            프로필 패널: 훅 대신 wui:status/wui:tick 이벤트 구독으로 갱신
    ├── settings.nf.{html,js}         설정 탭: form 컴포넌트가 컨트롤을 렌더하고, 페이지는 검증(check)·저장(save)만
    ├── status-card.nf.html           js 없는 조립 조각: <heartbeat/>+<uptime/>을 나란히 둔 별칭 예시
    └── texts.resx / texts.ko.resx    프로젝트 루트 사전: hb.* 신규 추가 + conn.label 재정의(아래 i18n)
```

- **반복 인스턴스**: heartbeat.nf.html 첫 줄의 `@instance` 마커가 있으면 인라인 병합 대신
  `<wui-heartbeat>` 태그 + `<template>`으로 바뀌고, WUI 런타임이 태그마다 shadow root와
  클로저 상태를 만든다. 같은 컴포넌트를 여러 곳에 써도 id가 충돌하지 않는다(설정 탭에도 하나 더 있음).
- **라이브러리 컴포넌트**: sysinfo·uptime·conn-dot·badge·groupbox·modal·tabs 등은
  `Components\` 표준 라이브러리를 그대로 쓴다(프로젝트에 복사 없음, 참조된 것만 번들).
- **빌드 시점**: `VagabondK.nF.WebUi.targets`가 페이지+컴포넌트를 머지·검증·축소·gzip해서
  `obj\...\WebPage.g.cs`(internal static class)로 생성한다. 마커/manifest 규칙 위반은 컴파일 실패로 드러난다.
- **런타임**: `GET /`는 미인증이면 302로 `/login` 페이지로 보내고(보호 마크업이 익명에게 노출되지 않음),
  인증되면 ETag 재검증 후 gzip 파트를 스트림에 직접 쓴다. `login` 라우트도 `[PublicEndpoint]`이며
  이미 로그인된 클라이언트는 `/`로 되돌린다. 페이지 안의 401은 core.js가 `/login`으로 리다이렉트한다.
  `[PublicEndpoint]`가 없는 `api/*`는 `GuardedWebServer`가 자동 401 차단(fail-closed).
  모든 POST는 `CsrfGuard`(마커 헤더 `x-web-sample` + Origin 지역 주소)를 통과해야 한다.

## 사용자 정의 다국어(i18n)

이 샘플은 프로젝트 루트 `WebUi\texts.resx`(en 원문)와 `WebUi\texts.ko.resx`(번역)로
라이브러리 사전을 직접 확장·재정의한다. 빌드 시 프로젝트 루트가 라이브러리
`Components\` 사전보다 우선하여 **키 단위**로 덮어쓴다.

- `hb.checking` / `hb.title`: 앱 전용 신규 키. `heartbeat.nf.js`가 `uiText('hb.title', { n: … })`로 읽고
  en 원문은 빌드가 두 번째 인자로 자동 주입하므로 호출부에는 키만 적는다.
- `conn.label`: 라이브러리 키를 의도적으로 재정의한 예 — conn-dot 문구가
  "연결(샘플 재정의)" / "Connected (sample override)"로 바뀐 것을 화면에서 확인할 수 있다.
- 빌드가 아니라 런타임에 사전만 보강/재정의하려면 페이지 스크립트에서
  `uiAddTexts('ko', { 'hb.title': '커스텀 문구 · {n}초' })`를 호출하면 된다.
- 언어 선택은 `localStorage['wui.lang']` > 브라우저 언어 순이고, `uiSetLang('en')`으로 전환한다.

## 빌드

저장소 루트에서:

```powershell
msbuild Samples\Web\VagabondK.nF.Web.Sample\VagabondK.nF.Web.Sample.nfproj /t:Rebuild /p:Configuration=Release
```

## 실장치 배포

ESP32 계열 보드에 저장된 Wi-Fi가 잡힌 상태에서:

```powershell
nanoff --deploy --image Samples\Web\VagabondK.nF.Web.Sample\bin\Release\VagabondK.nF.Web.Sample.bin --target ESP32_S3_QUAD --serialport COM3 --reset
```

--image는 반드시 **빌드가 만든 .bin 배포 이미지**(PE+어셈블리 전체)여야 합니다.
`.pe` 단편 배포는 기기에 이미 같은 어셈블리가 있을 때만 동작하므로 주의.
플래시 후 부팅에 약 35초 걸린다.

장치 IP로 브라우저 접속 → 로그인. **기본 비밀번호는 `sample1234`**이며
첫 실행 시 `I:\web-sample.auth`에 해시로 저장된다(실사용 전 변경 필수).

## 동작

| 화면 | API | 설명 |
|---|---|---|
| 로그인 페이지(`/login`) | `POST api/auth/login` | 비밀번호 → 세션 쿠키(`wsauth`) 발급. 실패 5회 시 60초 잠금. 미인증 `GET /`는 여기서 302 |
| 상태 탭 | `GET api/status` | sysinfo(가동/힙/요청 수) + heartbeat 인스턴스 + panel(프로필 패널) |
| 설정 탭 | `GET/POST api/settings` | 폼 렌더·변경 감지·저장 확인은 form 컴포넌트(`WebUi.autoForm`), `I:\web-sample.cfg` 영속화 |
| 로그아웃 | `POST api/auth/logout` | 세션 폐기 + 쿠키 삭제 |

## 내 프로젝트로 확장하기

1. `WebUi\index.nf.html`의 첫 줄 `@route:`과 탭 구성을 바꾼다. 매니페스트 `id`는 csproj의 `<WebUiId>` 속성이고,
   폼 필드는 페이지·컴포넌트 마크업의/ `data-label`/`data-fmt`에서 빌드 타임에 유도된다.
2. 화면 조각은 `이름.nf.html`(+선택 `.nf.js`/`.nf.css`) 컴포넌트로 빼고
   페이지에서 `<이름/>` 한 줄로 조립한다. 여러 곳에서 쓸 컴포넌트는 `@instance` 마커 줄을 붙인다.
3. `Controllers.cs`의 `SampleConfig`/`Status`/`SettingsPost`를 실제 장치 값에 맞춰 확장한다.
4. 페이지 코드에는 폴러(`refresh`/`tick`)와 조립만 남기고, 기계장치(bindForm·transport)는
   core.js가 담당하므로 손대지 않는 것이 WebUi의 의도다.

## 보안 참고

- 이 샘플은 HTTPS를 제공하지 않는다. 같은 LAN 전용으로 쓰고, 공개망에서는 리버스 프록시 뒤에만 두세요.
- API 키 인증도 지원한다: `web-sample.auth`의 `k=` 줄에 값을 넣으면 `x-web-sample-key` 헤더로 호출 가능.
- 설정 파일·인증 파일은 장치 파일시스템(`I:\`)에 평문(해시)으로 저장된다.
