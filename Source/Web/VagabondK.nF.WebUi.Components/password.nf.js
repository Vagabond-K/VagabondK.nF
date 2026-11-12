// password(라이브러리 인스턴스): show/hide 토글이 있는 비밀번호 입력.
// 사용: <password placeholder="관리자 비밀번호"/> — form의 fmt:"secret"과 별도(수동 배치용).
var self = this;
var inp = $('p'), e = $('e');
if (self.getAttribute('placeholder')) inp.placeholder = self.getAttribute('placeholder');
e.onclick = function () {
  var show = inp.type === 'password';
  inp.type = show ? 'text' : 'password';
  e.textContent = show ? '숨김' : '표시';
};
