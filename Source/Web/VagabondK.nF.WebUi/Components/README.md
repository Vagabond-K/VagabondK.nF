# Components — 표준 컴포넌트 라이브러리 (VagabondK.nF.WebUi에 내장)

장치를 위해 미리 만들어 둔 `*.nf.{html,js,css}` 컴포넌트 모음입니다.
`VagabondK.nF.WebUi.targets`가 이 폴더(저장소에서는 `Source\Web\VagabondK.nF.WebUi\Components`,
NuGet 패키지에서는 `build\Components`)를 **자동으로 `$(WebUiComponents)` 후미 루트에 추가**하므로
개별 지정은 필요 없습니다. 자체 multi-root 사전과 같은 다른 루트를 더 쓰려면:

```xml
<PropertyGroup>
  <WebUiComponents>$(MSBuildThisFileDirectory)WebUi;$(MSBuildThisFileDirectory)WebUi\admin</WebUiComponents>
  <!-- 패키지의 Components는 비활성화할 때만: <WebUiNoSharedComponents>true</WebUiNoSharedComponents> -->
</PropertyGroup>
```

- 페이지가 `<name/>` 참조나 `<wui-name>` 태그로 **사용한 컴포넌트만** 빌드 결과에 들어갑니다.
  이 폴더에 아무리 많은 컴포넌트를 둬도 사용하지 않으면 장치 힙을 1바이트도 차지하지 않습니다.
