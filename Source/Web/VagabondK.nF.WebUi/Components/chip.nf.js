// chip(라이브러리 인스턴스): 라벨+선택/삭제 태그. 본문을 slot으로 받습니다.
// 사용: <wui-chip removable>RTU</wui-chip> — 태그 자체가 컴포넌트를 포함합니다.
// × 클릭 시 host를 제거하고 wui:chip-close {text}를 발행합니다(페이지가 필터로 활용 가능).
var self = this;
if (self.hasAttribute("removable")) $("wkX").hidden = false;
$("wkX").onclick = function () {
  WebUi.fire("wui:chip-close", {
    text: (self.textContent || "").replace(/\s+/g, " ").trim(),
  });
  self.remove();
};
