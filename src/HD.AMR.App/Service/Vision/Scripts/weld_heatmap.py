"""
용접선 **중심선 히트맵** 학습/평가/내보내기 — 사전학습 ResNet18 인코더 U-Net.

  python weld_heatmap.py train   --captures <캡처폴더> [<폴더2> ...] --modality rgb --out runs/heatmap_rgb
                                 [--epochs 150 --batch 8 --width 640 --sigma 2.5 --patience 40 --device auto]
                                 [--no-pretrained]  (ImageNet 가중치 미사용 — 첫 실행 시 약 45MB 내려받기를 피할 때)
  python weld_heatmap.py train   --captures <폴더> --modality rgb --out runs/heatmap_rgb --init runs/heatmap_rgb/prev/best_….pt --epochs 50
                                 (이어 학습·보강: 이전 best.pt 에서 시작, 이전 모델 성능을 먼저 재고 그보다 나을 때만 best.pt 교체)
  python weld_heatmap.py eval    --weights runs/heatmap_rgb/best.pt --captures <폴더> --modality rgb [--split val|all]
  python weld_heatmap.py predict --weights runs/heatmap_rgb/best.pt --images <png...> --out <폴더>   (오버레이 jpg)
  python weld_heatmap.py export  --weights runs/heatmap_rgb/best.pt --out models/weld_heatmap_rgb.onnx

데이터: 앱 캡처 폴더의 {stem}_{modality}.png + {stem}_{modality}_line.json(수동 비드 팝업·라벨 편집기·IR↔RGB 자동 변환이
만든 선 라벨)을 그대로 읽는다 — 마스크·데이터셋 변환 단계가 필요 없다.

설계 메모
- 목표는 "비드 마스크"가 아니라 "비드 중심선의 위치"다. 그래서 선 라벨에서 중심선까지 거리의 가우시안(σ px)을 정답
  히트맵으로 만들고 예측한다. 두꺼운 이진 마스크보다 중심 위치가 날카롭고, 라벨 두께에 좌우되지 않는다.
- 디코딩: 열마다 최댓값 위치(부분 픽셀 보정) → 신뢰도 낮은 열 제외 → 거의 수평이라는 조건의 RANSAC 직선 피팅.
  반사로 가려지거나 코루게이션에 끊긴 구간이 있어도 나머지 열로 선을 잡는다.
- 평가: IoU 가 아니라 **원본 픽셀 기준 위치 오차**(영상 가로 중앙에서의 세로 오차, 라벨 구간 평균 오차)와 검출률.
- 학습/검증 분할은 **촬영 세션 단위**(촬영 시각 간격 --session-gap-min 이상이면 다른 세션). 연속 촬영본이 거의 같아
  무작위 분할은 검증 점수를 부풀린다.
- 회전 증강본(_cw90/_ccw90)은 원본 라벨이 있으면 뺀다 — 전치하면 원본의 상하/좌우 뒤집기와 같아 학습 중 무작위
  뒤집기로 이미 생기므로 중복이다(--keep-rotated 로 포함 가능). 90/180/270° 회전 증강은 이 모델에 필요 없다.
- 세로 용접선 라벨은 전치(x↔y)해 가로선으로 학습한다 — 앱은 진행축이 세로면 같은 전치를 한다.
  배율은 긴 변 기준(--width)이라 회전본도 원본과 같은 축척, 학습은 고정 크기 크롭(--crop-h×--crop-w)으로 배치를 맞춘다.
- 입력은 3채널(IR 은 그레이를 3채널로 복제). ONNX 는 [1,3,H,W] 0~1 입력 → [1,1,H,W] logit, ImageNet 정규화는 모델 안에
  포함하므로 추론측은 /255 만 하면 된다. H·W 는 32 배수 동적 축.
- 에폭마다 "Epoch k/N ..." 한 줄을 출력한다(앱 진행 바 파싱 규약, weld_unet.py 와 동일).
"""
import argparse
import glob
import json
import math
import os
import random
import re
import sys
import time

import cv2
import numpy as np
import torch
import torch.nn as nn
import torch.nn.functional as F

MIN_SAMPLES_PER_EPOCH = 300
IMAGENET_MEAN = (0.485, 0.456, 0.406)
IMAGENET_STD = (0.229, 0.224, 0.225)


# ── 모델 ──────────────────────────────────────────────────────────────────
class ConvBlock(nn.Module):
    def __init__(self, cin, cout):
        super().__init__()
        self.net = nn.Sequential(
            nn.Conv2d(cin, cout, 3, padding=1, bias=False), nn.BatchNorm2d(cout), nn.ReLU(inplace=True),
            nn.Conv2d(cout, cout, 3, padding=1, bias=False), nn.BatchNorm2d(cout), nn.ReLU(inplace=True),
        )

    def forward(self, x):
        return self.net(x)


