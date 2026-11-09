using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service.Vision;

/// <summary>
/// 학습 산출물(weld_yolo*.onnx, YOLOv8-seg)을 CPU EP 로 네이티브 추론해 비드 마스크를 얻는 서비스.
/// Windows 는 OpenCvSharp 로 전처리·후처리하고, 그 외(macOS 등)는 같은 판정 로직을 관리 코드로 수행한다.
/// 캡처 이미지 파일에 대해 추론하고, 결과를 반투명 오버레이 JPEG(LastOverlay)로 만들어 UI 가 표시한다.
/// InferenceSession 은 모델 경로+수정시각 기준으로 캐시하며, 단일 운영자 전제로 추론을 직렬화한다.
/// </summary>
public sealed class OnnxBeadSegmentationService
{
    private readonly WeldTrainingService _train;
    private readonly ILogger<OnnxBeadSegmentationService> _log;
    private readonly object _gate = new();
    private (string Path, DateTime Mtime, InferenceSession Session)? _cache;

    public byte[]? LastOverlay { get; private set; }
    /// <summary>마지막 추론의 이진 마스크(원본 해상도 W×H, 비드=255). 추론·검증 화면의 용접선 추출용.</summary>
    public byte[]? LastMask { get; private set; }
    /// <summary>마지막 추론에 CLAHE 전처리를 걸었는지(모델 이름 _clahe).</summary>
    public bool LastClahe { get; private set; }

    public OnnxBeadSegmentationService(WeldTrainingService train, ILogger<OnnxBeadSegmentationService> log)
    {
        _train = train;
        _log = log;
    }

    /// <summary>라벨링 폴더의 {stem}_{modality}.png 에 대해 추론. 오버레이는 LastOverlay 에 저장.</summary>
    public async Task<InferResult> RunOnCaptureAsync(
        string modelPath, string stem, string modality, float conf, float maskThr, string? dir = null)
    {
        modality = modality == "ir" ? "ir" : "rgb";
        // dir 생략 = 라벨링 폴더. ⑤ 목록의 학습·검증 폴더 이미지는 그 폴더를 넘긴다.
        dir ??= await _train.GetLabelDirAsync()
                ?? throw new InvalidOperationException("라벨링 폴더가 설정되지 않았습니다.");
        var imgPath = Path.Combine(dir, $"{stem}_{modality}.png");
        if (!File.Exists(imgPath)) throw new FileNotFoundException("이미지가 없습니다.", imgPath);
        // 바이트로 읽어 공통 경로로(한글 경로 안전 · CLAHE 전처리도 한곳에서).
        return await RunOnBytesAsync(modelPath, await File.ReadAllBytesAsync(imgPath), conf, maskThr);
    }

    /// <summary>업로드/외부 이미지 바이트에 대해 추론. 캡처 폴더 밖의 검증용 이미지에 사용.</summary>
    public async Task<InferResult> RunOnBytesAsync(string modelPath, byte[] imageBytes, float conf, float maskThr)
    {
        if (!File.Exists(modelPath)) throw new FileNotFoundException("모델(.onnx)이 없습니다.", modelPath);
        if (imageBytes is null || imageBytes.Length == 0) throw new ArgumentException("빈 이미지입니다.", nameof(imageBytes));
        // CLAHE 로 학습한 모델(이름에 _clahe)이면 학습 데이터셋과 같은 전처리를 전체 이미지에 먼저 건다.
        LastClahe = WeldModelNames.HasClahe(modelPath);
        if (LastClahe) imageBytes = await Task.Run(() => ImageClahe.ApplyEncoded(imageBytes));
        if (IsHeatmapModel(modelPath))
            return await Task.Run(() => InferHeatmap(modelPath, imageBytes, maskThr));
        if (IsUnet(modelPath))
            return await Task.Run(() => InferUnet(modelPath, imageBytes, maskThr));
        if (!OperatingSystem.IsWindows())
            return await Task.Run(() => InferManaged(modelPath, imageBytes, conf, maskThr));
        return await Task.Run(() =>
        {
            using var bgr = Cv2.ImDecode(imageBytes, ImreadModes.Color);
            if (bgr.Empty()) throw new InvalidOperationException("이미지를 디코드하지 못했습니다(형식 확인).");
            return Infer(modelPath, bgr, conf, maskThr);
        });
    }

