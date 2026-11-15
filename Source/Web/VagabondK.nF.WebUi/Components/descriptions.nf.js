// descriptions(라이브러리 인스턴스): Ant Descriptions식 kv 다단 표시.
// 사용: <descriptions title="장치 정보" keys="deviceName,pollMs,debug,mode"/>
// wui:status를 구독해 keys 순서대로 <dt>=LABELS(없으면 키)/<dd>=값을 렌더합니다.
// 값 포맷은 페이지가 원하면 detail 콜백 대신 showValue(form,...)로 확장 가능(미포함).
var keys = (this.getAttribute("keys") || "").split(",");
var t = this.getAttribute("title");
if (t) {
  $("dD").textContent = t;
  $("dD").hidden = false;
}
WebUi.on("wui:status", function (s) {
  var rows = [],
    i,
    k;
  for (i = 0; i < keys.length; i++) {
    k = keys[i].trim();
    if (!k) continue;
    rows.push(WebUi.node("dt", null, (WebUi.LABELS && WebUi.LABELS[k]) || k));
    rows.push(
      WebUi.node("dd", { key: k, text: s && s[k] != null ? String(s[k]) : "—" }),
    );
  }
  WebUi.render($("dL"), rows);
});
