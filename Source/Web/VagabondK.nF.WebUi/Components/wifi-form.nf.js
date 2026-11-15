// wifi-form(라이브러리 인스턴스): 라우터 UI식 네트워크 설정 폼. api GET으로 로드, POST로 적용.
// 사용: <wifi-form api="/api/network"/> — GET {ssid}, POST {ssid,pass} 규약.
var self = this;
var api = self.getAttribute("api") || "/api/network";
var st = $("st");
$("lw").textContent = uiText("wifi.pass");
$("cn").textContent = uiText("wifi.connect");
// 표시/가리기 토글: password.nf(컴포넌트)와 같은 눈 패턴. shadow 안에 있어 업그레이드가 못 타므로 자체 구현.
var pwInp = $("pw"),
  pwEye = $("pwE");
var EYE = "\uD83D\uDC41\uFE0E";
pwEye.textContent = EYE;
pwEye.setAttribute("aria-label", uiText("pw.show"));
pwEye.onclick = function () {
  var show = pwInp.type === "password";
  pwInp.type = show ? "text" : "password";
  pwEye.className = show ? "slash" : "";
  pwEye.setAttribute("aria-label", show ? uiText("pw.hide") : uiText("pw.show"));
};
// WebUi.post는 urlencoded 문자열 body를 보냅니다: 객체 그대로 넘기면 "[object Object]"가 됩니다.
function load() {
  WebUi.api(api).then(
    function (r) {
      if (r && r.ssid) $("ss").value = r.ssid;
    },
    function () {
      setTimeout(load, 3000);
    },
  ); // MCU가 바쁜 로드 시점이라곤 재시도
}
load();
$("cn").onclick = function () {
  st.textContent = uiText("wifi.applying");
  WebUi.post(
    api,
    "ssid=" +
      encodeURIComponent($("ss").value) +
      "&pass=" +
      encodeURIComponent($("pw").value),
  ).then(
    function (r) {
      st.textContent = r && r.ok ? uiText("wifi.saved") : uiText("wifi.failed");
    },
    function (e) {
      st.textContent = uiText("wifi.fail", {
        m: e && e.message ? e.message : e,
      });
    },
  );
};
