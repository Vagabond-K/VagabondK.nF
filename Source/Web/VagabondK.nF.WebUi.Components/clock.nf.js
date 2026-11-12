// clock(라이브러리 인스턴스): 초 단위 시계. DNP3 타임스탬프 확인·장치 시각 대비용.
// 사용: <clock/> — 브라우저 시각 기준(장치 시각은 status의 uptime으로 역산).
var ck = $('ck');
function tick() {
  var d = new Date(), p = function (n) { return (n < 10 ? '0' : '') + n; };
  ck.textContent = p(d.getHours()) + ':' + p(d.getMinutes()) + ':' + p(d.getSeconds());
}
tick();
setInterval(tick, 1000);
