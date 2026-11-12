// password(라이브러리 인스턴스): show/hide 토글이 있는 비밀번호 입력. 폼 연관(@instance form).
// 사용: <password name="password" placeholder maxlength autocomplete/> — form의 fmt:"secret"과 별도(수동 배치용)로
// form.elements[name].value로 읽고 쓸 수 있다.
var self = this;
var inp = $('p'), e = $('e');
if (self.getAttribute('placeholder') != null) inp.placeholder = self.getAttribute('placeholder');
if (self.getAttribute('maxlength') != null) inp.maxLength = parseInt(self.getAttribute('maxlength'), 10) || 0;
if (self.getAttribute('autocomplete') != null) inp.autocomplete = self.getAttribute('autocomplete');
// setFormValue만으로는 폼의 'input' 리스너(bindForm sync)가 못 잡는다: 내부 input 이벤트는
// shadow 경계를 못 넘으므로 호스트에서 composed 이벤트로 재발행한다.
function fv() {
  if (self.__internals) self.__internals.setFormValue(inp.value);
  self.dispatchEvent(new Event('input', {bubbles: true, composed: true}));
}
inp.addEventListener('input', fv);
e.onclick = function () {
  var show = inp.type === 'password';
  inp.type = show ? 'text' : 'password';
  e.textContent = show ? '숨김' : '표시';
};
Object.defineProperty(self, 'value', {
get: function () { return inp.value; },
set: function (v) { inp.value = v == null ? '' : String(v); fv(); }
});
Object.defineProperty(self, 'disabled', {
get: function () { return inp.disabled; },
set: function (d) { inp.disabled = !!d; if (d) self.setAttribute('disabled', ''); else self.removeAttribute('disabled'); }
});
self.focus = function () { inp.focus(); };
fv();
