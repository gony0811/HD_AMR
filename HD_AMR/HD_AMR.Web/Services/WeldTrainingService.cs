using System.Diagnostics;
using HD_AMR.Service;
using OpenCvSharp;

namespace HD_AMR.Web.Services;

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
        var pyVer = (ver.Output + ver.Error).Trim();
        return u.Ok
            ? new PythonInfo(true, true, py, $"{pyVer} · ultralytics {u.Output.Trim()}")
            : new PythonInfo(true, false, py, $"{pyVer} · ultralytics 미설치 (pip install ultralytics)");
    }

    // ── ① 마스크 → YOLO-seg 데이터셋 변환(네이티브) ────────────────────────────
    /// <param name="modality">"rgb" 또는 "ir" — 이 모달리티의 마스크 초안만 사용.</param>
    public async Task<ConvertResult> ConvertAsync(string modality)
    {
        modality = modality == "ir" ? "ir" : "rgb";
        var paths = await GetPathsAsync();
        if (paths is null) throw new InvalidOperationException("캡처 저장 폴더가 설정되지 않았습니다.");
        if (!Directory.Exists(paths.CaptureDir)) throw new DirectoryNotFoundException(paths.CaptureDir);

        var imagesTrain = Path.Combine(paths.Dataset, "images", "train");
        var labelsTrain = Path.Combine(paths.Dataset, "labels", "train");
        // 매 변환마다 train 폴더를 새로 만든다(오래된 라벨/이미지 잔재 제거).
        RecreateDir(imagesTrain);
        RecreateDir(labelsTrain);

        int used = 0, skipped = 0, instances = 0;
        var maskGlob = $"*_{modality}_maskdraft.png";
        var masks = Directory.Exists(paths.CaptureDir)
            ? Directory.GetFiles(paths.CaptureDir, maskGlob).OrderBy(x => x).ToArray()
            : Array.Empty<string>();

        foreach (var maskPath in masks)
        {
            var fname = Path.GetFileName(maskPath);
            var stem = fname[..^$"_{modality}_maskdraft.png".Length];
            var imgPath = Path.Combine(paths.CaptureDir, $"{stem}_{modality}.png");
            if (!File.Exists(imgPath)) { skipped++; Emit($"[변환] 원본 없음 → 건너뜀: {stem}_{modality}.png"); continue; }

            var (label, n) = await Task.Run(() => MaskToYoloLabel(maskPath));
            if (n == 0) { skipped++; Emit($"[변환] 마스크 비어있음 → 건너뜀: {fname}"); continue; }

            File.Copy(imgPath, Path.Combine(imagesTrain, $"{stem}_{modality}.png"), overwrite: true);
            await File.WriteAllTextAsync(Path.Combine(labelsTrain, $"{stem}_{modality}.txt"), label);
            used++; instances += n;
            Emit($"[변환] {stem}_{modality}: 폴리곤 {n}개");
        }

        // data.yaml — 표본이 적어 val = train 로 두고 과적합 확인(파이프라인 점검용).
        var yaml =
            $"path: {paths.Dataset.Replace('\\', '/')}\n" +
            "train: images/train\n" +
            "val: images/train\n" +
            "nc: 1\n" +
            "names: [bead]\n";
        var yamlPath = Path.Combine(paths.Dataset, "data.yaml");
        Directory.CreateDirectory(paths.Dataset);
        await File.WriteAllTextAsync(yamlPath, yaml);

        Emit($"[변환] 완료 — 사용 {used}건 / 건너뜀 {skipped}건 / 폴리곤 {instances}개 → {yamlPath}");
        return new ConvertResult(used, skipped, instances, yamlPath);
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
                        using var m = Cv2.ImRead(src, ImreadModes.Unchanged);
                        if (m.Empty()) continue;
                        using var r = new Mat();
                        Cv2.Rotate(m, r, flag);
                        Cv2.ImWrite(Path.Combine(dir, $"{newStem}_{kind}.png"), r);
                        any = true;
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
        using var gray = Cv2.ImRead(maskPath, ImreadModes.Grayscale);
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
    public async Task StartTrainingAsync(int epochs, int imgsz, int batch, string baseModel, bool minimalAug)
    {
        if (IsBusy) throw new InvalidOperationException("이미 실행 중인 작업이 있습니다.");
        var paths = await GetPathsAsync() ?? throw new InvalidOperationException("캡처 폴더 미설정");
        var dataYaml = Path.Combine(paths.Dataset, "data.yaml");
        if (!File.Exists(dataYaml)) throw new FileNotFoundException("먼저 데이터셋 변환을 실행하세요.", dataYaml);

        Directory.CreateDirectory(paths.Runs);
        var script = Path.Combine(paths.Workspace, "_train.py");
        await File.WriteAllTextAsync(script, TrainPy);

        var py = await GetPythonAsync();
        var aug = minimalAug ? "min" : "full";
        var args = new[]
        {
            script, baseModel, dataYaml, imgsz.ToString(), epochs.ToString(),
            batch.ToString(), "cpu", paths.Runs, "hd", aug
        };
        // 진행 상태 초기화(진행 바·ETA).
        TrainTotalEpochs = epochs;
        TrainCurrentEpoch = 0;
        TrainStartUtc = DateTime.UtcNow;
        Emit($"[학습] 시작 — model={baseModel} epochs={epochs} imgsz={imgsz} batch={batch} device=cpu 증강={(minimalAug ? "최소" : "기본")}");
        StartProcess(py, args, paths.Workspace, TrainingPhase.Training, "학습");
    }

    // ── ③ ONNX 내보내기 ──────────────────────────────────────────────────────
    public async Task StartExportAsync(int opset = 12, int imgsz = 640)
    {
        if (IsBusy) throw new InvalidOperationException("이미 실행 중인 작업이 있습니다.");
        var paths = await GetPathsAsync() ?? throw new InvalidOperationException("캡처 폴더 미설정");
        var best = Path.Combine(paths.Runs, "hd", "weights", "best.pt");
        if (!File.Exists(best)) throw new FileNotFoundException("학습된 가중치(best.pt)가 없습니다. 먼저 학습을 완료하세요.", best);

        Directory.CreateDirectory(paths.Models);
        var script = Path.Combine(paths.Workspace, "_export.py");
        await File.WriteAllTextAsync(script, ExportPy);

        var py = await GetPythonAsync();
        var args = new[] { script, best, opset.ToString(), imgsz.ToString(), paths.Models };
        TrainTotalEpochs = 0; // 내보내기는 epoch 진행 개념 없음.
        Emit($"[내보내기] 시작 — {best} → ONNX(opset {opset}, imgsz {imgsz})");
        StartProcess(py, args, paths.Workspace, TrainingPhase.Exporting, "내보내기");
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

    private static void RecreateDir(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        Directory.CreateDirectory(path);
    }

    // 파이썬 러너 스크립트(임베드) — ultralytics API 를 sys.argv 로 구동.
    private const string TrainPy =
        "import sys\n" +
        "from ultralytics import YOLO\n" +
        "model, data, imgsz, epochs, batch, device, project, name, aug = sys.argv[1:10]\n" +
        "kw = dict(data=data, imgsz=int(imgsz), epochs=int(epochs), batch=int(batch),\n" +
        "          device=device, project=project, name=name, exist_ok=True, plots=True)\n" +
        "if aug == 'min':\n" +
        "    kw.update(mosaic=0.0, close_mosaic=0, hsv_h=0.0, hsv_s=0.0, hsv_v=0.2,\n" +
        "              translate=0.0, scale=0.0, fliplr=0.0, erasing=0.0)\n" +
        "YOLO(model).train(**kw)\n" +
        "print('TRAIN_DONE')\n";

    private const string ExportPy =
        "import sys, shutil, os\n" +
        "from ultralytics import YOLO\n" +
        "best, opset, imgsz, outdir = sys.argv[1], int(sys.argv[2]), int(sys.argv[3]), sys.argv[4]\n" +
        "path = YOLO(best).export(format='onnx', opset=opset, imgsz=imgsz)\n" +
        "os.makedirs(outdir, exist_ok=True)\n" +
        "dst = os.path.join(outdir, 'weld_seg.onnx')\n" +
        "shutil.copyfile(path, dst)\n" +
        "print('EXPORT_DONE', dst)\n";
}

public enum TrainingPhase { Idle, Converting, Training, Exporting, Done, Error }

public sealed record TrainingPaths(string CaptureDir, string Workspace, string Dataset, string Runs, string Models);
public sealed record PythonInfo(bool PythonOk, bool UltralyticsOk, string Exe, string Detail);
public sealed record ConvertResult(int Used, int Skipped, int Instances, string DataYaml);
public sealed record RotateResult(int Created, int Skipped);
public sealed record ModelInfo(string Path, string Name, long Bytes, DateTime Modified);
