// heap-gauge(라이브러리 인스턴스): 남은 힙 게이지. nanoFramework는 총 힙 API가 없으므로
// 기준값을 max 속성(바이트)으로 주거나, 관측된 heapFree의 최대치(peak)로 자동 스케일하는 상대 게이지입니다.
// 속성: max(기준 바이트, 생략 시 자동), label(기본 '힙'). 20% 미만이면 빨간색.
var max = parseInt(this.getAttribute("max"), 10) || 0,
  peak = 0;
var label = this.getAttribute("label") || uiText("heap.label");

WebUi.on("wui:status", function (s) {
  var free = s.heapFree || 0;
  if (free > peak) peak = free;
  var ref = max || peak;
  var pct = ref > 0 ? Math.round((free * 100) / ref) : 0;
  var fill = $("hgF");
  if (!fill) return;
  $("hgL").textContent = label;
  fill.style.width = pct + "%";
  fill.className = pct < 20 ? "low" : "";
  $("hgV").textContent = (free / 1024).toFixed(1) + " KB";
});
