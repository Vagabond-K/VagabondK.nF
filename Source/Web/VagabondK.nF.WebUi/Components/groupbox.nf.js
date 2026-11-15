// groupbox(라이브러리 인스턴스): fieldset/legend식 그룹 박스. 본문을 slot으로 받습니다.
// 사용: <wui-groupbox title="시리얼 통신">…본문…</wui-groupbox> — 태그 자체가 컴포넌트를 포함합니다.
// card와 달리 제목이 테두리를 끊고 얹히는(legend) 형태. 본문 없으면 자동 숨김.
var t = this.getAttribute("title");
var l = $("gT");
if (l && t) l.textContent = t;
