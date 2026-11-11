// sysinfo 컴포넌트: wui:tick을 구독해 상태 목록(가동 시간/힙/요청 수/프로필)을 채운다.
// dur/kb/fillDl/el은 여기서만 쓰는 도우미라 페이지가 아니라 컴포넌트에 속한다.

const el = (tag, cls, text) => {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text != null) e.textContent = text;
  return e;
};

function dur(sec) {
  sec = Math.max(0, Math.floor(sec));
  const h = Math.floor(sec / 3600), m = Math.floor(sec % 3600 / 60);
  return h ? h + '시간 ' + m + '분' : m ? m + '분 ' + (sec % 60) + '초' : sec + '초';
}

const kb = v => (v / 1024).toFixed(1) + ' KB';

function fillDl(id, rows) {
  const d = $(id);
  d.textContent = '';
  for (const [k, v] of rows) d.append(el('dt', '', k), el('dd', '', String(v)));
}

WebUi.on('wui:tick', d => {
  fillDl('sysDl', [
    ['가동 시간', dur(d.uptime)],
    ['남은 힙', kb(WebUi.ST.heapFree)],
    ['요청 수', WebUi.ST.requests],
    ['프로필', WebUi.M.id],
  ]);
});
