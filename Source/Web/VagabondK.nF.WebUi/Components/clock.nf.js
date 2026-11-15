// clock(라이브러리 인스턴스): 초 단위로 갱신하는 현재 시각 표시. 브라우저 시각 기준.
// 사용: <clock/> — 서버/장치 시각과 비교하려면 uptime 컴포넌트를 함께 씁니다.
var ck = $("ck");
function tick() {
  var d = new Date(),
    p = function (n) {
      return (n < 10 ? "0" : "") + n;
    };
  ck.textContent =
    p(d.getHours()) + ":" + p(d.getMinutes()) + ":" + p(d.getSeconds());
}
tick();
setInterval(tick, 1000);
