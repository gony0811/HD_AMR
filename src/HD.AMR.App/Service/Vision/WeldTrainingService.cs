using System.Diagnostics;
using HD.AMR.App.Service;
using OpenCvSharp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace HD.AMR.App.Service.Vision;

/// <summary>
/// DL 비드 세그멘테이션(YOLOv8-seg, CPU) 학습 파이프라인의 UI 오케스트레이터.
/// ① 마스크 초안 → YOLO-seg 라벨 데이터셋 변환은 .NET(OpenCvSharp)으로 네이티브 수행.
/// ② 학습 / ③ ONNX 내보내기는 앱이 Python(ultralytics)을 서브프로세스로 실행하고 로그를 스트리밍한다.
/// 학습 프로세스는 페이지 이동/서킷과 무관하게 살아 있어야 하므로 이 서비스는 싱글톤으로 상태를 보관하며,
/// 캡처 폴더/파이썬 경로 등 스코프 의존(ParameterService→DbContext)은 필요 시 스코프를 열어 조회한다.
/// </summary>
public sealed class WeldTrainingService
{
    public const string PythonExeKey = "Vision.Python.Exe";

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<WeldTrainingService> _log;

    private readonly object _gate = new();
    private readonly List<string> _lines = new();
    private Process? _proc;

    public TrainingPhase Phase { get; private set; } = TrainingPhase.Idle;
    public string? StatusText { get; private set; }
    public bool IsBusy { get { lock (_gate) return _proc is { HasExited: false }; } }

    // 학습 진행 상태(진행 바·ETA용). 학습이 아닌 작업 중에는 TrainTotalEpochs=0.
    public int TrainTotalEpochs { get; private set; }
    public int TrainCurrentEpoch { get; private set; }
    public DateTime? TrainStartUtc { get; private set; }
    // 프로세스 종료(정지/완료/실패) 시각. 설정되면 경과시간이 여기서 멈춘다(계속 카운트 방지).
    public DateTime? TrainEndUtc { get; private set; }

    // 마지막으로 데이터셋 변환한 모달리티("ir"/"rgb"). 내보내기 파일명 weld_seg_{modality}.onnx 기본값.
    public string LastConvertedModality { get; private set; } = "ir";

    public WeldTrainingService(IServiceScopeFactory scopes, ILogger<WeldTrainingService> log)
    {
        _scopes = scopes;
        _log = log;
    }

    // ── 로그 버퍼 ────────────────────────────────────────────────────────────
    private void Emit(string? line)
    {
        if (line is null) return;
        // 학습 중이면 ultralytics 진행 로그의 "현재/전체 epoch"(예: 17/100)을 파싱해 진행 상태 갱신.
        if (TrainTotalEpochs > 0)
        {
            var ep = TryParseEpoch(line, TrainTotalEpochs);
            if (ep > 0 && ep >= TrainCurrentEpoch) TrainCurrentEpoch = ep;
        }
        lock (_gate)
        {
            _lines.Add(line);
            if (_lines.Count > 4000) _lines.RemoveRange(0, _lines.Count - 4000);
        }
    }

    // "…  17/100  0G  …" 같은 줄에서 현재 epoch(17)을 뽑는다. 분모가 전체 epoch 수와 일치하는
    // "/{total}" 토큰을 찾아, 바로 뒤가 숫자가 아니고(예: /1000 배제) 앞의 연속 숫자를 읽는다.
    private static int TryParseEpoch(string line, int total)
    {
        var tok = "/" + total.ToString(System.Globalization.CultureInfo.InvariantCulture);
        int idx = line.IndexOf(tok, StringComparison.Ordinal);
        if (idx <= 0) return 0;
        int after = idx + tok.Length;
        if (after < line.Length && char.IsDigit(line[after])) return 0; // "/1000" 등 배제
        int end = idx - 1, j = end;
        while (j >= 0 && char.IsDigit(line[j])) j--;
        if (j == end) return 0;
        return int.TryParse(line.AsSpan(j + 1, end - j), out var v) ? v : 0;
    }

    /// <summary>최근 로그 tail 줄. UI 폴링용.</summary>
    public IReadOnlyList<string> LogTail(int tail = 500)
    {
        lock (_gate)
            return _lines.Count <= tail ? _lines.ToArray() : _lines.GetRange(_lines.Count - tail, tail).ToArray();
    }

    public void ClearLog() { lock (_gate) _lines.Clear(); }

    // ── 설정 조회(스코프) ─────────────────────────────────────────────────────
    private async Task<string?> GetCaptureDirAsync()
    {
        using var s = _scopes.CreateScope();
        var p = s.ServiceProvider.GetRequiredService<ParameterService>();
        var dir = await p.GetAsync(LabelDataService.CaptureDirKey);
        return string.IsNullOrWhiteSpace(dir) ? null : dir;
    }

