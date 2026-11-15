// tabs 컴포넌트: main 안의 section[data-tab]을 모아서 상단 내비와 해시 라우팅을 만듭니다.
// <section data-tab="설정" data-hash="settings"> — data-hash가 없으면 순번(#t2, #t3…)이고
// 첫 섹션이 기본(#해시 없음). 탭이 바뀌면 wui:tab 이벤트가 인덱스와 함께 발행됩니다.
// WebUi.tabs.el(i)는 i번째 버튼(바인딩용), .go(i)는 그 탭으로 이동.

(function () {
  var nav = document.getElementById("wt-nav");
  var secs = [].slice.call(document.querySelectorAll("section[data-tab]"));
  var btns = [];

  function hashOf(s, i) {
    var h = s.getAttribute("data-hash");
    return h != null ? h : i ? "t" + (i + 1) : "";
  }
  function route() {
    var h = decodeURIComponent(location.hash.replace(/^#/, "")),
      cur = 0,
      i;
    for (i = 0; i < secs.length; i++) if (hashOf(secs[i], i) === h) cur = i;
    for (i = 0; i < secs.length; i++) {
      secs[i].hidden = i !== cur;
      btns[i].className = i === cur ? "on" : "";
    }
    WebUi.fire("wui:tab", cur);
  }
  secs.forEach(function (s, i) {
    var b = document.createElement("button");
    b.type = "button";
    b.textContent = s.getAttribute("data-tab");
    b.onclick = function () {
      var h = hashOf(s, i);
      if (h) {
        if (location.hash === "#" + h) route();
        else location.hash = h;
      } else {
        history.replaceState(
          "",
          document.title,
          location.pathname + location.search,
        );
        route();
      }
    };
    nav.appendChild(b);
    btns.push(b);
  });
  addEventListener("hashchange", route);
  WebUi.tabs = {
    el: function (i) {
      return btns[i];
    },
    go: function (i) {
      if (btns[i]) btns[i].click();
    },
  };
  route();
})();
