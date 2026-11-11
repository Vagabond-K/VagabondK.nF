// form 컴포넌트: MANIFEST.form 스키마로 입력 컨트롤을 런타임에 렌더하고 bindForm 기계장치를 배선한다.
// 페이지는 (1) @manifest 줄에 form을 선언하고 (2) 스크립트에서 WebUi.autoForm({check, save}) 한 번만 부르면 된다.
// 마크업에 name= 컨트롤이 없어서 생기는 빌드 타임 교차 검증은 생성기가 이 컴포넌트를 알고 건너뛴다.

WebUi.autoForm = function (opts) {
  var o = opts || {};
  var f = document.getElementById(o.formId || 'wf');
  if (!f) return null;
  var F = WebUi.M.form || [], html = '', i;
  for (i = 0; i < F.length; i++) {
    var it = F[i], k = it.name, lab = it.label || k, fmt = it.fmt || 'text', ctl;
    if (fmt === 'bool' || fmt === 'switch') ctl = '<span class="sw"><label><input type="radio" name="' + k + '" value="0"></label><label><input type="radio" name="' + k + '" value="1"></label><i></i></span>';
    else if (fmt === 'seg') {
      var os = it.options || [], j, ov, ol;
      ctl = '<span class="sg">';
      for (j = 0; j < os.length; j++) {
        ov = os[j]; ol = String(ov);
        if (ov && typeof ov === 'object') { ol = ov.label; ov = ov.value; }
        ctl += '<label><input type="radio" name="' + k + '" value="' + ov + '"><span>' + ol + '</span></label>';
      }
      ctl += '</span>';
    }
    else if (fmt === 'secret') ctl = '<input type="password" name="' + k + '" autocomplete="new-password">';
    else if (fmt === 'ms' || fmt === 'num' || fmt === 'int') ctl = '<input name="' + k + '" inputmode="numeric">';
    else ctl = '<input name="' + k + '">';
    html += '<label class="row"><span>' + lab + '</span>' + ctl + '</label>';
  }
  f.innerHTML = html;
  var SF = WebUi.bindForm(f, Object.assign({ saveBtn: 'wfSave', undoBtn: 'wfUndo', stateEl: 'wfState' }, o));
  // 상태가 오면 한 번만 채운다(로컬 편집 중 덮어쓰지 않음). 이후 되돌리기는 마지막 스냅샷을 쓴다.
  WebUi.on('wui:status', function (s) { if (!SF.base) SF.fill(s); });
  return SF;
};
