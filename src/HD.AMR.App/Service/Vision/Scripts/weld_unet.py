"""
용접선 U-Net 세그멘테이션 학습/내보내기 (CPU, PyTorch 만 필요 — ultralytics 설치 시 함께 들어옴).

  python weld_unet.py train  --data <dataset> --out <runs/unet> [--epochs 100 --batch 8 --crop 384 --patience 30 --base 16]
  python weld_unet.py export --weights <runs/unet/best.pt> --out <models/weld_unet_ir.onnx> [--opset 17]

데이터셋 구조(앱의 '데이터셋 변환'이 만든다):
  <dataset>/images/{train,val}/*.png   — 원본(그레이로 읽음)
  <dataset>/masks/{train,val}/*.png    — 같은 파일명의 이진 마스크(흰색=비드)

설계 메모
- 입력 1채널 그레이, 출력 1채널 logit. 완전 합성곱이라 학습은 크롭(crop×crop), 추론은 전체/ROI 를 32 배수로
  패딩해 그대로 넣는다. ONNX 는 H/W 동적 축으로 내보낸다.
- 가는 선은 전체 픽셀의 1~2% 라서, 크롭의 70% 는 비드를 포함하도록 뽑고(positive 편향) 손실은 BCE + Dice.
- 증강: 뒤집기, ±8° 회전, 밝기/대비/감마, 가우시안 잡음·블러. mosaic·잘라붙이기는 하지 않는다(선이 끊김).
- 에폭마다 "Epoch k/N ..." 한 줄을 출력한다(앱이 k/N 을 파싱해 진행 바를 갱신).
"""
import argparse
import glob
import math
import os
import random
import sys
import time

import cv2
import numpy as np
import torch
import torch.nn as nn
import torch.nn.functional as F


# ── 모델 ──────────────────────────────────────────────────────────────────
class Block(nn.Module):
    def __init__(self, cin, cout):
        super().__init__()
        self.net = nn.Sequential(
            nn.Conv2d(cin, cout, 3, padding=1, bias=False), nn.BatchNorm2d(cout), nn.ReLU(inplace=True),
            nn.Conv2d(cout, cout, 3, padding=1, bias=False), nn.BatchNorm2d(cout), nn.ReLU(inplace=True),
        )

    def forward(self, x):
        return self.net(x)


class UNet(nn.Module):
    """경량 U-Net. base=16, depth=4 → 약 1.9M 파라미터(CPU 실시간 추론 가능 수준)."""

    def __init__(self, base=16, depth=4):
        super().__init__()
        chs = [base * 2 ** i for i in range(depth + 1)]
        self.downs = nn.ModuleList()
        cin = 1
        for c in chs[:-1]:
            self.downs.append(Block(cin, c))
            cin = c
        self.bottleneck = Block(chs[-2], chs[-1])
        self.ups = nn.ModuleList()
        self.upblocks = nn.ModuleList()
        for i in range(depth, 0, -1):
            self.ups.append(nn.ConvTranspose2d(chs[i], chs[i - 1], 2, stride=2))
            self.upblocks.append(Block(chs[i - 1] * 2, chs[i - 1]))
        self.head = nn.Conv2d(chs[0], 1, 1)

    def forward(self, x):
        skips = []
        for d in self.downs:
            x = d(x)
            skips.append(x)
            x = F.max_pool2d(x, 2)
        x = self.bottleneck(x)
        for up, blk, skip in zip(self.ups, self.upblocks, reversed(skips)):
            x = up(x)
            x = blk(torch.cat([x, skip], dim=1))
        return self.head(x)


# ── 데이터 ────────────────────────────────────────────────────────────────
def list_pairs(data, split):
    imgs = sorted(glob.glob(os.path.join(data, "images", split, "*.png")))
    pairs = []
    for p in imgs:
        m = os.path.join(data, "masks", split, os.path.basename(p))
        if os.path.exists(m):
            pairs.append((p, m))
    return pairs


def imread_gray(path):
    # 한글 경로 안전(np.fromfile → imdecode).
    buf = np.fromfile(path, dtype=np.uint8)
    return cv2.imdecode(buf, cv2.IMREAD_GRAYSCALE)


def load_pairs(pairs):
    out = []
    for ip, mp in pairs:
        img = imread_gray(ip)
        msk = imread_gray(mp)
        if img is None or msk is None:
            continue
        if msk.shape != img.shape:
            msk = cv2.resize(msk, (img.shape[1], img.shape[0]), interpolation=cv2.INTER_NEAREST)
        out.append((img, (msk > 127).astype(np.uint8)))
    return out


def pad_to(img, h, w, mode=cv2.BORDER_REFLECT_101):
    ph, pw = max(0, h - img.shape[0]), max(0, w - img.shape[1])
    if ph == 0 and pw == 0:
        return img
    return cv2.copyMakeBorder(img, 0, ph, 0, pw, mode)