    private async Task<string> GetPythonAsync()
    {
        using var s = _scopes.CreateScope();
        var p = s.ServiceProvider.GetRequiredService<ParameterService>();
        var py = await p.GetAsync(PythonExeKey);
        return string.IsNullOrWhiteSpace(py) ? "python" : py;
    }

    public async Task SetPythonAsync(string exe)
    {
        using var s = _scopes.CreateScope();
        var p = s.ServiceProvider.GetRequiredService<ParameterService>();
        await p.SetAsync(PythonExeKey, exe, "DL 학습용 Python 실행 파일 경로");
    }

    /// <summary>워크스페이스 경로들(캡처 폴더/dl/...). 캡처 폴더 미설정이면 null.</summary>
    public async Task<TrainingPaths?> GetPathsAsync()
    {
        var dir = await GetCaptureDirAsync();
        if (dir is null) return null;
        var ws = Path.Combine(dir, "dl");
        return new TrainingPaths(
            CaptureDir: dir,
            Workspace: ws,
            Dataset: Path.Combine(ws, "dataset"),
            Runs: Path.Combine(ws, "runs"),
            Models: Path.Combine(ws, "models"));
    }

    // ── Python 감지 ──────────────────────────────────────────────────────────
    public async Task<PythonInfo> DetectPythonAsync()
    {
        var py = await GetPythonAsync();
        var ver = await RunQuickAsync(py, new[] { "--version" });
        if (!ver.Ok)
            return new PythonInfo(false, false, py, $"'{py}' 실행 실패 — Python 미설치 또는 경로 오류");

        var u = await RunQuickAsync(py, new[]
        {
            "-c", "import ultralytics,sys; sys.stdout.write(ultralytics.__version__)"
        });
        var t = await RunQuickAsync(py, new[]
        {
            "-c", "import torch,sys; sys.stdout.write(torch.__version__)"
        });
        var pyVer = (ver.Output + ver.Error).Trim();
        var torchTxt = t.Ok ? $" · torch {t.Output.Trim()}" : " · torch 미설치(U-Net 불가)";
        return u.Ok
            ? new PythonInfo(true, true, py, $"{pyVer} · ultralytics {u.Output.Trim()}{torchTxt}", t.Ok)
            : new PythonInfo(true, false, py, $"{pyVer} · ultralytics 미설치 (pip install ultralytics){torchTxt}", t.Ok);
    }

