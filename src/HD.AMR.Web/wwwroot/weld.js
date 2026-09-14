// 용접라인 추적 ROI/포인트 에디터. 스트림 <img> 위에 <canvas> 를 겹쳐:
//  - weld/peak: 사각형 ROI 드래그
//  - cal/validate: 길이를 아는 두 점 클릭(2점 보정/검증)
// 좌표는 화면 표시 픽셀 → 이미지 네이티브 픽셀로 환산해 .NET 으로 콜백한다.
window.hdAmrWeld = {
  initRoiEditor(img, canvas, dotnetRef) {
    if (!img || !canvas || canvas._weldInit) return;
    canvas._weldInit = true;
    const st = canvas._weld = {
      img, dotnetRef, kind: 'weld',
      peak: null, weld: null,
      drag: null, down: null,
      calPts: [], valPts: [],
    };

    const toImg = (clientX, clientY) => {
      const r = img.getBoundingClientRect();
      const nw = img.naturalWidth || r.width, nh = img.naturalHeight || r.height;
      return { x: (clientX - r.left) * nw / r.width, y: (clientY - r.top) * nh / r.height };
    };
    const isRoi = () => st.kind === 'weld' || st.kind === 'peak';

    canvas.addEventListener('mousedown', (e) => {
      const p = toImg(e.clientX, e.clientY);
      st.down = p;
      if (isRoi()) st.drag = { x0: p.x, y0: p.y, x1: p.x, y1: p.y };
    });
    window.addEventListener('mousemove', (e) => {
      if (!st.drag) return;
      const p = toImg(e.clientX, e.clientY);
      st.drag.x1 = p.x; st.drag.y1 = p.y;
    });
    window.addEventListener('mouseup', (e) => {
      const p = toImg(e.clientX, e.clientY);
      if (isRoi()) {
        if (!st.drag) return;
        const d = st.drag; st.drag = null;
        const x = Math.round(Math.min(d.x0, d.x1)), y = Math.round(Math.min(d.y0, d.y1));
        const w = Math.round(Math.abs(d.x1 - d.x0)), h = Math.round(Math.abs(d.y1 - d.y0));
        if (w < 4 || h < 4) return;
        if (st.kind === 'peak') st.peak = { x, y, w, h }; else st.weld = { x, y, w, h };
        st.dotnetRef.invokeMethodAsync('OnRoiDrawn', st.kind, x, y, w, h);
      } else {
        // cal/validate: 클릭이면 점 추가(드래그는 무시)
        if (!st.down) return;
        const moved = Math.hypot(p.x - st.down.x, p.y - st.down.y);
        st.down = null;
        if (moved > 6) return;
        const arr = st.kind === 'cal' ? st.calPts : st.valPts;
        if (arr.length >= 2) arr.length = 0;   // 3번째 클릭이면 새로 시작
        arr.push({ x: p.x, y: p.y });
        if (arr.length === 2)
          st.dotnetRef.invokeMethodAsync('OnTwoPointsPicked', st.kind, arr[0].x, arr[0].y, arr[1].x, arr[1].y);
      }
    });

    const draw = () => {
      if (!document.body.contains(canvas)) return;
      const r = img.getBoundingClientRect();
      if (canvas.width !== Math.round(r.width)) canvas.width = Math.round(r.width);
      if (canvas.height !== Math.round(r.height)) canvas.height = Math.round(r.height);
      const nw = img.naturalWidth || r.width, nh = img.naturalHeight || r.height;
      const fx = r.width / nw, fy = r.height / nh;
      const ctx = canvas.getContext('2d');
      ctx.clearRect(0, 0, canvas.width, canvas.height);
      ctx.lineWidth = 2;
      const rect = (rc, color, label) => {
        if (!rc) return;
        ctx.strokeStyle = color; ctx.strokeRect(rc.x * fx, rc.y * fy, rc.w * fx, rc.h * fy);
        ctx.fillStyle = color; ctx.font = '12px sans-serif'; ctx.fillText(label, rc.x * fx + 3, rc.y * fy + 13);
      };
      rect(st.weld, '#ffd000', 'Weld');
      rect(st.peak, '#00e0ff', 'Peak');
      if (st.drag) {
        const x = Math.min(st.drag.x0, st.drag.x1) * fx, y = Math.min(st.drag.y0, st.drag.y1) * fy;
        const w = Math.abs(st.drag.x1 - st.drag.x0) * fx, h = Math.abs(st.drag.y1 - st.drag.y0) * fy;
        ctx.strokeStyle = st.kind === 'peak' ? '#00e0ff' : '#ffd000';
        ctx.setLineDash([5, 4]); ctx.strokeRect(x, y, w, h); ctx.setLineDash([]);
      }
      const pts = (arr, color) => {
        if (!arr.length) return;
        ctx.strokeStyle = color; ctx.fillStyle = color; ctx.font = '12px sans-serif';
        if (arr.length === 2) { ctx.beginPath(); ctx.moveTo(arr[0].x * fx, arr[0].y * fy); ctx.lineTo(arr[1].x * fx, arr[1].y * fy); ctx.stroke(); }
        arr.forEach((p, i) => { ctx.beginPath(); ctx.arc(p.x * fx, p.y * fy, 5, 0, Math.PI * 2); ctx.fill(); ctx.fillText('' + (i + 1), p.x * fx + 7, p.y * fy - 7); });
      };
      pts(st.calPts, '#28a745');   // 보정 점 = 초록
      pts(st.valPts, '#e83e8c');   // 검증 점 = 분홍
      requestAnimationFrame(draw);
    };
    requestAnimationFrame(draw);
  },

  setKind(canvas, kind) { if (canvas && canvas._weld) canvas._weld.kind = kind; },
  setRois(canvas, peak, weld) { if (canvas && canvas._weld) { canvas._weld.peak = peak || null; canvas._weld.weld = weld || null; } },
  clearPoints(canvas, kind) {
    if (!canvas || !canvas._weld) return;
    if (kind === 'cal') canvas._weld.calPts = [];
    else if (kind === 'validate') canvas._weld.valPts = [];
  },
};
