// empty(라이브러리 인스턴스): 데이터 없음 상태 표시.
// 사용: <empty text="로그가 비어 있음" icon="▤"/>
var t = this.getAttribute('text');
var i = this.getAttribute('icon');
if (t) $('eT').textContent = t;
if (i) $('eI').textContent = i;
