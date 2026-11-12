// ap-banner(라이브러리 인스턴스): nanoGate 설정 AP 경고 배너의 일반화.
// wui:status의 key(기본 "ap")가 참이면 경고 바를 보이고, 아니면 숨긴다.
// 사용: <ap-banner/> 또는 <ap-banner key="ap" text="…"/>
var self = this;
var key = self.getAttribute('key') || 'ap';
WebUi.on('wui:status', function (s) {
  var on = !!(s && s[key]);
  $('b').hidden = !on;
  if (on && !$('tx').textContent) {
    $('tx').textContent = self.getAttribute('text') || '이 장치는 설정 AP 모드입니다. 운영 네트워크에 연결해 최종 설정하세요.';
  }
});
