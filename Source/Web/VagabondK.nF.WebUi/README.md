# VagabondK.nF.WebUi

[Web 그룹](../README.md) · [전체 프로젝트](../../../README.md)

nanoGate 스타일 장치 관리 화면을 빌드 타임에 조립하는 페이지 컴포저입니다.
공용 **셸**(탭·로그인·fetch·다이얼로그·포인트 편집기) 하나와 게이트웨이별 작은 **프로필** 하나를
머지·검증·축소해 C# 문자열 리소스 하나로 임베드합니다 — 런타임 리플렉션 없음, 외부 자산 없음.

장치에서 실행되는 코드는 없으므로 이 프로젝트는 일반 Roslyn **소스 생성기**(netstandard2.0)입니다.
`.targets`가 셸·프로필·`Assets/core.js`를 `AdditionalFiles`로, 네임스페이스 등 옵션을 globalconfig로
주입하면 생성기가 컴파일 타임에 페이지를 조립해 `WebPage` 소스를 만들어 냅니다.

## 파일

| 파일 | 역할 |
|---|---|
| `Assets/core.js` | 런타임 라이브러리(`<!--@core-->`에 주입): 매니페스트 접근, 폼 팩토리, 값 포맷, 마이크로 vDOM, 컴포넌트 레지스트리 |
| `VagabondK.nF.WebUi.targets` | MSBuild 배선(`WebUiWriteGeneratorConfig`, `CoreCompile` 직전 실행): `AdditionalFiles`+globalconfig 주입 |
| `WebUiGenerator.cs` | 생성기 진입점(`IIncrementalGenerator`): 입력 수집→조립→`WebPage` 소스 출력, WUI000~002 진단 |
| `PageComposer.cs` | 머지·매니페스트 검증·축소·gzip·FNV 해시와 C# 코드 생성 |
| `MiniJson.cs` | 매니페스트 파싱/직렬화용 최소 JSON 구현 |

## 마커

`shell.html`은 다음 순서로 포함해야 합니다:
`<!--@manifest-->` `<!--@core-->` `<!--@style-->` `<!--@views-->` `<!--@script-->`

`profile.html`은 다음을 포함해야 합니다: `<!--@manifest-->` `<!--@style-->` `<!--@views-->` `<!--@script-->`
(프로필의 `@core`는 오류입니다 — 라이브러리가 제공합니다.)
여기에 선택적 `<!--@slots-->` 섹션을 둘 수 있습니다(*컴포넌트와 슬롯* 참조).

## @use 컴포넌트 번들링

조각 HTML을 파일로 분리해 빌드 타임에 조립합니다. 위치는 한 줄 주석 토큰 `<!--@use:name-->` —
셸/프로필의 마크업(뷰·슬롯) 어디에든, 또 컴포넌트 HTML 안에도 넣을 수 있습니다(중첩, 순환 검출).

```
WebUi\components\
  heartbeat\
    heartbeat.html   ← 필수: @use 자리 인라인, 내부에 <!--@use:다른이름--> 가능
    heartbeat.css    ← 선택: @style 섹션 *앞쪽*에 병합(프로필이 오버라이드 가능)
    heartbeat.js     ← 선택: @script 섹션 *앞쪽*에 병합(프로필이 오버라이드 가능)
```

폴더명과 파일명이 같아야 합니다(`note/note.html`). 기본 경로는 프로젝트의 `WebUi\components`이며
`$(WebUiComponents)`로 바꿀 수 있습니다. 세미콜론으로 여러 루트를 지정하면 전부 훑습니다
(예: 공용 셸을 쓰는 프로젝트군에서 `$(MSBuildProjectDirectory)\WebUi\components;$(공유)\WebUi\components`).
컴포넌트 HTML 안의 `<!--@slot:name-->` 토큰도 셸과 똑같이 프로필 슬롯 내용으로 채워집니다.
컴포넌트는 셸·프로필과 함께 `AdditionalFiles`로 전달되어 파일 수정 시 증분 재생성됩니다.
미지정 이름이나 순환 참조는 WUI001 빌드 오류가 됩니다.

## 컴포넌트와 슬롯

셸이 프로필을 미리 알지 못한 채로, 프로필이 셸의 고정 위치에 자기 UI를 놓는 두 가지 상호 보완 방식입니다.
Blazor의 레이아웃/컴포넌트 개념을 정적 페이지에 맞게 축소한 것입니다.

**런타임 함수 컴포넌트** (`Assets/core.js`) — 컴포넌트는 `(props) -> vnode | 배열 | 문자열 | null` 함수입니다.
`WebUi.h(fn, props)`는 인라인으로 렌더하고, `WebUi.component(name, fn)`은 이름으로 등록,
`WebUi.slot(name, props)`는 등록된 것을 렌더합니다. 셸은 자기가 쓰는 명명 슬롯마다 *기본* 컴포넌트를
등록하므로 아무것도 하지 않는 프로필은 그대로 동작하고, `@script`(셸 코드보다 나중에 실행)에서
`WebUi.component(name, ...)`을 호출하는 프로필은 기본을 투명하게 오버라이드합니다.

셸의 캐시 테이블이 이 방식을 씁니다: `renderCache`가 앞 컬럼을 `WebUi.slot('cacheRow', {p, cc})`로
렌더하고 공용 값·품질 셀은 셸이 직접 append합니다. 기본 `cacheRow`는 레거시
`profileCacheRow(p)` 문자열 배열 훅(과 ID 폴백)을 브리지하므로 기존 프로필은 무영향이고,
프로필이 `td` vnode를 직접 반환해 마크업을 완전히 통제할 수도 있습니다.

