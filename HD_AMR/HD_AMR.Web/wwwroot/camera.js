// Gemini 2 Depth 영상 hover 프로브. 깊이 원본값(mm)은 브라우저에 없고 서버 LatestDepth 버퍼에만
// 있으므로, 커서 위치를 정규화 좌표(u,v)로 /camera/depth/value 에 조회해 mm 라벨을 띄운다.
window.hdAmrCamera = {
  initDepthProbe(img, label) {
    if (!img || img._probeInit) return;   // 같은 엘리먼트 중복 init 방지
    img._probeInit = true;
    let busy = false;                      // 단일 in-flight 로 요청 throttle

    img.addEventListener('mousemove', async (e) => {
      const r = img.getBoundingClientRect();
      if (r.width === 0 || r.height === 0) return;
      const u = (e.clientX - r.left) / r.width;
      const v = (e.clientY - r.top) / r.height;

      // 라벨은 커서를 따라다님(컨테이너 기준 절대좌표).
      label.style.left = (e.clientX - r.left + 12) + 'px';
      label.style.top  = (e.clientY - r.top  + 12) + 'px';
      label.style.display = 'block';

      if (busy) return;
      busy = true;
      try {
        const res = await fetch(`/camera/depth/value?u=${u.toFixed(4)}&v=${v.toFixed(4)}`);
        const j = await res.json();
        label.textContent = (j.mm == null) ? '— mm' : `${j.mm} mm`;
      } catch { /* 네트워크 오류 무시 — 다음 이동에서 재시도 */ }
      busy = false;
    });

    img.addEventListener('mouseleave', () => { label.style.display = 'none'; });
  }
};
