// grid(라이브러리 인스턴스): CSS grid 유틸. cols로 열 개수 지정(기본 2).
// 사용: <wui-grid cols="3"><div>…</div><div>…</div></wui-grid> — 태그 자체가 컴포넌트를 포함한다.
var self = this;
var c = parseInt(self.getAttribute('cols'), 10);
if (c > 0) $('gr').style.gridTemplateColumns = 'repeat(' + c + ', 1fr)';
