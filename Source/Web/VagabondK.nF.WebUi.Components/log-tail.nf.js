// log-tail(라이브러리 인스턴스): Portainer식 로그 스트리밍 뷰어. api를 주기 폴링해 꼬리 n줄 유지.
// 사용: <log-tail api="/api/log" lines="10" ms="3000"/> — 서버 응답은 {lines:[...]} 기대.
var self = this;
var api = self.getAttribute('api') || '/api/log';
var ms = parseInt(self.getAttribute('ms'), 10) || 3000;
var max = parseInt(self.getAttribute('lines'), 10) || 12;
var pre = $('lt');
function poll() {
  WebUi.api(api).then(function (r) {
    if (!r || !r.lines) return;
    var txt = pre.textContent, i, t;
    for (i = 0; i < r.lines.length; i++) txt += (txt ? '\n' : '') + r.lines[i];
    t = txt.split('\n');
    if (t.length > max) t = t.slice(t.length - max);
    pre.textContent = t.join('\n');
  }, function () { /* MCU가 다른 요청에 바쁜 틱은 건너뛰고 다음 폴링에서 재시도 */ });
}
poll();
setInterval(poll, ms);
