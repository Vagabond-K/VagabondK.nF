# 컴포넌트 사용 매뉴얼

[README](README.md)의 카탈로그를 속성·이벤트·API 수준으로 벌린 사용 설명서입니다.
44종 전 컴포넌트의 실제 소스(`*.nf.{html,js,css}`)를 기준으로 작성했으며, 동작이 바뀌면 이 문서를 함께 고칩니다.

## 공통 규칙

- **로딩**: `<name/>` 참조 또는 `<wui-name>` 태그로 사용한 컴포넌트만 빌드 결과에 들어갑니다.
  미사용 컴포넌트는 0바이트이며, 프로젝트 로컬 `WebUi\`가 같은 이름을 섀도잉합니다.
- **인스턴스 vs 인라인**: 첫 줄 `@instance` 마커 줄이 있으면 shadow DOM 인스턴스
  (태그마다 격리된 상태·id)이고, 없으면 참조 자리에 마크업이 그대로 병합되는 정적/단일 조각입니다.
  `@instance form`은 폼 연관 사용자 정의 요소 — `<form>` 안에 두면 `form.elements[name]`로
  잡히고 `.value`가 저장 페이로드로 동기화됩니다.
- **색감**: 전부 테마 `:root` 토큰(`--bg --card --line --tx --mut --acc --ok --bad --fld --in`)
  사용. 테마 파일 하나로 라이브러리 전체 색이 바뀝니다([WebUi 테마](../VagabondK.nF.WebUi/README.md#독립-로그인-페이지-webuilogin)).
- **포커스 링**: 입력 컴포넌트는 `:host(:focus-within)`으로 컨테이너에
  `outline: 2px solid var(--acc)`를, 내부 `input`에는 `outline: none`을 줍니다. 페이지(light DOM)의
  `input:focus` 규칙도 같은 `--acc` 토큰을 쓰면 전 필드의 포커스 색이 일치합니다.
- **id 충돌 주의**: 페이지 `<style>` 전체가 모든 컴포넌트 shadow에 `adoptedStyleSheets`로
  삽입되므로, 컴포넌트 내부 id가 페이지 id와 같으면 페이지 규칙이 컴포넌트까지 스타일링합니다
  (pagination의 `#pv` 사고가 실제 사례). 컴포넌트 id는 `pg`류처럼 이름 접두사를 붙이십시오.
  CSS 규칙에 미지원 선택자가 하나라도 섞이면 규칙 전체가 버려지는 것도 주의하십시오
  (네이티브 눈 숨김은 `::-webkit-textfield-decoration-container, ::-ms-reveal { display:none }` 사용).
- **이벤트 버스**: 컴포넌트 간 결합은 `wui:*` 커스텀 이벤트입니다.
  페이지 poller가 `wui:tick`(매 초: age·uptime·heapFree 등)과 `wui:status`(설정보다 무거운 실시간 상태)를
  발행하고, 스크립트는 `WebUi.on(name, fn)` / `WebUi.fire(name, data)`로 읽고 씁니다.

| 신호 | 발행자 | 구독자 |
|---|---|---|
| `wui:tick` | 페이지 poller | uptime, conn-dot, sysinfo |
| `wui:status` | 페이지 poller | descriptions, statistic, sparkline, heap-gauge, panel, ap-banner |
| `wui:progress` | firmware | progress |
| `wui:result` | firmware | result |
| `wui:alert` | 임의 스크립트(api 실패 안내 등) | alert |
| `wui:tab` | tabs | (페이지) |
| `wui:number-change`, `wui:search`, `wui:page`, `wui:list-click`, `wui:chip-close`, `wui:drawer-close` | 각 입력 컴포넌트 | (페이지) |

---

## 입력·폼

### `<number>` — 인스턴스 · 폼 연관
숫자 입력. 입력란 좌측, **스테퍼 페어(− +)는 우측**에 몰려 있습니다(native number 관례).
버튼이 아닌 직접 입력도 되며 Enter는 폼 제출로 동작합니다.

| 속성 | 기본값 | 설명 |
|---|---|---|
| `name` | — | 폼 값 이름 |
| `value` | 빈 칸 | 시작 값 (min/max로 클램프) |
| `min` / `max` | ∓2^31 | 클램프 범위 |
| `step` | 1 | 버튼 증감 단위 |
| `placeholder` / `autocomplete` | — | 입력 안내 / 자동완성 |