class HeatmapUNet(nn.Module):
    """ResNet18 인코더(1/2,1/4,1/8,1/16,1/32) + U-Net 디코더 → 원 해상도 1채널 logit. 입력은 0~1 RGB."""

    def __init__(self, pretrained=True):
        super().__init__()
        import torchvision
        weights = torchvision.models.ResNet18_Weights.IMAGENET1K_V1 if pretrained else None
        r = torchvision.models.resnet18(weights=weights)
        self.stem = nn.Sequential(r.conv1, r.bn1, r.relu)          # 1/2, 64
        self.pool = r.maxpool
        self.l1, self.l2, self.l3, self.l4 = r.layer1, r.layer2, r.layer3, r.layer4  # 64,128,256,512
        self.register_buffer("mean", torch.tensor(IMAGENET_MEAN).view(1, 3, 1, 1))
        self.register_buffer("std", torch.tensor(IMAGENET_STD).view(1, 3, 1, 1))
        self.d4 = ConvBlock(512 + 256, 256)
        self.d3 = ConvBlock(256 + 128, 128)
        self.d2 = ConvBlock(128 + 64, 64)
        self.d1 = ConvBlock(64 + 64, 48)
        self.d0 = ConvBlock(48 + 3, 32)
        self.head = nn.Conv2d(32, 1, 1)
        nn.init.constant_(self.head.bias, -4.0)   # 대부분이 배경 — 초기 확률을 낮게 시작

    @staticmethod
    def _up(x, ref):
        return F.interpolate(x, size=ref.shape[-2:], mode="bilinear", align_corners=False)

    def forward(self, x):
        raw = x
        x = (x - self.mean) / self.std
        s0 = self.stem(x)            # 1/2
        s1 = self.l1(self.pool(s0))  # 1/4
        s2 = self.l2(s1)             # 1/8
        s3 = self.l3(s2)             # 1/16
        s4 = self.l4(s3)             # 1/32
        y = self.d4(torch.cat([self._up(s4, s3), s3], 1))
        y = self.d3(torch.cat([self._up(y, s2), s2], 1))
        y = self.d2(torch.cat([self._up(y, s1), s1], 1))
        y = self.d1(torch.cat([self._up(y, s0), s0], 1))
        y = self.d0(torch.cat([self._up(y, raw), raw], 1))
        return self.head(y)


# ── 데이터 ────────────────────────────────────────────────────────────────
TS_RE = re.compile(r"(\d{8}_\d{6})")


ROT_RE = re.compile(r"_c?cw90$")


def drop_rotated(samples):
    """회전 증강본(_cw90/_ccw90)을 뺀다 — 원본 라벨이 있을 때만. 전치 학습에서 회전본은 원본의 상하/좌우 뒤집기와
    같아(학습 중 무작위 뒤집기로 이미 생김) 같은 장면을 중복으로 세고 학습만 느리게 한다.
    원본 라벨 없이 회전본에만 라벨이 있으면 그 회전본은 남긴다(전치하면 원본과 같은 정보)."""
    base = {(os.path.dirname(x["img"]), x["stem"]) for x in samples if not ROT_RE.search(x["stem"])}
    kept, dropped = [], 0
    for x in samples:
        if ROT_RE.search(x["stem"]) and (os.path.dirname(x["img"]), ROT_RE.sub("", x["stem"])) in base:
            dropped += 1
            continue
        kept.append(x)
    return kept, dropped


def find_samples(dirs, modality):
    """(이미지 경로, 선 라벨 dict, stem, 촬영시각 epoch초) 목록."""
    out = []
    for d in dirs:
        for lp in sorted(glob.glob(os.path.join(d, f"*_{modality}_line.json"))):
            stem = os.path.basename(lp)[: -len(f"_{modality}_line.json")]
            ip = os.path.join(d, f"{stem}_{modality}.png")
            if not os.path.exists(ip):
                continue
            with open(lp, "r", encoding="utf-8") as f:
                lab = json.load(f)
            pts = lab.get("points") or lab.get("Points") or []
            if len(pts) < 2:
                continue
            m = TS_RE.search(stem)
            t = time.mktime(time.strptime(m.group(1), "%Y%m%d_%H%M%S")) if m else os.path.getmtime(ip)
            out.append({"img": ip, "points": np.array(pts, dtype=np.float32), "stem": stem, "t": t,
                        "origin": lab.get("origin")})
    return out


