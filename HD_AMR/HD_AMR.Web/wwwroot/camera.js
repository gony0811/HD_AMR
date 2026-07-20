// RealSense Depth 영상 hover 프로브. 깊이 원본값(mm)은 브라우저에 없고 서버 LatestDepth 버퍼에만
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
  },

  // Depth <img> 위에 <canvas> 를 겹쳐 ROI 사각형을 드래그/표시한다. weld.js initRoiEditor 의 단순화 버전으로,
  // 좌표는 표시 스케일과 무관하도록 정규화([0,1]) 로 .NET 에 콜백한다(OnRoiDrawn).
  initDepthRoi(img, canvas, dotnetRef) {
    if (!img || !canvas || canvas._roiInit) return;
    canvas._roiInit = true;
    console.debug('[hdAmrCamera] initDepthRoi bound — drag to set depth ROI');
    const st = canvas._roi = { img, dotnetRef, roi: null, enabled: true, drag: null };

    // clientX/Y → 이미지 박스 기준 정규화 좌표(0..1, 박스 밖은 클램프).
    const toNorm = (clientX, clientY) => {
      const r = img.getBoundingClientRect();
      if (r.width === 0 || r.height === 0) return { x: 0, y: 0 };
      return {
        x: Math.min(1, Math.max(0, (clientX - r.left) / r.width)),
        y: Math.min(1, Math.max(0, (clientY - r.top) / r.height)),
      };
    };

    canvas.addEventListener('mousedown', (e) => {
      const p = toNorm(e.clientX, e.clientY);
      st.drag = { x0: p.x, y0: p.y, x1: p.x, y1: p.y };
      e.preventDefault();
    });
    window.addEventListener('mousemove', (e) => {
      if (!st.drag) return;
      const p = toNorm(e.clientX, e.clientY);
      st.drag.x1 = p.x; st.drag.y1 = p.y;
    });
    window.addEventListener('mouseup', () => {
      if (!st.drag) return;
      const d = st.drag; st.drag = null;
      const x = Math.min(d.x0, d.x1), y = Math.min(d.y0, d.y1);
      const w = Math.abs(d.x1 - d.x0), h = Math.abs(d.y1 - d.y0);
      if (w < 0.01 || h < 0.01) return;   // 너무 작은 드래그는 무시(오클릭)
      st.roi = { x, y, w, h };
      st.dotnetRef.invokeMethodAsync('OnRoiDrawn', x, y, w, h);
    });

    const draw = () => {
      if (!document.body.contains(canvas)) return;
      const r = img.getBoundingClientRect();
      if (canvas.width !== Math.round(r.width)) canvas.width = Math.round(r.width);
      if (canvas.height !== Math.round(r.height)) canvas.height = Math.round(r.height);
      const ctx = canvas.getContext('2d');
      ctx.clearRect(0, 0, canvas.width, canvas.height);
      ctx.lineWidth = 2;
      // 확정 ROI(노랑). 비활성화면 점선으로 흐리게.
      if (st.roi) {
        ctx.strokeStyle = st.enabled ? '#ffd000' : 'rgba(255,208,0,.5)';
        if (!st.enabled) ctx.setLineDash([6, 4]);
        ctx.strokeRect(st.roi.x * canvas.width, st.roi.y * canvas.height,
                       st.roi.w * canvas.width, st.roi.h * canvas.height);
        ctx.setLineDash([]);
        ctx.fillStyle = ctx.strokeStyle; ctx.font = '12px sans-serif';
        ctx.fillText('ROI', st.roi.x * canvas.width + 3, st.roi.y * canvas.height + 13);
      }
      // 드래그 중 미리보기(점선).
      if (st.drag) {
        const x = Math.min(st.drag.x0, st.drag.x1) * canvas.width;
        const y = Math.min(st.drag.y0, st.drag.y1) * canvas.height;
        const w = Math.abs(st.drag.x1 - st.drag.x0) * canvas.width;
        const h = Math.abs(st.drag.y1 - st.drag.y0) * canvas.height;
        ctx.strokeStyle = '#ffd000'; ctx.setLineDash([5, 4]);
        ctx.strokeRect(x, y, w, h); ctx.setLineDash([]);
      }
      // 최소 깊이 지점 마젠타 화살표 + 거리값(서버에서 1초마다 setMinMarker 로 갱신).
      const m = canvas._minMarker;
      if (m) {
        const tipX = m.u * canvas.width, tipY = m.v * canvas.height;
        // 꼬리는 끝점 좌상단 26px. 화면 밖이면 우하단으로 뒤집어 항상 보이게.
        const offX = tipX < 30 ? 26 : -26;
        const offY = tipY < 30 ? 26 : -26;
        const tailX = tipX + offX, tailY = tipY + offY;
        ctx.save();
        ctx.shadowColor = '#fff'; ctx.shadowBlur = 2;   // 어두운 깊이맵 위 가독성 확보
        ctx.strokeStyle = '#ff00ff'; ctx.fillStyle = '#ff00ff'; ctx.lineWidth = 2;
        // 화살대
        ctx.beginPath(); ctx.moveTo(tailX, tailY); ctx.lineTo(tipX, tipY); ctx.stroke();
        // 화살촉(끝점 방향)
        const ang = Math.atan2(tipY - tailY, tipX - tailX), hl = 9, ha = 0.45;
        ctx.beginPath();
        ctx.moveTo(tipX, tipY);
        ctx.lineTo(tipX - hl * Math.cos(ang - ha), tipY - hl * Math.sin(ang - ha));
        ctx.lineTo(tipX - hl * Math.cos(ang + ha), tipY - hl * Math.sin(ang + ha));
        ctx.closePath(); ctx.fill();
        // 끝점 표시
        ctx.beginPath(); ctx.arc(tipX, tipY, 2.5, 0, Math.PI * 2); ctx.fill();
        // 거리값 라벨(꼬리 쪽, 화면 안으로 클램프)
        ctx.font = 'bold 13px sans-serif';
        const txt = `${m.mm} mm`;
        const tw = ctx.measureText(txt).width;
        let lx = offX < 0 ? tailX - tw - 2 : tailX + 2;
        let ly = offY < 0 ? tailY - 4 : tailY + 14;
        lx = Math.min(Math.max(2, lx), canvas.width - tw - 2);
        ly = Math.min(Math.max(12, ly), canvas.height - 2);
        ctx.fillText(txt, lx, ly);
        ctx.restore();
      }
      // 평면 검출 결과 — 가장 평평한 셀(시안 사각형 + 중심점). VerifyPlane 버튼이 setFlatCell 로 설정.
      const fc = canvas._flatCell;
      if (fc) {
        ctx.save();
        ctx.shadowColor = 'rgba(0,0,0,.7)'; ctx.shadowBlur = 2;   // 밝은 시안이 뜨도록 어두운 외곽
        ctx.strokeStyle = '#00e5ff'; ctx.fillStyle = '#00e5ff'; ctx.lineWidth = 2.5;
        const rx = fc.x * canvas.width, ry = fc.y * canvas.height;
        const rw = fc.w * canvas.width, rh = fc.h * canvas.height;
        ctx.strokeRect(rx, ry, rw, rh);
        const cx = (fc.x + fc.w / 2) * canvas.width, cy = (fc.y + fc.h / 2) * canvas.height;
        ctx.beginPath(); ctx.arc(cx, cy, 4, 0, Math.PI * 2); ctx.fill();   // 중심점
        ctx.font = 'bold 12px sans-serif';
        ctx.fillText('평탄', rx + 3, ry + 13);
        ctx.restore();
      }
      requestAnimationFrame(draw);
    };
    requestAnimationFrame(draw);
  },

  // 저장/숫자입력값으로 ROI 박스를 갱신(정규화 좌표). null 이면 박스 제거.
  setRoi(canvas, x, y, w, h, enabled) {
    if (!canvas || !canvas._roi) return;
    canvas._roi.enabled = !!enabled;
    canvas._roi.roi = (w > 0 && h > 0) ? { x, y, w, h } : null;
  },

  // 최소 깊이 지점 마커를 갱신(정규화 좌표 u,v + 거리 mm). u<0 또는 mm<=0 이면 마커 제거.
  setMinMarker(canvas, u, v, mm) {
    if (!canvas) return;
    canvas._minMarker = (u >= 0 && mm > 0) ? { u, v, mm } : null;
  },

  // 가장 평평한 셀(정규화 x,y,w,h). w/h<=0 이면 오버레이 제거.
  setFlatCell(canvas, x, y, w, h) {
    if (!canvas) return;
    canvas._flatCell = (w > 0 && h > 0) ? { x, y, w, h } : null;
  },
};