    // 실제 추론 본체(캡처/바이트 공통). 세션은 캐시하며 단일 운영자 전제로 직렬화한다.
    private InferResult Infer(string modelPath, Mat bgr, float conf, float maskThr)
    {
        lock (_gate)
        {
            var session = GetSession(modelPath);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (mask, count, maxScore) = YoloSeg.Decode(session, bgr, conf, 0.5f, maskThr);
            sw.Stop();
            using (mask)
            {
                LastOverlay = BuildOverlay(bgr, mask);
                // 마스크(CV_8UC1, 원본 해상도) → 바이트. Clone 은 항상 연속 메모리.
                var mb = new byte[bgr.Width * bgr.Height];
                using (var m8 = mask.Clone())
                    System.Runtime.InteropServices.Marshal.Copy(m8.Data, mb, 0, mb.Length);
                LastMask = mb;
                double covered = count > 0 ? Cv2.CountNonZero(mask) / (double)(bgr.Width * bgr.Height) : 0;
                return new InferResult(count, maxScore, covered, sw.ElapsedMilliseconds, bgr.Width, bgr.Height);
            }
        }
    }

    // macOS 등: OpenCvSharp 네이티브(win-x64 전용) 없이 ImageSharp + 관리 코드로 같은 추론(YoloSegManaged).
    private InferResult InferManaged(string modelPath, byte[] imageBytes, float conf, float maskThr)
    {
        SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24> img;
        try { img = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgb24>(imageBytes); }
        catch (Exception ex) { throw new InvalidOperationException("이미지를 디코드하지 못했습니다(형식 확인).", ex); }
        using (img)
        lock (_gate)
        {
            var session = GetSession(modelPath);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (mask, count, maxScore) = YoloSegManaged.Decode(session, img, conf, 0.5f, maskThr);
            sw.Stop();
            LastOverlay = YoloSegManaged.BuildOverlay(img, mask);
            LastMask = mask;
            int on = 0;
            foreach (var b in mask) if (b != 0) on++;
            double covered = count > 0 ? on / (double)(img.Width * img.Height) : 0;
            return new InferResult(count, maxScore, covered, sw.ElapsedMilliseconds, img.Width, img.Height);
        }
    }

    /// <summary>모델이 중심선 히트맵(weld_heatmap, 입력 3채널·출력 1개 [1,1,H,W])인지.</summary>
    private bool IsHeatmapModel(string modelPath)
    {
        lock (_gate) return HeatmapSegmenter.LooksLikeHeatmap(GetSession(modelPath));
    }

    /// <summary>
    /// 히트맵 판정: 맞춘 직선의 inlier 슬라이스가 진행 슬라이스의 이 비율 이상이면 "검출 1건".
    /// 대각선은 화면 일부 슬라이스에만 걸치고 코로게이션 위에서 끊기므로 낮게 둔다.
    /// </summary>
    internal const double HeatmapFoundFraction = 0.1;

    /// <summary>직선 inlier 허용 거리 [학습 해상도 px] — 학습 스크립트 decode_line 의 ransac_px 와 같다.</summary>
    internal const double HeatmapInlierTrainPx = 3.0;