- 이벤트: `wui:number-change` `{value}` · 폼 동기화용 `input`(composed, shadow 경계 재발행)
- API: `.value` 읽기/쓰기 · `.disabled` · `.focus()`
- 테마: `--fld`(입력 배경) `--line` `--tx` `--acc` `--bad`(invalid 클래스)
- 예: `<number name="pollMs" value="500" min="0" max="9999" step="100"/>`

### `<password>` — 인스턴스 · 폼 연관
표시/숨김 토글이 달린 비밀번호 입력. 토글 버튼은 눈 글자(U+1F441)를 쓰고, 숨김 상태에서는
눈 위에 대각선이 CSS로 표시됩니다.
속성: `name` `placeholder` `maxlength` `autocomplete` · API: `.value` `.disabled` `.focus()` · `input` 재발행.
예: `<password name="password" maxlength="64" autocomplete="new-password"/>`

### `<search>` — 인스턴스 · 폼 연관
디바운스(300ms) 검색 입력. × 클릭 시 빈 값으로 초기화.
- 이벤트: `wui:search` `{q}` · `input`
- API: `.value` `.placeholder`(읽기/쓰기) `.focus()`
예: `<search placeholder="포인트 검색"/>`

### `<wui-input-group>` — 인스턴스
입력 좌우 고정 접두/접미.
- 슬롯: `pre` · `post` · 기본(입력란)
- API: `.value` 왕복
예: `<wui-input-group placeholder="10.0.0.5"><b slot="pre">http://</b><b slot="post">:502</b></wui-input-group>`

### `form` — 인라인(단일) + 스크립트 API
마크업 대신 **manifest 스키마**로 컨트롤을 런타임 렌더하는 폼 통짜 조각.
`@manifest`의 `form` 배열(또는 `data-label`/`data-fmt` 자동 유도) + 이 `<form/>` 참조, 그리고:

```js
WebUi.autoForm({
  formId: 'wf',                                   // 기본 'wf'
  check: function (v) { return v.pollMs >= 100; }, // 저장 가능 여부
  save:  function (v, done) { api('POST', '/api/settings', v, done); }
});
```

