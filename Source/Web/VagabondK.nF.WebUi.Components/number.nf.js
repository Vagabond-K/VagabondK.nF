// number(라이브러리 인스턴스): Ant InputNumber식 +/- 스테퍼 숫자 입력. 폼 연관(@instance form).
// 사용: <number name="timeout" value min max step/> — form 안에 두면 form.elements[name]로 이 호스트가
// 잡히고 .value(get/set)가 폼 값과 동기화된다. value 속성이 없으면 빈 칸으로 시작한다.
// 값 변경(버튼·직접 입력)마다 wui:number-change {value} 발행.
var self = this;
var min = parseInt(self.getAttribute('min'), 10); if (isNaN(min)) min = -2147483648;
var max = parseInt(self.getAttribute('max'), 10); if (isNaN(max)) max = 2147483647;
var step = parseInt(self.getAttribute('step'), 10) || 1;
var inp = $('i');
if (self.getAttribute('placeholder') != null) inp.placeholder = self.getAttribute('placeholder');
if (self.getAttribute('autocomplete') != null) inp.autocomplete = self.getAttribute('autocomplete');
// setFormValue만으로는 폼의 'input' 리스너(bindForm sync)가 못 잡는다: 내부 input 이벤트는
// shadow 경계를 못 넘으므로 호스트에서 composed 이벤트로 재발행한다.
function fv() {
  if (self.__internals) self.__internals.setFormValue(inp.value);
  self.dispatchEvent(new Event('input', {bubbles: true, composed: true}));
}
function set(n) {
  if (isNaN(n)) n = parseInt(inp.value, 10);
  if (isNaN(n)) n = min > 0 ? min : 0;
  inp.value = n < min ? min : (n > max ? max : n);
  fv();
  WebUi.fire('wui:number-change', { value: inp.value });
}
var v0 = self.getAttribute('value');
inp.value = v0 == null ? '' : String(Math.min(max, Math.max(min, parseInt(v0, 10) || 0)));
$('m').onclick = function () { set((parseInt(inp.value, 10) || 0) - step); };
$('p').onclick = function () { set((parseInt(inp.value, 10) || 0) + step); };
inp.onchange = function () { set(parseInt(inp.value, 10)); };
inp.addEventListener('input', fv);
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
