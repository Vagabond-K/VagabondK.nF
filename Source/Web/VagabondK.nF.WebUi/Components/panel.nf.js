// panel(라이브러리): wui:status를 구독하는 범용 프로필 패널.
// 프로젝트 WebUi에 같은 이름 panel.nf.html이 있으면 이 파일은 섀도잉되어 번들에 들어가지 않습니다.
WebUi.on("wui:status", function (s) {
  WebUi.render(document.getElementById("pv"), [
    WebUi.node("h2", null, uiText("panel.device", { id: WebUi.M.id })),
    WebUi.node(
      "dl",
      null,
      WebUi.node("dt", null, uiText("panel.name")),
      WebUi.node("dd", { key: "dn", text: s.deviceName }),
      WebUi.node("dt", null, "IP"),
      WebUi.node("dd", { key: "ip", text: s.ip }),
    ),
  ]);
});
