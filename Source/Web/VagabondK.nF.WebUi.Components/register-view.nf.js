// register-view(라이브러리 인스턴스): Modbus 툴식 레지스터 라이브 표. api의 values 배열을 cols열 표로 폴링.
// 사용: <register-view api="/api/registers" cols="8" ms="2000"/> — 응답은 {values:[...]} 기대.
var self = this;
var api = self.getAttribute('api') || '/api/registers';
var cols = parseInt(self.getAttribute('cols'), 10) || 8;
var ms = parseInt(self.getAttribute('ms'), 10) || 2000;
var tb = $('rt');
function poll() {
  WebUi.api(api).then(function (r) {
    if (!r || !r.values) return;
    var v = r.values, i, j;
    tb.textContent = '';
    for (i = 0; i * cols < v.length; i++) {
      var tr = document.createElement('tr');
      var th = document.createElement('th');
      th.textContent = String(i * cols);
      tr.appendChild(th);
      for (j = 0; j < cols && i * cols + j < v.length; j++) {
        var td = document.createElement('td');
        td.textContent = String(v[i * cols + j]);
        tr.appendChild(td);
      }
      tb.appendChild(tr);
    }
  }, function () { /* 바쁜 틱 건너뛰기(다음 폴링 재시도) */ });
}
poll();
setInterval(poll, ms);
