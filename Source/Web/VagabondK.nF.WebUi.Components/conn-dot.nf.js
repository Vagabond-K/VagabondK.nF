// conn-dot(라이브러리 인스턴스): Grafana 스타일 연결 상태 점. wui:tick의 age(마지막 status 수신 후 초)로 판단한다.
// 속성: timeout(초, 기본 6 — 폴링 주기의 2~3배로 잡는다), label(기본 '연결').
// 서버 폴러가 없어도 tick은 status가 온 뒤 1초마다 age를 외삽하므로 별도 배선 없이 동작.
var timeout = parseFloat(this.getAttribute('timeout')) || 6;
var label = this.getAttribute('label') || '연결';

WebUi.on('wui:tick', function (d) {
  var ok = d.age <= timeout;
  var dot = $('cdD'), txt = $('cdT');
  if (!dot) return;
  dot.className = ok ? 'ok' : 'bad';
  txt.textContent = ok ? label : '무응답 ' + Math.floor(d.age) + '초';
});
