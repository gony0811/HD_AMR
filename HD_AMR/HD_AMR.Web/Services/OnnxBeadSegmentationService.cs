using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace HD_AMR.Web.Services;

/// <summary>
/// 학습 산출물(weld_seg.onnx, YOLOv8-seg)을 CPU EP 로 네이티브 추론해 비드 마스크를 얻는 서비스.
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

    public OnnxBeadSegmentationService(WeldTrainingService train, ILogger<OnnxBeadSegmentationService> log)
    {
        _train = train;
        _log = log;
    }

    /// <summary>캡처 폴더의 {stem}_{modality}.png 에 대해 추론. 오버레이는 LastOverlay 에 저장.</summary>
    public async Task<InferResult> RunOnCaptureAsync(
        string modelPath, string stem, string modality, float conf, float maskThr)
    {
        modality = modality == "ir" ? "ir" : "rgb";
        var paths = await _train.GetPathsAsync()
                    ?? throw new InvalidOperationException("캡처 저장 폴더가 설정되지 않았습니다.");
        var imgPath = Path.Combine(paths.CaptureDir, $"{stem}_{modality}.png");
        if (!File.Exists(imgPath)) throw new FileNotFoundException("이미지가 없습니다.", imgPath);
        if (!File.Exists(modelPath)) throw new FileNotFoundException("모델(.onnx)이 없습니다.", modelPath);

        return await Task.Run(() =>
        {
            using var bgr = Cv2.ImRead(imgPath, ImreadModes.Color);
            if (bgr.Empty()) throw new InvalidOperationException("이미지를 읽지 못했습니다.");
            return Infer(modelPath, bgr, conf, maskThr);
        });
    }

    /// <summary>업로드/외부 이미지 바이트에 대해 추론. 캡처 폴더 밖의 검증용 이미지에 사용.</summary>
    public async Task<InferResult> RunOnBytesAsync(string modelPath, byte[] imageBytes, float conf, float maskThr)
    {
        if (!File.Exists(modelPath)) throw new FileNotFoundException("모델(.onnx)이 없습니다.", modelPath);
        if (imageBytes is null || imageBytes.Length == 0) throw new ArgumentException("빈 이미지입니다.", nameof(imageBytes));
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
                double covered = count > 0 ? Cv2.CountNonZero(mask) / (double)(bgr.Width * bgr.Height) : 0;
                return new InferResult(count, maxScore, covered, sw.ElapsedMilliseconds, bgr.Width, bgr.Height);
            }
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

/// <summary>YOLOv8-seg ONNX 출력 디코더(단일 클래스 비드). letterbox 전처리 → NMS → proto 마스크 합성.</summary>
internal static class YoloSeg
{
    private const int S = 640; // 입력 정사각 크기(export imgsz 와 일치)

    public static (Mat Mask, int Count, float MaxScore) Decode(
        InferenceSession sess, Mat bgr, float conf, float iou, float maskThr)
    {
        int W = bgr.Width, H = bgr.Height;
        double r = Math.Min((double)S / W, (double)S / H);
        int newW = (int)Math.Round(W * r), newH = (int)Math.Round(H * r);
        int dx = (S - newW) / 2, dy = (S - newH) / 2;

        using var resized = new Mat();
        Cv2.Resize(bgr, resized, new Size(newW, newH));
        using var lb = new Mat();
        Cv2.CopyMakeBorder(resized, lb, dy, S - newH - dy, dx, S - newW - dx,
            BorderTypes.Constant, new Scalar(114, 114, 114));

        // 입력 텐서 [1,3,S,S] — BGR→RGB, /255. 평면 순서 R,G,B.
        var data = new float[3 * S * S];
        int plane = S * S;
        var idx = lb.GetGenericIndexer<Vec3b>();
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                var p = idx[y, x];
                int o = y * S + x;
                data[o] = p.Item2 / 255f;             // R
                data[plane + o] = p.Item1 / 255f;     // G
                data[2 * plane + o] = p.Item0 / 255f; // B
            }
        var input = new DenseTensor<float>(data, new[] { 1, 3, S, S });

        var inName = sess.InputMetadata.Keys.First();
        using var results = sess.Run(new[] { NamedOnnxValue.CreateFromTensor(inName, input) });

        // 출력 식별: rank3 = 검출[1,ch,na], rank4 = proto[1,pc,ph,pw].
        Tensor<float>? det = null, proto = null;
        foreach (var v in results)
        {
            var t = v.AsTensor<float>();
            if (t.Dimensions.Length == 3) det = t;
            else if (t.Dimensions.Length == 4) proto = t;
        }
        var empty = new Mat(H, W, MatType.CV_8UC1, Scalar.All(0));
        if (det is null || proto is null) return (empty, 0, 0f);

