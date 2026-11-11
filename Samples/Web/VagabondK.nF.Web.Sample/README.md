# VagabondK.nF.Web.Sample — WebUi + WebAuth 조합 샘플

[VagabondK.nF.WebUi](../../../Source/Web/VagabondK.nF.WebUi/README.md)(빌드타임 웹 페이지 생성)와
[VagabondK.nF.WebAuth](../../../Source/Web/VagabondK.nF.WebAuth/README.md)(로그인·API 키·CSRF·세션 인증)를
함께 쓰는 **가장 작은 참조 샘플**이다. 게이트웨이 없이도 단독으로 동작하며,
"내 장치에 웹 관리 화면을 붙이려면 이 파일을 이렇게 고치면 된다"는 출발점을 제공한다.

## 구성

```
VagabondK.nF.Web.Sample\
├── VagabondK.nF.Web.Sample.nfproj   WebUi targets를 import하는 최소 nfproj(WebUiPage=index.html)
├── Program.cs                        네트워크 대기 → GuardedWebServer(80) 시작
├── Controllers.cs                    라우트 핸들러(페이지/인증/상태/설정) + 설정·인증 파일 영속화
└── WebUi\
    ├── index.html                    루트 페이지: 얇은 셸(CSS + 헤더/탭 + 조립 + 폴러만 남김)
    ├── login.nf.{html,js,css}        로그인 오버레이. showLogin은 페이지의 checkAuth도 호출(함수 호이스트)
    ├── sysinfo.nf.{html,js}          상태 목록: wui:tick 구독해 dl 채우기. dur/kb/fillDl가 여기 속함
    ├── settings.nf.{html,js}         설정 폼: data-label/data-fmt 스키마 유도, 기계장치는 WebUi.bindForm
    ├── panel.nf.{html,js}            프로필 패널: 훅 대신 wui:status/wui:tick 이벤트 구독
    ├── status-card.nf.html           줄 하나가 `<heartbeat/>` 전부 — 컴포넌트를 조합하는 별칭 예시
    ├── heartbeat.nf.{html,js,css}    @instance 컴포넌트: 상태 탭·설정 탭 두 곳에서 재사용(태그마다 shadow root)
    └── note.nf.{html,css}            heartbeat 안의 조각(js 없음) — 이름 = `.nf` 앞 stem, 폴더는 자유
```

- **반복 인스턴스**: heartbeat.nf.html 첫 줄의 `<!--@instance-->` 마커가 있으면 인라인 병합 대신
  `<wui-heartbeat>` 태그 + `<template>`으로 바뀌고, WUI 런타임이 태그마다 shadow root와
  클로저 상태를 만든다. 같은 컴포넌트를 여러 곳에 써도 id가 충돌하지 않는다(아래 설정 탭에도 하나 더 있음).
- **빌드 시점**: `VagabondK.nF.WebUi.targets`가 페이지+컴포넌트를 머지·검증·축소·gzip해서
  `obj\...\WebPage.g.cs`(internal static class)로 생성한다. 마커/manifest 규칙 위반은 컴파일 실패로 드러난다.
- **런타임**: `GET /`는 ETag 재검증 후 gzip 파트를 스트림에 직접 쓴다.
  `[PublicEndpoint]`가 없는 `api/*`는 `GuardedWebServer`가 자동 401 차단(fail-closed).
  모든 POST는 `CsrfGuard`(마커 헤더 `x-web-sample` + Origin 지역 주소)를 통과해야 한다.

## 빌드

저장소 루트에서:

```powershell
msbuild Samples\Web\VagabondK.nF.Web.Sample\VagabondK.nF.Web.Sample.nfproj /t:Rebuild /p:Configuration=Release
```

## 실장치 배포

ESP32 계열 보드에 저장된 Wi-Fi가 잡힌 상태에서:

```powershell
nanoff --image Samples\Web\VagabondK.nF.Web.Sample\bin\Release\VagabondK.nF.Web.Sample.bin --serialport COM3 --deploy --reset
```

장치 IP로 브라우저 접속 → 로그인. **기본 비밀번호는 `sample1234`**이며
첫 실행 시 `I:\web-sample.auth`에 해시로 저장된다(실사용 전 변경 필수).

## 동작

| 화면 | API | 설명 |
|---|---|---|
| 로그인 오버레이 | `POST api/auth/login` | 비밀번호 → 세션 쿠키(`wsauth`) 발급. 실패 5회 시 60초 잠금 |
| 상태 탭 | `GET api/status` | sysinfo(가동/힙/요청 수) + heartbeat 인스턴스 + panel(프로필 패널) |
| 설정 탭 | `GET/POST api/settings` | 폼 변경 감지·저장 확인은 `WebUi.bindForm`, `I:\web-sample.cfg` 영속화 |
| 로그아웃 | `POST api/auth/logout` | 세션 폐기 + 쿠키 삭제 |

## 내 프로젝트로 확장하기

1. `WebUi\index.html`의 manifest(`<!--@manifest-->`) `id`와 탭 구성을 바꾼다.
   폼 필드는 컴포넌트 markup의 `data-label`/`data-fmt`에서 빌드 타임에 유도된다.
2. 화면 조각은 `이름.nf.html`(+선택 `.nf.js`/`.nf.css`) 컴포넌트로 빼고
   페이지에서 `<이름/>` 한 줄로 조립한다. 여러 곳에서 쓸 컴포넌트는 `<!--@instance-->` 마커를 붙인다.
3. `Controllers.cs`의 `SampleConfig`/`Status`/`SettingsPost`를 실제 장치 값에 맞춰 확장한다.
4. 페이지 코드에는 폴러(`refresh`/`tick`)와 조립만 남기고, 기계장치(bindForm·transport)는
   core.js가 담당하므로 손대지 않는 것이 WebUi의 의도다.

## 보안 참고

- 이 샘플은 HTTPS를 제공하지 않는다. 같은 LAN 전용으로 쓰고, 공개망에서는 리버스 프록시 뒤에만 두세요.
- API 키 인증도 지원한다: `web-sample.auth`의 `k=` 줄에 값을 넣으면 `x-web-sample-key` 헤더로 호출 가능.
- 설정 파일·인증 파일은 장치 파일시스템(`I:\`)에 평문(해시)으로 저장된다.