**빌드타임 명명 슬롯** (`.targets`) — 셸이 줄 전체 토큰 `<!--@slot:NAME-->`으로 위치를 예약하고,
프로필이 `@slots` 섹션에서 채웁니다:

```html
<!--@slots-->
<!--@slot:pointsNote-->
<span>이 프로필은 ... 포인트를 바꾼 후에는 재부팅해야 반영됩니다.</span>
```

머지 시 생성기가 각 셸 토큰을 대응하는 프로필 블록으로 치환합니다(프로필이 제공하지 않은
토큰은 삭제되므로 슬롯은 프로필별 선택입니다). 셸이 참조하지 않는 슬롯을 프로필이 채우면
빌드가 실패합니다. 이는 기존 `$('pNote').textContent = '...'` JS 주입 패턴을 정적 마크업으로
대체하며, 크기가 작고 런타임 비용이 0입니다.

## 매니페스트 (단일 진실 공급원)

프로필의 `@manifest` 섹션은 JSON 객체 하나를 담습니다:

```json
{"id":"modbus-to-mqtt","schema":5,
 "form":[{"name":"timeout","label":"응답 대기 시간","fmt":"ms"}, ...],
 "features":{"byteOrder":true,"access":["RO","RW"],"name":false,
             "addr":{...},"cacheCols":{"head":[...]},"points":false}}
```

* `id` — `/api/settings`에 `gatewayProfile`로 전송되는 게이트웨이 프로필 문자열.
* `schema` — 저장·백업 페이로드가 쓰는 정수 `ConfigurationSchemaVersion`.
* `form` — 순서가 있는 설정 필드. `name`은 프로필 `@views`의 `#sf` 폼 입력과 일치해야 합니다.
  선택 `fmt`(`ms | pin | secret | bool`)가 변경 다이얼로그 레이블을 결정합니다
  (select·radio는 표시 텍스트를 자동 해석).
* `features` — 화이트리스트: `points`, `scale`, `name`, `byteOrder`, `access`, `addr`,
  `cacheCols`. 셸은 `WebUi.feat(name, 기본값)`으로 읽습니다.

빌드 타임 검증은 다음에서 컴파일을 실패시킵니다: 알 수 없는 매니페스트 키, 잘못된 id/schema,
form 이름 중복·누락, views에 없는 form 이름, 프로필 스크립트에 남아 있는 레거시 전역
(`PROFILE`, `KEYS`, `LABELS`, `profileAddr`, ...), 필수 훅 누락, 셸에 없는 슬롯의 프로필 채움.

## 프로필 훅

필수: `profileRender(status)`, `profileTick(uptimeSeconds)`, `checkSettings(values)`,
`syncForm()`. 선택: `profilePoint()`(새 포인트 기본값), `profileCacheRow(p)`
(`features.cacheCols`와 함께. 기본 `cacheRow` 컴포넌트가 브리지 — 대신
`WebUi.component('cacheRow', ...)`를 등록해도 됨), `mapDirty()`(프로필 소유 편집기,
예: DNP3 범위 규칙). `profileExport/profileImport`는 셸이 제공합니다.

셸은 매니페스트 기반 공용 헬퍼를 노출합니다: `formValues()`, `fillForm(s)`,
`profileDirty()`, `formState(errs)`(오류 + 변경 점 + 저장/취소 상태),
`formChanges(v)`(다이얼로그 diff 줄), `settingsBody(v)`(POST 본문), `accessList()`,
`addrCfg(side)`, `feat()`, `MAN`, `PROFILE`, `KEYS`, `LABELS`.

## 마이크로 vDOM

`WebUi.h(tag, props, ...children)`로 vnode 트리를 만들고(`{key: ...}` 안정적 diff,
`onclick` 핸들러, `text`·`className`·`hidden`·`style` 프로퍼티 지원), `tag`가 함수면
컴포넌트로 취급해 `props`로 호출합니다.
`WebUi.render(parentEl, vnodes)`는 최소 변경으로 DOM을 제자리 패치합니다.
`WebUi.component(name, fn)` / `WebUi.slot(name, props)`는 위의 명명 레지스트리입니다.
셸의 포인트·캐시 테이블이 사용합니다.

## 사용법 (소비 프로젝트)

```xml
<PropertyGroup>
  <WebUiShell>..\shared\shell.html</WebUiShell>
  <WebUiProfile>$(MSBuildProjectDirectory)\WebUi\profile.html</WebUiProfile>
  <WebUiNamespace>MyGateway.Management</WebUiNamespace>
</PropertyGroup>
<Import Project="..\..\VagabondK.nF\Source\Web\VagabondK.nF.WebUi\VagabondK.nF.WebUi.targets" />
<ProjectReference Include="..\..\VagabondK.nF\Source\Web\VagabondK.nF.WebUi\VagabondK.nF.WebUi.csproj"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false" SkipGetTargetFrameworkProperties="true" />
```

생성 소스는 컴파일러에 의해 메모리에서 직접 컴파일됩니다(디스크에 내려다보려면
`/p:EmitCompilerGeneratedFiles=true`). 페이지가 64KB를 넘지 않는 장치를
배려해 gzip 바이트열을 여러 파트로 나눠 `WebPage.Length` / `PartCount` / `ETag` 상수와
`WebPage.GetPart(int)` 메서드로 노출하며, 관리 프로젝트의 일반 소스처럼 컴파일됩니다.