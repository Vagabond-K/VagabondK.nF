// result(라이브러리 인스턴스): Ant Result식 성공/실패 안내. 큰 아이콘+제목+보조 문구.
// 속성: status(ok|bad|info, 기본 info), title, sub. title이 비면 숨김.
// wui:result {status,title,sub} 구독 — firmware·export-import가 완료 시 이 이벤트로 결과 화면을 채웁니다.
var st = this.getAttribute("status") || "info";
var ti = this.getAttribute("title") || "";
var su = this.getAttribute("sub") || "";
var icons = { ok: "✓", bad: "✕", info: "ℹ" };

function show(s, t, u) {
  var b = $("rsB");
  if (!b) return;
  if (s) st = s;
  if (t != null) ti = t;
  if (u != null) su = u;
  b.className = "rs " + st;
  $("rsI").textContent = icons[st] || "ℹ";
  $("rsT").textContent = ti;
  $("rsS").textContent = su;
  b.hidden = ti === "";
}

show("", null, null);
WebUi.on("wui:result", function (d) {
  show(d.status, d.title, d.sub);
});