def split_by_session(samples, gap_min, val_frac, seed):
    """촬영 시각을 정렬해 간격이 gap_min 분 이상이면 세션을 끊고, 세션 단위로 검증셋을 고른다."""
    if not samples:
        return [], [], 0
    s = sorted(samples, key=lambda x: x["t"])
    sessions, cur = [], [s[0]]
    for a, b in zip(s, s[1:]):
        if b["t"] - a["t"] > gap_min * 60:
            sessions.append(cur)
            cur = []
        cur.append(b)
    sessions.append(cur)
    if len(sessions) < 2:
        return s, s, len(sessions)   # 세션 하나 — 검증을 학습셋으로(경고)
    rng = random.Random(seed)
    order = list(range(len(sessions)))
    rng.shuffle(order)
    n_val = max(1, round(len(sessions) * val_frac))
    val_ids = set(order[:n_val])
    train = [x for i, ss in enumerate(sessions) if i not in val_ids for x in ss]
    val = [x for i, ss in enumerate(sessions) if i in val_ids for x in ss]
    return train, val, len(sessions)


def load_image(path):
    img = cv2.imread(path, cv2.IMREAD_COLOR)   # IR 그레이도 3채널로
    if img is None:
        raise RuntimeError(f"이미지를 읽지 못했습니다: {path}")
    return cv2.cvtColor(img, cv2.COLOR_BGR2RGB)


def resize_sample(img, pts, width):
    """긴 변을 width 로 맞추는 배율로 줄인다 — 카메라 원본(가로 영상)은 가로=width, 회전본(세로 영상)도 같은 배율이 된다.
    앱 추론도 같은 규약(배율 = train_width / 프레임 가로)이라 비드 굵기·물결무늬 크기가 학습과 같아진다."""
    h, w = img.shape[:2]
    s = width / max(h, w)
    nw, nh = max(32, int(round(w * s))), max(32, int(round(h * s)))
    sx, sy = nw / w, nh / h
    return cv2.resize(img, (nw, nh), interpolation=cv2.INTER_AREA), pts * np.array([sx, sy], np.float32), (sx, sy)


def is_vertical(pts):
    """선 라벨이 세로에 가까운지(끝점 기준 세로 변화가 가로 변화보다 큼)."""
    return abs(pts[-1, 1] - pts[0, 1]) > abs(pts[-1, 0] - pts[0, 0])


def to_horizontal(img, pts):
    """세로선이면 전치(x↔y)해 가로선으로 — 모델은 가로선만 배우고, 앱은 세로 진행축일 때 같은 전치를 한다."""
    if not is_vertical(pts):
        return img, pts, False
    return np.ascontiguousarray(np.transpose(img, (1, 0, 2))), pts[:, ::-1].copy(), True


def crop_pad(img, pts, ch, cw, rng, pos_frac=0.9):
    """고정 크기(ch×cw)로 자르기 — 작으면 반사 패딩. pos_frac 확률로 선이 창 안에 들어오게 고른다(배치 크기 통일)."""
    h, w = img.shape[:2]
    py, px = max(0, ch - h), max(0, cw - w)
    if py or px:
        top, left = py // 2, px // 2
        img = cv2.copyMakeBorder(img, top, py - top, left, px - left, cv2.BORDER_REFLECT_101)
        pts = pts + np.array([left, top], np.float32)
        h, w = img.shape[:2]
    x0 = rng.randint(0, w - cw)
    if rng.random() < pos_frac:
        ly = label_y_at(pts, x0 + cw / 2)
        ly = float(np.mean(pts[:, 1])) if ly is None else ly
        lo, hi = int(max(0, ly - ch + 16)), int(min(h - ch, ly - 16))
        y0 = rng.randint(lo, hi) if hi >= lo else rng.randint(0, h - ch)
    else:
        y0 = rng.randint(0, h - ch)
    return img[y0:y0 + ch, x0:x0 + cw].copy(), pts - np.array([x0, y0], np.float32)


def polyline_distance(h, w, pts):
    """각 픽셀에서 폴리라인까지의 최소 거리(px)."""
    ys, xs = np.mgrid[0:h, 0:w].astype(np.float32)
    best = np.full((h, w), np.inf, np.float32)
    for (ax, ay), (bx, by) in zip(pts[:-1], pts[1:]):
        dx, dy = bx - ax, by - ay
        l2 = dx * dx + dy * dy
        t = np.zeros_like(xs) if l2 <= 0 else np.clip(((xs - ax) * dx + (ys - ay) * dy) / l2, 0, 1)
        d = np.hypot(ax + t * dx - xs, ay + t * dy - ys)
        best = np.minimum(best, d)
    return best


def heatmap_target(h, w, pts, sigma):
    d = polyline_distance(h, w, pts)
    return np.exp(-(d * d) / (2 * sigma * sigma)).astype(np.float32)