        int ch = det.Dimensions[1], na = det.Dimensions[2];
        int pc = proto.Dimensions[1], ph = proto.Dimensions[2], pw = proto.Dimensions[3];
        int nc = ch - 4 - pc;              // 클래스 수(비드=1)
        if (nc < 1) { return (empty, 0, 0f); }

        var d = det.ToArray();             // [ch*na] row-major: c*na + a
        var pr = proto.ToArray();          // [pc*ph*pw]
        int protoArea = ph * pw;

        // 후보 수집.
        var cand = new List<(float score, float x1, float y1, float x2, float y2, float[] coeff)>();
        for (int a = 0; a < na; a++)
        {
            // 최고 클래스 점수(비드 단일이라 사실상 인덱스 4).
            float best = 0; int bestC = 0;
            for (int cI = 0; cI < nc; cI++)
            {
                float sc = d[(4 + cI) * na + a];
                if (sc > best) { best = sc; bestC = cI; }
            }
            if (best < conf) continue;

            float cx = d[0 * na + a], cy = d[1 * na + a], ww = d[2 * na + a], hh = d[3 * na + a];
            float x1 = cx - ww / 2, y1 = cy - hh / 2, x2 = cx + ww / 2, y2 = cy + hh / 2;
            var coeff = new float[pc];
            for (int k = 0; k < pc; k++) coeff[k] = d[(4 + nc + k) * na + a];
            cand.Add((best, x1, y1, x2, y2, coeff));
        }
        if (cand.Count == 0) return (empty, 0, 0f);

        // NMS.
        var order = cand.Select((c, i) => (c, i)).OrderByDescending(t => t.c.score).ToList();
        var keep = new List<int>();
        var removed = new bool[order.Count];
        for (int i = 0; i < order.Count; i++)
        {
            if (removed[i]) continue;
            keep.Add(order[i].i);
            for (int j = i + 1; j < order.Count; j++)
            {
                if (removed[j]) continue;
                if (Iou(order[i].c, order[j].c) > iou) removed[j] = true;
            }
        }

        float maxScore = cand[keep[0]].score;
        // 검출별 proto 마스크 합성 → 640 마스크 누적.
        using var comb = new Mat(S, S, MatType.CV_8UC1, Scalar.All(0));
        foreach (var ci in keep)
        {
            var c = cand[ci];
            var mm = new float[protoArea];
            for (int k = 0; k < pc; k++)
            {
                float ck = c.coeff[k];
                if (ck == 0f) continue;
                int off = k * protoArea;
                for (int i = 0; i < protoArea; i++) mm[i] += ck * pr[off + i];
            }
            for (int i = 0; i < protoArea; i++) mm[i] = Sigmoid(mm[i]);

            using var mMat = new Mat(ph, pw, MatType.CV_32FC1);
            mMat.SetArray(mm);
            using var up = new Mat();
            Cv2.Resize(mMat, up, new Size(S, S));
            using var bin = new Mat();
            Cv2.Compare(up, new Scalar(maskThr), bin, CmpType.GT); // 8UC1: 255 where prob>thr

            // 박스 영역만 남기고 누적(박스 밖 마스크 노이즈 제거).
            int bx1 = Math.Clamp((int)Math.Floor(c.x1), 0, S - 1);
            int by1 = Math.Clamp((int)Math.Floor(c.y1), 0, S - 1);
            int bx2 = Math.Clamp((int)Math.Ceiling(c.x2), 1, S);
            int by2 = Math.Clamp((int)Math.Ceiling(c.y2), 1, S);
            if (bx2 <= bx1 || by2 <= by1) continue;
            var rect = new Rect(bx1, by1, bx2 - bx1, by2 - by1);
            using var boxMask = new Mat(S, S, MatType.CV_8UC1, Scalar.All(0));
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
        return (mask, keep.Count, maxScore);
    }

    private static float Sigmoid(float x) => 1f / (1f + MathF.Exp(-x));

    private static float Iou(
        (float score, float x1, float y1, float x2, float y2, float[] coeff) a,
        (float score, float x1, float y1, float x2, float y2, float[] coeff) b)
    {
        float ix1 = Math.Max(a.x1, b.x1), iy1 = Math.Max(a.y1, b.y1);
        float ix2 = Math.Min(a.x2, b.x2), iy2 = Math.Min(a.y2, b.y2);
        float iw = Math.Max(0, ix2 - ix1), ih = Math.Max(0, iy2 - iy1);
        float inter = iw * ih;
        float ua = (a.x2 - a.x1) * (a.y2 - a.y1) + (b.x2 - b.x1) * (b.y2 - b.y1) - inter;
        return ua <= 0 ? 0 : inter / ua;
    }
}
