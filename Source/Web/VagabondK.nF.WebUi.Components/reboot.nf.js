// reboot(라이브러리 인스턴스): 라우터 UI식 카운트다운 재부팅.
// 사용: <reboot api="/api/reboot" secs="8"/> — api 기본 /api/reboot, secs 기본 10.
// modal 확인 → POST → 카운트다운 → 0에서 페이지 새로고침(장치 부팅 재시도).
var self = this;
var api = self.getAttribute('api') || '/api/reboot';
var secs = parseInt(self.getAttribute('secs'), 10) || 10;
$('rB').onclick = function () {
  WebUi.modal({ title: '재부팅', body: '장치를 재시작합니다. 잠시 접속이 중단됩니다.', ok: '재부팅', cancel: '취소' }).then(function (yes) {
    if (!yes) return;
    WebUi.post(api, {}).then(function (r) {
      if (!r || !r.ok) { WebUi.toast('재부팅 실패: ' + (r && r.error ? r.error : '응답 없음'), 'bad'); return; }
      var btn = $('rB'), c = $('rC'), n = secs;
      btn.disabled = true;
      c.hidden = false;
      c.textContent = n + '초 후 재기동…';
      var iv = setInterval(function () {
        n--;
        if (n <= 0) { clearInterval(iv); location.reload(); return; }
        c.textContent = n + '초 후 재기동…';
      }, 1000);
    });
  });
};
