// progress(라이브러리 인스턴스): 바형 진행률. firmware/export-import의 구성 부품이며 단독 데모도 가능.
// 속성: max(기본 100), label(문구). wui:progress 이벤트 {value, max?, label?}로 갱신; value 속성으로 정적 초기값.
var max = Number(this.getAttribute("max")) || 100;
var label = this.getAttribute("label") || "";
var host = this;

function set(v, m, l) {
  var bar = $("pgF");
  if (!bar) return;
  m = m || max;
  var p = Math.max(0, Math.min(100, Math.round((v / m) * 100)));
  bar.style.width = p + "%";
  $("pgN").textContent = p + "%";
  $("pgL").textContent = l || label || "";
  host.classList.toggle("done", p >= 100);
}

if (this.hasAttribute("value")) set(Number(this.getAttribute("value")));
else $("pgL").textContent = label;
WebUi.on("wui:progress", function (d) {
  set(d.value, d.max, d.label);
});
