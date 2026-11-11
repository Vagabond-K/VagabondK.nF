// login 컴포넌트: 로그인 오버레이 마크업과 로그인/로그아웃 처리를 담당한다.
// showLogin은 페이지의 checkAuth도 호출한다(컴포넌트 JS는 페이지 코드 뒤에 병합되지만
// 함수 선언은 블록 전체에서 호이스트되므로 어디서든 보인다). authed/failures는 페이지의 공유 상태.

function showLogin(msg) {
  authed = false;
  $('login').hidden = false;
  $('loginMsg').textContent = msg || '';
}

WebUi.on('wui:unauth', () => showLogin('세션이 만료되었습니다. 다시 로그인하세요.'));

document.getElementById('loginForm').onsubmit = async ev => {
  ev.preventDefault();
  try {
    await WebUi.post('/api/auth/login', 'password=' + encodeURIComponent($('pw').value));
    $('pw').value = '';
    failures = 0;
    // 서버가 세션 쿠키를 거부(브라우저 차단 등)하면 창이 다시 나타나므로 원인을 안내한다.
    if (!await checkAuth()) showLogin('로그인되었으나 세션 쿠키가 유지되지 않습니다. 브라우저의 쿠키 차단 설정을 확인하세요.');
    else refresh();
  } catch (err) {
    $('loginMsg').textContent = err.message;
  }
};

// btnOut은 페이지 헤더의 버튼: 인라인(비-인스턴스) 컴포넌트는 페이지와 DOM을 공유하므로 결선한다.
// 로그아웃 확인은 modal 컴포넌트(WebUi.modal)를 사용한다 — 핸들러가 실행되는 시점에는 모든 컴포넌트가 준비된다.
document.getElementById('btnOut').onclick = async () => {
  if (!await WebUi.modal({ title: '로그아웃', body: '로그아웃하시겠습니까?' })) return;
  try { await WebUi.post('/api/auth/logout', ''); } catch (e) {}
  location.reload();
};
