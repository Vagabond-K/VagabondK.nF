// drawer(라이브러리 인스턴스): MUI식 우측 슬라이드 패널. 본문을 slot으로 받는다.
// 사용: <!--@use:drawer--> + <wui-drawer title="포인트 상세">…</wui-drawer>
// 열기: host에 open 속성(마크업 또는 js). ×/배경 클릭 시 open 제거 + wui:drawer-close 발행.
var self = this;
var t = self.getAttribute('title');
if (t) $('dR').textContent = t;
function close() {
  self.removeAttribute('open');
  WebUi.fire('wui:drawer-close', {});
}
$('dX').onclick = close;
$('dV').onclick = function (ev) { if (ev.target === $('dV')) close(); };
