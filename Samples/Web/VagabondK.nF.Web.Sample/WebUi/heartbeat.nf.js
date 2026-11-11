// heartbeat 인스턴스 컴포넌트(html의 @instance 마커): <heartbeat/> 태그마다
// 자기 shadow root와 클로저 상태를 갖는다. 여러 곳에서 반복 사용해도 id가 충돌하지 않고,
// t0와 타이머가 인스턴스별로 독립이다. body 안의 $와 document는 shadow 스코프.

var t0 = Date.now();

setInterval(function () {
  var e = $('hb');
  if (e) e.textContent = 'heartbeat 인스턴스 · 가동 ' + Math.floor((Date.now() - t0) / 1000) + '초';
}, 1000);
