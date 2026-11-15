// pagination(라이브러리 인스턴스): ‹ n / m › 페이지 네비게이터.
// 사용: <pagination page="2" total="5"/> — 이동마다 wui:page {page} 발행. 페이지는 그 값으로 표를 다시 그립니다.
var self = this;
var total = Math.max(1, parseInt(self.getAttribute("total"), 10) || 1);
var page = Math.min(
  total,
  Math.max(1, parseInt(self.getAttribute("page"), 10) || 1),
);
function show() {
  $("pgpn").textContent = page + " / " + total;
  $("pgpv").disabled = page <= 1;
  $("pgnx").disabled = page >= total;
}
function go(d) {
  var n = Math.min(total, Math.max(1, page + d));
  if (n === page) return;
  page = n;
  show();
  WebUi.fire("wui:page", { page: page });
}
$("pgpv").onclick = function () {
  go(-1);
};
$("pgnx").onclick = function () {
  go(1);
};
show();