def augment(img, pts, rng):
    h, w = img.shape[:2]
    if rng.random() < 0.5:
        img = img[:, ::-1].copy()
        pts = pts.copy(); pts[:, 0] = (w - 1) - pts[:, 0]
    if rng.random() < 0.5:
        img = img[::-1].copy()
        pts = pts.copy(); pts[:, 1] = (h - 1) - pts[:, 1]
    # 회전 ±6°, 배율 0.85~1.15, 평행이동 ±10% — 영상과 선을 같은 변환으로
    ang = rng.uniform(-6, 6)
    sc = rng.uniform(0.85, 1.15)
    tx, ty = rng.uniform(-0.1, 0.1) * w, rng.uniform(-0.1, 0.1) * h
    M = cv2.getRotationMatrix2D((w / 2, h / 2), ang, sc)
    M[:, 2] += (tx, ty)
    img = cv2.warpAffine(img, M, (w, h), flags=cv2.INTER_LINEAR, borderMode=cv2.BORDER_REFLECT_101)
    pts = (np.hstack([pts, np.ones((len(pts), 1), np.float32)]) @ M.T).astype(np.float32)
    # 밝기·대비·감마, 반사 번짐 모사(밝은 타원), 잡음·흐림
    f = img.astype(np.float32) / 255.0
    f = np.clip(f * rng.uniform(0.7, 1.3) + rng.uniform(-0.12, 0.12), 0, 1)
    f = np.power(f, rng.uniform(0.7, 1.4))
    if rng.random() < 0.3:
        cx, cy = rng.uniform(0, w), rng.uniform(0, h)
        rx, ry = rng.uniform(0.1, 0.35) * w, rng.uniform(0.1, 0.35) * h
        yy, xx = np.mgrid[0:h, 0:w]
        g = np.exp(-(((xx - cx) / rx) ** 2 + ((yy - cy) / ry) ** 2))[..., None]
        f = np.clip(f + g * rng.uniform(0.3, 0.9), 0, 1)
    if rng.random() < 0.3:
        f = cv2.GaussianBlur(f, (0, 0), rng.uniform(0.5, 1.2))
    if rng.random() < 0.3:
        f = np.clip(f + np.random.default_rng(rng.randrange(1 << 30)).normal(0, rng.uniform(0.005, 0.03), f.shape), 0, 1)
    return (f * 255).astype(np.uint8), pts


def prepare(samples, width):
    out = []
    for s in samples:
        img, pts, transposed = to_horizontal(load_image(s["img"]), s["points"])
        img, pts, scale = resize_sample(img, pts, width)
        out.append({**s, "im": img, "pts": pts, "scale": scale, "transposed": transposed})
    return out


def to_tensor(img):
    return torch.from_numpy(img.astype(np.float32) / 255.0).permute(2, 0, 1)


# ── 디코딩·평가 ───────────────────────────────────────────────────────────
# 직선 맞춤 허용 기울기 [°]. 세로선(>45°)은 전치해서 다루므로 판독 대상은 0~45° — 대각선(30~45°)도 받아들이도록
# 여유를 둔다(예전 25° 제한은 대각선 용접선을 버리고 잡음 조각으로 수평선을 맞추는 원인이었다).
MAX_LINE_ANGLE_DEG = 50.0


def decode_line(prob, thr=0.3, ransac_px=3.0, iters=200, seed=0, max_angle_deg=MAX_LINE_ANGLE_DEG):
    """확률맵 → (열별 점 [N,2], 직선 (a,b): y = a·x + b, 사용 열 비율). 실패 시 (점, None, 비율)."""
    h, w = prob.shape
    ys = prob.argmax(0)
    peak = prob[ys, np.arange(w)]
    keep = peak >= thr
    xs = np.arange(w)[keep].astype(np.float32)
    yv = ys[keep].astype(np.float32)
    # 부분 픽셀: 최댓값 위아래 1px 포물선 보정
    for i, (x, y) in enumerate(zip(xs.astype(int), ys[keep])):
        if 0 < y < h - 1:
            l, c, r = prob[y - 1, x], prob[y, x], prob[y + 1, x]
            den = l - 2 * c + r
            if den < 0:
                yv[i] = y + 0.5 * (l - r) / den
    pts = np.stack([xs, yv], 1)
    frac = keep.mean()
    if len(pts) < max(8, 0.05 * w):
        return pts, None, frac
    rng = np.random.default_rng(seed)
    best, best_in = None, None
    for _ in range(iters):
        i, j = rng.choice(len(pts), 2, replace=False)
        (x1, y1), (x2, y2) = pts[i], pts[j]
        if abs(x2 - x1) < w * 0.1:
            continue
        a = (y2 - y1) / (x2 - x1)
        if abs(a) > math.tan(math.radians(max_angle_deg)):   # 가로 쪽 선(전치로 0~45°)만
            continue
        b = y1 - a * x1
        inl = np.abs(pts[:, 1] - (a * pts[:, 0] + b)) <= ransac_px
        if best_in is None or inl.sum() > best_in.sum():
            best, best_in = (a, b), inl
    if best is None:
        return pts, None, frac
    a, b = np.polyfit(pts[best_in, 0], pts[best_in, 1], 1)
    return pts, (float(a), float(b)), frac


