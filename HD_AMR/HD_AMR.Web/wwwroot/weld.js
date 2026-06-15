// 용접라인 추적 ROI 에디터. 스트림 <img> 위에 <canvas> 를 겹쳐 사각형 ROI 를 마우스로 그린다.
// 좌표는 화면 표시 픽셀 → 이미지 네이티브 픽셀로 환산해 .NET 으로 콜백한다.
window.hdAmrWeld = {
  initRoiEditor(img, canvas, dotnetRef) {
    if (!img || !canvas || canvas._weldInit) return;
    canvas._weldInit = true;
    const st = canvas._weld = {
      img, dotnetRef, kind: 'weld',
      peak: null, weld: null,        // 이미지 좌표 {x,y,w,h}
      drag: null,                    // 진행 중 드래그(표시 좌표)
    };

    const toImg = (clientX, clientY) => {
      const r = img.getBoundingClientRect();
      const nw = img.naturalWidth || r.width, nh = img.naturalHeight || r.height;
      const sx = nw / r.width, sy = nh / r.height;
      return { x: (clientX - r.left) * sx, y: (clientY - r.top) * sy, r, nw, nh };
    };

    canvas.addEventListener('mousedown', (e) => {
      const p = toImg(e.clientX, e.clientY);
      st.drag = { x0: p.x, y0: p.y, x1: p.x, y1: p.y };
    });
    window.addEventListener('mousemove', (e) => {
      if (!st.drag) return;
      const p = toImg(e.clientX, e.clientY);
      st.drag.x1 = p.x; st.drag.y1 = p.y;
    });
    window.addEventListener('mouseup', () => {
      if (!st.drag) return;
      const d = st.drag; st.drag = null;
      const x = Math.round(Math.min(d.x0, d.x1)), y = Math.round(Math.min(d.y0, d.y1));
      const w = Math.round(Math.abs(d.x1 - d.x0)), h = Math.round(Math.abs(d.y1 - d.y0));
      if (w < 4 || h < 4) return;     // 클릭 수준은 무시
      if (st.kind === 'peak') st.peak = { x, y, w, h }; else st.weld = { x, y, w, h };
      st.dotnetRef.invokeMethodAsync('OnRoiDrawn', st.kind, x, y, w, h);
    });

    const draw = () => {
      if (!document.body.contains(canvas)) return; // 컴포넌트 제거 시 루프 종료
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
        ctx.strokeStyle = color;
        ctx.strokeRect(rc.x * fx, rc.y * fy, rc.w * fx, rc.h * fy);
        ctx.fillStyle = color;
        ctx.font = '12px sans-serif';
        ctx.fillText(label, rc.x * fx + 3, rc.y * fy + 13);
      };
      rect(st.weld, '#ffd000', 'Weld');
      rect(st.peak, '#00e0ff', 'Peak');
      if (st.drag) {
        const x = Math.min(st.drag.x0, st.drag.x1) * fx, y = Math.min(st.drag.y0, st.drag.y1) * fy;
        const w = Math.abs(st.drag.x1 - st.drag.x0) * fx, h = Math.abs(st.drag.y1 - st.drag.y0) * fy;
        ctx.strokeStyle = st.kind === 'peak' ? '#00e0ff' : '#ffd000';
        ctx.setLineDash([5, 4]); ctx.strokeRect(x, y, w, h); ctx.setLineDash([]);
      }
      requestAnimationFrame(draw);
    };
    requestAnimationFrame(draw);
  },

  setKind(canvas, kind) { if (canvas && canvas._weld) canvas._weld.kind = kind; },

  setRois(canvas, peak, weld) {
    if (!canvas || !canvas._weld) return;
    canvas._weld.peak = peak || null;
    canvas._weld.weld = weld || null;
  },
};
