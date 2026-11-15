// heartbeat 인스턴스 컴포넌트(html의 @instance 마커): <heartbeat/> 태그마다
// 자기 shadow root와 클로저 상태를 갖습니다. 여러 곳에서 반복 사용해도 id가 충돌하지 않고,
// t0와 타이머가 인스턴스별로 독립입니다. body 안의 $와 document는 shadow 스코프.
//
// 사용자 정의 다국어 예시: 문구를 언어 하드코딩 대신 uiText로 사전에서 가져옵니다.
// 사전은 프로젝트 루트 WebUi\texts.resx(en 원문)/texts.ko.resx(번역)로, 빌드 시
// 라이브러리 Components\ 사전을 "키 단위"로 덮어씁니다(hb.* 신규 추가, conn.label 재정의).
// uiText(key, params)는 현재 언어 사전을 찾고 없으면 en 원문으로 폴백합니다
// (en 원문은 빌드가 두 번째 인자로 자동 주입하므로 호출부는 키만 적습니다).
// 빌드가 아니라 런타임에 사전만 보강/재정의하려면: uiAddTexts('ko', { 'hb.title': '커스텀 문구 · {n}초' }).

var t0 = Date.now();
var e0 = $("hb");
if (e0) e0.textContent = uiText("hb.checking");

setInterval(function () {
  var e = $("hb");
  if (e)
    e.textContent = uiText("hb.title", {
      n: Math.floor((Date.now() - t0) / 1000),
    });
}, 1000);
