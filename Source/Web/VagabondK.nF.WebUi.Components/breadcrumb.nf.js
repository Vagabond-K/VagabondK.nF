// breadcrumb(라이브러리 인스턴스): Ant/MUI식 경로 표시. items는 | 구분, 마지막이 현재 위치(굵게).
// 사용: <breadcrumb items="홈|설정|포인트"/>
var self = this;
var items = (self.getAttribute('items') || '').split('|').filter(function (s) { return s.trim(); });
var kids = [], i;
for (i = 0; i < items.length; i++) {
  if (i) kids.push(WebUi.h('i', { text: '›' }));
  kids.push(i === items.length - 1 ? WebUi.h('b', { text: items[i].trim() }) : WebUi.h('span', { text: items[i].trim() }));
}
WebUi.render($('bc'), kids);