def label_y_at(pts, x):
    """라벨 폴리라인의 x 위치 y(구간 밖이면 None)."""
    for (ax, ay), (bx, by) in zip(pts[:-1], pts[1:]):
        lo, hi = min(ax, bx), max(ax, bx)
        if lo <= x <= hi and hi > lo:
            return ay + (by - ay) * (x - ax) / (bx - ax)
    return None


@torch.no_grad()
def predict_prob(model, img, device):
    """임의 크기 영상 → 같은 크기 확률맵(32 배수 반사 패딩 후 잘라냄)."""
    h, w = img.shape[:2]
    ph, pw = (-h) % 32, (-w) % 32
    padded = cv2.copyMakeBorder(img, 0, ph, 0, pw, cv2.BORDER_REFLECT_101) if (ph or pw) else img
    x = to_tensor(padded).unsqueeze(0).to(device)
    return torch.sigmoid(model(x))[0, 0, :h, :w].float().cpu().numpy()


@torch.no_grad()
def evaluate(model, samples, device, thr=0.3):
    """원본 픽셀 기준 중앙 오차·평균 오차·검출률."""
    model.eval()
    centers, means, found = [], [], 0
    for s in samples:
        prob = predict_prob(model, s["im"], device)
        _, line, _ = decode_line(prob, thr)
        h, w = prob.shape
        sy = s["scale"][1]
        if line is None:
            continue
        found += 1
        a, b = line
        errs = []
        for x in np.linspace(0, w - 1, 41):
            ly = label_y_at(s["pts"], x)
            if ly is not None:
                errs.append(abs((a * x + b) - ly) / sy)
        cy = label_y_at(s["pts"], (w - 1) / 2)
        if cy is not None:
            centers.append(abs((a * (w - 1) / 2 + b) - cy) / sy)
        if errs:
            means.append(float(np.mean(errs)))
    n = max(1, len(samples))
    return {
        "found": found / n,
        "center_px": float(np.median(centers)) if centers else float("inf"),
        "center_p90": float(np.percentile(centers, 90)) if centers else float("inf"),
        "mean_px": float(np.mean(means)) if means else float("inf"),
    }


# ── 명령 ─────────────────────────────────────────────────────────────────
def pick_device(name):
    if name in ("auto", "cuda") and torch.cuda.is_available():
        return torch.device("cuda")
    if name in ("auto", "mps") and getattr(torch.backends, "mps", None) and torch.backends.mps.is_available():
        return torch.device("mps")
    if name not in ("auto", "cpu"):
        print(f"WARN 장치 '{name}' 사용 불가 — cpu 로 대체", flush=True)
    return torch.device("cpu")


