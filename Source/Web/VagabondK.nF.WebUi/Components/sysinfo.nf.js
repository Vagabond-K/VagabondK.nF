// sysinfo(라이브러리): wui:tick을 구독해 가동 시간/남은 힙/요청 수/프로필을 채웁니다.
// 합쳐진 스크립트에서 이름 충돌을 막으려 IIFE로 격리합니다. 페이지 전역($ 등)에 의존하지 않습니다.
(function () {
  function el(tag, cls, text) {
    const e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text != null) e.textContent = text;
    return e;
  }

  function dur(sec) {
    sec = Math.max(0, Math.floor(sec));
    const h = Math.floor(sec / 3600),
      m = Math.floor((sec % 3600) / 60),
      s = sec % 60;
    return h
      ? uiText("up.hm", { h: h, m: m })
      : m
        ? uiText("up.ms", { m: m, s: s })
        : uiText("up.s", { s: s });
  }

  function kb(v) {
    return (v / 1024).toFixed(1) + " KB";
  }

  WebUi.on("wui:tick", function (d) {
    const dl = document.getElementById("sysDl");
    if (!dl) return;
    dl.textContent = "";
    dl.append(
      el("dt", "", uiText("sys.uptime")),
      el("dd", "", dur(d.uptime)),
      el("dt", "", uiText("sys.heap")),
      el("dd", "", kb(WebUi.ST.heapFree)),
      el("dt", "", uiText("sys.req")),
      el("dd", "", String(WebUi.ST.requests)),
      el("dt", "", uiText("sys.profile")),
      el("dd", "", WebUi.M.id),
    );
  });
})();
