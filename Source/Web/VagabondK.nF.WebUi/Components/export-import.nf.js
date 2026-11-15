// export-import(라이브러리 인스턴스): 설정 백업 내보내기/가져오기(phpMyAdmin 스타일).
// 내보내기: GET /api/settings → JSON 파일 다운로드(deviceName.json).
// 가져오기: .json → WebUi.KEYS로 필터 → POST /api/settings(form-encoded, csrf는 core가 첨부).
// 장치 API는 기존 settings 엔드포인트를 그대로 씁니다. 파일 입력은 firmware와 같은 패턴.
function formBody(o) {
  var s = "",
    i,
    k;
  for (i = 0; i < WebUi.KEYS.length; i++) {
    k = WebUi.KEYS[i];
    if (o[k] != null) s += (s ? "&" : "") + k + "=" + encodeURIComponent(o[k]);
  }
  return s;
}

$("eiX").textContent = uiText("ei.export");
$("eiL").textContent = uiText("ei.import");

$("eiX").onclick = function () {
  var m = $("eiM");
  m.textContent = uiText("ei.exporting");
  WebUi.api("/api/settings").then(
    function (o) {
      var url = URL.createObjectURL(
        new Blob([JSON.stringify(o, null, 1)], { type: "application/json" }),
      );
      var a = document.createElement("a");
      a.href = url;
      a.download = String(o.deviceName || "settings") + ".json";
      document.body.appendChild(a);
      a.click();
      a.remove();
      setTimeout(function () {
        URL.revokeObjectURL(url);
      }, 2000);
      m.textContent = uiText("ei.exported");
    },
    function (e) {
      m.textContent = uiText("ei.exportFail", { m: e.message });
    },
  );
};

$("eiF").onchange = function () {
  var inp = this,
    f = inp.files && inp.files[0],
    m = $("eiM");
  inp.value = "";
  if (!f) return;
  var r = new FileReader();
  r.onload = function () {
    var o;
    try {
      o = JSON.parse(r.result);
    } catch (e) {
      m.textContent = uiText("ei.badJson");
      return;
    }
    m.textContent = uiText("ei.importing");
    // 파일에 없는 키는 장치의 현재 값으로 채웁니다(부분 백업 파일도 가져오기 되도록).
    WebUi.api("/api/settings")
      .then(function (cur) {
        var merged = {},
          k;
        for (k in cur) if (cur.hasOwnProperty(k)) merged[k] = cur[k];
        for (k in o) if (o.hasOwnProperty(k)) merged[k] = o[k];
        var body = formBody(merged);
        if (!body) throw new Error(uiText("ei.noKeys"));
        return WebUi.post("/api/settings", body);
      })
      .then(
        function (res) {
          if (res && res.ok) {
            m.textContent = uiText("ei.imported");
            setTimeout(function () {
              location.reload();
            }, 800);
          } else
            m.textContent = uiText("ei.rejected", {
              m: (res && res.error) || "?",
            });
        },
        function (e) {
          m.textContent = uiText("ei.importFail", { m: e.message });
        },
      );
  };
  r.readAsText(f);
};
