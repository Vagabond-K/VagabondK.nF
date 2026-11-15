// steps(라이브러리 인스턴스): MUI/Ant식 진행 단계 표시.
// 사용: <steps items="다운로드|전송|적용" current="2"/> — current는 1-based(생략 시 1).
// current 미만은 ✓(done), current는 강조, 초과는 회색.
var items = (this.getAttribute("items") || "").split("|");
var cur = parseInt(this.getAttribute("current"), 10) || 1;
var rows = [],
  i;
for (i = 0; i < items.length; i++) {
  if (!items[i].trim()) continue;
  rows.push(
    WebUi.node("li", {
      className: i + 1 < cur ? "done" : i + 1 === cur ? "act" : "",
      "data-n": String(i + 1),
      text: items[i].trim(),
    }),
  );
}
WebUi.render($("sL"), rows);