def cmd_train(a):
    samples = find_samples(a.captures, a.modality)
    rot_dropped = 0
    if not a.keep_rotated:
        samples, rot_dropped = drop_rotated(samples)
    if not samples:
        sys.exit(f"ERROR {a.modality} 선 라벨(*_{a.modality}_line.json)이 있는 촬영본이 없습니다: {a.captures}")
    train, val, n_sess = split_by_session(samples, a.session_gap_min, a.val_frac, a.seed)
    print(f"DATA {len(samples)}장 (세션 {n_sess}개) → 학습 {len(train)} / 검증 {len(val)}"
          f" · 자동 변환 라벨 {sum(1 for s in samples if (s['origin'] or '').startswith('transfer'))}장"
          f" · 세로선(전치해 학습) {sum(1 for s in samples if is_vertical(s['points']))}장"
          + (f" · 회전본 제외 {rot_dropped}장(원본과 중복)" if rot_dropped else ""), flush=True)
    if n_sess < 2:
        print("WARN 촬영 세션이 1개뿐 — 검증셋을 학습셋으로 대신합니다(점수는 과대평가). 여러 날·위치에서 라벨을 모으세요.", flush=True)
    if len(samples) < 50:
        print(f"WARN 라벨 {len(samples)}장은 학습에 크게 부족합니다(권장 150~300장 이상).", flush=True)

    device = pick_device(a.device)
    init_ck = None
    if a.init:
        # 이어 학습(보강): 이전 best.pt 의 가중치에서 시작. 학습 해상도는 이전 모델 값을 따른다(축척이 바뀌면 배운 것이 어긋남).
        init_ck = torch.load(a.init, map_location="cpu", weights_only=False)
        prev_w = (init_ck.get("cfg") or {}).get("width", a.width)
        if prev_w != a.width:
            print(f"WARN 이전 모델 학습 해상도 {prev_w} 를 따릅니다(요청 {a.width}).", flush=True)
            a.width = prev_w
    tr, va = prepare(train, a.width), prepare(val, a.width)
    model = HeatmapUNet(pretrained=not a.no_pretrained and init_ck is None)
    lr = a.lr
    if init_ck is not None:
        model.load_state_dict(init_ck["model"])
        lr = a.lr * a.finetune_lr_scale
        print(f"INIT 이전 모델에서 이어 학습 — {a.init} (epoch {init_ck.get('epoch')}, 학습률 ×{a.finetune_lr_scale})", flush=True)
    model = model.to(device)
    enc = [p for n, p in model.named_parameters() if n.split(".")[0] in ("stem", "l1", "l2", "l3", "l4")]
    dec = [p for n, p in model.named_parameters() if n.split(".")[0] not in ("stem", "l1", "l2", "l3", "l4")]
    opt = torch.optim.AdamW([{"params": enc, "lr": lr * 0.3}, {"params": dec, "lr": lr}], weight_decay=1e-4)
    # 라벨이 적을 때도 에폭당 최소 MIN_SAMPLES_PER_EPOCH 개를 증강해 쓰도록 반복 횟수를 올린다(회전본 제외로 장수가 줄어도
    # 에폭당 학습량·조기 종료 감각이 비슷하게 유지된다).
    repeats = max(a.repeats, math.ceil(MIN_SAMPLES_PER_EPOCH / max(1, len(tr))))
    steps = max(1, math.ceil(len(tr) * repeats / a.batch))
    sched = torch.optim.lr_scheduler.OneCycleLR(opt, max_lr=[lr * 0.3, lr], total_steps=a.epochs * steps,
                                                pct_start=0.05 if init_ck is not None else 0.1)
    pos_w = torch.tensor(a.pos_weight, device=device)
    rng = random.Random(a.seed)
    os.makedirs(a.out, exist_ok=True)
    cfg = {"modality": a.modality, "width": a.width, "sigma": a.sigma}
    best, best_ep, since = None, 0, 0   # (−검출률, 중앙 오차) 사전식 — 검출률이 먼저
    baseline = None
    if init_ck is not None:
        # 이전 모델을 이번 검증셋(새 데이터 포함)으로 먼저 잰다 — 이보다 나아질 때만 best.pt 가 바뀐다.
        m0 = evaluate(model, va, device, a.thr)
        baseline = (-m0["found"], m0["center_px"])
        best = baseline
        ck0 = {"model": {k: v.detach().cpu() for k, v in model.state_dict().items()}, "cfg": cfg, "epoch": 0,
               "val": m0, "init": a.init}
        torch.save(ck0, os.path.join(a.out, "best.pt"))
        print(f"BASELINE 이전 모델 val_found={m0['found']:.2f} val_center_px={m0['center_px']:.2f} "
              f"val_p90={m0['center_p90']:.2f} val_mean_px={m0['mean_px']:.2f}", flush=True)

    for ep in range(1, a.epochs + 1):
        model.train()
        pool = tr * repeats
        rng.shuffle(pool)
        tot = 0.0
        for i in range(steps):
            chunk = pool[i * a.batch:(i + 1) * a.batch] or pool[: a.batch]
            xs, ys = [], []
            for s in chunk:
                img, pts = augment(s["im"], s["pts"], rng)
                img, pts = crop_pad(img, pts, a.crop_h, a.crop_w, rng)
                xs.append(to_tensor(img))
                ys.append(torch.from_numpy(heatmap_target(img.shape[0], img.shape[1], pts, a.sigma))[None])
            x = torch.stack(xs).to(device)
            y = torch.stack(ys).to(device)
            logits = model(x)
            loss = F.binary_cross_entropy_with_logits(logits, y, pos_weight=pos_w)
            opt.zero_grad()
            loss.backward()
            opt.step()
            sched.step()
            tot += loss.item()
        m = evaluate(model, va, device, a.thr)
        score = (-m["found"], m["center_px"])
        state = {k: v.detach().cpu() for k, v in model.state_dict().items()}
        ck = {"model": state, "cfg": cfg, "epoch": ep, "val": m}
        torch.save(ck, os.path.join(a.out, "last.pt"))
        if best is None or score < best:
            best, best_ep, since = score, ep, 0
            torch.save(ck, os.path.join(a.out, "best.pt"))
        else:
            since += 1
        print(f"Epoch {ep}/{a.epochs} loss={tot / steps:.4f} val_found={m['found']:.2f} "
              f"val_center_px={m['center_px']:.2f} val_p90={m['center_p90']:.2f} val_mean_px={m['mean_px']:.2f}", flush=True)
        if since >= a.patience:
            print(f"EARLY_STOP {a.patience} epoch 동안 검증 성능 개선 없음", flush=True)
            break
    if baseline is not None and best_ep == 0:
        print("NO_IMPROVEMENT 이어 학습이 이전 모델보다 나아지지 않았습니다 — best.pt 는 이전 모델 그대로입니다.", flush=True)
    elif baseline is not None:
        print(f"IMPROVED 이전 모델 대비 중앙 오차 {baseline[1]:.2f} → {best[1]:.2f}px, 검출률 {-baseline[0]:.2f} → {-best[0]:.2f}", flush=True)
    print(f"TRAIN_DONE best_found={-best[0]:.2f} best_center_px={best[1]:.2f} epoch={best_ep} -> {os.path.join(a.out, 'best.pt')}", flush=True)


