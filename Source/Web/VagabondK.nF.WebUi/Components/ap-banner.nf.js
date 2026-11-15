// ap-banner(라이브러리 인스턴스): 서버 상태의 특정 값이 참일 때 나타나는 경고 바.
// wui:status의 key(기본 "ap")가 참이면 경고 바를 보이고, 아니면 숨깁니다.
// 사용: <ap-banner/> 또는 <ap-banner key="ap" text="…"/>
var self = this;
var key = self.getAttribute("key") || "ap";
WebUi.on("wui:status", function (s) {
  var on = !!(s && s[key]);
  $("b").hidden = !on;
  if (on && !$("tx").textContent) {
    $("tx").textContent = self.getAttribute("text") || uiText("apbar.text");
  }
});
