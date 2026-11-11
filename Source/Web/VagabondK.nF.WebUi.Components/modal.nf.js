// modal 컴포넌트: 확인/입력 대화상자. WebUi.modal(opts) → Promise<boolean>(확인 true, 취소/바깥 클릭 false).
// opts = {title, body(문자열 또는 DOM 노드), ok='확인', cancel(false=취소 버튼 숨김, 문자열=라벨)}
// ask(nanoGate pointdlg) 패턴의 일반화: 페이지는 오버레이 마크업 없이 한 번의 호출로 충분하다.

WebUi.modal = function (opts) {
  var o = opts || {};
  return new Promise(function (res) {
    var mask = document.getElementById('wm-mask');
    var ok = document.getElementById('wm-ok'), cancel = document.getElementById('wm-cancel');
    document.getElementById('wm-title').textContent = o.title || '';
    var body = document.getElementById('wm-body');
    body.textContent = '';
    if (typeof o.body === 'string') body.textContent = o.body;
    else if (o.body) body.appendChild(o.body);
    ok.textContent = o.ok || '확인';
    if (o.cancel === false) cancel.hidden = true;
    else if (typeof o.cancel === 'string') { cancel.hidden = false; cancel.textContent = o.cancel; }
    else cancel.hidden = false;
    function close(r) {
      mask.hidden = true;
      ok.onclick = cancel.onclick = mask.onclick = null;
      res(r);
    }
    ok.onclick = function () { close(true); };
    cancel.onclick = function () { close(false); };
    mask.onclick = function (ev) { if (ev.target === mask) close(false); };
    mask.hidden = false;
  });
};
