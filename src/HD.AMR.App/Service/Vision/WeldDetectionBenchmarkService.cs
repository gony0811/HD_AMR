using System.Diagnostics;
using System.Globalization;
using System.Text;
using HD.AMR.App.Models;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace HD.AMR.App.Service.Vision;

/// <summary>
/// 용접선 검출 방식 <b>일괄 비교평가</b>. 데이터셋 폴더(학습+검증)에서 선 라벨(<c>{stem}_{modality}_line.json</c>)이 있는
/// 촬영본마다 선택한 검출 방식들을 모두 돌리고, 정답 대비 각도·중심 오차(<see cref="WeldLineMetrics"/>)를
/// 방식별로 요약한다. 검출기는 운영과 같은 인스턴스(<see cref="WeldTrackingService.GetDetector"/>)를 쓰고,
/// 파라미터는 운영값을 복사해 모달리티만 맞춘다(운영 상태는 건드리지 않음).
/// 결과는 CSV(<c>dl/bench/</c>)로도 남겨 나중에 다시 볼 수 있다.
/// </summary>
public sealed class WeldDetectionBenchmarkService
{
    private readonly WeldTrackingService _weld;
    private readonly WeldTrainingService _train;
    private readonly OnnxBeadSegmentationService _seg;
    private readonly ILogger<WeldDetectionBenchmarkService> _log;
    private int _running;

    public WeldDetectionBenchmarkService(WeldTrackingService weld, WeldTrainingService train,
        OnnxBeadSegmentationService seg, ILogger<WeldDetectionBenchmarkService> log)
    {
        _weld = weld;
        _train = train;
        _seg = seg;
        _log = log;
    }

    public bool IsRunning => Volatile.Read(ref _running) == 1;
    public BenchmarkReport? LastReport { get; private set; }

    /// <summary>선 라벨이 있는 평가 대상 stem 목록(모달리티별). 데이터셋 폴더(학습+검증) 전체에서 찾는다.</summary>
    public async Task<List<string>> ListLabeledStemsAsync(string modality)
        => (await ListLabeledAsync(LabelFiles.Mod(modality))).Select(x => x.Stem).ToList();

    /// <summary>선 라벨 촬영본 수 — 전체(학습+검증 폴더)와 검증 폴더만. 검증 폴더 미지정이면 Val=0.</summary>
    public async Task<(int Total, int Val)> CountLabeledAsync(string modality)
    {
        var list = await ListLabeledAsync(LabelFiles.Mod(modality));
        return (list.Count, list.Count(x => x.IsVal));
    }

    // (stem, 폴더, 검증 폴더 여부). 같은 stem 이 여러 폴더에 있으면 데이터셋 변환처럼 먼저 나온 것만.
    private async Task<List<(string Stem, string Dir, bool IsVal)>> ListLabeledAsync(string modality)
    {
        var src = await _train.GetDatasetSourcesAsync();
        var dirs = src.TrainDirs.Where(d => !LabelFiles.SameDir(d, src.ValDir)).Select(d => (d, false)).ToList();
        if (src.ValDir is not null) dirs.Add((src.ValDir, true));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<(string, string, bool)>();
        foreach (var (dir, isVal) in dirs)
            foreach (var stem in LabelFiles.LabeledStems(dir, modality, LabelKind.Line))
                if (seen.Add(stem)) list.Add((stem, dir, isVal));
        return list;
    }