    /// <summary>
    /// 중심선 히트맵 모델 추론(OpenCV 불필요 — macOS 포함). 진행축을 모르므로 가로·세로(전치) 둘 다 풀고, 슬라이스마다 찾은
    /// 중심 점들에 RANSAC 직선(기울기 ≤ <see cref="HeatmapSegmenter.MaxLineAngleDeg"/>, 대각선 포함)을 맞춰 inlier 가 많은 쪽을 쓴다.
    /// 결과: Count=1/0(선 검출 여부), MaxScore=최대 확률, Coverage=직선 inlier 슬라이스 비율.
    /// 오버레이는 맞춘 직선(잡음 조각 제외, 끊긴 구간은 이어서)을 칠한다. 직선을 못 맞추면 원래 띠 마스크를 보여 준다.
    /// </summary>
    private InferResult InferHeatmap(string modelPath, byte[] imageBytes, float threshold)
    {
        SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24> img;
        try { img = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgb24>(imageBytes); }
        catch (Exception ex) { throw new InvalidOperationException("이미지를 디코드하지 못했습니다(형식 확인).", ex); }
        using (img)
        lock (_gate)
        {
            int w = img.Width, h = img.Height;
            var rgb = new byte[w * h * 3];
            img.CopyPixelDataTo(rgb);
            var session = GetSession(modelPath);
            float thr = Math.Clamp(threshold, 0.05f, 0.95f);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var probH = new float[w * h];
            var mH = HeatmapSegmenter.RidgeMask(session, rgb, w, h, Math.Max(w, h), horizontal: true, thr, prob: probH);
            var probV = new float[w * h];
            var mV = HeatmapSegmenter.RidgeMask(session, rgb, w, h, Math.Max(w, h), horizontal: false, thr, prob: probV);
            double inlierPx = Math.Max(2.0, HeatmapInlierTrainPx * Math.Max(w, h) / HeatmapSegmenter.TrainWidth(session));
            var lH = HeatmapSegmenter.FitLine(HeatmapSegmenter.RidgePoints(mH, w, h, true), inlierPx, w);
            var lV = HeatmapSegmenter.FitLine(HeatmapSegmenter.RidgePoints(mV, w, h, false), inlierPx, h);
            sw.Stop();
            double fH = (lH?.Inliers ?? 0) / (double)Math.Max(1, w), fV = (lV?.Inliers ?? 0) / (double)Math.Max(1, h);
            bool horiz = lH is null && lV is null
                ? SliceFraction(mH, w, h, true) >= SliceFraction(mV, w, h, false)
                : (lH?.Inliers ?? 0) >= (lV?.Inliers ?? 0);
            var line = horiz ? lH : lV;
            var prob = horiz ? probH : probV;
            double frac = horiz ? fH : fV;
            LastMask = horiz ? mH : mV;   // 추론·검증의 용접선 추출용(띠 마스크)
            LastOverlay = YoloSegManaged.BuildOverlay(img,
                line is { } l ? HeatmapSegmenter.LineMask(w, h, horiz, l) : horiz ? mH : mV);
            float max = 0;
            foreach (var v in prob) if (v > max) max = v;
            double angle = line is { } la ? Math.Atan(la.A) * 180 / Math.PI : double.NaN;
            _log.LogInformation("히트맵 추론 {Model}: {Dir} 진행, 직선 {Angle:0.0}° inlier {In}/{Pts} 슬라이스 ({Frac:P0}, 가로 {FH:P0} / 세로 {FV:P0}), 최대 확률 {Max:0.00}, {Ms}ms",
                Path.GetFileName(modelPath), horiz ? "가로" : "세로", angle, line?.Inliers ?? 0, line?.Points ?? 0,
                frac, fH, fV, max, sw.ElapsedMilliseconds);
            return new InferResult(line is not null && frac >= HeatmapFoundFraction ? 1 : 0, max, frac, sw.ElapsedMilliseconds, w, h);
        }
    }

    /// <summary>진행 슬라이스(가로면 열, 세로면 행) 중 마스크가 있는 비율.</summary>
    internal static double SliceFraction(byte[] mask, int w, int h, bool horizontal)
    {
        int n = horizontal ? w : h, c = horizontal ? h : w, hit = 0;
        for (int s = 0; s < n; s++)
            for (int k = 0; k < c; k++)
                if (mask[horizontal ? k * w + s : s * w + k] != 0) { hit++; break; }
        return n == 0 ? 0 : (double)hit / n;
    }

    private bool IsUnet(string modelPath)
    {
        lock (_gate) return UnetSegmenter.LooksLikeUnet(GetSession(modelPath));
    }

