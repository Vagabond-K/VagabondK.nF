// 프로필 패널 인스턴스 컴포넌트: 서버 상태 갱신을 "구독"으로 처리하는 최소 예시.
// - wui:status: core.js가 /api/status 폴링마다 발행(전체 상태 객체 s). 여기서 DOM을 통째로 그립니다.
// - wui:tick: status를 받은 뒤 매초 발행(age/uptime 외삽). 가벼운 텍스트만 갱신합니다.
// WebUi.render(node 파생 트리를 target에 재사용 병합)와 WebUi.node(빠른 요소 생성)의 조합이
// 이 프레임워크의 표준 렌더 방식입니다. key를 주면 다음 render에서 같은 key 노드를 재활용합니다.
// (문구 데모라 여기서는 하드코딩 한국어 — 다국어가 필요하면 heartbeat.nf.js처럼 uiText로 옮깁니다.)

WebUi.on("wui:status", (s) =>
  WebUi.render(document.getElementById("pv"), [
    WebUi.node("h2", null, "프로필 패널 · " + WebUi.M.id),
    WebUi.node(
      "dl",
      null,
      WebUi.node("dt", null, "장치 이름"),
      WebUi.node("dd", { key: "dn", text: s.deviceName }),
      WebUi.node("dt", null, "폴링 주기"),
      WebUi.node("dd", {
        key: "pm",
        text: WebUi.showValue(
          document.getElementById("wf"),
          "pollMs",
          s.pollMs,
        ),
      }),
      WebUi.node("dt", null, "진단 모드"),
      WebUi.node("dd", {
        key: "db",
        text: s.debug === "1" ? "켜짐 (디버그 출력에 기록)" : "꺼짐",
      }),
    ),
    WebUi.node("p", { key: "age", className: "age", text: "" }),
  ]),
);

WebUi.on("wui:tick", (d) => {
  const a = document.getElementById("pv").querySelector(".age");
  if (a)
    a.textContent = "가동 " + Math.floor(d.uptime) + "초 · tick은 매초 실행";
});