- 프로젝트 로컬 루트(앞쪽)에 같은 이름이 있으면 로컬이 **섀도잉**하여 라이브러리 버전을 대체합니다.
  내용·모양을 제품별로 바꾸고 싶을 때 해당 이름의 조각을 프로젝트 `WebUi\`에 두면 됩니다.
- 라이브러리 컴포넌트는 페이지 전역 변수에 의존하지 않습니다(필요하면 IIFE로 격리).

속성·이벤트·API 수준의 상세 사용 설명서는 **[USAGE.md](USAGE.md)** 를 봅니다.

## 포함된 컴포넌트

| 이름 | 한 벌 | 설명 |
|---|---|---|
| `note` | html+css | 여백 없는 한 줄 안내 문구. `.nt` 클래스. |
| `sysinfo` | html+js | `wui:tick`을 구독해 가동 시간/남은 힙/요청 수/프로필을 `<dl>`로 채움. |
| `panel` | html+js | `wui:status`를 구독하는 범용 프로필 패널(장치 이름·IP). 프로젝트가 같은 이름으로 대체하기 좋은 예시. |
| `form` | html+js+css | `<form/>` 한 줄로 설정 페이지 완성. `@manifest`의 `form` 스키마로 컨트롤을 런타임 렌더하고 `WebUi.autoForm({check, save})`가 저장/되돌리기/dirty를 bindForm으로 배선. 필드가 마크업이 아니라 manifest에 있으므로 생성기의 name= 대조 검사는 이 컴포넌트 사용 시 생략. |
| `tabs` | html+js+css | `<tabs/>`가 `section[data-tab]`을 모아 내비 버튼과 해시 라우팅을 생성. `data-hash`로 URL 키 지정(생략 시 `#t2`…), 탭 전환 시 `wui:tab` 발행. `WebUi.tabs.el(i)`를 bindForm의 `dotEl`로 쓸 수 있습니다. |
| `modal` | html+js+css | `WebUi.modal({title, body, ok, cancel})` → `Promise<boolean>`. nanoGate ask(pointdlg) 패턴의 일반화. 확인/취소/바깥 클릭 지원. |
| `conn-dot` | html+js+css | `<conn-dot timeout="6"/>` Grafana식 연결 점. `wui:tick`의 age가 timeout 이하면 초록 "연결", 초과하면 빨간 점멸 "무응답 Xs". 인스턴스. |
| `uptime` | html+js+css | `<uptime/>` heartbeat의 일반화(장치 기준 payload 외삽). `long` 속성으로 "X시간 X분 X초" 3단 표시. heartbeat와 병기해 페이지/장치 기준 비교 예시 가능. 인스턴스. |
| `heap-gauge` | html+js+css | `<heap-gauge max="…"/>` 남은 힙 게이지. `wui:status`의 heapFree 표시, max 생략 시 관측 peak로 자동 스케일, 20% 미만 빨간. 인스턴스. |
| `statistic` | html+js+css | `<statistic key="reqCount" label="요청 수"/>` Ant/Carbon식 큰 숫자+라벨. `wui:status`의 `key` 값을 `fmt`(kb·ms·pct)로 포맷해 표시. 인스턴스. |
| `progress` | html+js+css | `<progress max="100" label="저장 중"/>` 바형 진행률. `wui:progress`의 `{value,max,label}` 구독, 도달 시 host에 `done`(초록). 저장·업로드·펌웨어와 짝. 인스턴스. |
| `alert` | html+js+css | `<alert severity="warn" text="…"/>` note의 일반화. severity `info/warn/error/ok` + 아이콘. `wui:alert` 이벤트로 동적 갱신. 인스턴스. |
| `result` | html+js+css | `<result status="ok" title="전송 완료" sub="…"/>` Ant Result식 성공/실패 안내. `wui:result`의 `{status,title,sub}` 구독. firmware·저장과 짝. 인스턴스. |
| `card` | html+js+css | `<wui-card title="설정 백업">본문 마크업</wui-card>` 제목+구분선+본문 컨테이너. 본문을 `<slot/>`으로 받아 light DOM을 그대로 렌더. 빈 카드는 자동 숨김(`:host(:empty)`). `<wui-card>` 태그 자체가 참조. 인스턴스. |
| `groupbox` | html+js+css | `<wui-groupbox title="시리얼 통신">본문 마크업</wui-groupbox>` fieldset/legend식 그룹 박스 — 제목이 테두리를 끊고 얹힙니다. 본문을 `<slot/>`으로 받음. 빈 것·`hidden`은 자동 숨김. 인스턴스. |
| `export-import` | html+js+css | 설정 백업 내보내기/가져오기(phpMyAdmin 스타일). 내보내기: GET `/api/settings` → JSON 다운로드. 가져오기: 파일의 키만 장치 현재 설정에 병합해 POST(부분 백업 허용). 인스턴스. |
| `firmware` | html+js+css | `<firmware api="/api/firmware" chunk="2048"/>` 파일→base64 청크 POST(seq/total). `wui:progress`/`wui:result` 발행으로 progress·result 인스턴스와 결합. 전송만 담당, 실제 플래싱은 장치 프로젝트 몫. 인스턴스. |
| `badge` | html+js+css | `<wui-badge variant="ok">12</wui-badge>` 카운트·상태 알약 뱃지. variant `info/ok/warn/bad`(없으면 회색). 본문을 `<slot/>`으로 받음. 태그 자체가 참조. 인스턴스. |
| `chip` | html+js+css | `<wui-chip removable>RTU</wui-chip>` 라벨+삭제 태그. `removable`이면 × 표시, 클릭 시 host 제거 후 `wui:chip-close {text}` 발행. 태그 자체가 참조. 인스턴스. |
| `spinner` | html+js+css | `<spinner/>` CSS-only 회전 로딩. `size="28"`로 지름 지정(두께 자동). 정지는 host `hidden`. 인스턴스. |
| `skeleton` | html+js+css | `<skeleton w="100%" h="14"/>` shimmer 자리받기. `w`는 숫자면 %(그림자 폭), `h`는 숫자면 px. `round` 속성으로 원형. 인스턴스. |
| `empty` | html+js+css | `<empty text="로그 없음" icon="▤"/>` 데이터 없음 상태(기본 "데이터 없음"/◇). 인스턴스. |
| `descriptions` | html+js+css | `<descriptions title="장치 정보" keys="deviceName,pollMs"/>` Ant식 kv 다단. `wui:status` 구독, 라벨은 `WebUi.LABELS`(manifest form 유래) 폴백 키 이름. 인스턴스. |
| `navbar` | html+js+css | `<wui-navbar title="게이트웨이">액션…</wui-navbar>` 제목+우측 액션 slot 헤더 바. 태그 자체가 참조. 인스턴스. |
| `steps` | html+js+css | `<steps items="다운로드|전송|적용" current="2"/>` MUI식 단계 표시. current 미만 ✓(done), current 강조. 1-based. 인스턴스. |
| `collapse` | html+js+css | `<wui-collapse title="고급 설정" open>본문</wui-collapse>` 아코디언. 헤더 클릭으로 host `open` 속성 토글(CSS max-height). 태그 자체가 참조. 인스턴스. |
| `list-group` | html+js+css | `<list-group items="요약|상세|로그" selected="1"/>` 행 목록. 행 클릭 시 `on` 이동 + `wui:list-click {index,text}` 발행(1-based). drawer와 결합해 마스터-디테일 가능. 인스턴스. |
| `drawer` | html+js+css | `<wui-drawer title="상세">본문</wui-drawer>` 우측 슬라이드 패널. host `open` 속성으로 열림. ×/배경 클릭 시 닫고 `wui:drawer-close` 발행. 태그 자체가 참조. 인스턴스. |
| `reboot` | html+js+css | `<reboot api="/api/reboot" secs="8"/>` 라우터 UI식 재부팅. modal 확인 → POST → 카운트다운 → `location.reload()`. 인스턴스. |
| `number` | html+js+css | `<number value="500" min="0" max="9999" step="100"/>` Ant InputNumber식 숫자 입력. 스테퍼(− +)는 우측 페어. 변경마다 `wui:number-change {value}`. 인스턴스·폼 연관(`form`). 호스트에 `.value`/`.disabled`/`focus()` API. |
| `search` | html+js+css | `<search placeholder="검색"/>` Carbon식 필터 입력. 300ms 디바운스 후 `wui:search {q}`. ×로 초기화. 인스턴스·폼 연관(`form`). 호스트에 `.value`/`.placeholder`/`focus()` API. |
| `password` | html+js+css | `<password placeholder="비밀번호"/>` show/hide 토글 비밀번호 입력. 인스턴스·폼 연관(`form`). 호스트에 `.value`/`.disabled`/`focus()` API. |
| `input-group` | html+js+css | `<wui-input-group placeholder="…"><b slot="pre">http://</b><b slot="post">:502</b></wui-input-group>` Bootstrap식 입력+접두/접미. `el.value` 왕복. 태그 자체가 참조. 인스턴스. |
| `tooltip` | html+js+css | `<wui-tooltip text="설명">본문</wui-tooltip>` 호버 버블. 태그 자체가 참조. 인스턴스. |
| `timeline` | html+js+css | `<timeline items="10:04 기동|10:05 연결"/>` Ant식 세로 이벤트 스트립. 인스턴스. |
| `pagination` | html+js+css | `<pagination page="2" total="5"/>` ‹ n / m › 네비게이터. 이동 시 `wui:page {page}`. 인스턴스. |
| `breadcrumb` | html+js+css | `<breadcrumb items="홈|설정|포인트"/>` 경로 표시(마지막 굵게). 인스턴스. |
| `divider` | html+js+css | `<divider label="고급 설정"/>` MUI식 구분선(label 없으면 선만). 인스턴스. |
| `grid` | html+js+css | `<wui-grid cols="3">본문</wui-grid>` CSS grid 유틸. 태그 자체가 참조. 인스턴스. |
| `ap-banner` | html+js+css | `<ap-banner/>` `wui:status`의 `ap`(또는 `key` 속성 키)가 참일 때 설정 AP 경고 바 표시. nanoGate 승격. 인스턴스. |
| `log-tail` | html+js+css | `<log-tail api="/api/log" max="8" ms="2000"/>` Portainer식 로그 스트리밍. 주기 poll로 새 줄 appended, max줄 유지. MCU가 바쁜 틱은 건너뛰고 다음 poll 재시도. 인스턴스. |
| `register-view` | html+js+css | `<register-view api="/api/registers" cols="8" ms="2000"/>` Modbus Tool식 레지스터 라이브 표. `values` 배열을 행×열로 재렌더(행두 주소 th). 인스턴스. |
| `wifi-form` | html+js+css | `<wifi-form api="/api/network"/>` 라우터식 SSID/비밀번호 폼. 로드 시 GET로 현재 값 채움(실패 시 3초 후 재시도), 연결 버튼 POST → 저장됨/실패. body는 urlencoded. 인스턴스. |
| `sparkline` | html+js+css | `<sparkline key="requests" w="120" h="32"/>` Grafana식 canvas 값 추이. `wui:status`의 `key` 값을 40포인트 링버퍼로 polyline 렌더. 인스턴스. |
| `clock` | html+js+css | `<clock/>` HH:mm:ss 시계(setInterval 1000). DNP3 타임스탬프 확인용. 인스턴스. |

