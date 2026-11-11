// uptime(라이브러리 인스턴스): heartbeat의 일반화. wui:tick이 외삽한 장치 가동 시간을 표시한다.
// 속성: label(문구 접두어, 기본 '가동'), long(붙이면 초까지 3단 표시). 인스턴스마다 독립 구독이라 여러 곳에서 재사용 가능.
var label = this.getAttribute('label') || '가동';
var long = this.hasAttribute('long');

function dur(sec) {
  sec = Math.max(0, Math.floor(sec));
  var h = Math.floor(sec / 3600), m = Math.floor(sec % 3600 / 60);
  if (long) return h + '시간 ' + m + '분 ' + (sec % 60) + '초';
  return h ? h + '시간 ' + m + '분' : m ? m + '분 ' + (sec % 60) + '초' : sec + '초';
}

WebUi.on('wui:tick', function (d) {
  var e = $('ut');
  if (e) e.textContent = label + ' ' + dur(d.uptime);
});
