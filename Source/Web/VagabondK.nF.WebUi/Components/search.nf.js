// search(라이브러리 인스턴스): Carbon식 필터 입력. 폼 연관(@instance form).
// 사용: <search placeholder="포인트 검색"/> — .value로 읽고 쓸 수 있고 × 클릭 시 공백으로 다시 발행.
// 입력 300ms 후 wui:search {q} 발행(디바운스).
var self = this;
var inp = $("s"),
  x = $("x"),
  t = 0;
if (self.getAttribute("placeholder") != null)
  inp.placeholder = self.getAttribute("placeholder");
// setFormValue만으로는 폼의 'input' 리스너(bindForm sync)가 못 잡습니다: 내부 input 이벤트는
// shadow 경계를 못 넘으므로 호스트에서 composed 이벤트로 재발행합니다.
function sync() {
  if (self.__internals) self.__internals.setFormValue(inp.value);
}
function fv() {
  sync();
  self.dispatchEvent(new Event("input", { bubbles: true, composed: true }));
}
inp.oninput = function () {
  x.hidden = !inp.value;
  fv();
  clearTimeout(t);
  t = setTimeout(function () {
    WebUi.fire("wui:search", { q: inp.value });
  }, 300);
};
inp.addEventListener("input", fv);
// Enter = 폼 제출(native input의 implicit submission 대체; 폼 밖이면 동작 없음).
inp.addEventListener("keydown", function (ev) {
  if (ev.key !== "Enter" || ev.shiftKey) return;
  var f = self.closest("form");
  if (!f) return;
  ev.preventDefault();
  if (f.requestSubmit) f.requestSubmit();
  else
    f.dispatchEvent(new Event("submit", { bubbles: true, cancelable: true }));
});
x.onclick = function () {
  inp.value = "";
  x.hidden = true;
  fv();
  WebUi.fire("wui:search", { q: "" });
  inp.focus();
};
Object.defineProperty(self, "value", {
  get: function () {
    return inp.value;
  },
  set: function (v) {
    var old = inp.value;
    inp.value = v == null ? "" : String(v);
    x.hidden = !inp.value;
    if (inp.value !== old) fv();
  },
});
Object.defineProperty(self, "placeholder", {
  get: function () {
    return inp.placeholder;
  },
  set: function (v) {
    inp.placeholder = v == null ? "" : String(v);
  },
});
// 최초 커넥트 시에는 값만 동기화하고 input 이벤트는 쏘지 않습니다: 같은 폼의
// 다른 FACE 엘리먼트가 아직 upgrade되기 전이라 syncForm이 미준비 필드를 참조합니다.
sync();
