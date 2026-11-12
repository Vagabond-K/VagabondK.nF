// divider(라이브러리 인스턴스): MUI/Ant식 구분선. label이 있으면 한가운데 라벨을 둔다.
// 사용: <divider/> 또는 <divider label="고급 설정"/>
var self = this;
var lb = $('lb'), t = self.getAttribute('label');
if (t) lb.textContent = t; else lb.remove();
