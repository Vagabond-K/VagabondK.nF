# VagabondK.nF.WebUi.Components — 표준 컴포넌트 라이브러리

장치를 위해 미리 만들어 둔 `*.nf.{html,js,css}` 컴포넌트 모음입니다. 프로젝트 폴더에 복사하지 않고
`$(WebUiComponents)`의 **두 번째 루트**로 이 폴더를 지정하기만 하면 됩니다:

```xml
<PropertyGroup>
  <WebUiComponents>$(MSBuildThisFileDirectory)WebUi;$(MSBuildThisFileDirectory)..\..\..\Source\Web\VagabondK.nF.WebUi.Components</WebUiComponents>
</PropertyGroup>
```

- 페이지가 `<!--@use:name-->`(또는 `<name/>`)으로 **참조한 컴포넌트만** 빌드 결과에 들어갑니다.
  이 폴더에 아무리 많은 컴포넌트를 둬도 사용하지 않으면 장치 힙을 1바이트도 차지하지 않습니다.
- 프로젝트 로컬 루트(앞쪽)에 같은 이름이 있으면 로컬이 **섀도잉**하여 라이브러리 버전을 대체합니다.
  내용·모양을 제품별로 바꾸고 싶을 때 해당 이름의 조각을 프로젝트 `WebUi\`에 두면 됩니다.
- 라이브러리 컴포넌트는 페이지 전역 변수에 의존하지 않습니다(필요하면 IIFE로 격리).

## 포함된 컴포넌트

| 이름 | 한 벌 | 설명 |
|---|---|---|
| `note` | html+css | 여백 없는 한 줄 안내 문구. `.nt` 클래스. |
| `sysinfo` | html+js | `wui:tick`을 구독해 가동 시간/남은 힙/요청 수/프로필을 `<dl>`로 채움. |
| `panel` | html+js | `wui:status`를 구독하는 범용 프로필 패널(장치 이름·IP). 프로젝트가 같은 이름으로 대체하기 좋은 예시. |

## 규칙

- 이름은 공개 API입니다. 삭제·개명 없이 추가만 권장(호환).
- 새 컴포넌트는 범용성(프로토콜·제품 무관)을 먼저 확인하고, 제품 고유 조각은 각 프로젝트에 둡니다.
- js/css는 html과 **같은 폴더**에 있어야 짝으로 인식됩니다(생성기 규칙).
