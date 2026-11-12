// collapse(라이브러리 인스턴스): Bootstrap식 아코디언 한 벌. 본문을 slot으로 받는다.
// 사용: <!--@use:collapse--> + <wui-collapse title="고급 설정" open>…본문…</wui-collapse>
// 헤더 클릭 시 host의 open 속성을 토글(확장은 CSS :host([open]) max-height).
var self = this;
var t = self.getAttribute('title');
if (t) $('cT').textContent = t;
$('cB').onclick = function () {
  if (self.hasAttribute('open')) self.removeAttribute('open');
  else self.setAttribute('open', '');
};