저장/되돌리기 버튼과 dirty 상태(`#wfState`)가 자동 배선됩니다. `fmt` 종류(`ms·pin·secret·bool·text`…)는
[WebUi README의 form 자동 유도](../VagabondK.nF.WebUi/README.md#form-자동-유도-data-label--data-fmt)를 참고하십시오.

`bool`(토글 스위치)은 트랙의 어느 지점을 클릭해도 값이 반전되며, 포커스 후 Space/Enter 키로도
토글할 수 있습니다(`role="switch"`로 접근성 노출). 행의 라벨이나 빈 공간 클릭은 값에 영향을 주지 않습니다.

## 배치·내비

### `<wui-card>` — 인스턴스
| 속성 | 설명 |
|---|---|
| `title` | 제목(없으면 제목 영역 생략). 빈 카드는 `:host(:empty)`로 자동 숨김 |
- 슬롯: 본문. 예: `<wui-card title="설정 백업"><export-import/></wui-card>`

### `<wui-groupbox>` — 인스턴스
| 속성 | 설명 |
|---|---|
| `title` | legend 문구(테두리를 끊고 얹힘). 없으면 빈 legend |
- 슬롯: 본문. 예: `<wui-groupbox title="시리얼 통신"><wifi-form/></wui-groupbox>`
- fieldset/legend를 그대로 써서 브라우저가 테두리 구멍을 자동 관리하므로, card와 달리 배경 색 맞추기가 불필요합니다.
- 자식 노드가 하나도 없는 빈 것은 card와 같은 규약으로 자동 숨김 처리합니다(`:host(:empty)`). `hidden` 속성도 지원합니다.

### `<wui-grid>` — 인스턴스
`cols`(기본 2)열 균등 CSS grid 컨테이너. 자식 배치만 담당, 이벤트 없음.

### `<wui-divider>` — 인스턴스
`label` 지정 시 중앙에 문구가 얹힌 가로 구분선.

### `<wui-navbar>` — 인스턴스
`title` 좌측 상단 바. 기본 슬롯 = 우측 액션 영역.
예: `<wui-navbar title="게이트웨이"><reboot/></wui-navbar>`

### `<breadcrumb>` — 인스턴스
`items="홈|설정|포인트"` — `|` 구분 경로, 마지막 항목 강조.

### `<tabs>` — 인라인(빌드TIME)
`<section data-tab="설정" data-hash="settings">` 섹션들을 수집해 내비 버튼 + 해시 라우팅 생성.
`data-hash` 생략 시 `#t1,#t2,…`, 첫 섹션이 기본. 전환 시 `wui:tab`(인덱스) 발행.
API: `WebUi.tabs.el(i)`(i번 버튼 — bindForm `dotEl`로 사용 가능) · `WebUi.tabs.go(i)`.

### `<steps>` — 인스턴스
`items="다운로드|전송|적용" current="2"` — current 미만 ✓, current 강조(1-based).

### `<list-group>` — 인스턴스
`items="요약|상세|로그" selected="1"` — 클릭 시 `on` 이동 + `wui:list-click {index, text}`(1-based).
drawer와 짝지어 마스터-디테일 구성.

### `<pagination>` — 인스턴스
`page="2" total="5"` ‹ n/m › 이동. `wui:page {page}` 발행.

### `<timeline>` — 인스턴스
`items="10:04 기동|10:05 연결"` 세로 이벤트 스트립(최근 강조).

## 표시

### `<descriptions>` — 인스턴스
`title` · `keys="deviceName,pollMs"`(쉼표) — `wui:status`를 받아 kv 목록 렌더.
라벨은 `WebUi.LABELS` 우선, 없으면 키 이름. **`wui:status` 구독**.

### `<statistic>` — 인스턴스
`key="heapFree" label="힙" fmt="kb|ms|pct|num"` 상태의 한 키를 큰 숫자로. `kb`는 /1024. **`wui:status` 구독**.

### `<badge>` / `<wui-badge>` — 인스턴스
`variant="info|ok|warn|bad"`(생략 시 회색) 상태 알약. 본문은 슬롯.

### `<chip>` / `<wui-chip>` — 인스턴스
라벨 태그. `removable`이면 × → `wui:chip-close {text}` 발행 후 자기 제거.

### `<empty>` — 인스턴스
`text`(기본 "데이터 없음") `icon`(기본 ◇) 빈 상태 안내.

### `<skeleton>` — 인스턴스
shimmer 자리받기. `w`(숫자=%)/`h`(숫자=px, 기본 16px)/`round`(원형).
예: `<skeleton w="60%" h="14"/>`

### `<spinner>` — 인스턴스
CSS-only 회전 로더. `size`(지름 px, 기본 18). 정지는 host `hidden` 토글.

### `<progress>` — 인스턴스
`value` `max`(기본 100) `label` 막대. `wui:progress {value, max?, label?}` **수신**으로 갱신(0~100% 클램프), 도달 시 host에 `done`.
firmware·저장 배선과 짝.

### `<result>` — 인스턴스
`status="ok|bad|info" title sub` 큰 결과 화면. `wui:result` **수신**.

### `<wui-tooltip>` — 인스턴스
`text` 설명 버블. 감싼 콘텐츠(기본 슬롯) hover로 표시.

### `<note>` — 정적 조각
js 없는 한 줄 안내(`.nt`). 인라인 병합형의 simplest 예시.

## 오버레이

### `modal` — 인라인 + 스크립트 API
```js
var ok = await WebUi.modal({ title: '재부팅', body: '장치를 재시작합니다.',
                             ok: '확인', cancel: '취소' });  // 취소·바깥 클릭 = false
```

### `<wui-drawer>` — 인스턴스
`title` · `open` 속성으로 열림/닫힘. ×/배경 클릭 시 닫고 `wui:drawer-close` 발행. 본문 = 기본 슬롯.

### `<wui-collapse>` — 인스턴스
`title` 헤더 클릭으로 `open` 토글(본문 슬롯, CSS max-height).

### `<alert>` — 인스턴스
하단 고정 안내줄. `severity="info|warn|error|ok" text`(`text` 비면 숨김). `wui:alert` **수신**으로 스크립트 없이 동적 갱신.

### `<ap-banner>` — 인스턴스
`key`(기본 `ap`)가 `wui:status`에서 참일 때 설정 AP 경고 바. `text`로 문구 교체. **`wui:status` 구독**.

## 장치 연동

### `<sysinfo>` — 인스턴스
속성 없음. `wui:tick`을 구독해 가동 시간/남은 힙/요청 수/프로필 id를 `<dl>`로 채웁니다(IIFE 격리).

### `panel` — 인라인 조각
`wui:status` 구독형 기본 프로필 패널(장치 이름·IP, `WebUi.M.id` 사용). 프로젝트 `WebUi\panel.nf.html`로 섀도잉 예시.

### `<clock>` — 인스턴스
브라우저 HH:mm:ss 시계(setInterval 1s).

### `<uptime>` — 인스턴스
`wui:tick`의 `uptime`을 외삽 표시. `label`(기본 "가동") · `long`(시·분·초 3단). **`wui:tick` 구독**.

### `<conn-dot>` — 인스턴스
`timeout`(초, 기본 6) · `label`(기본 "연결"). `wui:tick`의 `age ≤ timeout`이면 초록, 초과면 "무응답 Xs" 점멸. **`wui:tick` 구독**.

### `<heap-gauge>` — 인스턴스
`max`(생략 시 관측 peak 자동 스케일) · `label`(기본 "힙"). `wui:status`의 `heapFree`를 막대+KB로, 20% 미만 경고색. **`wui:status` 구독**.

### `<sparkline>` — 인스턴스
`key`(기본 `heapFree`)의 최근 40개 값을 canvas 절선으로. `max`로 스케일 고정. **`wui:status` 구독**.

### `<log-tail>` — 인스턴스
`api`(기본 `/api/log`) `ms`(폴링 간격) `max`/`lines`(유지 행수) — 새 줄 appended 방식으로 스트리밍하고, 장치가 바쁜 틱은 건너뛰고 다음 poll을 재시도합니다.

### `<register-view>` — 인스턴스
`api`(기본 `/api/registers`) `cols`(행당 열, 기본 8) `ms`(기본 2000). `values` 배열을 주소 행두 표로 재렌더.

### `<reboot>` — 인스턴스
`api`(기본 `/api/reboot`) `secs`(기본 10). modal 확인 → POST → 카운트다운 → `location.reload()`.

### `<firmware>` — 인스턴스
`api`(기본 `/api/firmware`) `chunk`(기본 2048). 파일→base64 청크 POST(seq/total).
진행 `wui:progress` · 종료 `wui:result` 발행(각 표시 컴포넌트가 화면 갱신). 실제 플래싱은 장치 사이드 몫.

### `<export-import>` — 인스턴스
내보내기: GET `/api/settings` → `settings.json` 다운로드.
가져오기: 파일의 키만 현재 설정에 병합해 POST(부분 백업 허용). phpMyAdmin식.

### `<wifi-form>` — 인스턴스
`api`(기본 `/api/network`). 로드 시 GET로 현재 값 채움(실패 3s 재시도), 연결 시 POST(body urlencoded) → 저장/실패 표시.

---

## 다국어 (i18n)

컴포넌트 소스에는 문구 리터럴이 없고 `uiText(키, {자리표값})`만 씁니다. 기본 언어는 **영어**로,
빌드가 `texts.resx`의 영어 값을 호출부 두 번째 인자(폴백)에 주입합니다. `uiText`와 언어 결정
로직은 메인·로그인 공용 파일 `/i18n.js` 한 곳에 있습니다(페이지가 자동으로 `/texts.js` →
`/i18n.js` → 코어 순으로 로드합니다). `/texts.js`에는 영어를 제외한 언어(ko 등)의 사전만 담깁니다 —
영어는 주입된 폴백으로 이미 배포되므로 중복 실리지 않습니다.

- **언어 결정**: `localStorage['wui.lang']`(수동 선택) > `navigator.language`(브라우저).
  `ko` 접두어면 한국어, 아니면 사전에 같은 접두어가 있는 언어를 쓰고 없으면 `en`으로 폴백합니다.
- **전환**: `uiSetLang('ko')` — 저장 후 새로고침합니다. 브라우저 감지로 되돌리려면 `removeItem('wui.lang')`을 사용하면 됩니다.
- **사전 구조**: `window.__wuiTx = { ko: { 'reboot.btn': '재시작', ... }, static: { ko: { 'Reboot': '재시작', ... } } }`.
  키가 사전에 없으면 그 문장만 영어로 표시됩니다(부분 번역 허용). `static`은 HTML 정적 영역의 영어→현지어 원문-번역 짝입니다.
- **자리표시자**: `{n}`처럼 문장 전체를 번역해 어순을 바꿉니다.
  예: 사전 값 `'Rebooting in {n}s'`(en) / `'{n}초 후 재시작'`(ko)를 호출하는
  `uiText('reboot.count', { n: 5 })` → 영어 `"Rebooting in 5s"`, 한국어 `"5초 후 재시작"`.

### 프로젝트에 언어 추가하기

1. 컴포넌트 루트(예: `WebUi\`)에 `texts.de.resx` 작성 — Name은 베이스 `texts.resx`와 같은 키,
   Value는 목표 언어 문장입니다. 사전에 없는 Name은 영어로 폴백되므로 부분 번역도 괜찮습니다.
   (정적 영역 번역은 `static.NNN` Name으로 베이스의 영어 원문 항목과 짝을 이룹니다.)

2. 빌드가 자동으로 수집·병합해 `/texts.js`에 포함시킵니다(베이스 `texts.resx`를 제외한 `texts.*.resx`).
3. 장치 서버에 `/texts.js` 공개 라우트를 한 줄 등록하십시오(아직 없으면 404 후 영어로 동작합니다):

```csharp
[Route("/texts.js")] [Method("GET")] [PublicEndpoint]
public void TextsAsset(WebServerEventArgs e)
{
    if (!WebPage.TryServeTexts(e.Context))
        WebServer.OutputHttpCode(e.Context.Response, HttpStatusCode.NotFound);
}
```

4. CSP를 사용하는 장치는 `script-src`에 `'self'`를 추가하면 됩니다.

### 페이지(셸) 전용 문구

프로젝트의 `index.html`에 직접 쓴 문자열은 라이브러리 사전에 없습니다. 프로젝트 `WebUi\` 루트에
`texts.resx`를 두어 영어 원문을 Name별로 등록하고 호출부는 `uiText('mine.title')`로 쓰면 빌드가 라이브러리
키와 똑같이 폴백을 주입합니다(`en` 사전은 키 단위로 병합됩니다). 런타임에 보강하려면
`uiAddTexts('ko', { 'mine.title': '대시보드' })`처럼 언어를 지정해 호출하면 됩니다.
표준 컴포넌트가 쓰는 키(`sys.*`, `form.*`, `reboot.*`, `wifi.*`, `fw.*`, `ei.*`, `conn.*`, `up.*`, `pw.*`,
`modal.*`, `empty.*`, `login.*` 등 전부는 `Components\texts.resx` 참조)와 겹치지 않게 쓰는 것을 권장합니다.
라이브러리 키를 덮어쓰고 싶으면 같은 키로 `uiAddTexts`를 호출하면 됩니다(프로젝트 것이 나중에 적용됨).

참고로 장치는 `/i18n.js`도 공개 라우트로 서빙해야 합니다(신규 프로젝트면 빌드가 페이지에
링크를 자동으로 넣으므로 라우트 등록만 하면 됩니다 — `TryServeI18n`, WebUi README 참조).

---

## 커스텀 컴포넌트 만들기

1. 프로젝트 `WebUi\`에 `my-widget.nf.html`을 작성하십시오(인스턴스는 첫 줄 `@instance`).
2. 필요하면 같은 폴더에 `my-widget.nf.js`(인스턴스당 실행, `$()`는 자기 shadow 한정)·`.nf.css`를 두십시오.
3. 마크업에서 `<my-widget/>`(또는 인스턴스 태그 `<wui-my-widget>`)로 사용합니다.
4. 라이브러리 동명 컴포넌트 대체는 같은 이름으로 프로젝트 루트에 두기만 하면 됩니다.

컴포넌트 js 상단 주석에도 사용법이 요약돼 있고, 전체 실전 예는
[Web.Sample](../../../samples/Web/VagabondK.nF.Web.Sample/README.md) index.html의 카탈로그 페이지에 있습니다.