### form 컴포넌트 사용 예

```html
<!-- MANIFEST는 빌드 자동 생성: form은 아래 폼 마크업(data-label/data-fmt)에서 유도 -->
...
<form/>
...
WebUi.autoForm({ check(v) {...}, save(v) { return WebUi.post('/api/settings', ...); } });
```

`fmt` 값: `text`(기본)·`ms`/`num`/`int`(숫자 키패드)·`bool`/`switch`(토글 스위치)·`secret`(비밀번호)·
`seg`(세그먼트 컨트롤. `options` 필수 — 문자열 배열 또는 `{"value":"0","label":"간이"}` 객체 배열.
장치 값이 숫자 문자열이면 value를 label과 따로 줘야 fill이 맞습니다).
`seg`/`switch`는 라디오 그룹으로 렌더되므로 `form.elements[k].value` 왕복이 네이티브로 동작합니다(bindForm 무변경).
표시는 core.js `showValue`가 같은 `fmt`로 일관되게 처리합니다.

### ops 컴포넌트 사용 예 (인스턴스 속성 문법)

```html
<p class="ops">
<conn-dot timeout="6"/>
<uptime long/>
</p>
<p>
<heap-gauge/>
</p>
```

셋 모두 `@instance` 줄이라 같은 페이지에 여러 번 두면 각각 독립 상태가 됩니다.
속성은 인스턴스 전용 문법이며(`<uptime long/>` → `<wui-uptime long></wui-uptime>`),
참조 태그는 **독립 줄**에 있어야 인식됩니다(다른 텍스트와 한 줄 혼입 시 원문이 남습니다).
`heartbeat`(페이지 기준)와 `uptime`(장치 payload 기준)을 나란히 두면 두 기준 차이를
화면에서 바로 비교할 수 있습니다 — 샘플의 `status-card.nf.html`이 그 예시입니다.