def load_model(path, device):
    ck = torch.load(path, map_location="cpu", weights_only=False)
    model = HeatmapUNet(pretrained=False)
    model.load_state_dict(ck["model"])
    return model.to(device).eval(), ck.get("cfg", {})


def cmd_eval(a):
    device = pick_device(a.device)
    model, cfg = load_model(a.weights, device)
    samples, _ = drop_rotated(find_samples(a.captures, a.modality or cfg.get("modality", "rgb")))
    if a.split == "val":
        _, samples, _ = split_by_session(samples, a.session_gap_min, a.val_frac, a.seed)
    m = evaluate(model, prepare(samples, cfg.get("width", 640)), device, a.thr)
    print(f"EVAL {len(samples)}장 found={m['found']:.2f} center_px(중앙값)={m['center_px']:.2f} "
          f"p90={m['center_p90']:.2f} mean_px={m['mean_px']:.2f}", flush=True)


def cmd_predict(a):
    """영상마다 히트맵·중심선 오버레이 jpg. 용접선 방향을 모르므로 기본은 가로·세로(전치) 둘 다 풀어 선을 찾은 쪽을 쓴다
    (앱 ⑤ 추론 탭과 같은 규칙). --vertical / --horizontal 로 방향을 고정할 수 있다."""
    device = pick_device(a.device)
    model, cfg = load_model(a.weights, device)
    os.makedirs(a.out, exist_ok=True)
    width = cfg.get("width", 640)
    orients = [True] if a.vertical else [False] if a.horizontal else [False, True]
    for p in a.images:
        img0 = load_image(p)
        best = None
        for vert in orients:
            work = np.ascontiguousarray(np.transpose(img0, (1, 0, 2))) if vert else img0
            # 배율은 원본 프레임 긴 변 기준(앱과 같음) — 전치해도 같다.
            img, _, (sx, sy) = resize_sample(work, np.zeros((2, 2), np.float32), width)
            prob = predict_prob(model, img, device)
            pts, line, frac = decode_line(prob, a.thr)
            key = (line is not None, frac)
            if best is None or key > best[0]:
                best = (key, vert, img, prob, pts, line, frac, sx, sy)
        _, vert, img, prob, pts, line, frac, sx, sy = best

        def to_orig(x, y):
            """작업 좌표(축소·전치) → 원본 픽셀."""
            ox, oy = x / sx, y / sy
            return (int(round(oy)), int(round(ox))) if vert else (int(round(ox)), int(round(oy)))

        vis = cv2.cvtColor(img0, cv2.COLOR_RGB2BGR)
        heat_w = prob.T if vert else prob
        heat = cv2.applyColorMap((cv2.resize(heat_w, (img0.shape[1], img0.shape[0])) * 255).astype(np.uint8), cv2.COLORMAP_JET)
        vis = cv2.addWeighted(vis, 0.7, heat, 0.3, 0)
        for x, y in pts:
            cv2.circle(vis, to_orig(x, y), 1, (0, 255, 255), -1)
        if line:
            a_, b_ = line
            W = img.shape[1]
            cv2.line(vis, to_orig(0, b_), to_orig(W - 1, a_ * (W - 1) + b_), (255, 0, 255), 2)
        txt = f"{'세로' if vert else '가로'} cols={frac:.0%} " + ("line OK" if line else "NO LINE")
        cv2.putText(vis, f"{'V' if vert else 'H'} cols={frac:.0%} " + ("line OK" if line else "NO LINE"), (10, 30),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.9, (0, 255, 0) if line else (0, 0, 255), 2)
        out = os.path.join(a.out, os.path.splitext(os.path.basename(p))[0] + "_heat.jpg")
        cv2.imwrite(out, vis)
        print(f"PRED {os.path.basename(p)} {txt} -> {out}", flush=True)


