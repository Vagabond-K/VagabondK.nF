// 정적 영역 로컬라이저: 기본 언어(영어) 외의 언어에서 HTML 마크업에 박힌 영어 텍스트를
// __wuiTx.static[uiLang] 사전(en 원문 -> 번역)으로 한 번에 치환합니다.
// 런타임 문자열(toast/ask 등)은 각 호출부의 uiText()가 담당하고, 이 코드는
// 정적 텍스트 노드·placeholder·title·form LABELS만 건드립니다.
// 사전은 빌드가 texts.resx/texts.<컬처>.resx의 static.* 짝으로 방출합니다.
// 인스턴스 컴포넌트의 정적 텍스트는 <template> 원본과 마운트된 shadow root에 있으므로
// 세 영역(라이트 DOM·template.content·shadowRoot)을 모두 순회합니다 — template을 고치면
// localizeStatic 실행 뒤에 마운트되는 인스턴스도 번역된 복제본을 받습니다.
function localizeStatic() {
  if (uiLang === "en" || typeof document === "undefined" || !document.body)
    return;
  var d = (window.__wuiTx || {}).static;
  var s = d && d[uiLang];
  if (!s) return;
  function locTexts(root) {
    var w = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, null, false),
      n;
    while ((n = w.nextNode())) {
      var p = n.parentNode;
      if (!p || p.nodeName === "SCRIPT" || p.nodeName === "STYLE") continue;
      var raw = n.nodeValue,
        cut = raw.trim();
      if (!cut) continue;
      // 개행/다중 공백이 섞인 마크업도 en 원문(한 줄)과 짝지어지도록 공백 정규화.
      var v = s[cut] != null ? s[cut] : s[cut.replace(/\s+/g, " ")];
      if (v != null) n.nodeValue = raw.replace(cut, v);
    }
  }
  function locAttrs(root) {
    if (!root.querySelectorAll) return;
    var at = root.querySelectorAll("[placeholder]");
    for (var i = 0; i < at.length; i++) {
      var t2 = s[at[i].getAttribute("placeholder")];
      if (t2 != null) at[i].setAttribute("placeholder", t2);
    }
  }
  locTexts(document.body);
  locAttrs(document.body);
  var tp = document.body.querySelectorAll("template");
  for (var ti = 0; ti < tp.length; ti++) {
    if (tp[ti].content) {
      locTexts(tp[ti].content);
      locAttrs(tp[ti].content);
    }
  }
  var el = document.body.querySelectorAll("*");
  for (var ei = 0; ei < el.length; ei++) {
    var sr = el[ei].shadowRoot;
    if (sr) {
      locTexts(sr);
      locAttrs(sr);
    }
  }
  var t3 = s[document.title.trim()];
  if (t3 != null) document.title = t3;
  if (typeof WebUi !== "undefined" && WebUi.LABELS) {
    var L = WebUi.LABELS;
    for (var k in L) {
      var v2 = s[L[k]];
      if (v2 != null) L[k] = v2;
    }
  }
}
if (typeof document !== "undefined") {
  if (document.readyState === "loading")
    document.addEventListener("DOMContentLoaded", localizeStatic);
  else localizeStatic();
}
