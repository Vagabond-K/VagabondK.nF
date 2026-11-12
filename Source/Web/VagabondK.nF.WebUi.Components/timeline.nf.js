// timeline(라이브러리 인스턴스): Ant식 세로 이벤트 스트립. items는 | 구분.
// 사용: <timeline items="10:04 기동|10:05 연결"/> — 로그·이력 표시에.
var self = this;
var items = (self.getAttribute('items') || '').split('|'), rows = [], i;
for (i = 0; i < items.length; i++) {
  if (!items[i].trim()) continue;
  rows.push(WebUi.h('li', { text: items[i].trim() }));
}
WebUi.render($('tl'), rows);
