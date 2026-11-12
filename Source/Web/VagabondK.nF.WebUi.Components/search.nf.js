// search(라이브러리 인스턴스): Carbon식 필터 입력. 폼 연관(@instance form).
// 사용: <search placeholder="포인트 검색"/> — .value로 읽고 쓸 수 있고 × 클릭 시 공백으로 다시 발행.
// 입력 300ms 후 wui:search {q} 발행(디바운스).
var self = this;
var inp = $('s'), x = $('x'), t = 0;
if (self.getAttribute('placeholder') != null) inp.placeholder = self.getAttribute('placeholder');
// setFormValue만으로는 폼의 'input' 리스너(bindForm sync)가 못 잡는다: 내부 input 이벤트는
// shadow 경계를 못 넘으므로 호스트에서 composed 이벤트로 재발행한다.
function fv() {
  if (self.__internals) self.__internals.setFormValue(inp.value);
  self.dispatchEvent(new Event('input', {bubbles: true, composed: true}));
}
inp.oninput = function () {
  x.hidden = !inp.value;
  fv();
  clearTimeout(t);
  t = setTimeout(function () { WebUi.fire('wui:search', { q: inp.value }); }, 300);
};
x.onclick = function () {
  inp.value = ''; x.hidden = true; fv();
  WebUi.fire('wui:search', { q: '' });
  inp.focus();
};
Object.defineProperty(self, 'value', {
get: function () { return inp.value; },
set: function (v) { inp.value = v == null ? '' : String(v); x.hidden = !inp.value; fv(); }
});
Object.defineProperty(self, 'placeholder', {
get: function () { return inp.placeholder; },
set: function (v) { inp.placeholder = v == null ? '' : String(v); }
});
self.focus = function () { inp.focus(); };
fv();