def augment(img, msk, rng):
    if rng.random() < 0.5:
        img, msk = img[:, ::-1], msk[:, ::-1]
    if rng.random() < 0.5:
        img, msk = img[::-1, :], msk[::-1, :]
    img, msk = np.ascontiguousarray(img), np.ascontiguousarray(msk)
    if rng.random() < 0.5:
        h, w = img.shape
        M = cv2.getRotationMatrix2D((w / 2, h / 2), rng.uniform(-8, 8), 1.0)
        img = cv2.warpAffine(img, M, (w, h), flags=cv2.INTER_LINEAR, borderMode=cv2.BORDER_REFLECT_101)
        msk = cv2.warpAffine(msk, M, (w, h), flags=cv2.INTER_NEAREST, borderMode=cv2.BORDER_REFLECT_101)
    f = img.astype(np.float32)
    f = f * rng.uniform(0.7, 1.3) + rng.uniform(-25, 25)          # 대비/밝기
    f = np.clip(f, 0, 255)
    g = rng.uniform(0.75, 1.33)                                    # 감마
    f = 255.0 * (f / 255.0) ** g
    if rng.random() < 0.3:
        f = cv2.GaussianBlur(f, (3, 3), rng.uniform(0.3, 1.0))
    if rng.random() < 0.5:
        f = f + np.random.normal(0, rng.uniform(1, 6), f.shape).astype(np.float32)
    return np.clip(f, 0, 255).astype(np.uint8), msk


def random_crop(img, msk, crop, rng, pos_frac=0.7):
    img = pad_to(img, crop, crop)
    msk = pad_to(msk, crop, crop, cv2.BORDER_CONSTANT)
    h, w = img.shape
    ys, xs = np.nonzero(msk)
    if len(ys) > 0 and rng.random() < pos_frac:
        k = rng.randrange(len(ys))
        cy, cx = ys[k], xs[k]
        y0 = int(np.clip(cy - rng.randrange(crop), 0, h - crop))
        x0 = int(np.clip(cx - rng.randrange(crop), 0, w - crop))
    else:
        y0, x0 = rng.randrange(h - crop + 1), rng.randrange(w - crop + 1)
    return img[y0:y0 + crop, x0:x0 + crop], msk[y0:y0 + crop, x0:x0 + crop]


def make_batch(samples, batch, crop, rng):
    xs, ys = [], []
    for _ in range(batch):
        img, msk = samples[rng.randrange(len(samples))]
        img, msk = augment(img, msk, rng)
        img, msk = random_crop(img, msk, crop, rng)
        xs.append(img)
        ys.append(msk)
    x = torch.from_numpy(np.stack(xs)[:, None].astype(np.float32) / 255.0)
    y = torch.from_numpy(np.stack(ys)[:, None].astype(np.float32))
    return x, y


def dice_loss(logits, y, eps=1.0):
    p = torch.sigmoid(logits)
    inter = (p * y).sum(dim=(1, 2, 3))
    den = p.sum(dim=(1, 2, 3)) + y.sum(dim=(1, 2, 3))
    return (1 - (2 * inter + eps) / (den + eps)).mean()


@torch.no_grad()
def evaluate(model, samples):
    model.eval()
    inter = union = psum = tsum = 0.0
    for img, msk in samples:
        h, w = img.shape
        H, W = int(math.ceil(h / 32) * 32), int(math.ceil(w / 32) * 32)
        x = torch.from_numpy(pad_to(img, H, W)[None, None].astype(np.float32) / 255.0)
        pred = (torch.sigmoid(model(x))[0, 0, :h, :w].numpy() > 0.5)
        t = msk.astype(bool)
        inter += np.logical_and(pred, t).sum()
        union += np.logical_or(pred, t).sum()
        psum += pred.sum()
        tsum += t.sum()
    model.train()
    dice = (2 * inter) / (psum + tsum) if (psum + tsum) > 0 else 1.0
    iou = inter / union if union > 0 else 1.0
    return float(dice), float(iou)


