// sysinfo(라이브러리): wui:tick을 구독해 가동 시간/남은 힙/요청 수/프로필을 채운다.
// 합쳐진 스크립트에서 이름 충돌을 막으려 IIFE로 격리한다. 페이지 전역($ 등)에 의존하지 않는다.
(function () {
  function el(tag, cls, text) {
    const e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text != null) e.textContent = text;
    return e;
  }

  function dur(sec) {
    sec = Math.max(0, Math.floor(sec));
    const h = Math.floor(sec / 3600), m = Math.floor(sec % 3600 / 60);
    return h ? h + '시간 ' + m + '분' : m ? m + '분 ' + (sec % 60) + '초' : sec + '초';
  }

  function kb(v) { return (v / 1024).toFixed(1) + ' KB'; }

  WebUi.on('wui:tick', function (d) {
    const dl = document.getElementById('sysDl');
    if (!dl) return;
    dl.textContent = '';
    dl.append(
      el('dt', '', '가동 시간'), el('dd', '', dur(d.uptime)),
      el('dt', '', '남은 힙'), el('dd', '', kb(WebUi.ST.heapFree)),
      el('dt', '', '요청 수'), el('dd', '', String(WebUi.ST.requests)),
      el('dt', '', '프로필'), el('dd', '', WebUi.M.id));
  });
})();
