// badge(라이브러리 인스턴스): 카운트·상태 뱃지. 본문을 slot으로 받는다.
// 사용: <wui-badge variant="ok">연결</wui-badge> — 태그 자체가 컴포넌트를 포함한다.
// variant: info(기본)·ok·warn·bad.
var v = this.getAttribute('variant');
if (v) $('wb').className = 'wb ' + v;
