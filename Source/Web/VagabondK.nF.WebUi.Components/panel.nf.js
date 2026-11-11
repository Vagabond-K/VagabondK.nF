// panel(라이브러리): wui:status를 구독하는 범용 프로필 패널.
// 프로젝트 WebUi에 같은 이름 panel.nf.html이 있으면 이 파일은 섀도잉되어 번들에 들어가지 않는다.
WebUi.on('wui:status', function (s) {
  WebUi.render(document.getElementById('pv'), [
    WebUi.h('h2', null, '장치 · ' + WebUi.M.id),
    WebUi.h('dl', null,
      WebUi.h('dt', null, '장치 이름'), WebUi.h('dd', { key: 'dn', text: s.deviceName }),
      WebUi.h('dt', null, 'IP'), WebUi.h('dd', { key: 'ip', text: s.ip })),
  ]);
});