def cmd_export(a):
    model, cfg = load_model(a.weights, torch.device("cpu"))
    H, W = 352, cfg.get("width", 640) // 32 * 32
    dummy = torch.rand(1, 3, H, W)
    os.makedirs(os.path.dirname(os.path.abspath(a.out)), exist_ok=True)
    torch.onnx.export(model, dummy, a.out, opset_version=a.opset, input_names=["images"], output_names=["logits"],
                      dynamic_axes={"images": {2: "h", 3: "w"}, "logits": {2: "h", 3: "w"}}, dynamo=False)
    # 앱이 학습 배율을 맞추도록 메타데이터를 남긴다(train_width = 학습 때 전체 영상을 줄인 가로 해상도).
    try:
        import onnx
        m = onnx.load(a.out)
        for k, v in (("kind", "weld_heatmap"), ("train_width", str(cfg.get("width", 640))),
                     ("modality", str(cfg.get("modality", ""))), ("sigma", str(cfg.get("sigma", "")))):
            e = m.metadata_props.add()
            e.key, e.value = k, v
        onnx.save(m, a.out)
    except ImportError:
        print("WARN onnx 패키지가 없어 메타데이터(train_width)를 못 남겼습니다 — 앱은 640 으로 가정합니다.", flush=True)
    try:
        import onnxruntime as ort
        o = ort.InferenceSession(a.out, providers=["CPUExecutionProvider"]).run(None, {"images": dummy.numpy()})[0]
        with torch.no_grad():
            ref = model(dummy).numpy()
        print(f"EXPORT_CHECK max|onnx-torch|={np.abs(o - ref).max():.2e}", flush=True)
    except ImportError:
        pass
    epoch = torch.load(a.weights, map_location="cpu", weights_only=False).get("epoch")
    print(f"EXPORT_DONE {a.out} (입력 [1,3,H,W] 0~1 RGB, H·W 32배수 · 출력 [1,1,H,W] logit · epoch {epoch})", flush=True)


def main():
    ap = argparse.ArgumentParser(description="용접선 중심선 히트맵 학습")
    sub = ap.add_subparsers(dest="cmd", required=True)

    def common_data(p):
        p.add_argument("--captures", nargs="+", required=True, help="캡처 폴더(여러 개 가능)")
        p.add_argument("--session-gap-min", dest="session_gap_min", type=float, default=10)
        p.add_argument("--val-frac", dest="val_frac", type=float, default=0.2)
        p.add_argument("--seed", type=int, default=0)

    t = sub.add_parser("train")
    common_data(t)
    t.add_argument("--modality", choices=["rgb", "ir"], default="rgb")
    t.add_argument("--out", required=True)
    t.add_argument("--epochs", type=int, default=150)
    t.add_argument("--batch", type=int, default=8)
    t.add_argument("--repeats", type=int, default=4, help="에폭당 각 영상을 증강해 몇 번 쓸지")
    t.add_argument("--width", type=int, default=640, help="영상 긴 변을 이 크기로 줄여 학습(앱 추론도 같은 배율)")
    t.add_argument("--crop-h", dest="crop_h", type=int, default=320, help="학습 크롭 높이(32 배수)")
    t.add_argument("--crop-w", dest="crop_w", type=int, default=512, help="학습 크롭 너비(32 배수)")
    t.add_argument("--sigma", type=float, default=2.5, help="중심선 가우시안 폭 [px, 학습 해상도]")
    t.add_argument("--lr", type=float, default=1e-3)
    t.add_argument("--pos-weight", dest="pos_weight", type=float, default=20.0)
    t.add_argument("--thr", type=float, default=0.3)
    t.add_argument("--patience", type=int, default=40)
    t.add_argument("--device", default="auto", help="auto | cuda | mps | cpu")
    t.add_argument("--no-pretrained", dest="no_pretrained", action="store_true")
    t.add_argument("--init", help="이어 학습(보강): 이 체크포인트(.pt) 가중치에서 시작 — 기존+새 라벨 전체로 학습 권장")
    t.add_argument("--finetune-lr-scale", dest="finetune_lr_scale", type=float, default=0.3,
                   help="--init 일 때 학습률 배율(기존에 배운 것을 크게 흔들지 않게)")
    t.add_argument("--keep-rotated", dest="keep_rotated", action="store_true",
                   help="회전 증강본(_cw90/_ccw90)도 학습에 포함(기본은 원본이 있으면 제외)")
    t.set_defaults(fn=cmd_train)

    e = sub.add_parser("eval")
    common_data(e)
    e.add_argument("--weights", required=True)
    e.add_argument("--modality", choices=["rgb", "ir"])
    e.add_argument("--split", choices=["val", "all"], default="all")
    e.add_argument("--thr", type=float, default=0.3)
    e.add_argument("--device", default="auto")
    e.set_defaults(fn=cmd_eval)

    p = sub.add_parser("predict")
    p.add_argument("--weights", required=True)
    p.add_argument("--images", nargs="+", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--thr", type=float, default=0.3)
    p.add_argument("--vertical", action="store_true", help="세로 용접선으로 고정(기본: 가로·세로 자동)")
    p.add_argument("--horizontal", action="store_true", help="가로 용접선으로 고정(기본: 가로·세로 자동)")
    p.add_argument("--device", default="auto")
    p.set_defaults(fn=cmd_predict)

    x = sub.add_parser("export")
    x.add_argument("--weights", required=True)
    x.add_argument("--out", required=True)
    x.add_argument("--opset", type=int, default=17)
    x.set_defaults(fn=cmd_export)

    a = ap.parse_args()
    a.fn(a)


if __name__ == "__main__":
    main()
