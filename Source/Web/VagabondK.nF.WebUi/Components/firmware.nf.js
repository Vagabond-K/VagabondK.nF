// firmware(라이브러리 인스턴스): file+progress+result 조합(ESP Web Tools 플래셔의 MCU축소판).
// 속성: api(POST 엔드포인트, 기본 /api/firmware), chunk(청크 바 크기, 기본 2048 — base64 후 본문이
// 장치의 FormReader 제한을 넘지 않게). 파일을 seq/total/base64-data 청크로 쪼개 보냅니다.
// 진행은 wui:progress(다른 progress 인스턴스들도 함께 갱신), 결과는 wui:result 발행 + 자체 바.
// 실제 플래싱(부트로터 점프 등)은 장치 프로젝트의 몫 — 이 컴포넌트는 전송과 표시만 담당합니다.
var api = this.getAttribute("api") || "/api/firmware";
var CH = parseInt(this.getAttribute("chunk"), 10) || 2048;
var file = null;
$("fwP").textContent = uiText("fw.choose");
$("fwU").textContent = uiText("fw.upload");
msg(uiText("fw.pick"));

function bar(p) {
  var b = $("fwB");
  b.style.width = Math.round(p * 100) + "%";
  b.className = p >= 1 ? "done" : "";
}
function msg(t) {
  $("fwM").textContent = t;
}

$("fwF").onchange = function () {
  file = (this.files && this.files[0]) || null;
  this.value = "";
  $("fwU").disabled = !file;
  msg(
    file
      ? file.name + " · " + Math.ceil(file.size / 1024) + " KB"
      : uiText("fw.pick"),
  );
  bar(0);
};

$("fwU").onclick = function () {
  if (!file) return;
  $("fwU").disabled = true;
  var n = Math.max(1, Math.ceil(file.size / CH)),
    i = 0;
  function next() {
    file
      .slice(i * CH, (i + 1) * CH)
      .arrayBuffer()
      .then(function (buf) {
        var u = new Uint8Array(buf),
          s = "",
          j;
        for (j = 0; j < u.length; j++) s += String.fromCharCode(u[j]);
        return WebUi.post(
          api,
          "seq=" + i + "&total=" + n + "&data=" + btoa(s),
          null,
          15000,
        );
      })
      .then(function (r) {
        if (!r || !r.ok)
          throw new Error((r && r.error) || uiText("fw.refused"));
        i++;
        bar(i / n);
        WebUi.fire("wui:progress", {
          value: i,
          max: n,
          label: uiText("fw.sending", { n: Math.round((i / n) * 100) }),
        });
        if (i < n) next();
        else {
          msg(uiText("fw.done", { n: n }));
          WebUi.fire("wui:result", {
            status: "ok",
            title: uiText("fw.okTitle"),
            sub: uiText("fw.okSub"),
          });
        }
      })
      .catch(function (e) {
        msg(uiText("fw.fail", { m: e.message }));
        $("fwU").disabled = false;
        WebUi.fire("wui:result", {
          status: "bad",
          title: uiText("fw.badTitle"),
          sub: e.message,
        });
      });
  }
  next();
};
