// heartbeat 컴포넌트: 자기 DOM 조각을 스스로 갱신한다(셸·프로필과 무관).
var _hbT0 = Date.now();
setInterval(function () {
    var e = $('hb');
    if (e) e.textContent = '<heartbeat/> 컴포넌트 데모 · 가동 ' + Math.floor((Date.now() - _hbT0) / 1000) + '초';
}, 1000);