    public async Task<BenchmarkReport> RunAsync(BenchmarkOptions opt, IProgress<(int Done, int Total)>? progress = null,
        CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
            throw new InvalidOperationException("이미 비교평가가 실행 중입니다.");
        try
        {
            var modality = opt.Modality == "ir" ? "ir" : "rgb";
            var paths = await _train.GetPathsAsync() ?? throw new InvalidOperationException("캡처 저장 폴더가 설정되지 않았습니다.");
            var labeled = await ListLabeledAsync(modality);
            var stems = labeled;
            string? warning = null;

            if (opt.ValOnly)
            {
                // 데이터셋 변환(ConvertAsync)과 같은 규칙 — 검증 폴더가 있으면 그 폴더, 없으면 세션 단위 분리의 val.
                List<(string Stem, string Dir, bool IsVal)> filtered;
                bool single = false;
                if (labeled.Any(x => x.IsVal)) filtered = labeled.Where(x => x.IsVal).ToList();
                else
                {
                    var split = DatasetSplitter.Split(labeled.Select(x => x.Stem));
                    var val = split.Val.ToHashSet();
                    filtered = labeled.Where(x => val.Contains(x.Stem)).ToList();
                    single = split.SingleSession;
                }
                if (filtered.Count == 0)
                    warning = "검증셋(val)에 선 라벨이 있는 촬영본이 없어 전체로 평가했습니다.";
                else
                {
                    stems = filtered;
                    if (single) warning = "촬영 세션이 1개뿐이라 val 이 학습셋과 같은 자리 영상입니다 — 점수가 낙관적일 수 있습니다.";
                }
            }
            if (stems.Count == 0)
                throw new InvalidOperationException($"선 라벨({modality})이 있는 촬영본이 없습니다. 비드 라벨링에서 📏 선 도구로 라벨링하세요.");

            var methods = opt.Methods.Distinct().ToList();
            var baseParams = _weld.Params.Clone();
            baseParams.Mode = modality == "ir" ? WeldImageMode.Ir
                : baseParams.Mode == WeldImageMode.Ir ? WeldImageMode.RgbGrayscale : baseParams.Mode;
            if (opt.LineFit is { } fitMode) baseParams.LineFit = fitMode;   // null = 운영값 유지
            // 평가할 모델 — 지정하면 이 평가에서만 쓴다(운영 파라미터 Weld.Yolo/Unet.ModelPath 는 그대로). null = 운영 모델.
            if (!string.IsNullOrWhiteSpace(opt.DlModelPath)) baseParams.DlModelPath = opt.DlModelPath;
            if (!string.IsNullOrWhiteSpace(opt.UnetModelPath)) baseParams.UnetModelPath = opt.UnetModelPath;
            bool horiz = baseParams.ProgressAxis == WeldProgressAxis.Horizontal;
            var weldRoi = opt.UseWeldRoi ? _weld.WeldRoi : null;

            var items = new List<BenchmarkItem>();
            int total = stems.Count * methods.Count, done = 0;
            progress?.Report((0, total));

            // 운영 검출기가 없는 환경(macOS — OpenCV 네이티브 win 전용)은 비전 학습 ④ 추론·검증과 같은 관리 코드 경로로 평가.
            var managed = methods.Where(m => !_weld.GetDetector(m).IsAvailable).ToHashSet();
            await Task.Run(async () =>
            {
                foreach (var (stem, dir, _) in stems)
                {
                    ct.ThrowIfCancellationRequested();
                    var imgPath = Path.Combine(dir, LabelFiles.ImageName(stem, modality));
                    var gt = WeldLineLabel.FromJson(File.ReadAllText(Path.Combine(dir, LabelFiles.LineName(stem, modality))));
                    var bytes = File.ReadAllBytes(imgPath);

                    foreach (var m in methods)
                    {
                        ct.ThrowIfCancellationRequested();
                        items.Add(gt is null
                            ? new BenchmarkItem(stem, m, false, "선 라벨 손상", LineErrors.Missed, 0, null)
                            : managed.Contains(m)
                                ? await RunManagedAsync(stem, m, bytes, gt, baseParams, weldRoi, opt, paths.Models)
                                : RunOne(stem, m, bytes, gt, baseParams, weldRoi, horiz, opt));
                        progress?.Report((++done, total));
                    }
                }
            }, ct);

            var summaries = methods.Select(m => Summarize(m, items.Where(i => i.Method == m).ToList())).ToList();
            string? csv = null;
            string? ModelOf(WeldDetectionMethod m) => m switch
            {
                WeldDetectionMethod.Dl => baseParams.DlModelPath,
                WeldDetectionMethod.Unet => baseParams.UnetModelPath,
                _ => null,
            };
            var models = methods.Where(m => ModelOf(m) is not null).ToDictionary(m => m, m => ModelOf(m)!);
            try { csv = WriteCsv(paths, modality, baseParams.LineFit, items, summaries, models); }
            catch (Exception ex) { _log.LogWarning(ex, "비교평가 CSV 저장 실패"); }

            var report = new BenchmarkReport(DateTime.Now, modality, stems.Count, opt, items, summaries, csv, warning,
                baseParams.LineFit, models, managed.Count > 0);
            LastReport = report;
            return report;
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private BenchmarkItem RunOne(string stem, WeldDetectionMethod method, byte[] png, WeldLineLabel gt,
        WeldDetectionParams baseParams, RoiRect? weldRoi, bool horiz, BenchmarkOptions opt)
    {
        try
        {
            var det = _weld.GetDetector(method);
            if (!det.IsAvailable)
                return new BenchmarkItem(stem, method, false, "검출기 사용 불가(네이티브 없음)", LineErrors.Missed, 0, null);

            // PNG 바이트를 그대로 넘긴다 — 디코더의 "mjpg" 경로는 ImDecode 라 PNG 도 해석한다.
            var frame = new CameraFrame(png, gt.Width, gt.Height, "mjpg", DateTime.UtcNow);
            var roi = weldRoi ?? RoiRect.Full(gt.Width, gt.Height);
            var p = baseParams.Clone();

            var sw = Stopwatch.StartNew();
            var r = det.DetectWeld(frame, roi, p);
            sw.Stop();

            var err = r.Success
                ? WeldLineMetrics.Compare(gt, r.Centerline, horiz, opt.AngleTolDeg, opt.CenterTolPx)
                : LineErrors.Missed;
            var overlay = DrawGroundTruth(r.OverlayJpeg ?? png, gt);
            return new BenchmarkItem(stem, method, r.Success, r.Message, err, sw.ElapsedMilliseconds, overlay);
        }
        catch (Exception ex)
        {
            return new BenchmarkItem(stem, method, false, $"오류: {ex.Message}", LineErrors.Missed, 0, null);
        }
    }

    /// <summary>
    /// 운영 검출기 대체 경로(macOS 등) — 비전 학습 ④ 추론·검증과 같은 계산: ONNX 관리 코드 추론(<see cref="OnnxBeadSegmentationService"/>,
    /// _clahe 모델이면 CLAHE 전처리 포함) → 마스크에서 용접선 추출(<see cref="InferLineCheck"/>, 진행축은 정답 선 방향).
    /// 임계값은 운영 파라미터(YOLO conf/mask, U-Net threshold), 채점 기준은 이 평가의 각도/중심 기준.
    /// Weld ROI 를 고르면 전체 프레임 추론 후 마스크를 ROI 밖에서 지운다.
    /// </summary>
    private async Task<BenchmarkItem> RunManagedAsync(string stem, WeldDetectionMethod method, byte[] png, WeldLineLabel gt,
        WeldDetectionParams baseParams, RoiRect? weldRoi, BenchmarkOptions opt, string modelsDir)
    {
        var raw = method switch
        {
            WeldDetectionMethod.Dl => baseParams.DlModelPath,
            WeldDetectionMethod.Unet => baseParams.UnetModelPath,
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(raw))
            return new BenchmarkItem(stem, method, false, "이 환경에서는 운영 검출기를 쓸 수 없고 모델 방식(YOLO·U-Net)만 평가됩니다",
                LineErrors.Missed, 0, null);
        var model = raw.Trim().Trim('"').Trim();
        if (!Path.IsPathFullyQualified(model)) model = Path.Combine(modelsDir, model);
        try
        {
            var sw = Stopwatch.StartNew();
            float conf = (float)baseParams.DlConfidence;
            float thr = method == WeldDetectionMethod.Unet ? (float)baseParams.UnetThreshold : (float)baseParams.DlMaskThreshold;
            var r = await _seg.RunOnBytesAsync(model, png, conf, thr);
            var mask = _seg.LastMask;
            if (mask is null || mask.Length != r.Width * r.Height)
                return new BenchmarkItem(stem, method, false, "마스크를 얻지 못했습니다", LineErrors.Missed, sw.ElapsedMilliseconds, null);
            if (weldRoi is { } roi)
            {
                mask = (byte[])mask.Clone();
                for (int y = 0; y < r.Height; y++)
                    for (int x = 0; x < r.Width; x++)
                        if (x < roi.X || x >= roi.X + roi.Width || y < roi.Y || y >= roi.Y + roi.Height) mask[y * r.Width + x] = 0;
            }
            var lr = InferLineCheck.Run(png, mask, r.Width, r.Height, baseParams, gt);
            sw.Stop();
            var err = lr.Found
                ? WeldLineMetrics.Compare(gt, lr.Centerline, lr.Horizontal, opt.AngleTolDeg, opt.CenterTolPx)
                : LineErrors.Missed;
            return new BenchmarkItem(stem, method, lr.Found, lr.Message, err, sw.ElapsedMilliseconds, lr.OverlayJpeg);
        }
        catch (Exception ex)
        {
            return new BenchmarkItem(stem, method, false, $"오류: {ex.Message}", LineErrors.Missed, 0, null);
        }
    }

    /// <summary>검출 오버레이 위에 정답 선을 초록 점선 느낌(2px)으로 덧그린다. 실패하면 원본 오버레이.</summary>
    private static byte[]? DrawGroundTruth(byte[] img, WeldLineLabel gt)
    {
        try
        {
            using var m = Cv2.ImDecode(img, ImreadModes.Color);
            if (m.Empty()) return img;
            double sx = m.Width / (double)gt.Width, sy = m.Height / (double)gt.Height;
            var segs = gt.Segments().Where(s => s.Count >= 2)
                .Select(s => s.Select(p => new Point((int)Math.Round(p[0] * sx), (int)Math.Round(p[1] * sy))).ToArray()).ToArray();
            Cv2.Polylines(m, segs, false, new Scalar(0, 255, 0), 2, LineTypes.AntiAlias);   // 끊긴 곳은 잇지 않는다
            var pts = segs[0];
            Cv2.PutText(m, "GT", new Point(pts[0].X + 4, Math.Max(12, pts[0].Y - 6)),
                HersheyFonts.HersheySimplex, 0.45, new Scalar(0, 255, 0), 1, LineTypes.AntiAlias);
            Cv2.ImEncode(".jpg", m, out var buf, new[] { (int)ImwriteFlags.JpegQuality, 80 });
            return buf;
        }
        catch { return img; }
    }

    public static MethodSummary Summarize(WeldDetectionMethod m, IReadOnlyList<BenchmarkItem> items)
    {
        var det = items.Where(i => i.Err.Detected).ToList();
        return new MethodSummary(
            m,
            items.Count,
            det.Count,
            items.Count(i => i.Err.Pass),
            WeldLineMetrics.Quantile(det.Select(i => i.Err.AngleDeg), 0.5),
            WeldLineMetrics.Quantile(det.Select(i => i.Err.AngleDeg), 0.9),
            WeldLineMetrics.Quantile(det.Select(i => i.Err.CenterOffsetPx), 0.5),
            WeldLineMetrics.Quantile(det.Select(i => i.Err.CenterOffsetPx), 0.9),
            det.Count == 0 ? double.NaN : det.Average(i => i.Err.Coverage),
            items.Count == 0 ? double.NaN : items.Average(i => (double)i.Millis));
    }

    private static string WriteCsv(TrainingPaths paths, string modality, WeldLineFitMode fit, IReadOnlyList<BenchmarkItem> items,
        IReadOnlyList<MethodSummary> summaries, IReadOnlyDictionary<WeldDetectionMethod, string> models)
    {
        var dir = Path.Combine(paths.Workspace, "bench");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"bench_{DateTime.Now:yyyyMMdd_HHmmss}_{modality}_{fit}.csv");
        string F(double v) => double.IsNaN(v) || double.IsInfinity(v) ? "" : v.ToString("0.###", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.AppendLine("stem,method,success,detected,pass,angle_deg,center_offset_px,mean_dist_px,coverage,ms,message");
        foreach (var i in items)
            sb.AppendLine(string.Join(',', i.Stem, i.Method, i.Success, i.Err.Detected, i.Err.Pass,
                F(i.Err.AngleDeg), F(i.Err.CenterOffsetPx), F(i.Err.MeanDistPx), F(i.Err.Coverage), i.Millis,
                "\"" + (i.Message ?? "").Replace("\"", "'") + "\""));
        sb.AppendLine();
        sb.AppendLine("method,n,detected,pass,angle_med,angle_p90,offset_med,offset_p90,coverage_mean,ms_mean,model");
        foreach (var s in summaries)
            sb.AppendLine(string.Join(',', s.Method, s.Count, s.Detected, s.Passed, F(s.AngleMedian), F(s.AngleP90),
                F(s.OffsetMedian), F(s.OffsetP90), F(s.CoverageMean), F(s.MillisMean),
                models.TryGetValue(s.Method, out var mp) ? Path.GetFileName(mp) : ""));
        // Excel 한글 깨짐 방지 BOM.
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        return file;
    }
}

/// <summary>비교평가 옵션. UseWeldRoi=false 면 전체 프레임을 ROI 로 쓴다(촬영본마다 위치가 달라 기본 권장).
/// DlModelPath/UnetModelPath — 이 평가에서만 쓸 모델(null = 운영 모델). 운영 파라미터는 바꾸지 않는다.</summary>
public sealed record BenchmarkOptions(
    string Modality,
    IReadOnlyList<WeldDetectionMethod> Methods,
    bool ValOnly = false,
    bool UseWeldRoi = false,
    double AngleTolDeg = WeldLineMetrics.DefaultAngleTolDeg,
    double CenterTolPx = WeldLineMetrics.DefaultCenterTolPx,
    WeldLineFitMode? LineFit = null,
    string? DlModelPath = null,
    string? UnetModelPath = null);

/// <summary>촬영본 1장 × 방식 1개의 결과. Overlay = 검출 오버레이 + 정답(초록) JPEG.</summary>
public sealed record BenchmarkItem(string Stem, WeldDetectionMethod Method, bool Success, string? Message,
    LineErrors Err, long Millis, byte[]? Overlay);

/// <summary>방식별 요약. 오차 분위수는 검출 성공 건만으로 계산(미검출은 Detected/Passed 비율에 반영).</summary>
public sealed record MethodSummary(WeldDetectionMethod Method, int Count, int Detected, int Passed,
    double AngleMedian, double AngleP90, double OffsetMedian, double OffsetP90, double CoverageMean, double MillisMean)
{
    public double PassRate => Count == 0 ? 0 : Passed / (double)Count;
    public double DetectRate => Count == 0 ? 0 : Detected / (double)Count;
}

public sealed record BenchmarkReport(DateTime At, string Modality, int ImageCount, BenchmarkOptions Options,
    IReadOnlyList<BenchmarkItem> Items, IReadOnlyList<MethodSummary> Summaries, string? CsvPath, string? Warning,
    WeldLineFitMode LineFit = WeldLineFitMode.Ransac,
    IReadOnlyDictionary<WeldDetectionMethod, string>? Models = null,
    bool ManagedPath = false)
{
    /// <summary>추론 경로 표기 — 운영 검출기(Windows) 또는 ④ 추론·검증과 같은 관리 코드 경로(macOS 등).</summary>
    public string EngineText => ManagedPath ? "④ 추론·검증 경로(운영 검출기 없음)" : "운영 검출기";

    /// <summary>방식별로 실제 평가에 쓴 모델 파일명(모델을 쓰지 않는 방식이면 null).</summary>
    public string? ModelName(WeldDetectionMethod m) => Models is not null && Models.TryGetValue(m, out var p) ? Path.GetFileName(p) : null;
}
