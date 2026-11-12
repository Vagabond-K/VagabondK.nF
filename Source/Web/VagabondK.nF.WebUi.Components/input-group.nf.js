// input-group(라이브러리 인스턴스): Bootstrap식 입력+접두/접미 결합.
// 사용: <wui-input-group><b slot="pre">http://</b><b slot="post">:502</b></wui-input-group>
// 값은 wui-input-group의 .value 속성으로 왕복한다. 본문이 없는 빈 참조는 자동 숨김.
var self = this;
var inp = $('g');
Object.defineProperty(self, 'value', {
  get: function () { return inp.value; },
  set: function (v) { inp.value = v; }
});
if (self.getAttribute('placeholder')) inp.placeholder = self.getAttribute('placeholder');