    // ── ① 마스크 → YOLO-seg 데이터셋 변환(네이티브) ────────────────────────────
    /// <param name="modality">"rgb" 또는 "ir" — 이 모달리티의 마스크 초안만 사용.</param>
    public async Task<ConvertResult> ConvertAsync(string modality)
    {
        modality = modality == "ir" ? "ir" : "rgb";
        LastConvertedModality = modality;
        var paths = await GetPathsAsync();
        if (paths is null) throw new InvalidOperationException("캡처 저장 폴더가 설정되지 않았습니다.");
        if (!Directory.Exists(paths.CaptureDir)) throw new DirectoryNotFoundException(paths.CaptureDir);

        // 매 변환마다 train/val 폴더를 새로 만든다(오래된 라벨/이미지 잔재 제거).
        foreach (var split in new[] { "train", "val" })
        {
            RecreateDir(Path.Combine(paths.Dataset, "images", split));
            RecreateDir(Path.Combine(paths.Dataset, "labels", split));
            RecreateDir(Path.Combine(paths.Dataset, "masks", split));   // U-Net 용 이진 마스크(이미지와 같은 파일명)
        }

        int used = 0, skipped = 0, instances = 0;
        var maskGlob = $"*_{modality}_maskdraft.png";
        var masks = Directory.Exists(paths.CaptureDir)
            ? Directory.GetFiles(paths.CaptureDir, maskGlob).OrderBy(x => x).ToArray()
            : Array.Empty<string>();

        // 1) 유효한 (stem, 라벨) 수집.
        var items = new List<(string Stem, string ImgPath, string MaskPath, string Label, int N)>();
        foreach (var maskPath in masks)
        {
            var fname = Path.GetFileName(maskPath);
            var stem = fname[..^$"_{modality}_maskdraft.png".Length];
            var imgPath = Path.Combine(paths.CaptureDir, $"{stem}_{modality}.png");
            if (!File.Exists(imgPath)) { skipped++; Emit($"[변환] 원본 없음 → 건너뜀: {stem}_{modality}.png"); continue; }

            var (label, n) = await Task.Run(() => MaskToYoloLabel(maskPath));
            if (n == 0) { skipped++; Emit($"[변환] 마스크 비어있음 → 건너뜀: {fname}"); continue; }
            items.Add((stem, imgPath, maskPath, label, n));
        }

        // 2) 촬영 세션 단위 train/val 분리(같은 자리 연속 프레임이 양쪽에 섞이는 누수 방지).
        var split2 = DatasetSplitter.Split(items.Select(i => i.Stem));
        var valSet = split2.Val.ToHashSet();
        if (split2.SingleSession)
            Emit("[변환] 경고: 촬영 세션이 1개뿐 — 같은 세션을 시간순으로 앞/뒤로 나눴습니다. " +
                 "검증 점수가 실제보다 좋게 나올 수 있으니 다른 위치·시간대 촬영본을 추가하세요.");

        foreach (var (stem, imgPath, maskPath, label, n) in items)
        {
            var sub = valSet.Contains(stem) ? "val" : "train";
            File.Copy(imgPath, Path.Combine(paths.Dataset, "images", sub, $"{stem}_{modality}.png"), overwrite: true);
            File.Copy(maskPath, Path.Combine(paths.Dataset, "masks", sub, $"{stem}_{modality}.png"), overwrite: true);
            await File.WriteAllTextAsync(Path.Combine(paths.Dataset, "labels", sub, $"{stem}_{modality}.txt"), label);
            used++; instances += n;
            Emit($"[변환] {stem}_{modality} → {sub}: 폴리곤 {n}개");
        }
        Emit($"[변환] 세션 {split2.SessionCount}개 → train {split2.Train.Count}건 / val {split2.Val.Count}건");

        // data.yaml — val 이 비면(표본 1건 등) 파이프라인 점검용으로 train 을 val 로 쓴다.
        var valDir = split2.Val.Count > 0 ? "images/val" : "images/train";
        if (split2.Val.Count == 0) Emit("[변환] 경고: 검증셋이 비어 val = train 으로 둡니다(과적합 확인만 가능).");
        var yaml =
            $"path: {paths.Dataset.Replace('\\', '/')}\n" +
            "train: images/train\n" +
            $"val: {valDir}\n" +
            "nc: 1\n" +
            "names: [bead]\n";
        var yamlPath = Path.Combine(paths.Dataset, "data.yaml");
        Directory.CreateDirectory(paths.Dataset);
        await File.WriteAllTextAsync(yamlPath, yaml);

        Emit($"[변환] 완료 — 사용 {used}건 / 건너뜀 {skipped}건 / 폴리곤 {instances}개 → {yamlPath}");
        return new ConvertResult(used, skipped, instances, yamlPath, split2.Train.Count, split2.Val.Count, split2.SessionCount);
    }

