// sparkline(라이브러리 인스턴스): Grafana식 값 추이 캔버스. wui:status의 key 값을 40포인트 링버퍼로 그려요.
// 사용: <sparkline key="requests"/> — max 생략 시 관측 peak로 자동 스케일.
var self = this;
var key = self.getAttribute('key') || 'heapFree';
var max = parseInt(self.getAttribute('max'), 10) || 0;
var cv = $('cv'), ctx = cv.getContext('2d'), buf = [], N = 40;
WebUi.on('wui:status', function (s) {
  var v = s && s[key];
  if (v === undefined) return;
  v = parseFloat(v);
  if (isNaN(v)) return;
  buf.push(v);
  if (buf.length > N) buf.shift();
  var hi = max, i;
  if (!hi) { hi = 1; for (i = 0; i < buf.length; i++) if (buf[i] > hi) hi = buf[i]; }
  ctx.clearRect(0, 0, cv.width, cv.height);
  ctx.strokeStyle = '#38bdf8';
  ctx.lineWidth = 1;
  ctx.beginPath();
  for (i = 0; i < buf.length; i++) {
    var x = buf.length > 1 ? i * cv.width / (buf.length - 1) : 0;
    var y = cv.height - 2 - (buf[i] / hi) * (cv.height - 4);
    if (i) ctx.lineTo(x, y); else ctx.moveTo(x, y);
  }
  ctx.stroke();
});
