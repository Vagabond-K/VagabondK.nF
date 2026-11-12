// tooltip(라이브러리 인스턴스): 감싼 본문 위에 마우스를 올리면 text 버블 표시.
// 사용: <wui-tooltip text="DNP3 마스터 IP"><button>도움말</button></wui-tooltip>
// 참조는 <!--@use:tooltip--> + 수동 태그(본문이 있으므로).
var self = this;
var tip = $('tp');
self.onmouseenter = function () {
  tip.textContent = self.getAttribute('text') || '';
  tip.hidden = !tip.textContent;
};
self.onmouseleave = function () { tip.hidden = true; };
