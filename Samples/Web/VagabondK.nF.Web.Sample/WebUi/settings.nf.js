// 설정 폼(탭 안 인스턴스): 컨트롤은 settings.nf.html 마크업이 선언하고(빌드가 MANIFEST.form 생성),
// 채우기/dirty/되돌리기/저장 배선은 WebUi.bindForm이 합니다.
// 이 파일에는 "장치마다 다른 부분" 두 가지만 남습니다:
// - check(v): 클라이언트 검증. 서버가 /api/status로 내려준 limit(SRV)과 비교해 오류 지도를 반환.
// - save(v): 저장 엔드포인트로 POST. schemaVersion(낙관적 동시성)과 프로필 id를 함께 보냅니다.
// SRV는 이 페이지의 인라인 스크립트가 /api/status 응답에서 채운 전역입니다(index.nf.html 참조).

WebUi.bindForm(document.getElementById("wform"), {
  dotEl: WebUi.tabs.el(1),
  saveBtn: "wsave",
  undoBtn: "wundo",
  stateEl: "wstate",

  check(v) {
    const e = {},
      L = SRV.limits || {};
    if (!v.deviceName) e.deviceName = "장치 이름을 입력하세요.";
    else if (L.nameMax && v.deviceName.length > L.nameMax)
      e.deviceName = L.nameMax + "자 이내로 입력하세요.";
    if (!/^\d+$/.test(v.pollMs)) e.pollMs = "정수를 입력하세요.";
    else if (L.pollMin && (+v.pollMs < L.pollMin || +v.pollMs > L.pollMax))
      e.pollMs = L.pollMin + "~" + L.pollMax + " 사이 정수여야 합니다.";
    return e;
  },

  save(v) {
    return WebUi.post(
      "/api/settings",
      new URLSearchParams(
        Object.assign(
          {
            schemaVersion: String(SRV.schemaVersion),
            gatewayProfile: WebUi.M.id,
          },
          v,
        ),
      ).toString(),
    );
  },
});
