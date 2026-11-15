// card(라이브러리 인스턴스): 제목+본문 컨테이너(section의 컴포넌트 승격).
// 본문 없이 제목만: <card title="X"/> — 내용(light DOM)이 없으면 :host(:empty)로 숨김.
// 본문을 담으려면 로컬 래퍼처럼 한 조각으로 감싸고 아래처럼 직접 태그를 씁니다:
//   <wui-card title="X">본문 마크업</wui-card>   ← 태그 자체가 라이브러리(card.{html,css,js}+정의)를 포함하고 slot으로 본문이 렌더됩니다
var t = this.getAttribute("title");
var h = $("cT");
if (h && t) {
  h.textContent = t;
  h.hidden = false;
}