    /// <summary>
    /// U-Net(입력 1채널 그레이) 추론 — 운영 U-Net 검출(UnetWeldVisionDetector)과 같은 전처리:
    /// BGR→GRAY(0.299R+0.587G+0.114B), 확률 임계 <paramref name="thr"/>. OpenCV 없이 동작(Windows·macOS 공통).
    /// 검출 수는 비드 픽셀이 있으면 1, 점수는 최대 확률.
    /// </summary>
    private InferResult InferUnet(string modelPath, byte[] imageBytes, float thr)
    {
        SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24> img;
        try { img = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgb24>(imageBytes); }
        catch (Exception ex) { throw new InvalidOperationException("이미지를 디코드하지 못했습니다(형식 확인).", ex); }
        using (img)
        lock (_gate)
        {
            int w = img.Width, h = img.Height;
            var rgb = new byte[w * h * 3];
            img.CopyPixelDataTo(rgb);
            var gray = new byte[w * h];
            for (int i = 0; i < gray.Length; i++)
                gray[i] = (byte)Math.Clamp((int)Math.Round(0.299 * rgb[i * 3] + 0.587 * rgb[i * 3 + 1] + 0.114 * rgb[i * 3 + 2]), 0, 255);
            var prob = new float[w * h];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var mask = UnetSegmenter.Segment(GetSession(modelPath), gray, w, h, thr, prob);
            sw.Stop();
            LastOverlay = YoloSegManaged.BuildOverlay(img, mask);
            LastMask = mask;
            int on = 0; float max = 0;
            for (int i = 0; i < mask.Length; i++) { if (mask[i] != 0) on++; if (prob[i] > max) max = prob[i]; }
            return new InferResult(on > 0 ? 1 : 0, max, on / (double)(w * h), sw.ElapsedMilliseconds, w, h);
        }
    }

    private InferenceSession GetSession(string modelPath)
    {
        var mtime = File.GetLastWriteTimeUtc(modelPath);
        if (_cache is { } c && c.Path == modelPath && c.Mtime == mtime) return c.Session;
        _cache?.Session.Dispose();
        var opts = new Microsoft.ML.OnnxRuntime.SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };
        var sess = new InferenceSession(modelPath, opts);
        _cache = (modelPath, mtime, sess);
        _log.LogInformation("ONNX 세션 로드: {Path}", modelPath);
        return sess;
    }

    // 원본 위에 비드 마스크를 반투명 채우기 + 외곽선으로 그린 JPEG.
    private static byte[] BuildOverlay(Mat bgr, Mat mask)
    {
        using var overlay = bgr.Clone();
        if (Cv2.CountNonZero(mask) > 0)
        {
            using var color = new Mat(overlay.Size(), MatType.CV_8UC3, new Scalar(0, 0, 255)); // 빨강(BGR)
            using var blended = new Mat();
            Cv2.AddWeighted(overlay, 0.55, color, 0.45, 0, blended);
            blended.CopyTo(overlay, mask); // 마스크 영역만 블렌딩 결과로 대체
            Cv2.FindContours(mask, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
            Cv2.DrawContours(overlay, contours, -1, new Scalar(0, 255, 255), 2); // 노랑 외곽선
        }
        Cv2.ImEncode(".jpg", overlay, out var buf, new[] { (int)ImwriteFlags.JpegQuality, 85 });
        return buf;
    }
}

/// <summary>추론 결과 요약.</summary>
public sealed record InferResult(int Count, float MaxScore, double Coverage, long Millis, int Width, int Height);

/// <summary>YOLOv8-seg 검출 후보 하나(입력 해상도 좌표 박스 + proto 계수).</summary>
internal sealed record SegCand(float Score, float X1, float Y1, float X2, float Y2, float[] Coeff);

/// <summary>
/// YOLOv8-seg ONNX 출력 디코더(단일 클래스 비드). letterbox 전처리 → NMS → proto 마스크 합성.
/// 출력 해석·NMS·proto 계수 합성은 픽셀 연산이 없는 공용 코드(<see cref="InputSize"/>/<see cref="Parse"/>/
/// <see cref="ProtoProb"/>)이고, 크기 조정·마스크 합성 같은 픽셀 연산만 OpenCV(Windows, <see cref="Decode"/>)와
/// 관리 코드(macOS 등, <see cref="YoloSegManaged"/>)로 나뉜다 — 두 경로의 판정 로직이 어긋나지 않게.
/// </summary>
internal static class YoloSeg
{
    /// <summary>
    /// 입력 크기(Sh×Sw)를 모델 입력 메타데이터에서 자동으로 읽는다 → export imgsz 와 자동 일치.
    /// 정사각(864×864)과 직사각(512×864) 내보내기 모두 지원. 동적 차원(-1) 등으로 못 읽으면 640 폴백.
    /// </summary>
    public static (string InName, int Sh, int Sw) InputSize(InferenceSession sess)
    {
        var inName = sess.InputMetadata.Keys.First();
        int Sh = 640, Sw = 640;
        var inDims = sess.InputMetadata[inName].Dimensions;
        if (inDims is { Length: 4 } && inDims[2] > 0 && inDims[3] > 0) { Sh = inDims[2]; Sw = inDims[3]; }
        return (inName, Sh, Sw);
    }

