// search(라이브러리 인스턴스): Carbon식 필터 입력. 입력 300ms 후 wui:search {q} 발행(디바운스).
// 사용: <search placeholder="포인트 검색"/> — × 클릭 시 공백으로 다시 발행.
var self = this;
var inp = $('s'), x = $('x'), t = 0;
if (self.getAttribute('placeholder')) inp.placeholder = self.getAttribute('placeholder');
inp.oninput = function () {
  x.hidden = !inp.value;
  clearTimeout(t);
  t = setTimeout(function () { WebUi.fire('wui:search', { q: inp.value }); }, 300);
};
x.onclick = function () {
  inp.value = ''; x.hidden = true;
  WebUi.fire('wui:search', { q: '' });
  inp.focus();
};
