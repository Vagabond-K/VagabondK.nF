// form 컴포넌트: MANIFEST.form 스키마로 입력 컨트롤을 런타임에 렌더하고 bindForm 기계장치를 배선합니다.
// 페이지는 (1) @manifest 줄에 form을 선언하고 (2) 스크립트에서 WebUi.autoForm({check, save}) 한 번만 부르면 됩니다.
// 마크업에 name= 컨트롤이 없어서 생기는 빌드 타임 교차 검증은 생성기가 이 컴포넌트를 알고 건너뜁니다.

WebUi.autoForm = function (opts) {
  var o = opts || {};
  var f = document.getElementById(o.formId || "wf");
  if (!f) return null;
  var F = WebUi.M.form || [],
    html = "",
    i;
  for (i = 0; i < F.length; i++) {
    var it = F[i],
      k = it.name,
      lab = it.label || k,
      fmt = it.fmt || "text",
      ctl;
    if (fmt === "bool" || fmt === "switch")
      ctl =
        '<span class="sw" role="switch" tabindex="0"><input type="radio" name="' +
        k +
        '" value="0"><input type="radio" name="' +
        k +
        '" value="1"><i></i></span>';
    else if (fmt === "seg") {
      var os = it.options || [],
        j,
        ov,
        ol;
      ctl = '<span class="sg">';
      for (j = 0; j < os.length; j++) {
        ov = os[j];
        ol = String(ov);
        if (ov && typeof ov === "object") {
          ol = ov.label;
          ov = ov.value;
        }
        ctl +=
          '<label><input type="radio" name="' +
          k +
          '" value="' +
          ov +
          '"><span>' +
          ol +
          "</span></label>";
      }
      ctl += "</span>";
    } else if (fmt === "secret")
      ctl =
        '<input type="password" name="' + k + '" autocomplete="new-password">';
    else if (fmt === "ms" || fmt === "num" || fmt === "int")
      ctl = '<input name="' + k + '" inputmode="numeric">';
    else ctl = '<input name="' + k + '">';
    html +=
      '<div class="row"><span class="lab">' + lab + "</span>" + ctl + "</div>";
  }
  f.innerHTML = html;
  var sv = document.getElementById("wfSave"),
    un = document.getElementById("wfUndo");
  if (sv) sv.textContent = uiText("form.save");
  if (un) un.textContent = uiText("form.undo");
  // 스위치: 트랙 어디를 클릭/스페이스/Enter든 토글. 라디오는 hidden(pointer-events:none)이고
  // 프로그램 checked + input 버블링으로 bindForm의 dirty 감지를 그대로 탑니다.
  var sws = f.querySelectorAll(".sw"),
    si;
  for (si = 0; si < sws.length; si++)
    (function (w) {
      var ins = w.querySelectorAll("input");
      var tog = function () {
        var t = ins[ins[1].checked ? 0 : 1];
        t.checked = true;
        w.setAttribute("aria-checked", ins[1].checked ? "true" : "false");
        t.dispatchEvent(new Event("input", { bubbles: true }));
      };
      w.setAttribute("aria-checked", ins[1].checked ? "true" : "false");
      w.addEventListener("click", tog);
      w.addEventListener("keydown", function (e) {
        if (e.key === " " || e.key === "Enter") {
          e.preventDefault();
          tog();
        }
      });
    })(sws[si]);
  var SF = WebUi.bindForm(
    f,
    Object.assign(
      { saveBtn: "wfSave", undoBtn: "wfUndo", stateEl: "wfState" },
      o,
    ),
  );
  // 상태가 오면 한 번만 채웁니다(로컬 편집 중 덮어쓰지 않음). 이후 되돌리기는 마지막 스냅샷을 씁니다.
  WebUi.on("wui:status", function (s) {
    if (!SF.base) SF.fill(s);
  });
  return SF;
};