    /// <summary>
    /// 출력 해석 + NMS. rank3 = 검출[1,ch,na], rank4 = proto[1,pc,ph,pw]. 남은 후보는 점수 내림차순.
    /// 출력이 없거나 후보가 없으면 null.
    /// </summary>
    public static (List<SegCand> Kept, float[] Proto, int Pc, int Ph, int Pw)? Parse(
        IEnumerable<NamedOnnxValue> results, float conf, float iou)
    {
        Tensor<float>? det = null, proto = null;
        foreach (var v in results)
        {
            var t = v.AsTensor<float>();
            if (t.Dimensions.Length == 3) det = t;
            else if (t.Dimensions.Length == 4) proto = t;
        }
        if (det is null || proto is null) return null;

        int ch = det.Dimensions[1], na = det.Dimensions[2];
        int pc = proto.Dimensions[1], ph = proto.Dimensions[2], pw = proto.Dimensions[3];
        int nc = ch - 4 - pc;              // 클래스 수(비드=1)
        if (nc < 1) return null;

        var d = det.ToArray();             // [ch*na] row-major: c*na + a

        // 후보 수집.
        var cand = new List<SegCand>();
        for (int a = 0; a < na; a++)
        {
            // 최고 클래스 점수(비드 단일이라 사실상 인덱스 4).
            float best = 0;
            for (int cI = 0; cI < nc; cI++)
            {
                float sc = d[(4 + cI) * na + a];
                if (sc > best) best = sc;
            }
            if (best < conf) continue;

            float cx = d[0 * na + a], cy = d[1 * na + a], ww = d[2 * na + a], hh = d[3 * na + a];
            var coeff = new float[pc];
            for (int k = 0; k < pc; k++) coeff[k] = d[(4 + nc + k) * na + a];
            cand.Add(new SegCand(best, cx - ww / 2, cy - hh / 2, cx + ww / 2, cy + hh / 2, coeff));
        }
        if (cand.Count == 0) return null;

        // NMS.
        var order = cand.OrderByDescending(c => c.Score).ToList();
        var kept = new List<SegCand>();
        var removed = new bool[order.Count];
        for (int i = 0; i < order.Count; i++)
        {
            if (removed[i]) continue;
            kept.Add(order[i]);
            for (int j = i + 1; j < order.Count; j++)
            {
                if (removed[j]) continue;
                if (Iou(order[i], order[j]) > iou) removed[j] = true;
            }
        }
        return (kept, proto.ToArray(), pc, ph, pw);
    }

    /// <summary>검출 하나의 proto 마스크 확률(ph×pw, row-major, sigmoid 적용).</summary>
    public static float[] ProtoProb(SegCand c, float[] pr, int pc, int protoArea)
    {
        var mm = new float[protoArea];
        for (int k = 0; k < pc; k++)
        {
            float ck = c.Coeff[k];
            if (ck == 0f) continue;
            int off = k * protoArea;
            for (int i = 0; i < protoArea; i++) mm[i] += ck * pr[off + i];
        }
        for (int i = 0; i < protoArea; i++) mm[i] = Sigmoid(mm[i]);
        return mm;
    }