def cmd_train(a):
    torch.manual_seed(a.seed)
    rng = random.Random(a.seed)
    np.random.seed(a.seed)
    torch.set_num_threads(max(1, os.cpu_count() or 1))

    train = load_pairs(list_pairs(a.data, "train"))
    val = load_pairs(list_pairs(a.data, "val"))
    if not train:
        print("ERROR 학습 이미지/마스크가 없습니다:", os.path.join(a.data, "masks", "train"), flush=True)
        sys.exit(2)
    if not val:
        print("WARN 검증셋(val)이 비어 train 으로 평가합니다(과적합 확인만 가능).", flush=True)
        val = train
    print(f"DATA train={len(train)} val={len(val)} crop={a.crop} batch={a.batch} base={a.base}", flush=True)

    os.makedirs(a.out, exist_ok=True)
    model = UNet(base=a.base, depth=4)
    n_params = sum(p.numel() for p in model.parameters())
    print(f"MODEL UNet base={a.base} params={n_params / 1e6:.2f}M", flush=True)
    opt = torch.optim.AdamW(model.parameters(), lr=a.lr, weight_decay=1e-4)
    steps = max(10, math.ceil(len(train) * a.crops_per_image / a.batch))
    sched = torch.optim.lr_scheduler.CosineAnnealingLR(opt, T_max=a.epochs * steps)
    # 양성(비드) 픽셀이 매우 적어 BCE 가 배경으로 쏠리지 않게 가중.
    pos_weight = torch.tensor([a.pos_weight])

    best, best_ep, t0 = -1.0, 0, time.time()
    for ep in range(1, a.epochs + 1):
        model.train()
        tot = 0.0
        for _ in range(steps):
            x, y = make_batch(train, a.batch, a.crop, rng)
            logits = model(x)
            loss = F.binary_cross_entropy_with_logits(logits, y, pos_weight=pos_weight) + dice_loss(logits, y)
            opt.zero_grad()
            loss.backward()
            opt.step()
            sched.step()
            tot += loss.item()
        dice, iou = evaluate(model, val)
        cfg = {"base": a.base, "depth": 4}
        torch.save({"model": model.state_dict(), "cfg": cfg, "epoch": ep, "val_dice": dice}, os.path.join(a.out, "last.pt"))
        mark = ""
        if dice > best:
            best, best_ep = dice, ep
            torch.save({"model": model.state_dict(), "cfg": cfg, "epoch": ep, "val_dice": dice}, os.path.join(a.out, "best.pt"))
            mark = " *"
        print(f"Epoch {ep}/{a.epochs} loss={tot / steps:.4f} val_dice={dice:.4f} val_iou={iou:.4f} "
              f"best={best:.4f}@{best_ep} lr={sched.get_last_lr()[0]:.2e} t={time.time() - t0:.0f}s{mark}", flush=True)
        if a.patience > 0 and ep - best_ep >= a.patience:
            print(f"EARLY_STOP {a.patience} epoch 동안 val_dice 개선 없음", flush=True)
            break
    print(f"TRAIN_DONE best_dice={best:.4f} epoch={best_ep} -> {os.path.join(a.out, 'best.pt')}", flush=True)


def cmd_export(a):
    ck = torch.load(a.weights, map_location="cpu")
    cfg = ck.get("cfg", {"base": 16, "depth": 4})
    model = UNet(base=cfg["base"], depth=cfg["depth"])
    model.load_state_dict(ck["model"])
    model.eval()
    dummy = torch.zeros(1, 1, 480, 864)
    os.makedirs(os.path.dirname(os.path.abspath(a.out)), exist_ok=True)
    kw = dict(input_names=["image"], output_names=["logits"], opset_version=a.opset,
              dynamic_axes={"image": {0: "n", 2: "h", 3: "w"}, "logits": {0: "n", 2: "h", 3: "w"}})
    try:
        torch.onnx.export(model, dummy, a.out, dynamo=False, **kw)   # torch ≥ 2.5: 기존(TorchScript) 내보내기 명시
    except TypeError:
        torch.onnx.export(model, dummy, a.out, **kw)
    print(f"EXPORT_DONE {a.out} (epoch {ck.get('epoch')}, val_dice {ck.get('val_dice')})", flush=True)


def main():
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="cmd", required=True)
    t = sub.add_parser("train")
    t.add_argument("--data", required=True)
    t.add_argument("--out", required=True)
    t.add_argument("--epochs", type=int, default=100)
    t.add_argument("--batch", type=int, default=8)
    t.add_argument("--crop", type=int, default=384)
    t.add_argument("--patience", type=int, default=30)
    t.add_argument("--base", type=int, default=16)
    t.add_argument("--lr", type=float, default=1e-3)
    t.add_argument("--pos-weight", dest="pos_weight", type=float, default=5.0)
    t.add_argument("--crops-per-image", dest="crops_per_image", type=int, default=4)
    t.add_argument("--seed", type=int, default=0)
    e = sub.add_parser("export")
    e.add_argument("--weights", required=True)
    e.add_argument("--out", required=True)
    e.add_argument("--opset", type=int, default=17)
    a = ap.parse_args()
    if a.cmd == "train" and a.crop % 16 != 0:
        a.crop = int(math.ceil(a.crop / 16) * 16)   # depth 4 풀링 → 16 배수 필요
    {"train": cmd_train, "export": cmd_export}[a.cmd](a)


if __name__ == "__main__":
    main()
