// number(라이브러리 인스턴스): Ant InputNumber식 +/- 스테퍼 숫자 입력.
// 사용: <number value="500" min="0" max="9999" step="100"/>
// 값 변경(버튼·직접 입력)마다 wui:number-change {value} 발행.
var self = this;
var min = parseInt(self.getAttribute('min'), 10); if (isNaN(min)) min = -2147483648;
var max = parseInt(self.getAttribute('max'), 10); if (isNaN(max)) max = 2147483647;
var step = parseInt(self.getAttribute('step'), 10) || 1;
var inp = $('i');
function set(n) {
  if (isNaN(n)) n = parseInt(inp.value, 10);
  if (isNaN(n)) n = min > 0 ? min : 0;
  inp.value = n < min ? min : (n > max ? max : n);
  WebUi.fire('wui:number-change', { value: inp.value });
}
inp.value = Math.min(max, Math.max(min, parseInt(self.getAttribute('value'), 10) || 0));
$('m').onclick = function () { set((parseInt(inp.value, 10) || 0) - step); };
$('p').onclick = function () { set((parseInt(inp.value, 10) || 0) + step); };
inp.onchange = function () { set(parseInt(inp.value, 10)); };