    // ── 데이터 증강: 이미지+마스크 90° 회전본 생성 ────────────────────────────
    /// <summary>
    /// 캡처 폴더의 각 촬영본(이미지 + 마스크 초안)을 <b>쌍으로</b> 90° 회전해 새 촬영본으로 저장한다.
    /// 0°/90° 두 방향 비드를 모두 학습하기 위한 오프라인 증강. 마스크도 함께 회전하므로 정렬이 유지된다.
    /// 이미 회전본(stem 에 cw90/ccw90 포함)은 원본에서 제외해 재실행 시 중복 회전을 막는다.
    /// </summary>
    /// <param name="direction">"cw"(시계) | "ccw"(반시계) | "both"(양방향)</param>
    public async Task<RotateResult> GenerateRotatedCopiesAsync(string direction)
    {
        var paths = await GetPathsAsync() ?? throw new InvalidOperationException("캡처 저장 폴더가 설정되지 않았습니다.");
        var dir = paths.CaptureDir;
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException(dir);

        var dirs = direction switch
        {
            "both" => new[] { ("_cw90", RotateFlags.Rotate90Clockwise), ("_ccw90", RotateFlags.Rotate90Counterclockwise) },
            "ccw" => new[] { ("_ccw90", RotateFlags.Rotate90Counterclockwise) },
            _ => new[] { ("_cw90", RotateFlags.Rotate90Clockwise) },
        };
        var kinds = new[] { "rgb", "ir", "rgb_maskdraft", "ir_maskdraft" };

        // 원본 stem 수집(이미 회전된 것 제외).
        var stems = new HashSet<string>();
        foreach (var f in Directory.GetFiles(dir, "*_ir.png").Concat(Directory.GetFiles(dir, "*_rgb.png")))
        {
            var name = Path.GetFileName(f);
            var stem = name.EndsWith("_ir.png") ? name[..^"_ir.png".Length] : name[..^"_rgb.png".Length];
            if (stem.Contains("cw90")) continue; // cw90/ccw90 모두 제외
            stems.Add(stem);
        }

        int created = 0, skipped = 0;
        await Task.Run(() =>
        {
            foreach (var stem in stems)
                foreach (var (suffix, flag) in dirs)
                {
                    var newStem = stem + suffix;
                    bool any = false;
                    foreach (var kind in kinds)
                    {
                        var src = Path.Combine(dir, $"{stem}_{kind}.png");
                        if (!File.Exists(src)) continue;
                        using var m = CvIo.ReadMat(src, ImreadModes.Unchanged);   // 한글 경로 안전
                        if (m.Empty()) continue;
                        using var r = new Mat();
                        Cv2.Rotate(m, r, flag);
                        CvIo.WriteMat(Path.Combine(dir, $"{newStem}_{kind}.png"), r);
                        any = true;
                    }
                    // 선 라벨(정답 JSON)도 같은 방향으로 회전해 이미지·마스크와 정렬 유지.
                    foreach (var mod in new[] { "rgb", "ir" })
                    {
                        var src = Path.Combine(dir, $"{stem}_{mod}_line.json");
                        if (!File.Exists(src)) continue;
                        var line = WeldLineLabel.FromJson(File.ReadAllText(src));
                        if (line is null) continue;
                        var rot = line.Rotate90(clockwise: flag == RotateFlags.Rotate90Clockwise);
                        File.WriteAllText(Path.Combine(dir, $"{newStem}_{mod}_line.json"), rot.ToJson());
                    }
                    if (any) created++; else skipped++;
                }
        });
        Emit($"[증강] 90° 회전본 생성 — {created}건 (방향 {direction})");
        return new RotateResult(created, skipped);
    }

