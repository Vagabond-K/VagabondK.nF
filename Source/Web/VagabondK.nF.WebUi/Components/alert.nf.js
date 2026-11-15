// alert(라이브러리 인스턴스): note의 일반화(Bootstrap Alert/Ant Alert의 severity 변형).
// 속성: severity(info|warn|error|ok, 기본 info), text. text가 비면 숨김.
// wui:alert {severity,text} 이벤트로 런타임에도 갱신됩니다(토스트와 달리 자리 고정 안내용).
var sev = this.getAttribute("severity") || "info";
var txt = this.getAttribute("text") || "";
var icons = { info: "ℹ", warn: "⚠", error: "✕", ok: "✓" };

function show(s, t) {
  var b = $("alB");
  if (!b) return;
  if (s) sev = s;
  if (t != null) txt = t;
  b.className = "al " + sev;
  $("alI").textContent = icons[sev] || "ℹ";
  $("alT").textContent = txt;
  b.hidden = txt === "";
}

show("", null);
WebUi.on("wui:alert", function (d) {
  show(d.severity, d.text);
});