    /// <summary>OpenCV 경로(Windows). 원본 해상도 이진 마스크(0/255)·검출 수·최고 점수.</summary>
    public static (Mat Mask, int Count, float MaxScore) Decode(
        InferenceSession sess, Mat bgr, float conf, float iou, float maskThr)
    {
        var (inName, Sh, Sw) = InputSize(sess);

        int W = bgr.Width, H = bgr.Height;
        double r = Math.Min((double)Sw / W, (double)Sh / H);
        int newW = (int)Math.Round(W * r), newH = (int)Math.Round(H * r);
        int dx = (Sw - newW) / 2, dy = (Sh - newH) / 2;

        using var resized = new Mat();
        Cv2.Resize(bgr, resized, new Size(newW, newH));
        using var lb = new Mat();
        Cv2.CopyMakeBorder(resized, lb, dy, Sh - newH - dy, dx, Sw - newW - dx,
            BorderTypes.Constant, new Scalar(114, 114, 114));

        // 입력 텐서 [1,3,Sh,Sw] — BGR→RGB, /255. 평면 순서 R,G,B.
        var data = new float[3 * Sh * Sw];
        int plane = Sh * Sw;
        var idx = lb.GetGenericIndexer<Vec3b>();
        for (int y = 0; y < Sh; y++)
            for (int x = 0; x < Sw; x++)
            {
                var p = idx[y, x];
                int o = y * Sw + x;
                data[o] = p.Item2 / 255f;             // R
                data[plane + o] = p.Item1 / 255f;     // G
                data[2 * plane + o] = p.Item0 / 255f; // B
            }
        var input = new DenseTensor<float>(data, new[] { 1, 3, Sh, Sw });

        using var results = sess.Run(new[] { NamedOnnxValue.CreateFromTensor(inName, input) });
        var empty = new Mat(H, W, MatType.CV_8UC1, Scalar.All(0));
        if (Parse(results, conf, iou) is not { } parsed) return (empty, 0, 0f);
        var (kept, pr, pc, ph, pw) = parsed;
        int protoArea = ph * pw;

        float maxScore = kept[0].Score;
        // 검출별 proto 마스크 합성 → 입력 해상도(Sh×Sw) 마스크 누적.
        using var comb = new Mat(Sh, Sw, MatType.CV_8UC1, Scalar.All(0));
        foreach (var c in kept)
        {
            var mm = ProtoProb(c, pr, pc, protoArea);

            using var mMat = new Mat(ph, pw, MatType.CV_32FC1);
            mMat.SetArray(mm);
            using var up = new Mat();
            Cv2.Resize(mMat, up, new Size(Sw, Sh));
            using var bin = new Mat();
            Cv2.Compare(up, new Scalar(maskThr), bin, CmpType.GT); // 8UC1: 255 where prob>thr

            // 박스 영역만 남기고 누적(박스 밖 마스크 노이즈 제거).
            int bx1 = Math.Clamp((int)Math.Floor(c.X1), 0, Sw - 1);
            int by1 = Math.Clamp((int)Math.Floor(c.Y1), 0, Sh - 1);
            int bx2 = Math.Clamp((int)Math.Ceiling(c.X2), 1, Sw);
            int by2 = Math.Clamp((int)Math.Ceiling(c.Y2), 1, Sh);
            if (bx2 <= bx1 || by2 <= by1) continue;
            var rect = new Rect(bx1, by1, bx2 - bx1, by2 - by1);
            using var boxMask = new Mat(Sh, Sw, MatType.CV_8UC1, Scalar.All(0));
            bin[rect].CopyTo(boxMask[rect]);
            Cv2.Max(comb, boxMask, comb);
        }

        // letterbox 제거 → 원본 해상도로 복원.
        var inner = new Rect(dx, dy, newW, newH);
        using var cropped = new Mat(comb, inner);
        var mask = new Mat();
        Cv2.Resize(cropped, mask, new Size(W, H), 0, 0, InterpolationFlags.Nearest);
        Cv2.Threshold(mask, mask, 127, 255, ThresholdTypes.Binary);
        empty.Dispose();
        return (mask, kept.Count, maxScore);
    }

    private static float Sigmoid(float x) => 1f / (1f + MathF.Exp(-x));

    private static float Iou(SegCand a, SegCand b)
    {
        float ix1 = Math.Max(a.X1, b.X1), iy1 = Math.Max(a.Y1, b.Y1);
        float ix2 = Math.Min(a.X2, b.X2), iy2 = Math.Min(a.Y2, b.Y2);
        float iw = Math.Max(0, ix2 - ix1), ih = Math.Max(0, iy2 - iy1);
        float inter = iw * ih;
        float ua = (a.X2 - a.X1) * (a.Y2 - a.Y1) + (b.X2 - b.X1) * (b.Y2 - b.Y1) - inter;
        return ua <= 0 ? 0 : inter / ua;
    }
}
