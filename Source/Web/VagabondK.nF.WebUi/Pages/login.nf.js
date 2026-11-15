// 공용 로그인 스크립트: /api/auth/status + /api/auth/login(form-urlencoded) + CSRF 마커 헤더.
// 상태 JSON 필드명 호환: lockedOut/lockSec(샘플)과 locked/lockSeconds(nanoGate) 모두 허용.
// <form>의 native submit을 쓰므로 Enter 키 로그인이 별도 처리 없이 동작합니다.
// 세션 없음에서도 그려야 하므로 WebUi 코어(core.js)는 로드하지 않지만,
// 다국어 런타임(uiText/uiLang)은 메인 페이지와 동일한 공용 자산 /i18n.js를 사용합니다
// (texts.js → i18n.js → 본 스크립트 순으로 링크됩니다 — PageComposer가 유지).
(function () {
  function elById(id) {
    return document.getElementById(id);
  }
  // 번역 완료 시점(iife 말미)에 visibility hold를 해제하고, 스크립트 예외 시에도
  // 800ms 안에 원문(영어)으로 보이게 하는 안전 타이머. 클래스는 body가 아니라 html에 붙입니다(CSS 규칙 대상).
  setTimeout(
    function (d) {
      if ((" " + d.className + " ").indexOf(" wui-i18n ") < 0)
        d.className += " wui-i18n";
    },
    800,
    document.documentElement,
  );
  var formEl = elById("f");
  var passwordInput = elById("p");
  var submitButton = elById("b");
  var errorEl = elById("e");
  document.title =
    ((elById("t") || {}).textContent || "").replace(/ Sign in$/, "") +
    uiText("login.titleSuffix");
  // 브랜드 강조 분할: camelCase 어미(nanoGate→nano+Gate, WebUi→Web+Ui)를 <b>로 감쌉니다 — 메인 .brand b와 같은 색.
  var h1El = document.querySelector("h1");
  if (h1El) {
    var bs = h1El.textContent || "",
      bm = bs.match(/^(.+?)([A-Z][a-z0-9]*)$/);
    if (bm) {
      h1El.textContent = bm[1];
      var bEl = document.createElement("b");
      bEl.textContent = bm[2];
      h1El.appendChild(bEl);
    }
  }
  submitButton.textContent = uiText("login.title");
  elById("intro").textContent = uiText("login.intro");
  elById("lp").firstChild.nodeValue = uiText("wifi.pass");
  // 표시/숨김 토글(wui-password와 동일 메커니즘; id='e'는 오류 문구가 써서 'eye').
  var eye = elById("eye");
  if (eye) {
    eye.className = "";
    eye.textContent = "\uD83D\uDC41\uFE0E";
    eye.setAttribute("aria-label", uiText("pw.show"));
    eye.onclick = function () {
      var show = passwordInput.type === "password";
      passwordInput.type = show ? "text" : "password";
      eye.className = show ? "slash" : "";
      eye.setAttribute(
        "aria-label",
        show ? uiText("pw.hide") : uiText("pw.show"),
      );
    };
  }
  var redirectHash = location.hash || "";
  var csrfHeader = "";
  var submitting = 0;
  function showError(message) {
    errorEl.textContent = message;
    errorEl.hidden = !message;
  }
  function gotoTarget() {
    location.replace("/" + redirectHash);
  }
  function showLockMessage(statusJson) {
    var seconds = statusJson.lockSec || statusJson.lockSeconds || 60;
    submitButton.disabled = true;
    showError(uiText("login.locked", { n: seconds }));
    setTimeout(
      function () {
        location.reload();
      },
      (seconds + 1) * 1000,
    );
  }
  fetch("/api/auth/status", { cache: "no-store" })
    .then(function (r) {
      return r.json();
    })
    .then(function (j) {
      if (!j.authEnabled || j.loggedIn) {
        gotoTarget();
        return;
      }
      csrfHeader = j.csrfHeader || "";
      if (j.lockedOut || j.locked) showLockMessage(j);
    })
    .catch(function () {
      showError(uiText("login.nostatus"));
    });
  formEl.onsubmit = function (ev) {
    ev.preventDefault();
    if (submitting) return;
    submitting = 1;
    submitButton.disabled = true;
    submitButton.textContent = uiText("login.checking");
    showError("");
    var headers = { "Content-Type": "application/x-www-form-urlencoded" };
    if (csrfHeader) headers[csrfHeader] = "1";
    fetch("/api/auth/login", {
      method: "POST",
      headers: headers,
      body: "password=" + encodeURIComponent(passwordInput.value),
      cache: "no-store",
    })
      .then(function (r) {
        return r
          .json()
          .catch(function () {
            return {};
          })
          .then(function (j) {
            return { ok: r.ok, status: r.status, json: j };
          });
      })
      .then(function (x) {
        if (x.ok) {
          gotoTarget();
          return;
        }
        submitting = 0;
        submitButton.textContent = uiText("login.title");
        if (x.status === 429 || x.json.lockedOut || x.json.locked) {
          showLockMessage(x.json);
          return;
        }
        throw new Error(x.json.error || "HTTP " + x.status);
      })
      .catch(function (e) {
        submitting = 0;
        submitButton.disabled = false;
        submitButton.textContent = uiText("login.title");
        showError(e.message || uiText("login.fail"));
        passwordInput.select();
        passwordInput.focus();
      });
  };
  // 동기 번역 경로(타이틀/h1/라벨/버튼/에러 폴백)가 모두 정의됐으니 이제 드러냅니다.
  document.documentElement.className += " wui-i18n";
})();
