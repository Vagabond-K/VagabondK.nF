// tooltip(라이브러리 인스턴스): 감싼 본문 위에 마우스를 올리면 text 버블 표시.
// 사용: <wui-tooltip text="DNP3 마스터 IP"><button>도움말</button></wui-tooltip>
// <wui-tooltip> 태그 자체가 컴포넌트를 포함한다(별도 참조 불필요).
var self = this;
var tip = $('tp');
self.onmouseenter = function () {
  tip.textContent = self.getAttribute('text') || '';
  tip.hidden = !tip.textContent;
};
self.onmouseleave = function () { tip.hidden = true; };
