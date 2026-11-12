// navbar(라이브러리 인스턴스): Bootstrap식 제목+액션 헤더 바. 우측 액션은 slot.
// 사용: <wui-navbar title="게이트웨이"><conn-dot/><button>…</button></wui-navbar> — 태그 자체가 컴포넌트를 포함한다.
var t = this.getAttribute('title');
if (t) $('nT').textContent = t;
