// 프로필 패널(이전 profileRender/profileTick): 훅 대신 wui:status/wui:tick 이벤트 구독으로 갱신한다.
WebUi.on('wui:status', s => WebUi.render(document.getElementById('pv'), [
WebUi.h('h2', null, '프로필 패널 · ' + WebUi.M.id),
WebUi.h('dl', null,
WebUi.h('dt', null, '장치 이름'), WebUi.h('dd', {key: 'dn', text: s.deviceName}),
WebUi.h('dt', null, '폴링 주기'), WebUi.h('dd', {key: 'pm', text: WebUi.showValue(document.getElementById('sf'), 'pollMs', s.pollMs)}),
WebUi.h('dt', null, '진단 모드'), WebUi.h('dd', {key: 'db', text: s.debug === '1' ? '켜짐 (디버그 출력에 기록)' : '꺼짐'})),
WebUi.h('p', {key: 'age', className: 'age', text: ''})
]));
WebUi.on('wui:tick', d => {
const a = document.getElementById('pv').querySelector('.age');
if (a) a.textContent = '가동 ' + Math.floor(d.uptime) + '초 · tick은 매초 실행';
});
