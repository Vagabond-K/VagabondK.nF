// number(라이브러리 인스턴스): Ant InputNumber식 +/- 스테퍼 숫자 입력. 폼 연관(@instance form).
// 사용: <number name="timeout" value min max step/> — form 안에 두면 form.elements[name]로 이 호스트가
// 잡히고 .value(get/set)가 폼 값과 동기화됩니다. value 속성이 없으면 빈 칸으로 시작합니다.
// 값 변경(버튼·직접 입력)마다 wui:number-change {value} 발행.
var self = this;
var min = parseInt(self.getAttribute("min"), 10);
if (isNaN(min)) min = -2147483648;
var max = parseInt(self.getAttribute("max"), 10);
if (isNaN(max)) max = 2147483647;
var step = parseInt(self.getAttribute("step"), 10) || 1;
var inp = $("i");
if (self.getAttribute("placeholder") != null)
  inp.placeholder = self.getAttribute("placeholder");
if (self.getAttribute("autocomplete") != null)
  inp.autocomplete = self.getAttribute("autocomplete");
// setFormValue만으로는 폼의 'input' 리스너(bindForm sync)가 못 잡습니다: 내부 input 이벤트는
// shadow 경계를 못 넘으므로 호스트에서 composed 이벤트로 재발행합니다.
function sync() {
  if (self.__internals) self.__internals.setFormValue(inp.value);
}
function fv() {
  sync();
  self.dispatchEvent(new Event("input", { bubbles: true, composed: true }));
}
function set(n) {
  if (isNaN(n)) n = parseInt(inp.value, 10);
  if (isNaN(n)) n = min > 0 ? min : 0;
  inp.value = n < min ? min : n > max ? max : n;
  fv();
  WebUi.fire("wui:number-change", { value: inp.value });
}
var v0 = self.getAttribute("value");
inp.value =
  v0 == null ? "" : String(Math.min(max, Math.max(min, parseInt(v0, 10) || 0)));
$("m").onclick = function () {
  set((parseInt(inp.value, 10) || 0) - step);
};
$("p").onclick = function () {
  set((parseInt(inp.value, 10) || 0) + step);
};
inp.onchange = function () {
  set(parseInt(inp.value, 10));
};
inp.addEventListener("input", fv);
// Enter = 폼 제출(native input의 implicit submission 대체; shadow 경계로 발동하지 않습니다).
inp.addEventListener("keydown", function (ev) {
  if (ev.key !== "Enter" || ev.shiftKey) return;
  var f = self.closest("form");
  if (!f) return;
  ev.preventDefault();
  set(parseInt(inp.value, 10)); // change 상당의 clamp를 제출 전에 적용
  if (f.requestSubmit) f.requestSubmit();
  else
    f.dispatchEvent(new Event("submit", { bubbles: true, cancelable: true }));
});
Object.defineProperty(self, "value", {
  get: function () {
    return inp.value;
  },
  set: function (v) {
    inp.value = v == null ? "" : String(v);
    fv();
  },
});
Object.defineProperty(self, "disabled", {
  get: function () {
    return inp.disabled;
  },
  set: function (d) {
    inp.disabled = !!d;
    if (d) self.setAttribute("disabled", "");
    else self.removeAttribute("disabled");
  },
});
self.focus = function () {
  inp.focus();
};
// 최초 커넥트 시에는 값만 동기화하고 input 이벤트는 쏘지 않습니다: 같은 폼의
// 다른 FACE 엘리먼트가 아직 upgrade되기 전이라 syncForm이 미준비 필드를 참조합니다.
sync();
