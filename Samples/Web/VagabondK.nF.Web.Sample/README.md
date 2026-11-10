# VagabondK.nF.Web.Sample — WebUi + WebAuth 조합 샘플

[VagabondK.nF.WebUi](../../../Source/Web/VagabondK.nF.WebUi/README.md)(빌드타임 웹 페이지 생성)와
[VagabondK.nF.WebAuth](../../../Source/Web/VagabondK.nF.WebAuth/README.md)(로그인·API 키·CSRF·세션 인증)를
함께 쓰는 **가장 작은 참조 샘플**이다. 게이트웨이 없이도 단독으로 동작하며,
"내 장치에 웹 관리 화면을 붙이려면 이 파일을 이렇게 고치면 된다"는 출발점을 제공한다.

## 구성

```
VagabondK.nF.Web.Sample\
├── VagabondK.nF.Web.Sample.nfproj   WebUi targets를 import하는 최소 nfproj
├── Program.cs                        네트워크 대기 → GuardedWebServer(80) 시작
├── Controllers.cs                    라우트 핸들러(페이지/인증/상태/설정) + 설정·인증 파일 영속화
└── WebUi\
    ├── shell.html                    미니 셸(로그인 오버레이 + 상태/설정 탭 + 5개 머지 마커)
    └── profile.html                  프로필 "web-sample"(manifest + 폼 + 4개 훅)
```

- **빌드 시점**: `VagabondK.nF.WebUi.targets`가 셸+프로필을 머지·검증·축소·gzip해서
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
| 상태 탭 | `GET api/status` | 가동 시간·힙·요청 수 + 프로필 패널(`profileRender`/`profileTick`) |
| 설정 탭 | `GET/POST api/settings` | 폼 변경 감지·저장 확인(`checkSettings`/`syncForm`), `I:\web-sample.cfg` 영속화 |
| 로그아웃 | `POST api/auth/logout` | 세션 폐기 + 쿠키 삭제 |

## 내 프로젝트로 확장하기

1. `WebUi\profile.html`의 manifest `id`와 form 필드를 바꾼다(폼 name은 `@views`에 그대로 존재해야 함).
2. `@script`의 4개 훅(`profileRender`/`profileTick`/`checkSettings`/`syncForm`)을 새 필드에 맞게 고친다.
   포인트 테이블이 필요하면 `features.points`를 켜고 `profilePoint`를 추가한다.
3. `Controllers.cs`의 `SampleConfig`/`Status`/`SettingsPost`를 실제 장치 값에 맞춰 확장한다.
4. 셸(`shell.html`)은 보통 그대로 두고 프로필만 교체하는 것이 WebUi의 의도다.

## 보안 참고

- 이 샘플은 HTTPS를 제공하지 않는다. 같은 LAN 전용으로 쓰고, 공개망에서는 리버스 프록시 뒤에만 두세요.
- API 키 인증도 지원한다: `web-sample.auth`의 `k=` 줄에 값을 넣으면 `x-web-sample-key` 헤더로 호출 가능.
- 설정 파일·인증 파일은 장치 파일시스템(`I:\`)에 평문(해시)으로 저장된다.