## 규칙

- 이름은 공개 API입니다. 삭제·개명 없이 추가만 권장(호환).
- 새 컴포넌트는 범용성(프로토콜·제품 무관)을 먼저 확인하고, 제품 고유 조각은 각 프로젝트에 둡니다.
- js/css는 html과 **같은 폴더**에 있어야 짝으로 인식됩니다(생성기 규칙).

## 다국어 지원

컴포넌트 소스에는 문구 리터럴이 없고 `uiText(키, {자리표값})`만 있습니다. 기본 언어는 **영어**로,
빌드가 이 폴더 `texts.resx`의 영어 값을 호출부 두 번째 인자(폴백)로 주입합니다(`uiText`는 상용
`/i18n.js`가 제공 — 메인·로그인 페이지가 공용 부분을 한 번 복제하지 않고 같이 참조합니다).
한국어 등 나머지 언어만 `/texts.js`로 서빙되므로 영어가 페이지에 두 번 실리지 않고, 안 쓰는
언어가 페이지에 실리지도 않습니다. 언어 감지 순서(`wui.lang` > 브라우저), `uiSetLang`/`uiAddTexts`,
프로젝트 라우트 배선은 [USAGE.md의 다국어 절](USAGE.md#다국어-i18n)을 보세요.

- 새 문구를 추가할 때는 `form.` `modal.` `wifi.` `fw.` `ei.` `reboot.` `conn.` `up.` `heap.`
  `sys.` `apbar.` `pw.` `empty.` `panel.` `login.` `val.` `core.` 같은 접두 키를 쓰고
  `texts.resx`에 영어 원문(소스의 원본), `texts.ko.resx`에 한국어 번역을 같은 Name으로 넣습니다.
  컴포넌트 코드에는 `uiText('prefix.name')`만 남깁니다.
- 어순이 다른 언어는 `{자리표}`째 문장 전체를 번역합니다. 영어 어순을 다른 언어에 강제하지 마세요.

## 테마 토큰 계약 (2026-11 nanoGate 파일럿 검증 완료)

컴포넌트 CSS는 색을 직접 박지 않고 페이지의 CSS 변수를 참조합니다. 각 변수는
`var(--토큰, 기본 hex)` 형태로 폴백을 가져, 페이지가 아무것도 정의하지 않아도
기본 다크 테마가 그대로 동작합니다.

| 토큰 | 의미 | 폴백 |
|---|---|---|
| `--bg` | 페이지 배경 | `#0f172a` |
| `--card` | 카드·패널 배경 | `#1e293b` |
| `--tx` | 본문 텍스트 | `#e2e8f0` |
| `--mut` | 보조 텍스트 | `#94a3b8` |
| `--line` | 테두리·구분선 | `#334155` |
| `--acc` | 강조(액센트) | `#1d4ed8` |
| `--ok` / `--bad` / `--in` | 상태 텍스트(양호/오류/정보) | `#22c55e` / `#ef4444` / `#38bdf8` |

상태 배경 tint(진한 빨강/초록/앰버 등)는 의미색과 짝을 이루는 고정 값이라 토큰화하지 않았습니다.

기존 색상 체계가 다른 프로젝트는 별칭 한 줄로 채택할 수 있습니다(nanoGate 실측 예):

```css
:root {
  /* nanoGate 자체 토큰: --bg --p --ln --tx --mu --ac --bad --in */
  --card: var(--p); --mut: var(--mu); --line: var(--ln);
  --acc: var(--ac); --ok: var(--ac);
}
```

이 경우 컴포넌트는 페이지와 같은 nanoGate 색으로 렌더링됩니다(스피너 실측:
테두리 `#263843`, 상단 `#4fd1a5`).

## NuGet 배포 (`VagabondK.nF.WebUi` 패키지에 동봉)

컴포넌트와 이 README, 그리고 공용 다국어 사전(`texts*.resx`)은 **라이브러리 패키지에
빌드 자산으로 동봉**되어 배포됩니다(`Build\Pack-WebUi.ps1` → `bin\nuget\webui\`).
컴포넌트만 담는 별도 패키지가 아니라 **생성기(targets+Tasks dll)와 한 몸**인 패키지입니다.

```
VagabondK.nF.WebUi.&lt;ver&gt;.nupkg
└─ build/
   ├─ VagabondK.nF.WebUi.targets      # 소스의 것과 동일 파일(모드 자동 감지)
   ├─ Assets/                         # core.js, i18n.js, theme.css
   ├─ Components/                     # *.nf.{html,css,js} + texts*.resx(본 폴더)
   ├─ Pages/                          # route 마커가 있는 라이브러리 페이지(login.nf.*)
   └─ tools/net472|netstandard2.0/    # VagabondK.nF.WebUi.Tasks.dll
```

- 소비자(nanoGate 등)는 `packages\VagabondK.nF.WebUi.&lt;ver&gt;\build\VagabondK.nF.WebUi.targets`를
  import하면 끝입니다. targets가 `..\Components`(본 폴더)와 `..\Pages`를 자동으로
  `$(WebUiComponents)` 후미에 추가하고, 생성 태스크 dll도 `build\tools`에서 찾아서
  **저장소 소스 트리 없이 빌드**됩니다.
- 프로젝트 로컬 루트가 앞이므로 섀도잉 규칙이 유지되고, 미사용 컴포넌트는 0바이트입니다.
  패키지의 자동 추가를 끄려면 `WebUiNoSharedComponents=true`를 지정하면 됩니다.
- 저장소 내 프로젝트는 같은 targets를 소스에서 직접 import(repo 모드)하며,
  이 경우 태스크 dll은 Tasks 프로젝트를 빌드해 만듭니다(종전 동작).
- 패키지 업그레이드는 곧 ETag 갱신입니다(해시가 페이지 최종 html 기준이라 자동 무효화).

## 로드맵 (유명 프레임워크 카탈로그 매핑, 2026-11 검토)

Bootstrap·Material·Ant Design·Fluent/WinUI·IBM Carbon·shadcn·Grafana 컴포넌트 카탈로그를
MCU 제약(외부 JS/CSS 금지, vanilla + WebUi 버스, 힙 절약)으로 필터링한 결과 45종입니다.
형태 표기: 인라인 = 기존 인라인 병합, 인스턴스 = `@instance` 반복 사용형.

### A. 표시·정보
| 우선 | 이름 | 참조 | 형태 | 내용 |
|---|---|---|---|---|
| 상 | alert | Bootstrap/Ant | ✅ 인스턴스 | note의 일반화(severity 변형) |
| 상 | statistic | Ant/Carbon | ✅ 인스턴스 | 큰 숫자+라벨(status key 구독, fmt kb/ms/pct) |
| 상 | descriptions | Ant | ✅ 인스턴스 | kv의 다단 버전(keys 속성, status 구독) |
| 상 | progress | 전 프레임워크 | ✅ 인스턴스 | 바형 진행률(wui:progress bus 이벤트. 저장·업로드) |
| 중 | badge | Bootstrap/MUI | ✅ 인스턴스 | 카운트·상태 뱃지(slot 본문, variant) |
| 중 | chip | MUI/Ant | ✅ 인스턴스 | 라벨+선택/삭제 태그(wui:chip-close) |
| 중 | spinner | Bootstrap/MUI | ✅ 인스턴스 | CSS-only 회전 |
| 중 | skeleton | MUI/shadcn | ✅ 인스턴스 | 로딩 자리받기(CSS-only shimmer) |
| 중 | empty | Ant | ✅ 인스턴스 | 데이터 없음 상태 |
| 상 | result | Ant | ✅ 인스턴스 | 성공/실패 전페이지 안내 |
| 하 | tooltip | 전 | ✅ 인스턴스 | 호버 버블(text 속성) |
| 하 | timeline | Ant/MUI | ✅ 인스턴스 | 이벤트 로그 스트립(items) |

### B. 내비게이션
| 우선 | 이름 | 참조 | 형태 | 내용 |
|---|---|---|---|---|
| ✅ 완료 | tabs | 전 | 인라인 | 헤더+라우팅 승격(해시 연동). 페이지당 1개 내비라 인라인으로 구현 |
| 중 | navbar | Bootstrap | ✅ 인스턴스 | 제목·연결점·로그아웃 슬롯 헤더 |
| 중 | pagination | 전 | ✅ 인스턴스 | 표와 결합(wui:page) |
| 중 | steps | MUI/Ant | ✅ 인스턴스 | 설정 마법사·업데이트 단계 |
| 중 | collapse | Bootstrap | ✅ 인스턴스 | 아코디언(CSS max-height) |
| 하 | breadcrumb | Ant/MUI | ✅ 인스턴스 | 경로 표시(items) |

### C. 폼·입력
| 우선 | 이름 | 참조 | 형태 | 내용 |
|---|---|---|---|---|
| ✅ 완료 | form | Ant Form | 인라인 | MANIFEST.form 자동 렌더 — 설정 페이지가 한 줄이 됨 |
| 상 | switch | MUI/Ant | ✅ form fmt | `fmt:"switch"`(bool과 동일). 라디오 쌍 토글 |
| 상 | segmented | Ant/Fluent | ✅ form fmt | `fmt:"seg"`+options. 라디오 그룹 버튼형(RTU/ASCII 선택 등) |
| 중 | input-group | Bootstrap | ✅ 인스턴스 | 입력+단위/버튼 결합(slot pre/post) |
| 중 | number | Ant InputNumber | ✅ 인스턴스 | +/- 스테퍼 숫자(wui:number-change) |
| 중 | search | Carbon | ✅ 인스턴스 | 필터 입력(디바운스 wui:search) |
| 하 | password | Bootstrap | ✅ 인스턴스 | show/hide 포함 |
| 상 | file | Ant Upload | ✅ export-import | 백업 가져오기(file+progress+result 조합) |
| 상 | validate | shadcn | 인라인 | 필드 에러 메시지 규약(form과 짝) |

### D. 피드백·레이아웃
| 우선 | 이름 | 참조 | 형태 | 내용 |
|---|---|---|---|---|
| ✅ 완료 | modal | 전 | 인라인 | ask의 일반화(pointdlg 패턴). 토스트처럼 페이지당 1개 시스템이라 인라인으로 구현 |
| 중 | drawer | MUI/Ant | ✅ 인스턴스 | 우측 상세 패널(wui:drawer-close) |
| 상 | card | 전 | ✅ 인스턴스 | 제목+본문 컨테이너(section 승격) |
| 상 | groupbox | HTML fieldset | ✅ 인스턴스 | legend가 테두리를 끊는 그룹 박스(card의 대안 표시) |
| 중 | list-group | Bootstrap | ✅ 인스턴스 | 행 목록+액션(wui:list-click) |
| 하 | divider | MUI/Ant | ✅ 인스턴스 | 구분선+라벨 |
| 하 | grid | Bootstrap/MUI | ✅ 인스턴스 | CSS grid 유틸(cols 속성) |

### E. 장치·ops 특화(우리 니치)
| 우선 | 이름 | 참조/출처 | 형태 | 내용 |
|---|---|---|---|---|
| 상 | conn-dot | Grafana | ✅ 인스턴스 | tick 신선도 기반 연결 상태 |
| 상 | uptime | Grafana 헤더 | ✅ 인스턴스 | heartbeat 일반화 |
| 상 | heap-gauge | Grafana | ✅ 인스턴스 | 남은 힙 게이지 |
| 상 | reboot | 라우터 UI | ✅ 인스턴스 | 카운트다운 재부팅(modal+post 결합) |
| 상 | firmware | ESP Web Flasher | ✅ 인스턴스 | 펌웨어 업데이트(file+progress+result 조합) |
| 상 | export-import | phpMyAdmin | ✅ 인스턴스 | 설정 백업 내보내기/가져오기 |
| 중 | log-tail | Portainer | ✅ 인스턴스 | 로그 스트리밍 뷰어(poll) |
| 중 | register-view | Modbus Tool | ✅ 인스턴스 | 레지스터 라이브 표 |
| 중 | wifi-form | 라우터 UI | ✅ 인스턴스 | network 승격(API 규약 주입식) |
| 중 | sparkline | Grafana | ✅ 인스턴스 | canvas 값 추이(20줄 내외) |
| 상 | ap-banner | nanoGate | ✅ 인스턴스 | 설정 AP 모드 경고(status.ap 구독) |
| 하 | clock | — | ✅ 인스턴스 | 시계(DNP3 타임스탬프용) |

### 제외(MCU 비용 대비 가치 낮음)
carousel·calendar·datepicker(네이티브 `<input type=date>` 대체)·tree·transfer·mentions·
speed-dial·affix/scrollspy·image-lightbox·colorpicker

### 권장 착수 순서
~~form → tabs → modal~~(2026-11 완료) → ~~conn-dot·uptime·heap-gauge~~(2026-11 완료) →
~~segmented·switch·statistic·progress~~(2026-11 완료) →
~~firmware·export-import·alert·result·card~~(2026-11 완료) →
~~descriptions·badge·chip·spinner·skeleton·empty~~(2026-11 완료) →
~~navbar·steps·collapse~~(2026-11 완료) →
~~list-group·drawer·reboot~~(2026-11 완료) →
~~input-group·number·search·password~~(2026-11 완료) →
~~tooltip·timeline·pagination·breadcrumb~~(2026-11 완료) →
~~divider·grid·ap-banner~~(2026-11 완료) →
~~log-tail·register-view·wifi-form·sparkline·clock~~(2026-11 완료) — **45종 전부 완료**

### 인스턴스 컴포넌트 작성 시 주의(scope 문서)

인스턴스 js는 `function (root, document, $)`로 래핑되어 본문 안의 `document`가
**shadow scope 객체**를 가리킵니다. 따라서 전역 document 멤버는 쓰면 안 되고,
scope가 제공하는 것만 쓸 수 있습니다: `getElementById/querySelector/querySelectorAll/
createElement/body/addEventListener`. 실제 document에 이벤트를 발행해야 하면
`document.dispatchEvent(...)` 대신 **`WebUi.fire(name, detail)`**를 쓰십시오
(`WebUi.on`은 내부적으로 실제 document에 구독하므로 그대로 호환됩니다).

참조 태그 `<name 속성="값"/>`는 **줄 전체가 그 태그일 때만** 컴포넌트 포함을 트리거합니다(인라인 컴포넌트).
`<p><skeleton/></p>`처럼 인라인으로 감싸면 마크업으로 남고 라이브러리는 포함되지 않습니다.
인스턴스 컴포넌트는 `<wui-이름>` 태그 자체가 위치와 무관하게 포함을 트리거하므로,
본문이 필요한 컴포넌트(badge·chip·card)는 `<wui-이름>본문</wui-이름>`을 직접 쓰면 되고
빈 인스턴스는 css에 `:host(:empty){display:none}`을 두어 숨깁니다.
`@instance` 마커는 html 파일의 **단독 줄**에서만 인식됩니다(태그·텍스트와 한 줄 혼입 시 원문이 남습니다).
`@instance form`은 폼 연관(Form-associated Custom Element) 선언으로, 생성 시 `def(..., {form:1})`가 붙어
`form.elements[name]`이 호스트를 반환하고 `.value` 쓰기가 내부 값과 폼 값에 동기 반영됩니다(number·search·password).
