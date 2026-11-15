// statistic(라이브러리 인스턴스): wui:status의 한 키를 큰 숫자로 표시(Ant Statistic/Carbon Tile의 MCU축소판).
// 속성: key(상태 키, 기본 'requests'), label(라벨, 생략 시 key), fmt(kb|ms|pct|num 그 외 원문).
var key = this.getAttribute("key") || "requests";
var label = this.getAttribute("label") || key;
var fmt = this.getAttribute("fmt") || "";
$("stL").textContent = label;

function show(v) {
  var e = $("stV");
  if (!e) return;
  if (v == null) {
    e.textContent = "–";
    return;
  }
  var n = Number(v);
  if (isNaN(n)) {
    e.textContent = String(v);
    return;
  }
  if (fmt === "kb") e.textContent = (n / 1024).toFixed(1);
  else if (fmt === "ms") e.textContent = String(n);
  else if (fmt === "pct") e.textContent = Math.round(n) + "%";
  else e.textContent = String(n);
  if (fmt === "kb") {
    var u = $("stL");
    u.textContent = label + " (KB)";
  }
}

WebUi.on("wui:status", function (s) {
  show(s[key]);
});
