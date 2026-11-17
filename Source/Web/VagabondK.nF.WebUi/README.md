# VagabondK.nF.WebUi

[Web 그룹](../README.md) · [전체 프로젝트](../../../README.md)

nanoGate 스타일 장치 관리 화면을 빌드 타임에 조립하는 페이지 컴포저입니다.
공용 **셸**(탭·로그인·fetch·다이얼로그·포인트 편집기) 하나와 게이트웨이별 작은 **프로필** 하나를
머지·검증·축소·gzip해 **nanoFramework 이진 리소스**(바이트 파트)로 임베드합니다 —
런타임 리플렉션 없음, 외부 자산 없음, 문자열 힙 사용 없음.

장치에서 실행되는 코드는 없으므로 생성·조립은 전부 빌드 타임의 **MSBuild 작업**입니다.
`.targets`가 셸·프로필·`Assets/core.js`·테마/컴포넌트 폴더를 입력으로 `GenerateWebUiResourcesTask`
(`..\VagabondK.nF.WebUi.Tasks\`의 netstandard2.0 어셈블리)를 실행하면, 작업이 페이지를 조립해
`.resx`(gzip 바이트 파트)와 `WebPage` Designer 소스를 만들어 컴파일에 주입합니다.
(한때 Roslyn 소스 생성기로 만들었으나 64KB 힙 장치에서 리터럴 분해 비용이 커 2026-11에 이 방식으로 옮겼습니다.)

## 파일

| 파일 | 역할 |
|---|---|
| `Assets/core.js` | 런타임 라이브러리(`<nf-core/>`에 주입): 매니페스트 접근, 폼 팩토리, 값 포맷, 마이크로 vDOM, 컴포넌트 레지스트리 |
| `Assets/theme.css` | **기본 테마**(LibreOffice계 토큰). 소비자가 `WebUiTheme`을 지정하지 않으면 이 파일이 `/style.css`가 됩니다 |
| `Assets/Login/` | 독립 로그인 페이지 자산(login.html/css/js — 빌드TIME 조립) |
| `VagabondK.nF.WebUi.targets` | MSBuild 배선: `WebUiBuildTask`(작업 어셈블리 빌드) → `WebUiGenerateResources`(`PrepareResources` 직전 실행) → resx/Designer 생성·주입, `CustomAdditionalCompileInputs`로 변경 추적 |
| `..\VagabondK.nF.WebUi.Tasks\GenerateWebUiResourcesTask.cs` | **생성기 본체**(MSBuild 작업): 머지·매니페스트 검증·축소·gzip·FNV 해시 → `.resx` 바이트 파트 + Designer 소스 출력, WUI000~ 진단 |
| `..\VagabondK.nF.WebUi.Tasks\PageComposer.cs` | 머지·매니페스트 검증·축소·gzip·FNV 해시(작업이 호출) |
| `..\VagabondK.nF.WebUi.Tasks\MiniJson.cs` | 매니페스트 파싱/직렬화용 최소 JSON 구현 |
| `WebUiGenerator.cs` | (레거시) 과거 Roslyn 생성기 잔재 — 빌드에서 사용되지 않음 |

## 소스 서식과 빌드 축소

소스(`index.html`, `*.nf.{html,js,css}`)는 **들여쓰기와 주석을 자유롭게** 써도 됩니다.
빌드 작업이 최종 페이지를 내기 전 한 번 더 축소하므로, 장치에는 공백·주석이 제거된 최소 페이지가 올라갑니다.

축소 규칙(`PageComposer` 최종 패스):
- 모든 줄의 앞뒤 공백(인덴트) 제거.
- 빈 줄 제거.
- `//`로 시작하는 줄, 한 줄 `<!-- ... -->`/`/* ... */` 주석 제거.
- 여러 줄로 퍼진 `<!-- -->`/`/* */` 주석도 상태 추적해 제거.
- `<script>` 안에서는 `<!--` 를 JS 레거시 한 줄 주석으로 취급(HTML 파싱 규칙과 동일).

주의: `@instance` 컴포넌트의 JS/CSS는 `WUI.def(...)` 호출 한 줄 문자열로 합쳐지므로, 그 안의 줄
앞 공백은 위 최종 패스의 `Trim`이 끝부분만 건드려 남을 수 있습니다. 빌드 작업이 조각을 합칠 때 각 줄을
미리 `Trim`해 이를 막습니다(`TrimLines`). 즉 들여쓰기 제거는 컴포넌트 내부까지 균일하게 적용됩니다.

여백·주석이 줄어드는 만큼 장치 문자열 힙이 절약되지만, **ETag는 축소 전 원문이 아니라 축소·머지된
최종 페이지 문자열의 FNV 해시입니다**. 따라서 주석 하나를 지워도 ETag가 바뀌므로, 정적 캐싱 무효화
기준으로 삼을 때는 이 점을 유의하십시오.

## 마커

페이지 `.nf.html`는 주입 마커를 **HTML 주석이 아닌 요소 표기 단독 줄**로 담습니다
(주석형 `<!--@x-->`는 script/style 안에서 VS TS1206 경고를 내므로 폐기되었습니다):

| 마커 | 위치 | 필수 |
|---|---|---|
| `<nf-style/>` | 페이지 `<style>` 블록(`</style>`) **뒤** — 페이지 고유 CSS가 컴포넌트 CSS 뒤에 적용됩니다 | 필수 |
| `<nf-views/>` | 마크업 중 뷰 주입 자리 | 선택 |
| `<nf-core/>` | 페이지 `<script>` 블록 **앞** — 사전/i18n·MANIFEST·core.js 주입 | 선택(생략 시 `<nf-script/>` 자리에서 주입) |
| `<nf-script/>` | 페이지 `<script>` 블록 **뒤** — 컴포넌트 JS가 페이지 JS보다 먼저 실행됩니다 | 필수 |

`<nf-core/>`가 없으면 코어 주입은 `<nf-script/>` 자리에서 일어나므로, 페이지 훅(cacheRow
오버라이드 등)은 여전히 코어 뒤·마지막 쓰기 우선 규칙을 지킵니다. 마커 줄은 출력에서 사라집니다.

라우트/인스턴스 선언도 bare 줄입니다:
- `@route:/path [login] [public] [none]` — 페이지 파일 **첫 줄**에만 성립.
- `@instance [form]` — 컴포넌트 html의 단독 줄(`form`은 폼 연관 커스텀 엘리먼트).

manifest는 페이지가 선언하지 않습니다: `id`는 `<WebUiId>` 속성, `form`은 마크업 유도
(`data-label`/`data-fmt` 가진 input/select/textarea 자동 수집), 그 외 자기서술 정보는
컴포넌트/프로젝트의 capabilities JSON이 유일한 출처입니다.

## 페이지 마크업 규약

한 장의 `index.nf.html`(또는 여러 페이지)이 루트 아래 `@route:` 첫 줄로 라우팅됩니다.
페이지는 위 마커를 같은 순서로 담고, 공용 셸 테일(`<nf-views/>`/`<nf-script/>` 이후
마크업·스크립트)은 페이지가 직접 선언합니다. 페이지 고유의 프로파일 스크립트
(`profilePoint`/`profileCacheRow` 등 훅)는 `<nf-script/>`보다 앞의 페이지 `<script>`
블록 어디든 됩니다 — 훅 요구 검사는 페이지 전체(뷰+스크립트)를 대상으로 하므로 위치에
관계없이 발견되고, 출력 순서는 컴포넌트 JS → 페이지 JS로 보장됩니다.
`<nf-style/>` 줄 뒤에는 페이지 고유 CSS를 리터럴로 둘 수 있으며 컴포넌트 CSS 뒤에 적용됩니다.
폼 유도: 폼 필드는 `data-label`(표시 이름)과 선택 `data-fmt`(ms/pin/secret/bool/switch/seg/text)를
가지면 빌드가 MANIFEST.form을 자동 생성합니다. `<form>` 마크업이 전혀 없는 표시 전용
페이지는 form 필드 요구가 면제됩니다.

## 독립 로그인 페이지

로그인은 라이브러리 표준 페이지 `Pages\login.nf.html`(`@route:/login login`)로,
`WebUiComponents` 루트에 포함되면 자동으로 조립됩니다. 축약 UTF-8 단일 항목(gzip 없음 —
서빙 즉시 사용)으로 저장되고, 로그인 전용 가드(`LoginPagePath`)·텍스트 부분집합 인라인이
붙습니다.

색감은 라이브러리 **기본 공유 테마**(`Assets\theme.css`, nanoGate 청록 팔레트 :root 토큰)가
정합니다 — 로그인 css(`login.css`)의 `:root` 기본값과 동일하며, 지정하지 않는 한 프로젝트가
이 테마를 그대로 씁니다. 자기 색감을 쓰려면 `<WebUiTheme>`에 `:root` 토큰 css 파일을
지정해 **덮어쓰면** 됩니다(예: Web.Sample의 slate 테마) — 그러면 **로그인과 메인 페이지가 같은
테마를 공유**합니다. 인라인 모드에서는 두 페이지의 `/*@theme*/`·`<nf-style/>` 자리에 뒤에 주입되어
우선하고(나중에 오는 `:root`가 승리), `WebUiLinkCss` 모드에서는 `/style.css` 자산에 합쳐져
한 번의 다운로드(304 재검증 포함)로 두 페이지에 적용됩니다.

`login.js`는 Web 인증 규약(`/api/auth/status`·`/api/auth/login`, form-urlencoded,
CSRF 마커 헤더)을 따르며 `lockedOut/lockSec`(샘플)와 `locked/lockSeconds`(nanoGate)
필드명 둘 다 호환하고, `<form>` native submit이라 Enter 키 로그인이 동작합니다.
템플릿 파일 자체를 통째로 바꾸려면 `Pages\login.nf.html`(및 짝 `login.nf.js|css`)을
프로젝트 루트에 두어 섀도잉합니다.

## css 자산 링크 (WebUiLinkCss)

`<WebUiLinkCss>true</WebUiLinkCss>`(기본 false)로 설정하면 css가 HTML 인라인 대신
**gzip 정적 자산**으로 분리됩니다. js는 항상 인라인으로 유지합니다(외부화 시 src 라우트·타이밍
의존이 생겨 의도적으로 제외합니다).

- 메인 페이지: `WebUiTheme` 테마 + 컴포넌트 css(`*.nf.css` 병합분)가 `/style.css`로 빠집니다.
  페이지 고유 css(`<nf-style/>` 섹션의 페이지분)은 마커 앞 자리에 계속 인라인이며, link 태그가
  그 사이에 샌드위치로 들어가 cascade 순서(테마·컴포넌트 → 프로필 오버라이드)가 보존됩니다.
- 로그인 페이지: `login.css`(레이아웃·기본 토큰)는 `/*@css*/` 자리에 인라인 유지,
  `/*@theme*/` 자리에 같은 `/style.css` link를 둡니다 — 테마가 뒤에 와 기본값을 덮어쓰고,
  메인 페이지와 파일을 공유하므로 로그인→메인 이동 시 css 재다운로드가 없습니다.
  (`WebUiTheme` 미지정 시에는 예전처럼 로그인 css가 `/login.css`로 분리됩니다.)
- 효과: 페이지 gzip과 로그인 HTML에서 css 중복이 빠져 첫 로드 바이트가 줄고,
  css는 ETag 재검증(304) 대상으로 분리되어 재방문 시 본문 재전송이 없어집니다.

서빙은 생성되는 `WebPage.TryServeStyle(ctx)`(공유 테마 모드) / `WebPage.TryServeLoginCss(ctx)`
(비공유 모드)가 담당합니다(if-none-match → 304, gzip 본문 4KB 청크 스트리밍, Content-Type: text/css,
`Content-Encoding: gzip`). 리소스 미분리 상태(false)면 false를 반환합니다.
소비 프로젝트는 이걸 **`[PublicEndpoint]` 라우트로 붙여야 합니다** — 로그인 화면이
익명 상태에서 스타일을 받아야 하기 때문입니다. 401 JSON 가드에 가려지지 않게 예:

```csharp
[Route("/style.css")] [Method("GET")] [PublicEndpoint]
public void StyleAsset(WebServerEventArgs e)
{
    if (!WebPage.TryServeStyle(e.Context))
        WebServer.OutputHttpCode(e.Context.Response, HttpStatusCode.NotFound);
}
```

ETag는 gzip 바이트가 아니라 **gzip 전 텍스트**의 FNV-1a 해시로, deflate 출력의
비결정성과 무관하게 재현됩니다(페이지 HTML과 동일 규칙). 공용 자산(css/js)의 ETag에는 내용
해시에 더해 **빌드 스탬프**(`"<해시>-<stamp>"`)가 병합되고 헤더는 `Cache-Control: no-cache`로
서빙됩니다 — 자산 내용이 그대로여도 생성 로직 변경으로 남은 낡은(파손) 브라우저 캐시를
무효화하기 위함입니다. 재현성 유지: 스탬프는 bin 리소스 파일에 들어가지 않고 생성 코드(const)에만
씁니다. 페이지 본문 ETag에는 병합하지 않아 304 효율을 지킵니다.

## 컴포넌트 번들링

조각 HTML을 파일로 분리해 빌드 타임에 조립합니다. 참조 표기는 자기종결 태그 `<name/>`(줄 전체일 때만 성립).
셸/프로필의 마크업(뷰·슬롯) 어디에든, 또 컴포넌트 HTML 안에도 넣을 수 있습니다(중첩, 순환 검출).
`<name/>`은 `br`, `hr`, `img`, `input`, `link`, `meta` 등 HTML void 요소명이면 참조로 보지 않고
그대로 HTML로 통과시킵니다. 그 외 이름은 반드시 `name.nf.html` 컴포넌트로 존재해야 합니다.
인스턴스 컴포넌트(`@instance`)는 마크업에 `<wui-name>` 태그를 직접 쓰면 그 자체가 참조가 되어
별도 참조 없이도 자동으로 번들에 포함됩니다. (구 `<!--@use:name-->` 주석 마커는 제거되었습니다.
남아 있으면 빌드 오류로 마이그레이션을 안내합니다.)

```
WebUi\
  heartbeat.nf.html   ← 필수: 이름 = .nf 앞 파일명. <name/> 자리 인라인, 내부에 <다른이름/> 가능
  widgets\
    gauge.nf.css      ← 선택: <nf-style/> 마커 뒤 병합분 *앞쪽*에 합쳐짐(페이지가 오버라이드 가능). gauge.nf.html과 같은 폴더라 짝
    gauge.nf.js       ← 선택: <nf-script/> 마커 자리에서 페이지 JS *앞에* 병합(페이지가 오버라이드 가능)
```

컴포넌트 파일은 **`이름.nf.html` / `이름.nf.js` / `이름.nf.css`** 이중 확장자 규격이고,
루트(`WebUi\`) 아래 어디든 재귀로 수집됩니다. 폴더는 기능별 보관용일 뿐 이름에 영향을 주지
않습니다. 같은 이름의 `.js`/`.css`는 html과 **같은 폴더**에 있을 때만 한 벌로 묶이며,
같은 이름 html이 두 곳에 있으면 빌드 오류입니다. `.nf.` 없는 일반 `.html`/`.js`/`.css`는
컴포넌트로 수집되지 않으므로 루트 안에 셸·프로필 같은 일반 페이지를 함께 두어도 됩니다.
기본 경로는 프로젝트의 `WebUi\`이며 `$(WebUiComponents)`로 바꿀 수 있습니다.
세미콜론으로 여러 루트를 지정하면 앞 루트(프로젝트 로컬)가 뒤 루트(공유)의 같은 이름
컴포넌트를 **섀도잉**합니다
(예: `$(MSBuildProjectDirectory)\WebUi;$(공유)\WebUi`).
컴포넌트를 제품별로 다른 내용으로 바꾸려면 확장 지점 컴포넌트를 만들고 로컬 루트에서
같은 이름으로 덮어씁니다(예: 공유 `points.nf.html`이 `<points-note/>`를 참조, 제품 프로젝트의
`points-note.nf.html`이 섀도잉). 예전 빌드타임 `<!--@slot:name-->` 문법은 폐기되었고,
남은 파일에서 발견하면 빌드가 실패합니다(*컴포넌트와 슬롯* 참조).
컴포넌트는 셸·프로필과 함께 `AdditionalFiles`로 전달되어 파일 수정 시 증분 재생성됩니다.
미지정 이름이나 순환 참조는 WUI001 빌드 오류가 됩니다.

### 컴포넌트 라이브러리

페이지가 참조한 컴포넌트만 번들에 들어하므로(위 DFS), 미리 만들어 둔 컴포넌트를 별도
**라이브러리 루트**에 모아두면 사용하는 것만 장치에 올라갑니다(미사용 = 0바이트).
표준 예는 이 프로젝트에 내장된 [`Components`](Components/README.md) 폴더입니다
(note·sysinfo·panel 등 + 공용 사전 `texts*.resx`). targets가 이 폴더를
`$(WebUiComponents)` **후미에 자동 추가**하므로 별도 지정 없이 그냥 사용하면 됩니다.
끄려면 `WebUiNoSharedComponents=true`.

Web 샘플이 실제로 이 라이브러리를 참조하며, 로컬 `panel.nf.html`로 라이브러리 `panel`을
섀도잉하여 같은 이름의 제품별 대체가 동작함을 보여줍니다.

## @instance 컴포넌트 (Web Components)

컴포넌트 HTML에 `@instance` 단독 줄 마커를 두면 인라인 병합 대신
**인스턴스 컴포넌트**로 번들링됩니다(단독 줄뿐 아니라 태그와 같은 줄도 인식하지만,
첫 줄 단독 표기가 규약입니다). 참조 자리(`<name/>` 줄 또는 `<wui-name>` 태그)는 빈 커스텀
엘리먼트 `<wui-name>` 태그가 되고, 마크업은 페이지 끝의 `<template id="wui-t-name">`에
한 번만 들어갑니다(중복 없음). 런타임에 `WUI.def`이 태그마다 shadow root를 만들어
템플릿을 복제하므로 **id가 인스턴스별로 격리**되고, 짝 `.js`의 최상위 변수가
**인스턴스별 클로저 상태**가 됩니다. 같은 컴포넌트를 여러 곳에 두어도 서로 독립입니다.

```
WebUi\counter\           ← 폴더는 보관용, 이름은 파일 stem이 결정
  counter.nf.html    ← @instance 마커 필수(단독 줄). 마커 없으면 기존 인라인 방식
  counter.nf.js      ← function (root, document, $) 바디로 실행. document/$ 는 그림자 스코프
  counter.nf.css     ← adoptedStyleSheets로 페이지 css(베이스) 뒤에 추가. 실패 시 <style> 폴백
```

```html
<counter/>                    <!-- 단독 줄 참조 -->
<wui-counter></wui-counter>   <!-- 태그를 직접 써도 참조가 된다(같은 줄 혼입 가능) -->
```

* `:root` 커스텀 프로퍼티(`--ac` 등)와 페이지 css는 shadow 안에서도 상속/적용됩니다.
* 셸 전역(`api`, `toast`, `WebUi` 등)은 init에서 그대로 사용 가능하지만,
  셸이 id로 직접 만지는 DOM(`$('v-…')` 등)과는 격리되므로 **기존 셸 결합 컴포넌트는
  마커 없이 인라인 유지**가 맞습니다. 마커는 옵트인입니다.
* **인라인/인스턴스 판별 기준(설계 규칙).** 인스턴스 여부는 사용법이 아니라 컴포넌트
  코드의 계약 속성이므로 구현 단계에서 결정합니다. 전제: 인스턴스 JS는 요소별 스코프
  (`this`, shadow 안의 `$`)를, 인라인 JS는 페이지 전역 단일톤을 가정해 작성되므로
  소비 시점에 두 형태를 오갈 수 없습니다(필요하면 섀도잉으로 파일을 재작성).
  - 반복 사용 또는 요소별 속성 설정이 필요 → `@instance` (상태가 없어도 속성이
    요소별이면 인스턴스가 맞습니다. 예: card, divider)
  - 페이지당 1개이고 전역 API(`WebUi.modal`, `WebUi.autoForm`)나 페이지 마크업
    규약(`section[data-tab]`)으로 노출 → 인라인 (예: modal, tabs, form)
* init은 엘리먼트가 DOM에 붙는 시점에 실행되므로 페이지 JS의 함수를 호출하면
  안 됩니다(초기화 순서). 이벤트 핸들러 안에서 참조는 안전합니다.
* 인스턴스 안의 정적 확장 지점은 빌드타임 `<name/>` 섀도잉으로, 인스턴스별 내용은
  네이티브 shadow `<slot>`(마크업에 자리 예약)으로 표현합니다.
* 인스턴스는 자기종결 태그에 속성을 붙일 수 있습니다: `<conn-dot timeout="6"/>`, `<uptime long/>`.
  `<wui-conn-dot timeout="6"></wui-conn-dot>`로 치환되어 init에서 `this.getAttribute('timeout')`,
  `this.hasAttribute('long')`으로 읽습니다. 속성은 **인스턴스 전용**이며 인라인 컴포넌트
  `<name attr/>`는 빌드 오류입니다. `<name/>` 참조 인식은 줄 전체일 때만 성립하므로 인라인 컴포넌트
  참조는 독립 줄에 두어야 합니다(인라인 혼입 시 조용히 원문이 남습니다). 인스턴스 컴포넌트는
  자리에 `<wui-name attr>` 태그를 직접 쓰면 어디든(같은 줄 혼입 포함) 참조가 됩니다.
* 인스턴스 안의 다른 컴포넌트 참조는 중첩 가능합니다: 인스턴스는 `<wui-name>` 태그, 레거시는 `<name/>`.
* `WUI` 런타임(core.js 끝 블록)은 페이지에 `@instance` 컴포넌트가 실제로 있을 때만
  주입됩니다. 하나도 없으면 센티널(`// ---- WUI:`) 이후가 잘려 나가 nanoFramework
  문자열 힙을 아낍니다.

## 컴포넌트와 슬롯

셸이 프로필을 미리 알지 못한 채로 자기 UI를 놓는 방식입니다. Blazor의 레이아웃/컴포넌트 개념을
정적 페이지에 맞게 축소한 것입니다.

**런타임 함수 컴포넌트** (`Assets/core.js`) — 컴포넌트는 `(props) -> vnode | 배열 | 문자열 | null` 함수입니다.
`WebUi.node(fn, props)`는 인라인으로 렌더하고, `WebUi.component(name, fn)`은 이름으로 등록,
`WebUi.slot(name, props)`는 등록된 것을 렌더합니다. 셸은 자기가 쓰는 명명 슬롯마다 *기본* 컴포넌트를
등록하므로 아무것도 하지 않는 프로필은 그대로 동작하고, `<nf-script/>` 자리 JS(셸 코드보다 나중에 실행)에서
`WebUi.component(name, ...)`을 호출하는 프로필은 기본을 투명하게 오버라이드합니다.

셸의 캐시 테이블이 이 방식을 씁니다: `renderCache`가 앞 컬럼을 `WebUi.slot('cacheRow', {p, cc})`로
렌더하고 공용 값·품질 셀은 셸이 직접 append합니다. 기본 `cacheRow`는 레거시
`profileCacheRow(p)` 문자열 배열 훅(과 ID 폴백)을 브리지하므로 기존 프로필은 무영향이고,
프로필이 `td` vnode를 직접 반환해 마크업을 완전히 통제할 수도 있습니다.

**빌드타임 섀도잉 컴포넌트** (`<name/>`) — 확장 지점을 작은 `name.nf.html` 컴포넌트로 만들고
위치에 `<name/>`로 참조합니다. 제품 프로젝트는 자기 `WebUi\` 폴더에 같은 이름의 파일을 두어
섀도잉합니다(`$(WebUiComponents)`의 앞 루트가 승리). 기본 컴포넌트는 주석 한 줄이면
'내용 없음'이 됩니다. 예전 `<!--@slot:NAME-->`/`@slots` 문법은 폐기되었습니다 —
남은 파일은 빌드 오류로 잡히며, 마이그레이션은 다음 두 단계입니다:

1. 슬롯 내용을 `WebUi\name.nf.html`로 옮기고, 슬롯 토큰 자리에 `<name/>`를 씁니다.
2. 프로필의 `<!--@slots-->` 섹션을 지웁니다. 슬롯을 채우지 않던 제품은 기본(빈) 컴포넌트를
   공유 폴더에 두기만 하면 됩니다.

## MANIFEST (빌드 자동 생성)

페이지는 더 이상 manifest를 선언하지 않습니다. 빌드가 생성하는 `MANIFEST` 상수는 두 필드뿐입니다:

```json
{"id":"modbus-to-mqtt","form":[{"name":"timeout","label":"응답 대기 시간","fmt":"ms"},
                               {"name":"mode","label":"동작 모드","fmt":"bool"}]}
```

* `id` — `/api/settings`에 `gatewayProfile`로 전송되는 게이트웨이 프로필 문자열.
  프로젝트 속성 `<WebUiId>`가 유일한 출처입니다(구 manifest의 `id` 필드 폐기).
* `form` — 순서가 있는 설정 필드. 페이지/컴포넌트 마크업에서 자동 유도됩니다(아래 참조).
  수동 선언용 `<!--@manifest-->` 섹션은 폐기되었고, 발견 시 빌드가 실패합니다.
  `schema`/`features` 필드도 폐기: 스키마 버전은 장치 측 상수, 기능 플래그는
  프로젝트 capabilities JSON이 `/api/status`로 내려주는 런타임 `CAPS`의 유일한 출처이며
  셸은 `WebUi.feat(name, 기본값)`으로 읽습니다.

### form 자동 유도 (data-label / data-fmt)

마크업의 컨트롤에 두 속성 중 하나라도 있으면 그 필드가 form 항목으로 유도됩니다:

```html
<input id="sf_deviceName" name="deviceName" data-label="장치 이름" data-fmt="text">
<input id="sf_pollMs" name="pollMs" type="number" data-label="폴링 주기" data-fmt="ms">
```

* `label`은 `data-label`, 없으면 `name`으로 결정되고, `fmt`는 `data-fmt` 값입니다.
* 수동 `manifest.form`과 병존할 수 있습니다. 같은 `name`이 양쪽에 있으면 빌드가 실패합니다
  ("keep one source"). 수동 항목이 먼저, 유도 항목이 문서 순서로 뒤에 배치됩니다.
* 유도된 항목은 빌드 결과의 MANIFEST에 기존 form과 구분 없이 합쳐져 주입되므로
  런타임(core.js)은 차이를 모릅니다.
* Components 라이브러리의 `<form/>` 컴포넌트를 사용하면 컨트롤이 마크업이 아니라
  `manifest.form`으로 런타임에 렌더되므로 "views에 없는 form 이름" 검사는 자동 생략됩니다
  (사용 중인 컴포넌트에 `form`이 있으면 빌드 작업이 건너뜁니다).

빌드 타임 검증은 다음에서 컴파일을 실패시킵니다: 알 수 없는 매니페스트 키, 잘못된 id/schema,
WebUiId·manifest.id 충돌, form 이름 중복·누락, views에 없는 form 이름(`<form/>` 컴포넌트 사용 시 생략), 수동 form과 data-*의 이름 충돌,
`data-fmt` 오탈자, 미정의 컴포넌트 이름(`<name/>`/`<wui-name>`)·순환 참조, 레거시 전역
(`PROFILE`, `KEYS`, `LABELS`, `profileAddr`, ...), 필수 훅 누락, 셸에 없는 슬롯의 프로필 채움.

## 프로필 훅

필수: `profileRender(status)`, `profileTick(uptimeSeconds)`, `checkSettings(values)`,
`syncForm()`. 선택: `profilePoint()`(새 포인트 기본값), `profileCacheRow(p)`
(`features.cacheCols`와 함께. 기본 `cacheRow` 컴포넌트가 브리지 — 대신
`WebUi.component('cacheRow', ...)`를 등록해도 됩니다), `mapDirty()`(프로필 소유 편집기,
예: DNP3 범위 규칙). `profileExport/profileImport`는 셸이 제공합니다.

셸은 매니페스트 기반 공용 헬퍼를 노출합니다: `formValues()`, `fillForm(s)`,
`profileDirty()`, `formState(errs)`(오류 + 변경 점 + 저장/취소 상태),
`formChanges(v)`(다이얼로그 diff 줄), `settingsBody(v)`(POST 본문), `accessList()`,
`addrCfg(side)`, `feat()`, `MAN`, `PROFILE`, `KEYS`, `LABELS`.

## 마이크로 vDOM

`WebUi.node(tag, props, ...children)`로 vnode 트리를 만들고(`{key: ...}` 안정적 diff,
`onclick` 핸들러, `text`·`className`·`hidden`·`style` 프로퍼티 지원), `tag`가 함수면
컴포넌트로 취급해 `props`로 호출합니다.
`WebUi.render(parentEl, vnodes)`는 최소 변경으로 DOM을 제자리 패치합니다.
`WebUi.component(name, fn)` / `WebUi.slot(name, props)`는 위의 명명 레지스트리입니다.
셸의 포인트·캐시 테이블이 사용합니다.

## 사용법 (소비 프로젝트)

```xml
<PropertyGroup>
  <!-- 라우팅은 마커 기반: WebUiComponents 루트(기본 프로젝트 WebUi\ + 라이브러리
       Components\·Pages\) 아래의 *.nf.html 첫 줄 @route:가 라우트 선언입니다.
       구 WebUiPage/Shell/Profile/Login 속성은 폐기(설정 시 빌드 오류). -->
  <WebUiId>my-gateway</WebUiId>            <!-- 선택: MANIFEST.id -->
  <WebUiBrand>My Gateway</WebUiBrand>      <!-- 선택: {{BRAND}} 치환 -->
  <WebUiLinkCss>true</WebUiLinkCss>        <!-- 선택: css를 /style.css 자산으로 분리 -->
</PropertyGroup>
<Import Project="..\..\VagabondK.nF\Source\Web\VagabondK.nF.WebUi\VagabondK.nF.WebUi.targets" />
```

ProjectReference가 필요 없습니다(이전의 Roslyn 생성기 방식의 잔재).
`Import` 한 줄이면 targets가 작업 어셈블리를 찾아 빌드하고, `PrepareResources` 직전에
페이지를 조립해 `.resx` + Designer 소스를 컴파일에 주입합니다.

### NuGet 패키지 모드 (`VagabondK.nF.WebUi`)

저장소 클론 없이 빌드하려면 패키지를 씁니다. 릴리스 파이프라인(`Build\Prepare-Packages.ps1` → `Pack-WebUi.ps1`)이
targets + Assets + Components(표준 컴포넌트·공용 `texts*.resx`) + Tasks dll
(`build\tools\{net472,netstandard2.0}`)을 저장소 릴리스와 같은 버전의 `VagabondK.nF.WebUi` 패키지로 만듭니다(lib\ 없음 — 순수 빌드 도구).

```powershell
nuget install VagabondK.nF.WebUi -Version 0.1.0-preview.7   # 저장소 릴리스와 lockstep
```

```xml
<!-- 소비자 nfproj/공유 targets에서: -->
<Import Project="$(MSBuildThisFileDirectory)..\packages\VagabondK.nF.WebUi.0.1.0-preview.7\build\VagabondK.nF.WebUi.targets" />
```

- targets가 있는 위치가 곧 자산 루트입니다(`build\` 아래 Assets/Components를 그 상대에서 찾습니다).
- 작업 어셈블리도 같은 순서로 해석합니다: `build\tools` 동봉 dll(패키지) → 저장소 Tasks 빌드 산출물(repo).
  저장소 밖이면 Tasks 프로젝트 빌드(target)는 자동 건너뜁니다.
- 표준 컴포넌트(`Components`)와 생성 resx는 `WebUi` 폴더 복사 없이 **in-memory로 조립·gzip 후
  PE에 박히고**, 패키지/저장소 어디에도 소비자 트리에 컴포넌트 사본이 생기지 않습니다.
- 참고 소비 예: nanoGate(본 저장소 바깥) — `nanoGate.Management.targets`가 패키지→소스 순으로 해석합니다.

## 배포·서빙 원리

### 빌드: 소스 → 이진 리소스 파트

```
index.html / shell+profile / *.nf.{html,js,css} / texts*.resx / theme.css / login 자산
        │  WebUiGenerateResources (BeforeTargets=PrepareResources)
        ▼
GenerateWebUiResourcesTask
   1. 머지: 컴포넌트 인라인 치환(@instance는 레지스트리 주입), nf-core/style/views/script 마커
   2. 검증: manifest 대조(WUI0xx 진단)
   3. 축소: 공백·주석 제거(PageComposer)  4. gzip(Level9) + FNV 해시(ETag)
        │
        ├─ obj\Release\webui\page_p0.bin … 32KB씩 자른 gzip 파트 → .resx의 byte[] 항목
        │  (+ lg = 로그인 페이지, c0 = 테마 CSS, lc = 레거시 로그인 CSS, tx = 다국어 사전)
        └─ WebUi.g.cs Designer → _CoreCompileResourceInputs에 주입 → PE의 nanoFramework 리소스로 임베드
```

- **왜 문자열이 아니라 바이트인가**: 한때 페이지를 C# 문자열 리터럴(Pack7)로 생성했으나,
  64KB 힙 장치에서 분해에만 15초 이상, 어셈블리 팽창·GC 압박이 커 2026-11에 이진 리소스로 전환했습니다.
  런타임은 `ResourceUtility.GetObject`로 **플래시(PE)에서 해당 오프셋만 직접 자릅니다** —
  문자열 힙도 디코딩도 없습니다.
- **32KB 파트**: nanoFramework의 단일 리소스/버퍼 크기 상한을 겸손하게 잡은 값입니다.
  페이지가 커도 `WebPage.Read(offset, count)`가 파트 경계를 넘어 이어 읽습니다.
- **증분 빌드**: 셸·프로필·페이지·코어·테마·컴포넌트 전체가 `CustomAdditionalCompileInputs`에
  잡히므로 무엇이든 바뀌면 재생성, 안 바뀌면 `WriteIfChanged`가 타임스탬프를 보존해 재컴파일을 건너뜁니다.

### 런타임: 장치 HTTP 서버가 내는 것

생성되는 `WebPage` API와 전형적 배선(장치 코드에 문자열 리터럴 없음):

| API | 용도 |
|---|---|
| `WebPage.Length / PartCount / ETag` | gzip 페이지 전체 크기·파트 수·`WNNN` 해시 |
| `WebPage.Read(offset, count)` | 플래시 슬라이싱. HTTP 응답 스트림에 그대로 |
| `WebPage.Login / LoginLength / LoginId` | 로그인 페이지 gzip 바이트(링크 모드면 `null` — `/style.css` 공유) |
| `WebPage.CssLength / CssId / TryServeStyle` | `/style.css` 공용 테마(링크 모드) |
| `WebPage.TextsLength / TextsId / TryServeTexts` | `/texts.js` 다국어 사전(사전 파일이 없으면 `TextsLength = -1`, `TryServeTexts`는 false) |
| `WebPage.I18nLength / I18nId / I18nETag / TryServeI18n` | `/i18n.js` 공용 다국어 런타임(`uiText` 등). 항상 생성되는 필수 자산 |
| `WebPage.LoginCssLength / TryServeLoginCss` | 레거시 `/login.css`(테마 미지정 + 인라인 모드 하위 호환) |
| `WebPage.WebUiPartId` / `PartIds` | 리소스 키(bsearch용 short enum, nanoCLR 요구) |

컨트롤러 서빙 패턴([Web.Sample Controllers.cs](../../../samples/Web/VagabondK.nF.Web.Sample/Controllers.cs)):

```csharp
string etag = context.GetHeader("If-None-Match");
if (etag == WebPage.ETag) { context.Response.StatusCode = 304; return true; }
context.Response.ContentLength = WebPage.Length;
context.Response.ETag = WebPage.ETag;
context.Response.AddHeader("Content-Encoding", "gzip");
context.Response.AddHeader("Cache-Control", "no-cache");
int off = 0, n; byte[] buf = new byte[4096];
while ((n = WebPage.Read(off, buf.Length)) > 0) { context.Response.SendBody(buf, n); off += n; }
```

- **ETag = 축소·머지된 최종 페이지의 FNV 해시.** 동일하면 304로 본문 전송을 생략합니다.
  주석만 지워도 해시가 바뀌니 "의미 있는 변경" 기준으로는 쓰지 마십시오.
- **gzip을 미리 쪼갠 바이트를 그대로 흘립니다** — 장치에서 압축 CPU가 들는 일은 없습니다.
  브라우저는 `Content-Encoding: gzip` 헤더로 해석합니다.
- **`/login`은 공개 페이지**로 로그인 페이지의 gzip 바이트 원본을 그대로 보내고(`WebPage.Login`),
  `#legal`(법적 고지)은 로그인 후와 같은 셸 경로를 탑니다.
- **`/style.css`는 공개**입니다 — 로그인 화면이 CSS 때문에 인증에 걸리지 않습니다. `TryServeStyle`이
  같은 ETag/304/gzip 처리를 담당하므로 컨트롤러는 `if (WebPage.TryServeStyle(ctx)) return true;` 한 줄입니다.

### 다국어 사전(/texts.js)

라이브러리 컴포넌트의 기본 언어는 **영어**입니다. 영어 원문은 `texts.resx`(베이스 resx)에만 있고, 빌드가 호출부
`uiText('key', {자리표:값})`의 두 번째 인자로 주입합니다. 나머지 언어 사전(`texts.<컬처>.resx`)은 `/texts.js` 한 파일로
따로 서빙됩니다(베이스 값과 미사용 언어의 사전은 페이지에 전혀 실리지 않습니다).

- **빌드**: 컴포넌트 루트에서 `texts.resx` + `texts.<컬처>.resx`를 수집해 JS로 병합·축소하고, gzip 자산 `tx`로
  임베드합니다.
  베이스의 이름 있는 항목(uiText 키)은 `/texts.js`에 넣지 않고 `uiText` 폴백 주입에만 씁니다.
  `static.NNN` 이름 항목은 정적 영역의 영어 원문(value = 원문 텍스트)이며, 컬처 resx의 같은 Name과 짝지어
  `t.static.<컬처>`(영어→현지어)로 방출됩니다(`static.nf.js`의 `localizeStatic()` 사용).
  루트 여러 개의 사전은 **역순(라이브러리→공유→프로젝트) 키 단위 병합** — 프로젝트 resx가 같은 Name을 덮어씁니다.
  머지된 메인 페이지는 `<script src="/texts.js"></script>` → `<script src="/i18n.js"></script>`
  순으로 코어보다 먼저 실행되도록 삽입됩니다.
- **로그인 페이지는 예외(인라인 사전)**: `login.js`가 부르는 `uiText` 키만 사전에서 골라
  부분집합을 페이지 본문에 인라인하고 i18n 런타임(`i18n.js` 전문)도 함께 넣습니다 — 첫 페인트부터
  모국어로 그려져 `/texts.js`·`/i18n.js` 왕복이 0회이고 영어 깜빡임 구조가 사라집니다.
  부분집합이 비어 있으면(다국어 resx 없음) 메인처럼 링크 방식으로 후퇴합니다.
  이에 더해 `login.html`은 `html:not(.wui-i18n) body{visibility:hidden}` 홀드를 걸고
  `login.js`가 번역을 마치는 즉시 클래스를 붙여 해제합니다(시간 기반이 아닌 이벤트 기반;
  예외 시 800ms 안전 타이머가 원문으로 드러냅니다).
- **런타임**: i18n 로직은 메인/로그인 공용 파일 `/i18n.js` 한 곳에 있습니다(`uiText`, `uiSetLang`,
  `uiAddTexts` 전역 함수 제공). `uiText(key, 'English', {자리표:값})`가 활성 언어 사전에서 키를
  찾고, 없으면 폴백 영어로 되돌립니다(두 번째 인자는 빌드가 `texts.resx`에서 주입). 언어 결정은 `localStorage('wui.lang')` >
  `navigator.language` 순입니다(ko 접두면 한국어이고, 그 외엔 사전에서 일치 언어를 찾으며, 없으면 en입니다).
  `<html lang>`도 여기서 설정합니다. 프로젝트가 전용 문구를 추가하려면 `uiAddTexts(lang, map)`을
  페이지 스크립트에서 호출하면 됩니다.
- **서버 배선**: `/texts.js`와 `/i18n.js` 모두 공개 라우트로 미러링합니다. **`/i18n.js`는 필수**입니다
  (404 시 모든 페이지가 `uiText` 참조로 파손되므로 빌드가 자산 생성을 강제합니다).
  `/texts.js`는 사전이 없을 때 404 후 영어 폴백이라 선택입니다:

```csharp
[Route("/texts.js")] [Method("GET")] [PublicEndpoint]
public void TextsAsset(WebServerEventArgs e)
{
    if (!WebPage.TryServeTexts(e.Context))
        WebServer.OutputHttpCode(e.Context.Response, HttpStatusCode.NotFound);
}

[Route("/i18n.js")] [Method("GET")] [PublicEndpoint]
public void I18nAsset(WebServerEventArgs e)
{
    if (!WebPage.TryServeI18n(e.Context))
        WebServer.OutputHttpCode(e.Context.Response, HttpStatusCode.NotFound);
}
```

- **RAM 영향 없음**: 사전과 런타임은 페이지와 같은 gzip 리소스 바이트로만 존재하고, 브라우저가
  선택된 언어 하나만 해석합니다. 장치 힙에는 `uiText()` 호출 순간의 문자열만 잠깐 오릅니다.

CSP를 쓰는 장치는 `script-src`에 `'self'`가 있어야 `/texts.js`·`/i18n.js`가 로드됩니다
(`'unsafe-inline'`만 있으면 외부 파일 스크립트가 차단되어 영어로 폴백합니다. 단 로그인 페이지의
인라인 사전·런타임은 본문 스크립트이므로 `'unsafe-inline'`만으로 충분합니다).

### 장치에 올리기

```powershell
# 바이너리(.bin)까지 만드는 빌드
MSBuild /t:"Build;NanoGenerateBinaryOutput" /p:Configuration=Release <프로젝트>.nfproj
# 직렬 플래시 + 리셋 (nanoff)
nanoff --nanodevice --serialport COM3 --deploy --image bin\Release\<이름>.bin --reset
# 부팅 확인
Invoke-WebRequest http://<장치IP>/login   # 200이면 기동 완료
```

장비에 올린 후에도 브라우저가 구버전을 띄운 상태라면 no-cache 조건부 요청에서 ETag가 달라져 자동 갱신됩니다.