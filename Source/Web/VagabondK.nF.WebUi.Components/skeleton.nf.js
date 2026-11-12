// skeleton(라이브러리 인스턴스): shimmer 자리받기(CSS-only).
// 사용: <skeleton/> · <skeleton w="60%" h="14"/> · <skeleton round/>
var el = $('wsk');
var w = this.getAttribute('w');
var h = this.getAttribute('h');
if (w) el.style.width = /^[0-9]+$/.test(w) ? w + '%' : w;
el.style.height = h ? (/^[0-9]+$/.test(h) ? h + 'px' : h) : '16px';
if (this.hasAttribute('round')) el.style.borderRadius = '50%';
