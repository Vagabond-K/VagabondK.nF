// 설정 폼: 컨트롤 렌더와 저장/되돌리기/dirty 배선은 form 컴포넌트(WebUi.autoForm)가 담당한다.
// 페이지에는 장치-specific 부분(검증 규칙·저장 엔드포인트)만 남는다. SRV(서버 limit)는 페이지가 채운다.
// 필드 스키마는 index.html의 @manifest form 선언이 단일 출처다(빌드 타임에 KEYS/LABELS로 병합).

WebUi.autoForm({
  dotEl: WebUi.tabs.el(1),

  check(v) {
    const e = {}, L = SRV.limits || {};
    if (!v.deviceName) e.deviceName = '장치 이름을 입력하세요.';
    else if (L.nameMax && v.deviceName.length > L.nameMax) e.deviceName = L.nameMax + '자 이내로 입력하세요.';
    if (!/^\d+$/.test(v.pollMs)) e.pollMs = '정수를 입력하세요.';
    else if (L.pollMin && (+v.pollMs < L.pollMin || +v.pollMs > L.pollMax)) e.pollMs = L.pollMin + '~' + L.pollMax + ' 사이 정수여야 합니다.';
    return e;
  },

  save(v) {
    return WebUi.post('/api/settings', new URLSearchParams(Object.assign(
      { schemaVersion: String(SRV.schemaVersion), gatewayProfile: WebUi.M.id }, v)).toString());
  },
});
