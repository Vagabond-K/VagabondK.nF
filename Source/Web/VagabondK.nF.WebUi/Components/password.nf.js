// password(라이브러리 인스턴스): show/hide 토글이 있는 비밀번호 입력. 폼 연관(@instance form).
// 사용: <password name="password" placeholder maxlength autocomplete/> — form의 fmt:"secret"과 별도(수동 배치용)로
// form.elements[name].value로 읽고 쓸 수 있습니다.
var self = this;
var inp = $("p"),
  e = $("e");
if (self.getAttribute("placeholder") != null)
  inp.placeholder = self.getAttribute("placeholder");
if (self.getAttribute("maxlength") != null)
  inp.maxLength = parseInt(self.getAttribute("maxlength"), 10) || 0;
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
inp.addEventListener("input", fv);
// Enter = 폼 제출: 내부 input은 shadow 경계 때문에 브라우저의 implicit submission이
//발동하지 않습니다. 폼 안에 있을 때만 requestSubmit(없으면 submit 이벤트 수동 발행)으로 대신합니다.
inp.addEventListener("keydown", function (ev) {
  if (ev.key !== "Enter" || ev.shiftKey) return;
  var f = self.closest("form");
  if (!f) return;
  ev.preventDefault();
  if (f.requestSubmit) f.requestSubmit();
  else
    f.dispatchEvent(new Event("submit", { bubbles: true, cancelable: true }));
});
// 눈 글자: U+1F441(EYE) 서로게이트 + U+FE0E 텍스트 변형(컬러 이모지 대신 단색glyph 강제).
// U+1F441 + U+200D + 코드의 "눈 대가리 긋기" 합성은 폰트 지원이 불안정해
// 대각선은 .slash 클래스로 CSS에서 그립니다.
var EYE = "\uD83D\uDC41\uFE0E";
// 초기(가려짐)는 '표시' 동작을 뜻하는 눈금 없는 눈, 표시 중엔 대각선(.slash)을 긋습니다.
e.className = "";
e.textContent = EYE;
e.setAttribute("aria-label", uiText("pw.show"));
e.onclick = function () {
  var show = inp.type === "password";
  inp.type = show ? "text" : "password";
  e.className = show ? "slash" : "";
  e.setAttribute("aria-label", show ? uiText("pw.hide") : uiText("pw.show"));
};
Object.defineProperty(self, "value", {
  get: function () {
    return inp.value;
  },
  set: function (v) {
    var old = inp.value;
    inp.value = v == null ? "" : String(v);
    if (inp.value !== old) fv();
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
// 최초 커넥트 시에는 값만 동기화하고 input 이벤트는 쏘지 않습니다: 같은 폼의
// 다른 FACE 엘리먼트가 아직 upgrade되기 전이라 syncForm이 미준비 필드를 참조합니다.
sync();
