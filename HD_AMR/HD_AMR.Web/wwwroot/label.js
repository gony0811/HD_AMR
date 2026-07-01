// DL 비드 마스크 브러시 에디터. 캡처 이미지 위에 반투명 빨강으로 비드 영역을 칠/지우고,
// 저장 시 흑백 이진 마스크 PNG(base64)를 돌려준다. 캔버스는 이미지 원본 해상도로 동작하고
// CSS 로 표시 크기에 맞춰 스케일하므로, 저장 마스크는 항상 원본 해상도로 정렬된다.
window.hdAmrLabel = (function () {
    let baseImg = null, canvas = null, ctx = null;
    let painting = false, mode = 'paint', radius = 12, bound = false;

    function toNat(e) {
        const r = canvas.getBoundingClientRect();
        const sx = canvas.width / r.width, sy = canvas.height / r.height;
        return { x: (e.clientX - r.left) * sx, y: (e.clientY - r.top) * sy };
    }
    function stamp(x, y) {
        ctx.save();
        if (mode === 'erase') ctx.globalCompositeOperation = 'destination-out';
        ctx.beginPath();
        ctx.arc(x, y, radius, 0, Math.PI * 2);
        ctx.fillStyle = 'rgba(255,0,0,0.55)';
        ctx.fill();
        ctx.restore();
    }
    function down(e) { painting = true; const p = toNat(e); stamp(p.x, p.y); e.preventDefault(); }
    function move(e) { if (!painting) return; const p = toNat(e); stamp(p.x, p.y); e.preventDefault(); }
    function up() { painting = false; }

    function bind() {
        if (bound || !canvas) return; bound = true;
        canvas.addEventListener('pointerdown', down);
        canvas.addEventListener('pointermove', move);
        window.addEventListener('pointerup', up);
        canvas.addEventListener('pointerleave', up);
    }

    async function loadOverlay(url) {
        try {
            const resp = await fetch(url, { cache: 'no-store' });
            if (!resp.ok) return;   // 마스크 초안 없음
            const bmp = await createImageBitmap(await resp.blob());
            const tmp = document.createElement('canvas');
            tmp.width = canvas.width; tmp.height = canvas.height;
            const tctx = tmp.getContext('2d');
            tctx.drawImage(bmp, 0, 0, tmp.width, tmp.height);
            const img = tctx.getImageData(0, 0, tmp.width, tmp.height);
            const d = img.data;
            for (let i = 0; i < d.length; i += 4) {
                const on = d[i] > 127;              // 흰색(비드)
                d[i] = 255; d[i + 1] = 0; d[i + 2] = 0; d[i + 3] = on ? 140 : 0;
            }
            ctx.putImageData(img, 0, 0);
        } catch (e) { /* 무시 */ }
    }

    return {
        // 이미지 로드 → 캔버스 원본해상도 세팅 → (있으면) 마스크 초안 오버레이 → 브러시 바인딩.
        open: function (baseEl, canvasEl, imgUrl, maskUrl) {
            baseImg = baseEl; canvas = canvasEl; ctx = canvas.getContext('2d');
            baseImg.onload = function () {
                canvas.width = baseImg.naturalWidth;
                canvas.height = baseImg.naturalHeight;
                ctx.clearRect(0, 0, canvas.width, canvas.height);
                if (maskUrl) loadOverlay(maskUrl);
            };
            baseImg.src = imgUrl;
            bind();
        },
        setBrush: function (m, r) { mode = m; radius = Math.max(1, r | 0); },
        clear: function () { if (ctx) ctx.clearRect(0, 0, canvas.width, canvas.height); },
        reload: function (maskUrl) {
            if (!ctx) return;
            ctx.clearRect(0, 0, canvas.width, canvas.height);
            if (maskUrl) loadOverlay(maskUrl);
        },
        // 칠해진(alpha>0) 픽셀 → 흰색, 아니면 검정. 흑백 이진 마스크 PNG(base64) 반환.
        exportPng: function () {
            if (!ctx) return null;
            const out = document.createElement('canvas');
            out.width = canvas.width; out.height = canvas.height;
            const octx = out.getContext('2d');
            const src = ctx.getImageData(0, 0, canvas.width, canvas.height).data;
            const dst = octx.createImageData(out.width, out.height);
            const dd = dst.data;
            for (let i = 0; i < dd.length; i += 4) {
                const v = src[i + 3] > 10 ? 255 : 0;   // alpha 로 판정
                dd[i] = v; dd[i + 1] = v; dd[i + 2] = v; dd[i + 3] = 255;
            }
            octx.putImageData(dst, 0, 0);
            return out.toDataURL('image/png').split(',')[1];
        }
    };
})();
