// list-group(라이브러리 인스턴스): Bootstrap식 행 목록. 행 클릭 시 active + wui:list-click 발행.
// 사용: <list-group items="포인트 표|매핑 편집|진단 로그"/> — 라벨은 | 구분.
// 선택 유지: selected="2"(1-based). 페이지는 wui:list-click의 index로 패널 분기 가능.
var self = this;
var items = (self.getAttribute("items") || "").split("|");
var sel = (parseInt(self.getAttribute("selected"), 10) || 0) - 1;
function click(i, el) {
  var lis = $("lg").children,
    j;
  for (j = 0; j < lis.length; j++) lis[j].className = "";
  el.className = "on";
  WebUi.fire("wui:list-click", { index: i + 1, text: items[i] });
}
var rows = [],
  i;
for (i = 0; i < items.length; i++) {
  if (!items[i].trim()) continue;
  rows.push(
    WebUi.node("li", {
      className: i === sel ? "on" : "",
      text: items[i].trim(),
      onclick: (function (n) {
        return function () {
          click(n, this);
        };
      })(i),
    }),
  );
}
WebUi.render($("lg"), rows);