    /// <summary>마스크 PNG → YOLO-seg 라벨 텍스트(클래스0 정규화 폴리곤). 반환: (텍스트, 폴리곤 수).</summary>
    private static (string Text, int Count) MaskToYoloLabel(string maskPath)
    {
        using var gray = CvIo.ReadMat(maskPath, ImreadModes.Grayscale);   // 한글 경로 안전
        if (gray.Empty()) return ("", 0);
        int w = gray.Width, h = gray.Height;
        using var bin = new Mat();
        Cv2.Threshold(gray, bin, 127, 255, ThresholdTypes.Binary);
        Cv2.FindContours(bin, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        double minArea = Math.Max(16.0, w * h * 0.0002); // 노이즈 컨투어 제거
        var sb = new System.Text.StringBuilder();
        int count = 0;
        foreach (var c in contours)
        {
            if (Cv2.ContourArea(c) < minArea) continue;
            // 살짝 단순화(정점 수 축소) — 형태는 유지.
            var eps = 1.5;
            var poly = Cv2.ApproxPolyDP(c, eps, true);
            if (poly.Length < 3) continue;
            sb.Append('0');
            foreach (var p in poly)
            {
                double nx = Math.Clamp(p.X / (double)w, 0.0, 1.0);
                double ny = Math.Clamp(p.Y / (double)h, 0.0, 1.0);
                sb.Append(' ').Append(nx.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture));
                sb.Append(' ').Append(ny.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture));
            }
            sb.Append('\n');
            count++;
        }
        return (sb.ToString(), count);
    }

    // ── ② 학습 ───────────────────────────────────────────────────────────────
    /// <param name="minimalAug">
    /// 증강 최소화(소규모/과적합 검증용). true 면 mosaic·기하·색 증강을 끄고 밝기만 약간 준다.
    /// 표본이 적을 때 mosaic 은 오히려 학습을 방해하므로 기본 권장값은 true.
    /// </param>
    /// <param name="brightAug">밝기/채도 증강 강화(조명·노출 편차 대응). 회전 등 기하 증강과 독립적으로 켤 수 있다.</param>
    public Task StartTrainingAsync(int epochs, int imgsz, int batch, string baseModel, bool minimalAug, bool brightAug)
        => StartTrainingAsync(epochs, imgsz, batch, baseModel,
            minimalAug ? TrainingAugmentation.Minimal : TrainingAugmentation.Full, brightAug, patience: 0);

    /// <param name="aug">
    /// 증강 프리셋. <see cref="TrainingAugmentation.Line"/>(권장): mosaic 끔 + 상하/좌우 뒤집기·소각 회전·이동·
    /// 스케일·밝기 — 가는 선이 쪼개지거나 이어 붙지 않으면서 위치·각도·조명 변화에 일반화. Minimal: 과적합 검증용.
    /// Full: ultralytics 기본(mosaic 포함 — 가는 선 데이터에는 비권장).
    /// </param>
    /// <param name="patience">val 지표가 이 epoch 동안 개선되지 않으면 조기 종료(0=끔). val 이 train 과 같으면 의미 없음.</param>
    public async Task StartTrainingAsync(int epochs, int imgsz, int batch, string baseModel,
        TrainingAugmentation aug, bool brightAug, int patience)
    {
        if (IsBusy) throw new InvalidOperationException("이미 실행 중인 작업이 있습니다.");
        var paths = await GetPathsAsync() ?? throw new InvalidOperationException("캡처 폴더 미설정");
        var dataYaml = Path.Combine(paths.Dataset, "data.yaml");
        if (!File.Exists(dataYaml)) throw new FileNotFoundException("먼저 데이터셋 변환을 실행하세요.", dataYaml);

        Directory.CreateDirectory(paths.Runs);
        var script = Path.Combine(paths.Workspace, "_train.py");
        await File.WriteAllTextAsync(script, TrainPy);

        var py = await GetPythonAsync();
        var augKey = aug switch { TrainingAugmentation.Minimal => "min", TrainingAugmentation.Line => "line", _ => "full" };
        var args = new[]
        {
            script, baseModel, dataYaml, imgsz.ToString(), epochs.ToString(),
            batch.ToString(), "cpu", paths.Runs, "hd", augKey, brightAug ? "1" : "0", Math.Max(0, patience).ToString()
        };
        // 진행 상태 초기화(진행 바·ETA).
        TrainTotalEpochs = epochs;
        TrainCurrentEpoch = 0;
        TrainStartUtc = DateTime.UtcNow;
        TrainEndUtc = null;
        Emit($"[학습] 시작 — model={baseModel} epochs={epochs} imgsz={imgsz} batch={batch} device=cpu 증강={aug} 밝기증강={(brightAug ? "켬" : "끔")} patience={(patience > 0 ? patience : "끔")}");
        StartProcess(py, args, paths.Workspace, TrainingPhase.Training, "학습");
    }

    // ── ③ ONNX 내보내기 ──────────────────────────────────────────────────────
    /// <param name="modality">"ir"/"rgb" — 출력 파일명 weld_seg_{modality}.onnx (IR/RGB 모델 동시 보관).</param>
    /// <param name="imgsz">입력 크기(긴 변). 0 이면 학습 때 쓴 imgsz 를 best.pt 에서 읽어 자동으로 맞춘다.</param>
    /// <param name="rect">
    /// true 면 데이터셋 이미지 비율로 직사각 입력([h,w], 32 배수)으로 내보낸다. 848×480 이면 864 학습 → 512×864 —
    /// 정사각 864×864 대비 레터박스 여백 계산이 빠져 CPU 추론이 약 40% 빨라진다(합성곱 모델이라 정확도 동일).
    /// </param>
    public async Task StartExportAsync(int opset = 12, int imgsz = 640, string? modality = null, bool rect = false)
    {
        if (IsBusy) throw new InvalidOperationException("이미 실행 중인 작업이 있습니다.");
        modality = modality == "rgb" ? "rgb" : modality == "ir" ? "ir" : LastConvertedModality;
        var paths = await GetPathsAsync() ?? throw new InvalidOperationException("캡처 폴더 미설정");
        var best = Path.Combine(paths.Runs, "hd", "weights", "best.pt");
        if (!File.Exists(best)) throw new FileNotFoundException("학습된 가중치(best.pt)가 없습니다. 먼저 학습을 완료하세요.", best);

        Directory.CreateDirectory(paths.Models);
        var script = Path.Combine(paths.Workspace, "_export.py");
        await File.WriteAllTextAsync(script, ExportPy);

        var py = await GetPythonAsync();
        var outName = $"weld_seg_{modality}.onnx";
        var spec = imgsz.ToString();
        if (rect)
        {
            var size = DatasetImageSize(paths.Dataset);
            if (size is { } sz) spec = $"rect:{imgsz}:{sz.W}x{sz.H}";
            else Emit("[내보내기] 데이터셋 이미지를 찾지 못해 정사각 입력으로 내보냅니다.");
        }
        var args = new[] { script, best, opset.ToString(), spec, paths.Models, outName };
        TrainTotalEpochs = 0; // 내보내기는 epoch 진행 개념 없음.
        Emit($"[내보내기] 시작 — {best} → {outName} (opset {opset}, imgsz {(imgsz > 0 ? imgsz : "학습값")}{(rect ? ", 직사각" : "")})");
        StartProcess(py, args, paths.Workspace, TrainingPhase.Exporting, "내보내기");
    }

    // ── U-Net 학습 / 내보내기 ─────────────────────────────────────────────────
    /// <summary>U-Net 학습 산출물 폴더(runs/unet). best.pt / last.pt.</summary>
    public static string UnetRunDir(TrainingPaths p) => Path.Combine(p.Runs, "unet");

    /// <summary>
    /// U-Net 학습(CPU, PyTorch). 데이터셋 변환이 만든 images/·masks/{train,val} 을 쓴다.
    /// 크롭 학습이라 이미지 크기와 무관하고, val Dice 기준으로 best.pt 를 저장·조기 종료한다.
    /// </summary>
    public async Task StartUnetTrainingAsync(int epochs, int batch, int crop, int patience, int baseChannels)
    {
        if (IsBusy) throw new InvalidOperationException("이미 실행 중인 작업이 있습니다.");
        var paths = await GetPathsAsync() ?? throw new InvalidOperationException("캡처 폴더 미설정");
        var masksTrain = Path.Combine(paths.Dataset, "masks", "train");
        if (!Directory.Exists(masksTrain) || Directory.GetFiles(masksTrain, "*.png").Length == 0)
            throw new FileNotFoundException("U-Net 용 마스크가 없습니다. ① 데이터셋 변환을 다시 실행하세요.", masksTrain);

        var script = await WriteUnetScriptAsync(paths);
        var py = await GetPythonAsync();
        var args = new[]
        {
            script, "train", "--data", paths.Dataset, "--out", UnetRunDir(paths),
            "--epochs", epochs.ToString(), "--batch", batch.ToString(), "--crop", crop.ToString(),
            "--patience", Math.Max(0, patience).ToString(), "--base", baseChannels.ToString(),
        };
        TrainTotalEpochs = epochs;
        TrainCurrentEpoch = 0;
        TrainStartUtc = DateTime.UtcNow;
        TrainEndUtc = null;
        Emit($"[U-Net 학습] 시작 — epochs={epochs} batch={batch} crop={crop} patience={(patience > 0 ? patience : "끔")} base={baseChannels}");
        StartProcess(py, args, paths.Workspace, TrainingPhase.Training, "U-Net 학습");
    }

    /// <summary>U-Net best.pt → models/weld_unet_{modality}.onnx (H/W 동적 축, opset 17).</summary>
    public async Task StartUnetExportAsync(string? modality = null)
    {
        if (IsBusy) throw new InvalidOperationException("이미 실행 중인 작업이 있습니다.");
        modality = modality == "rgb" ? "rgb" : modality == "ir" ? "ir" : LastConvertedModality;
        var paths = await GetPathsAsync() ?? throw new InvalidOperationException("캡처 폴더 미설정");
        var best = Path.Combine(UnetRunDir(paths), "best.pt");
        if (!File.Exists(best)) throw new FileNotFoundException("U-Net 가중치(best.pt)가 없습니다. 먼저 U-Net 학습을 완료하세요.", best);

        var script = await WriteUnetScriptAsync(paths);
        var py = await GetPythonAsync();
        var outPath = Path.Combine(paths.Models, $"weld_unet_{modality}.onnx");
        Directory.CreateDirectory(paths.Models);
        TrainTotalEpochs = 0;
        Emit($"[U-Net 내보내기] 시작 — {best} → {Path.GetFileName(outPath)}");
        StartProcess(py, new[] { script, "export", "--weights", best, "--out", outPath }, paths.Workspace,
            TrainingPhase.Exporting, "U-Net 내보내기");
    }

    // 임베드 리소스(weld_unet.py)를 작업 폴더로 꺼낸다(매번 덮어써 앱 버전과 스크립트를 일치시킴).
    private static async Task<string> WriteUnetScriptAsync(TrainingPaths paths)
    {
        Directory.CreateDirectory(paths.Workspace);
        var dst = Path.Combine(paths.Workspace, "_weld_unet.py");
        await using var src = typeof(WeldTrainingService).Assembly.GetManifestResourceStream("weld_unet.py")
            ?? throw new InvalidOperationException("내장 U-Net 스크립트(weld_unet.py)를 찾지 못했습니다.");
        await using var fs = File.Create(dst);
        await src.CopyToAsync(fs);
        return dst;
    }

    /// <summary>테스트용: 내장 U-Net 스크립트 본문.</summary>
    public static string ReadUnetScript()
    {
        using var s = typeof(WeldTrainingService).Assembly.GetManifestResourceStream("weld_unet.py")
            ?? throw new InvalidOperationException("weld_unet.py 리소스 없음");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    // ── 모델 목록 ────────────────────────────────────────────────────────────
    public async Task<List<ModelInfo>> ListModelsAsync()
    {
        var paths = await GetPathsAsync();
        var list = new List<ModelInfo>();
        if (paths is null) return list;
        void Add(string f)
        {
            var fi = new FileInfo(f);
            if (fi.Exists) list.Add(new ModelInfo(fi.FullName, fi.Name, fi.Length, fi.LastWriteTime));
        }
        if (Directory.Exists(paths.Models))
            foreach (var f in Directory.GetFiles(paths.Models, "*.onnx")) Add(f);
        // 학습 run 안에 남은 best.onnx 도 노출.
        var runBest = Path.Combine(paths.Runs, "hd", "weights", "best.onnx");
        if (File.Exists(runBest) && !list.Any(m => m.Path == runBest)) Add(runBest);
        return list.OrderByDescending(m => m.Modified).ToList();
    }

    // ── 프로세스 정지 ─────────────────────────────────────────────────────────
    public void Stop()
    {
        lock (_gate)
        {
            if (_proc is { HasExited: false })
            {
                try { _proc.Kill(entireProcessTree: true); Emit("[중단] 사용자 요청으로 프로세스를 종료했습니다."); }
                catch (Exception ex) { Emit($"[중단] 종료 실패: {ex.Message}"); }
            }
        }
    }

    // ── 서브프로세스 실행(스트리밍) ───────────────────────────────────────────
    private void StartProcess(string exe, IEnumerable<string> args, string workDir, TrainingPhase phase, string label)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // 파이썬 출력 버퍼링 해제 — 진행 로그를 실시간으로 받기 위함.
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => Emit(e.Data);
        proc.ErrorDataReceived += (_, e) => Emit(e.Data);
        proc.Exited += (_, _) =>
        {
            int code = -1;
            try { code = proc.ExitCode; } catch { /* 이미 정리됨 */ }
            TrainEndUtc = DateTime.UtcNow; // 경과시간 고정(정지/완료/실패 시점).
            Phase = code == 0 ? TrainingPhase.Done : TrainingPhase.Error;
            StatusText = code == 0 ? $"{label} 완료" : $"{label} 실패(코드 {code})";
            Emit($"[{label}] 종료 코드 {code}");
            lock (_gate) { proc.Dispose(); if (ReferenceEquals(_proc, proc)) _proc = null; }
        };

        try
        {
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            lock (_gate) _proc = proc;
            Phase = phase;
            StatusText = $"{label} 진행 중…";
        }
        catch (Exception ex)
        {
            Phase = TrainingPhase.Error;
            StatusText = $"{label} 시작 실패";
            Emit($"[{label}] 시작 실패: {ex.Message}");
            _log.LogError(ex, "Training subprocess start failed");
        }
    }

    private static async Task<(bool Ok, string Output, string Error)> RunQuickAsync(string exe, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return (false, "", "프로세스 시작 실패");
            var so = await p.StandardOutput.ReadToEndAsync();
            var se = await p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(15000)) { try { p.Kill(true); } catch { } return (false, so, "시간 초과"); }
            return (p.ExitCode == 0, so, se);
        }
        catch (Exception ex) { return (false, "", ex.Message); }
    }

    /// <summary>데이터셋 첫 이미지의 (W,H). 직사각 내보내기 비율 산출용. 없으면 null.</summary>
    private static (int W, int H)? DatasetImageSize(string dataset)
    {
        foreach (var sub in new[] { "train", "val" })
        {
            var dir = Path.Combine(dataset, "images", sub);
            if (!Directory.Exists(dir)) continue;
            var f = Directory.GetFiles(dir, "*.png").OrderBy(x => x).FirstOrDefault();
            if (f is null) continue;
            using var m = CvIo.ReadMat(f, ImreadModes.Unchanged);
            if (!m.Empty()) return (m.Width, m.Height);
        }
        return null;
    }

    /// <summary>
    /// 직사각 내보내기 입력 크기 [h, w]. 긴 변 = size, 짧은 변 = size·(짧은/긴) 을 32 배수로 올림.
    /// 학습 레터박스와 같은 축척이 되도록 긴 변 기준(ExportPy 와 같은 규칙 — 테스트용으로 C# 에도 둔다).
    /// </summary>
    public static (int H, int W) RectExportSize(int size, int imgW, int imgH)
    {
        int Up32(double v) => (int)Math.Ceiling(v / 32.0) * 32;
        return imgW >= imgH
            ? (Math.Min(size, Up32(size * (double)imgH / imgW)), size)
            : (size, Math.Min(size, Up32(size * (double)imgW / imgH)));
    }

    private static void RecreateDir(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        Directory.CreateDirectory(path);
    }

    // 파이썬 러너 스크립트(임베드) — ultralytics API 를 sys.argv 로 구동.
    private const string TrainPy =
        "import sys\n" +
        "from ultralytics import YOLO\n" +
        "a = sys.argv[1:]\n" +
        "model, data, imgsz, epochs, batch, device, project, name, aug, bright = a[:10]\n" +
        "patience = int(a[10]) if len(a) > 10 else 0\n" +
        "kw = dict(data=data, imgsz=int(imgsz), epochs=int(epochs), batch=int(batch),\n" +
        "          device=device, project=project, name=name, exist_ok=True, plots=True, single_cls=True)\n" +
        "if patience > 0:\n" +
        "    kw.update(patience=patience)\n" +
        "if aug == 'min':\n" +
        "    kw.update(mosaic=0.0, close_mosaic=0, hsv_h=0.0, hsv_s=0.0, hsv_v=0.2,\n" +
        "              translate=0.0, scale=0.0, fliplr=0.0, erasing=0.0)\n" +
        "elif aug == 'line':\n" +
        "    # 가는 선 전용: mosaic/copy-paste/erasing 은 선을 끊거나 엉뚱하게 이어 붙여 끔.\n" +
        "    # 뒤집기·소각 회전·이동·스케일·밝기로 위치·각도·조명 일반화(진행축 가로/세로는 90° 회전 증강본으로).\n" +
        "    kw.update(mosaic=0.0, close_mosaic=0, mixup=0.0, copy_paste=0.0, erasing=0.0,\n" +
        "              fliplr=0.5, flipud=0.5, degrees=8.0, translate=0.1, scale=0.25,\n" +
        "              shear=0.0, perspective=0.0, hsv_h=0.0, hsv_s=0.0, hsv_v=0.4)\n" +
        "if bright == '1':\n" +
        "    kw.update(hsv_v=0.5, hsv_s=0.3)  # 밝기/채도 변동 강화(조명·노출 편차 대응)\n" +
        "YOLO(model).train(**kw)\n" +
        "print('TRAIN_DONE')\n";

    // imgsz 인자: "0"=학습값 자동, "864"=정사각, "rect:{size}:{W}x{H}"=데이터 비율 직사각([h,w], 32 배수, size 0 이면 학습값).
    private const string ExportPy =
        "import sys, shutil, os, math\n" +
        "from ultralytics import YOLO\n" +
        "best, opset, spec, outdir = sys.argv[1], int(sys.argv[2]), sys.argv[3], sys.argv[4]\n" +
        "outname = sys.argv[5] if len(sys.argv) > 5 else 'weld_seg.onnx'\n" +
        "m = YOLO(best)\n" +
        "tr = m.overrides.get('imgsz', 640)\n" +
        "tr = int(max(tr)) if isinstance(tr, (list, tuple)) else int(tr)\n" +
        "up32 = lambda v: int(math.ceil(v / 32.0) * 32)\n" +
        "if spec.startswith('rect:'):\n" +
        "    _, s, wh = spec.split(':')\n" +
        "    s = int(s) or tr\n" +
        "    W, H = map(int, wh.split('x'))\n" +
        "    imgsz = [min(s, up32(s * H / W)), s] if W >= H else [s, min(s, up32(s * W / H))]\n" +
        "else:\n" +
        "    imgsz = int(spec) or tr\n" +
        "print('EXPORT_IMGSZ', imgsz, '(train', tr, ')')\n" +
        "path = m.export(format='onnx', opset=opset, imgsz=imgsz)\n" +
        "os.makedirs(outdir, exist_ok=True)\n" +
        "dst = os.path.join(outdir, outname)\n" +
        "shutil.copyfile(path, dst)\n" +
        "print('EXPORT_DONE', dst)\n";
}

/// <summary>학습 증강 프리셋.</summary>
public enum TrainingAugmentation { Line, Minimal, Full }

public enum TrainingPhase { Idle, Converting, Training, Exporting, Done, Error }

public sealed record TrainingPaths(string CaptureDir, string Workspace, string Dataset, string Runs, string Models);
public sealed record PythonInfo(bool PythonOk, bool UltralyticsOk, string Exe, string Detail, bool TorchOk = false);
public sealed record ConvertResult(int Used, int Skipped, int Instances, string DataYaml, int Train = 0, int Val = 0, int Sessions = 0);
public sealed record RotateResult(int Created, int Skipped);
public sealed record ModelInfo(string Path, string Name, long Bytes, DateTime Modified);
