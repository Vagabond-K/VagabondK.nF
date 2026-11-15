// ===== WebUi 공용 i18n 런타임 =====
// 코어(main)와 로그인 두 페이지가 /i18n.js 하나를 링크해 같은 언어 로직을 씁니다.
// (이전: core.js와 login.js가 각각 축약복제본을 가져 드림 버그가 발생 — 2026-11에 통합.)
// 로드 순서: texts.js(사전을 window.__wuiTx에 채움) → i18n.js → 페이지 스크립트.
// 기본 언어는 영어이며 마크업과 빌드 주입 폴백(uiText 두 번째 인자)로 서빙됩니다 —
// texts.js에는 영어가 없습니다(en은 texts.resx에서 빌드가 주입하고, 사전은 ko 등 번역만 담습니다).
// 언어 결정 순서: localStorage('wui.lang') 수동 선택 > navigator.language(브라우저 언어).
// ko로 시작하면 한국어, 아니면 사전에서 일치하는 언어를 쓰고 없으면 기본 en으로 폴백합니다.
window.__wuiTx = window.__wuiTx || {};
var uiTx = window.__wuiTx;
var uiLang = (function () {
  var p = "",
    has = false;
  for (var k0 in uiTx) has = true;
  try {
    p = localStorage.getItem("wui.lang");
  } catch (e) {}
  if (!p) {
    try {
      p = navigator.language || navigator.userLanguage || "";
    } catch (e) {}
  }
  p = String(p).toLowerCase();
  if (!has || !p) return "en";
  if (p.indexOf("ko") === 0) return "ko";
  for (var k in uiTx) if (p.indexOf(k) === 0) return k;
  return "en";
})();
// html lang 속성: 접근성/글자꼴이 언어를 따라가도록 문서에 반영합니다.
if (typeof document !== "undefined") document.documentElement.lang = uiLang;
// uiText(key, en 폴백, {자리표시자:값}): 활성 사전에 키가 있으면 그 언어 문장, 없으면 영어 폴백.
// 두 번째 인자는 빌드가 texts.resx에서 주입합니다 — 소스 호출부는 uiText('key')만 씁니다.
// {n} 같은 자리표시자는 값으로 치환합니다(어순이 다른 언어를 위해 템플릿째로 번역).
function uiText(key, en, params) {
  var s = en;
  if (uiLang !== "en" && uiTx[uiLang]) {
    var v = uiTx[uiLang][key];
    if (v) s = v;
  }
  if (params) for (var k in params) s = s.split("{" + k + "}").join(params[k]);
  return s;
}
// uiSetLang: 언어를 저장하고 새로고침합니다(라벨은 마운트/렌더 시점에 결정되므로 새로고침이 필요합니다).
function uiSetLang(l) {
  try {
    localStorage.setItem("wui.lang", l);
  } catch (e) {}
  location.reload();
}
// uiAddTexts: 페이지 스크립트가 사후에 사전을 보강/덮어씁니다(프로젝트 전용 문구 추가용).
function uiAddTexts(lang, dict) {
  var d = uiTx[lang] || (uiTx[lang] = {});
  for (var k in dict) d[k] = dict[k];
}
