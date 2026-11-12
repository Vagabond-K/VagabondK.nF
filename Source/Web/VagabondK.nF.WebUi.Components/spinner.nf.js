// spinner(라이브러리 인스턴스): CSS-only 회전 로딩.
// 사용: <spinner/> 또는 <spinner size="28"/> — 정지하려면 host를 숨긴다(hidden).
var s = parseInt(this.getAttribute('size'), 10);
if (s > 0) {
  var b = $('ws');
  b.style.width = s + 'px';
  b.style.height = s + 'px';
  b.style.borderWidth = Math.max(2, Math.round(s / 6)) + 'px';
}
