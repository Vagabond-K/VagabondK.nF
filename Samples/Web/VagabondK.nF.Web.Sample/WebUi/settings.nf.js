// 설정 폼: 저장/되돌리기/dirty/상태문구는 WebUi.bindForm이 담당한다(이전 formState/formChanges/sfSave/sfUndo).
// 폼 필드 스키마는 markup의 data-label/data-fmt에서 빌드 타임에 유도된다. SRV(서버 limit)는 페이지가 채운다.
const SF = WebUi.bindForm(document.getElementById('sf'), {
saveBtn: 'sfSave', undoBtn: 'sfUndo', stateEl: 'sfState', dotEl: 't-settings',
check(v) {
const e = {}, L = SRV.limits || {};
if (!v.deviceName) e.deviceName = '장치 이름을 입력하세요.';
else if (L.nameMax && v.deviceName.length > L.nameMax) e.deviceName = L.nameMax + '자 이내로 입력하세요.';
if (!/^\d+$/.test(v.pollMs)) e.pollMs = '정수를 입력하세요.';
else if (L.pollMin && (+v.pollMs < L.pollMin || +v.pollMs > L.pollMax)) e.pollMs = L.pollMin + '~' + L.pollMax + ' 사이 정수여야 합니다.';
return e;
},
save(v) {
return WebUi.post('/api/settings', new URLSearchParams(Object.assign({schemaVersion: String(SRV.schemaVersion), gatewayProfile: WebUi.M.id}, v)).toString());
}
});
// 상태가 오면 한 번만 채운다(로컬 편집 중 덮어쓰지 않음). 이후 되돌리기는 마지막 스냅샷(src)을 쓴다.
WebUi.on('wui:status', s => { if (!SF.base) SF.fill(s); });
