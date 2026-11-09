using System.Diagnostics;
using HD.AMR.App.Service;
using OpenCvSharp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace HD.AMR.App.Service.Vision;

/// <summary>
/// DL 비드 세그멘테이션(YOLOv8-seg, CPU) 학습 파이프라인의 UI 오케스트레이터.
/// ① 마스크 초안 → YOLO-seg 라벨 데이터셋 변환은 .NET(OpenCvSharp)으로 네이티브 수행.
/// ② YOLO 학습·ONNX 내보내기(③ U-Net 도 같은 방식)는 앱이 Python(ultralytics)을 서브프로세스로 실행하고 로그를 스트리밍한다.
/// 학습 프로세스는 페이지 이동/서킷과 무관하게 살아 있어야 하므로 이 서비스는 싱글톤으로 상태를 보관하며,
/// 캡처 폴더/파이썬 경로 등 스코프 의존(ParameterService→DbContext)은 필요 시 스코프를 열어 조회한다.
/// </summary>
public sealed class WeldTrainingService
{
    public const string PythonExeKey = "Vision.Python.Exe";
    /// <summary>데이터셋 변환 학습 폴더 목록(줄바꿈 구분). 비어 있으면 라벨링 폴더 1개.</summary>
    public const string TrainDirsKey = "Vision.Dataset.TrainDirs";
    /// <summary>데이터셋 변환 검증 폴더(선택). 비어 있으면 촬영 세션 단위 자동 분리.</summary>
    public const string ValDirKey = "Vision.Dataset.ValDir";
    /// <summary>데이터셋 변환 시 시계 90° 회전본 포함 여부("1"/"0", 기본 포함).</summary>
    public const string AddRotatedKey = "Vision.Dataset.AddRotated";
    /// <summary>IR 데이터셋에 이미터 OFF(도트 없음)로 추정되는 촬영본만 쓸지("1"/"0", 기본 0). RGB 에는 영향 없음.</summary>
    public const string EmitterOffOnlyKey = "Vision.Dataset.EmitterOffOnly";
    /// <summary>데이터셋 변환 시 이미지에 CLAHE 전처리("1"/"0", 기본 0). 이 데이터셋으로 학습한 모델은 이름에 _clahe 가 붙는다.</summary>
    public const string ClaheKey = "Vision.Dataset.Clahe";
    /// <summary>데이터셋·학습 run 폴더의 전처리 표식 파일(내용 "clahe"). 없으면 전처리 없음.</summary>
    public const string PreprocessMarker = "_preprocess.txt";

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

    // 마지막으로 데이터셋 변환한 모달리티("ir"/"rgb"). 내보내기 파일명 weld_yolo_/weld_unet_{modality}.onnx 기본값.
    public string LastConvertedModality { get; private set; } = "ir";
    // 마지막 YOLO 학습에 쓴 데이터셋 폴더(직사각 내보내기 비율 산출용).
    private string? _lastYoloDataset;

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

    /// <summary>라벨링 폴더(Vision.Label.Dir, 비어 있으면 캡처 저장 폴더). 없으면 null.</summary>
    public async Task<string?> GetLabelDirAsync()
    {
        using var s = _scopes.CreateScope();
        var p = s.ServiceProvider.GetRequiredService<ParameterService>();
        var dir = await p.GetAsync(LabelDataService.LabelDirKey);
        if (string.IsNullOrWhiteSpace(dir)) dir = await p.GetAsync(LabelDataService.CaptureDirKey);
        return string.IsNullOrWhiteSpace(dir) ? null : dir;
    }

    /// <summary>저장된 데이터셋 원본 폴더 구성. 학습 폴더가 비어 있으면 라벨링 폴더 1개.</summary>
    public async Task<DatasetSources> GetDatasetSourcesAsync()
    {
        using var s = _scopes.CreateScope();
        var p = s.ServiceProvider.GetRequiredService<ParameterService>();
        var train = LabelFiles.NormalizeDirs((await p.GetAsync(TrainDirsKey) ?? "").Split('\n'));
        if (train.Count == 0 && await GetLabelDirAsync() is { } d) train.Add(d);
        var val = await p.GetAsync(ValDirKey);
        return new DatasetSources(train, string.IsNullOrWhiteSpace(val) ? null : val.Trim());
    }

    public async Task SetDatasetSourcesAsync(IEnumerable<string> trainDirs, string? valDir)
    {
        using var s = _scopes.CreateScope();
        var p = s.ServiceProvider.GetRequiredService<ParameterService>();
        await p.SetAsync(TrainDirsKey, string.Join('\n', LabelFiles.NormalizeDirs(trainDirs)), "데이터셋 변환 학습 폴더(줄바꿈 구분)");
        await p.SetAsync(ValDirKey, valDir?.Trim() ?? "", "데이터셋 변환 검증 폴더(빈값=세션 자동 분리)");
    }

    /// <summary>데이터셋 변환 시 90° 회전본 포함 여부(저장값 없으면 포함).</summary>
    public async Task<bool> GetAddRotatedAsync()
    {
        using var s = _scopes.CreateScope();
        var v = await s.ServiceProvider.GetRequiredService<ParameterService>().GetAsync(AddRotatedKey);
        return v?.Trim() != "0";
    }

    public async Task SetAddRotatedAsync(bool on)
    {
        using var s = _scopes.CreateScope();
        await s.ServiceProvider.GetRequiredService<ParameterService>()
            .SetAsync(AddRotatedKey, on ? "1" : "0", "데이터셋 변환 시 시계 90° 회전본 포함(1/0)");
    }

    /// <summary>IR 데이터셋 '이미터 OFF 만' 여부(저장값 없으면 끔).</summary>
    public async Task<bool> GetEmitterOffOnlyAsync()
    {
        using var s = _scopes.CreateScope();
        var v = await s.ServiceProvider.GetRequiredService<ParameterService>().GetAsync(EmitterOffOnlyKey);
        return v?.Trim() == "1";
    }

    public async Task SetEmitterOffOnlyAsync(bool on)
    {
        using var s = _scopes.CreateScope();
        await s.ServiceProvider.GetRequiredService<ParameterService>()
            .SetAsync(EmitterOffOnlyKey, on ? "1" : "0", "IR 데이터셋 변환 시 이미터 OFF(도트 없음) 촬영본만 사용(1/0)");
    }

    public async Task<bool> GetClaheAsync()
    {
        using var s = _scopes.CreateScope();
        var v = await s.ServiceProvider.GetRequiredService<ParameterService>().GetAsync(ClaheKey);
        return v?.Trim() == "1";
    }

    public async Task SetClaheAsync(bool on)
    {
        using var s = _scopes.CreateScope();
        await s.ServiceProvider.GetRequiredService<ParameterService>()
            .SetAsync(ClaheKey, on ? "1" : "0", "데이터셋 변환 시 CLAHE 전처리(1/0) — 이 데이터셋으로 학습한 모델은 _clahe");
    }

    /// <summary>폴더(데이터셋 또는 학습 run)에 CLAHE 표식이 있는지.</summary>
    public static bool HasClaheMarker(string dir)
    {
        try { var f = Path.Combine(dir, PreprocessMarker); return File.Exists(f) && File.ReadAllText(f).Trim() == "clahe"; }
        catch (IOException) { return false; }
    }

    // 학습 시작 때 데이터셋의 전처리 표식을 run 폴더로 복사 — 내보내기 이름(_clahe)은 '학습에 실제로 쓴' 데이터셋 기준.
    private static void CopyPreprocessMarker(string dataset, string runDir)
    {
        Directory.CreateDirectory(runDir);
        var dst = Path.Combine(runDir, PreprocessMarker);
        if (HasClaheMarker(dataset)) File.WriteAllText(dst, "clahe");
        else if (File.Exists(dst)) File.Delete(dst);
    }

    /// <summary>마지막 학습(best.pt)이 CLAHE 데이터셋으로 학습했는지 — 내보내기 파일명의 _clahe 표식.</summary>
    public async Task<bool> TrainedClaheAsync(bool unet)
    {
        var paths = await GetPathsAsync();
        return paths is not null && HasClaheMarker(unet ? UnetRunDir(paths) : Path.Combine(paths.Runs, "hd"));
    }

    // ── 학습 시점 데이터셋 정보(모달리티·원본 이미지 크기) ─────────────────────────
    // 학습 뒤 데이터셋을 다른 모달리티/해상도로 다시 변환해도, 내보내기는 '학습에 실제로 쓴' 값으로 이름(_ir/_rgb)과
    // 직사각 입력 비율을 정한다. 기록이 없는 예전 run 은 현재 데이터셋에서 추정(경고 로그).
    public const string TrainedMetaMarker = "_trained_meta.txt";

    /// <summary>데이터셋 이미지 파일명(…_ir.png / …_rgb.png)으로 본 모달리티. 판단 불가면 null.</summary>
    public static string? DatasetModality(string dataset)
    {
        foreach (var sub in new[] { "train", "val" })
        {
            var dir = Path.Combine(dataset, "images", sub);
            if (!Directory.Exists(dir)) continue;
            var f = Directory.EnumerateFiles(dir, "*.png").FirstOrDefault();
            if (f is null) continue;
            if (f.EndsWith("_ir.png", StringComparison.OrdinalIgnoreCase)) return "ir";
            if (f.EndsWith("_rgb.png", StringComparison.OrdinalIgnoreCase)) return "rgb";
        }
        return null;
    }

    private static void WriteTrainedMeta(string dataset, string runDir)
    {
        Directory.CreateDirectory(runDir);
        var lines = new List<string>();
        if (DatasetModality(dataset) is { } mod) lines.Add($"modality={mod}");
        if (DatasetImageSize(dataset) is { } sz) lines.Add($"size={sz.W}x{sz.H}");
        File.WriteAllLines(Path.Combine(runDir, TrainedMetaMarker), lines);
    }

    /// <summary>run 폴더의 학습 시점 기록. 없거나 항목이 빠지면 해당 값 null.</summary>
    public static (string? Modality, (int W, int H)? Size) ReadTrainedMeta(string runDir)
    {
        string? mod = null; (int W, int H)? size = null;
        try
        {
            var f = Path.Combine(runDir, TrainedMetaMarker);
            if (!File.Exists(f)) return (null, null);
            foreach (var line in File.ReadAllLines(f))
            {
                var kv = line.Split('=', 2);
                if (kv.Length != 2) continue;
                var v = kv[1].Trim();
                if (kv[0].Trim() == "modality" && v is "ir" or "rgb") mod = v;
                else if (kv[0].Trim() == "size" && v.Split('x') is [var w, var h] && int.TryParse(w, out var iw) && int.TryParse(h, out var ih))
                    size = (iw, ih);
            }
        }
        catch (IOException) { /* 기록 없음으로 취급 */ }
        return (mod, size);
    }

    /// <summary>
    /// 마지막 학습(best.pt)의 모달리티 — 내보내기 파일명의 _ir/_rgb 표식(④ 추론·운영이 입력 영상을 고르는 기준).
    /// 학습 시점 기록 → (예전 run) 현재 데이터셋 이미지 → 마지막 변환 모달리티 순. FromRecord=false 면 추정값.
    /// </summary>
    public async Task<(string Modality, bool FromRecord)> TrainedModalityAsync(bool unet)
    {
        var paths = await GetPathsAsync();
        if (paths is null) return (LastConvertedModality, false);
        var runDir = unet ? UnetRunDir(paths) : Path.Combine(paths.Runs, "hd");
        if (ReadTrainedMeta(runDir).Modality is { } m) return (m, true);
        var kind = await TrainedKindAsync(unet);
        return (DatasetModality(paths.DatasetOf(kind)) ?? LastConvertedModality, false);
    }

    /// <summary>'이미터 OFF 만' 필터에서 제외할 IR 촬영본인지 — 이미지로 추정한 상태가 OFF 가 아니면(ON·불확실) 제외.</summary>
    public static bool ExcludedByEmitter(string modality, bool emitterOffOnly, string imagePath)
        => emitterOffOnly && modality == "ir" && EmitterDotEstimator.ClassifyFile(imagePath) != EmitterState.Off;

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

    // ── ① 라벨 → 데이터셋 변환 ────────────────────────────────────────────────
    /// <summary>저장된 폴더 구성·회전 옵션으로 면 라벨 데이터셋 변환(웹 호환용).</summary>
    public async Task<ConvertResult> ConvertAsync(string modality)
    {
        var src = await GetDatasetSourcesAsync();
        return await ConvertAsync(modality, LabelKind.Area, src.TrainDirs, src.ValDir, await GetAddRotatedAsync(),
            await GetEmitterOffOnlyAsync(), await GetClaheAsync());
    }

    /// <summary>
    /// 여러 학습 폴더(+선택적 검증 폴더)의 라벨을 모아 <c>dl/dataset_{area|line}</c> 에 YOLO-seg 데이터셋
    /// (images·labels·masks/{train,val} + data.yaml)을 만든다. 원본 폴더의 파일은 바꾸지 않는다(복사만).
    /// </summary>
    /// <param name="modality">"rgb" 또는 "ir".</param>
    /// <param name="kind">면 = <c>_area.png</c>, 선 = <c>_line.json</c> 을 두께대로 래스터화한 마스크.</param>
    /// <param name="valDir">지정하면 이 폴더가 val, 비우면 학습 폴더들을 촬영 세션 단위로 자동 분리.</param>
    /// <param name="addRotated">
    /// true 면 각 촬영본의 시계 90° 회전본(이미지+마스크)을 <b>데이터셋 안에만</b> 추가한다(<c>{stem}_cw90</c>, 원본과 같은 split).
    /// 반시계 방향은 학습 증강의 상하·좌우 뒤집기가 만든다. 원본 폴더에 예전에 만든 회전본 파일은 중복을 막으려고 제외한다.
    /// </param>
    /// <param name="emitterOffOnly">IR 이면 이미터 OFF(도트 없음)로 추정되는 촬영본만 쓴다(<see cref="EmitterDotEstimator"/>).</param>
    /// <param name="clahe">데이터셋 이미지에 CLAHE(<see cref="ImageClahe"/>) 적용 — 원본 폴더는 그대로, 데이터셋 사본만. 마스크·라벨은 그대로.</param>
    public async Task<ConvertResult> ConvertAsync(string modality, LabelKind kind, IReadOnlyList<string> trainDirs, string? valDir,
        bool addRotated = false, bool emitterOffOnly = false, bool clahe = false)
    {
        modality = LabelFiles.Mod(modality);
        LastConvertedModality = modality;
        var paths = await GetPathsAsync() ?? throw new InvalidOperationException("캡처 저장 폴더(작업 폴더 기준)가 설정되지 않았습니다.");

        var train = LabelFiles.NormalizeDirs(trainDirs);
        var val = string.IsNullOrWhiteSpace(valDir) ? null : LabelFiles.NormalizeDirs(new[] { valDir })[0];
        if (val is not null) train.RemoveAll(d => LabelFiles.SameDir(d, val));   // 같은 폴더가 양쪽에 있으면 val 우선
        if (train.Count == 0) throw new InvalidOperationException("학습 폴더를 1개 이상 지정하세요.");
        foreach (var d in val is null ? train : train.Append(val))
            if (!Directory.Exists(d)) throw new DirectoryNotFoundException(d);

        var dataset = paths.DatasetOf(kind);
        var staging = Path.Combine(dataset, "_staging");   // 선 라벨 래스터화·회전본 임시 폴더
        // 매 변환마다 train/val 폴더를 새로 만든다(오래된 라벨/이미지 잔재 제거).
        foreach (var split in new[] { "train", "val" })
        {
            RecreateDir(Path.Combine(dataset, "images", split));
            RecreateDir(Path.Combine(dataset, "labels", split));
            RecreateDir(Path.Combine(dataset, "masks", split));   // U-Net 용 이진 마스크(이미지와 같은 파일명)
        }
        RecreateDir(staging);

        Emit($"[변환] {modality.ToUpperInvariant()} · {LabelFiles.KindText(kind)} 라벨{(addRotated ? " + 90° 회전본" : "")} → {dataset}");
        int used = 0, skipped = 0, instances = 0;

        // 1) 폴더별 라벨 수집. 같은 stem 이 여러 폴더에 있으면 먼저 나온 것만 쓴다(중복 학습·val 누수 방지).
        var srcs = new List<(string Stem, string ImgPath, string MaskPath, bool IsVal)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var dirs = train.Select(d => (Dir: d, IsVal: false)).ToList();
        if (val is not null) dirs.Add((val, true));
        foreach (var (dir, isVal) in dirs)
        {
            var mig = LabelFiles.MigrateLegacy(dir);
            if (mig.ToArea + mig.Archived > 0)
                Emit($"[변환] 예전 _maskdraft 정리({dir}) — 면 라벨로 변경 {mig.ToArea}건 · 보관 {mig.Archived}건");
            int n0 = srcs.Count, oldRot = 0;
            foreach (var stem in LabelFiles.LabeledStems(dir, modality, kind, requireImage: false))
            {
                // 예전 방식으로 원본 폴더에 만든 회전본(_cw90/_ccw90)은 제외 — 회전본은 아래에서 데이터셋에만 만든다.
                if (DatasetSplitter.BaseStem(stem) != stem) { oldRot++; continue; }
                var imgPath = Path.Combine(dir, LabelFiles.ImageName(stem, modality));
                if (!File.Exists(imgPath)) { skipped++; Emit($"[변환] 원본 없음 → 건너뜀: {Path.GetFileName(imgPath)}"); continue; }
                if (ExcludedByEmitter(modality, emitterOffOnly, imgPath))
                {
                    skipped++;
                    Emit($"[변환] 이미터 {EmitterDotEstimator.Text(EmitterDotEstimator.ClassifyFile(imgPath))} → 제외(이미터 OFF 만): {stem}");
                    continue;
                }
                if (!seen.Add(stem)) { skipped++; Emit($"[변환] 다른 폴더와 이름 중복 → 건너뜀: {stem} ({dir})"); continue; }
                string maskPath;
                if (kind == LabelKind.Area) maskPath = Path.Combine(dir, LabelFiles.AreaName(stem, modality));
                else
                {
                    var line = WeldLineLabel.FromJson(await File.ReadAllTextAsync(Path.Combine(dir, LabelFiles.LineName(stem, modality))));
                    if (line is null) { skipped++; Emit($"[변환] 선 라벨 손상 → 건너뜀: {stem}"); continue; }
                    maskPath = Path.Combine(staging, $"{stem}_{modality}.png");
                    await Task.Run(() => LabelFiles.WriteLineMaskPng(line, maskPath));
                }
                srcs.Add((stem, imgPath, maskPath, isVal));
            }
            Emit($"[변환] {(isVal ? "검증" : "학습")} 폴더 {dir} — {srcs.Count - n0}건" +
                 (oldRot > 0 ? $" (폴더 안 예전 회전본 {oldRot}건 제외)" : ""));
        }

        // 회전본 — 원본 이미지·마스크를 시계 90° 회전해 임시 폴더에 만들고 같은 split 으로 넣는다.
        int rotated = 0;
        if (addRotated && srcs.Count > 0)
        {
            var rotDir = Path.Combine(staging, "rot");
            Directory.CreateDirectory(rotDir);
            var jobs = new List<(string Src, string Dst, bool Cw)>();
            var rot = new List<(string Stem, string ImgPath, string MaskPath, bool IsVal)>();
            foreach (var x in srcs)
            {
                var rs = x.Stem + "_cw90";
                var img = Path.Combine(rotDir, $"{rs}_{modality}.png");
                var msk = Path.Combine(rotDir, $"{rs}_{modality}_mask.png");
                jobs.Add((x.ImgPath, img, true));
                jobs.Add((x.MaskPath, msk, true));
                rot.Add((rs, img, msk, x.IsVal));
            }
            await RotatePngsAsync(jobs, paths.Workspace);
            rot = rot.Where(r => File.Exists(r.ImgPath) && File.Exists(r.MaskPath)).ToList();
            srcs.AddRange(rot);
            rotated = rot.Count;
            Emit($"[변환] 90° 회전본 {rotated}건 추가(데이터셋에만 생성)");
        }

        // 비-Windows 는 OpenCvSharp 네이티브가 없으므로 마스크→폴리곤만 Python(cv2)로 한 번에 계산한다.
        // 분할·복사·data.yaml 등 나머지는 아래 공통 코드가 그대로 처리해 OS 별 결과가 같다.
        var pyLabels = UsePythonCv && srcs.Count > 0
            ? await MaskToYoloLabelsViaPythonAsync(srcs.Select(x => x.MaskPath).ToList(), paths.Workspace)
            : null;

        // 2) 유효한 (stem, 라벨) 수집.
        var items = new List<(string Stem, string ImgPath, string MaskPath, string Label, int N, bool IsVal)>();
        foreach (var (stem, imgPath, maskPath, isVal) in srcs)
        {
            var (label, n) = pyLabels is not null
                ? pyLabels.GetValueOrDefault(maskPath, ("", 0))
                : await Task.Run(() => MaskToYoloLabel(maskPath));
            if (n == 0) { skipped++; Emit($"[변환] 라벨 비어있음 → 건너뜀: {stem}_{modality}"); continue; }
            items.Add((stem, imgPath, maskPath, label, n, isVal));
        }

        // 3) train/val 분리 — 검증 폴더가 있으면 그대로, 없으면 촬영 세션 단위(같은 자리 연속 프레임 누수 방지).
        // (회전본은 BaseStem 으로 원본과 같은 세션에 묶여 항상 원본과 같은 쪽으로 간다.)
        var split2 = DatasetSplitter.Split(items.Where(i => !i.IsVal).Select(i => i.Stem));
        HashSet<string> valSet;
        if (val is not null)
        {
            valSet = items.Where(i => i.IsVal).Select(i => i.Stem).ToHashSet();
            if (valSet.Count == 0) Emit("[변환] 경고: 검증 폴더에 쓸 수 있는 라벨이 없습니다.");
        }
        else
        {
            valSet = split2.Val.ToHashSet();
            if (split2.SingleSession)
                Emit("[변환] 경고: 촬영 세션이 1개뿐 — 같은 세션을 시간순으로 앞/뒤로 나눴습니다. " +
                     "검증 점수가 실제보다 좋게 나올 수 있으니 다른 위치·시간대 촬영본을 추가하세요.");
        }

        int nTrain = 0, nVal = 0;
        foreach (var (stem, imgPath, maskPath, label, n, _) in items)
        {
            var sub = valSet.Contains(stem) ? "val" : "train";
            if (sub == "val") nVal++; else nTrain++;
            var dstImg = Path.Combine(dataset, "images", sub, $"{stem}_{modality}.png");
            // CLAHE: 전체 프레임(회전본은 회전한 영상)에 적용 — 추론도 전체 프레임에 같은 함수를 건 뒤 ROI 를 자른다.
            if (clahe) await Task.Run(() => ImageClahe.ApplyFile(imgPath, dstImg));
            else File.Copy(imgPath, dstImg, overwrite: true);
            File.Copy(maskPath, Path.Combine(dataset, "masks", sub, $"{stem}_{modality}.png"), overwrite: true);
            await File.WriteAllTextAsync(Path.Combine(dataset, "labels", sub, $"{stem}_{modality}.txt"), label);
            used++; instances += n;
            Emit($"[변환] {stem}_{modality} → {sub}: 폴리곤 {n}개");
        }
        try { Directory.Delete(staging, recursive: true); } catch { /* 무시 */ }
        Emit(val is not null
            ? $"[변환] 검증 폴더 지정 → train {nTrain}건 / val {nVal}건"
            : $"[변환] 세션 {split2.SessionCount}개 → train {nTrain}건 / val {nVal}건");

        // 전처리 표식 — 학습 시작 때 run 폴더로 복사되어 내보내기 이름(_clahe)·추론 전처리를 정한다.
        var marker = Path.Combine(dataset, PreprocessMarker);
        if (clahe) await File.WriteAllTextAsync(marker, "clahe");
        else if (File.Exists(marker)) File.Delete(marker);
        if (clahe) Emit($"[변환] CLAHE 전처리 적용(clip {ImageClahe.ClipLimit}, 타일 {ImageClahe.Tiles}×{ImageClahe.Tiles}) — 이 데이터셋으로 학습한 모델은 이름에 _clahe");

        // data.yaml — val 이 비면(표본 1건 등) 파이프라인 점검용으로 train 을 val 로 쓴다.
        var valDirYaml = nVal > 0 ? "images/val" : "images/train";
        if (nVal == 0) Emit("[변환] 경고: 검증셋이 비어 val = train 으로 둡니다(과적합 확인만 가능).");
        var yaml =
            $"path: {dataset.Replace('\\', '/')}\n" +
            "train: images/train\n" +
            $"val: {valDirYaml}\n" +
            "nc: 1\n" +
            "names: [bead]\n";
        var yamlPath = Path.Combine(dataset, "data.yaml");
        Directory.CreateDirectory(dataset);
        await File.WriteAllTextAsync(yamlPath, yaml);

        Emit($"[변환] 완료 — 사용 {used}건 / 건너뜀 {skipped}건 / 폴리곤 {instances}개 → {yamlPath}");
        return new ConvertResult(used, skipped, instances, yamlPath, nTrain, nVal, split2.SessionCount, kind, rotated);
    }

    /// <summary>PNG 90° 회전 일괄 처리(채널·비트심도 유지). Windows = OpenCvSharp, 그 외 = Python cv2(같은 cv2.rotate).</summary>
    private async Task RotatePngsAsync(IReadOnlyList<(string Src, string Dst, bool Cw)> jobs, string workDir)
    {
        if (jobs.Count == 0) return;
        if (UsePythonCv) { await RotateImagesViaPythonAsync(jobs, workDir); return; }
        await Task.Run(() =>
        {
            foreach (var (src, dst, cw) in jobs)
            {
                using var m = CvIo.ReadMat(src, ImreadModes.Unchanged);   // 한글 경로 안전
                if (m.Empty()) continue;
                using var r = new Mat();
                Cv2.Rotate(m, r, cw ? RotateFlags.Rotate90Clockwise : RotateFlags.Rotate90Counterclockwise);
                CvIo.WriteMat(dst, r);
            }
        });
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

    // ── 비-Windows(Python cv2) 픽셀 연산 ─────────────────────────────────────
    // macOS/Linux 에서는 OpenCvSharp 네이티브(win-x64)를 로드할 수 없다. 그래서 OpenCV 가 필요한 픽셀 연산
    // (마스크→폴리곤, 이미지 회전)만 학습용과 같은 Python 환경의 cv2 로 일괄 수행하고, 분할·복사·data.yaml·
    // 선 라벨 회전 등 나머지 로직은 OS 무관 공통 C# 이 처리한다 — 변환 로직이 바뀌어도 두 OS 결과가 어긋나지 않게.
    private static bool UsePythonCv => !OperatingSystem.IsWindows();

    /// <summary>마스크들 → YOLO-seg 라벨(MaskToYoloLabel 과 동일 알고리즘). 키는 입력 마스크 경로.</summary>
    private async Task<Dictionary<string, (string Text, int Count)>> MaskToYoloLabelsViaPythonAsync(
        IReadOnlyList<string> masks, string workDir)
    {
        Directory.CreateDirectory(workDir);
        var inPath = Path.Combine(workDir, "_cv_labels_in.json");
        var outPath = Path.Combine(workDir, "_cv_labels_out.json");
        await File.WriteAllTextAsync(inPath, System.Text.Json.JsonSerializer.Serialize(masks));
        if (File.Exists(outPath)) File.Delete(outPath);

        await RunPyCollectAsync("_cv_labels.py", LabelsPy, new[] { inPath, outPath }, workDir);

        using var doc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(outPath));
        var map = new Dictionary<string, (string, int)>();
        foreach (var e in doc.RootElement.EnumerateObject())
            map[e.Name] = (e.Value.GetProperty("label").GetString() ?? "", e.Value.GetProperty("count").GetInt32());
        return map;
    }

    /// <summary>이미지 90° 회전 일괄 처리(Cv2.Rotate 와 동일). Cw=true 시계, false 반시계.</summary>
    private async Task RotateImagesViaPythonAsync(IReadOnlyList<(string Src, string Dst, bool Cw)> jobs, string workDir)
    {
        Directory.CreateDirectory(workDir);
        var inPath = Path.Combine(workDir, "_cv_rotate_in.json");
        var payload = jobs.Select(j => new { src = j.Src, dst = j.Dst, cw = j.Cw }).ToArray();
        await File.WriteAllTextAsync(inPath, System.Text.Json.JsonSerializer.Serialize(payload));
        await RunPyCollectAsync("_cv_rotate.py", RotateImgPy, new[] { inPath }, workDir);
    }

    // Python 스크립트를 동기 실행하며 출력 줄을 로그로 스트리밍하고, 전체 stdout 을 반환한다.
    private async Task<string> RunPyCollectAsync(string scriptName, string scriptBody, string[] args, string workDir, int timeoutMs = 300000)
    {
        Directory.CreateDirectory(workDir);
        var script = Path.Combine(workDir, scriptName);
        await File.WriteAllTextAsync(script, scriptBody);

        var py = await GetPythonAsync();
        var psi = new ProcessStartInfo
        {
            FileName = py,
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        psi.ArgumentList.Add(script);
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        var sb = new System.Text.StringBuilder();
        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) { Emit(e.Data); lock (sb) sb.AppendLine(e.Data); } };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) Emit(e.Data); };

        try
        {
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Python 실행 실패 — '{py}' 경로/설치를 확인하세요. ({ex.Message})", ex);
        }

        using var cts = new CancellationTokenSource(timeoutMs);
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { proc.Kill(true); } catch { } throw new TimeoutException("Python 처리 시간 초과."); }

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"Python 처리 실패(코드 {proc.ExitCode}). 로그를 확인하세요.");
        lock (sb) return sb.ToString();
    }

    // ── ② YOLO 학습 ───────────────────────────────────────────────────────────────
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
    /// <param name="kind">학습할 데이터셋(면 = dataset_area, 선 = dataset_line).</param>
    /// <param name="afterSuccess">학습이 정상 종료(코드 0, 조기 종료 포함)하면 이어서 실행할 작업(예: 자동 내보내기). 중단·실패면 실행 안 함.</param>
    public async Task StartTrainingAsync(int epochs, int imgsz, int batch, string baseModel,
        TrainingAugmentation aug, bool brightAug, int patience, LabelKind kind = LabelKind.Area, Func<Task>? afterSuccess = null)
    {
        if (IsBusy) throw new InvalidOperationException("이미 실행 중인 작업이 있습니다.");
        var paths = await GetPathsAsync() ?? throw new InvalidOperationException("캡처 폴더 미설정");
        var dataset = paths.DatasetOf(kind);
        var dataYaml = Path.Combine(dataset, "data.yaml");
        if (!File.Exists(dataYaml))
            throw new FileNotFoundException($"{LabelFiles.KindText(kind)} 데이터셋이 없습니다. ① 데이터셋에서 '{LabelFiles.KindText(kind)}' 으로 변환하세요.", dataYaml);
        _lastYoloDataset = dataset;

        Directory.CreateDirectory(paths.Runs);
        CopyPreprocessMarker(dataset, Path.Combine(paths.Runs, "hd"));
        WriteTrainedMeta(dataset, Path.Combine(paths.Runs, "hd"));
        var script = Path.Combine(paths.Workspace, "_train.py");
        await File.WriteAllTextAsync(script, TrainPy);

        var py = await GetPythonAsync();
        var augKey = aug switch { TrainingAugmentation.Minimal => "min", TrainingAugmentation.Line => "line", _ => "full" };
        // macOS(Apple Silicon)에서는 GPU(Metal/MPS)를 쓰면 CPU 대비 수 배 빠르다. 그 외 OS 는 기존처럼 CPU.
        // (MPS 가 일부 연산을 지원하지 않으면 StartProcess 의 PYTORCH_ENABLE_MPS_FALLBACK=1 로 CPU 폴백.)
        var device = OperatingSystem.IsMacOS() ? "mps" : "cpu";
        var args = new[]
        {
            script, baseModel, dataYaml, imgsz.ToString(), epochs.ToString(),
            batch.ToString(), device, paths.Runs, "hd", augKey, brightAug ? "1" : "0", Math.Max(0, patience).ToString()
        };
        // 진행 상태 초기화(진행 바·ETA).
        TrainTotalEpochs = epochs;
        TrainCurrentEpoch = 0;
        TrainStartUtc = DateTime.UtcNow;
        TrainEndUtc = null;
        Emit($"[학습] 시작 — 데이터셋={LabelFiles.KindText(kind)}({Path.GetFileName(dataset)}) model={baseModel} epochs={epochs} imgsz={imgsz} batch={batch} device={device} 증강={aug} 밝기증강={(brightAug ? "켬" : "끔")} patience={(patience > 0 ? patience : "끔")}");
        StartProcess(py, args, paths.Workspace, TrainingPhase.Training, "학습", afterSuccess);
    }

    // ── ② YOLO ONNX 내보내기 ──────────────────────────────────────────────────────
    /// <summary>운영 고정값 — ONNX Runtime(Windows) 호환 opset. UI 에서는 바꾸지 않는다.</summary>
    public const int YoloExportOpset = 12;

    /// <param name="opset">ONNX opset(기본 <see cref="YoloExportOpset"/>).</param>
    /// <param name="modality">"ir"/"rgb" — 출력 파일명 weld_yolo_{modality}_{area|line}_{yyMMdd_HHmm}.onnx (덮어쓰지 않음).
    /// null 이면 학습 시점 기록(<see cref="TrainedModalityAsync"/>)으로 정한다.</param>
    /// <param name="imgsz">입력 크기(긴 변). 0 이면 학습 때 쓴 imgsz 를 best.pt 에서 읽어 자동으로 맞춘다.</param>
    /// <param name="rect">
    /// true 면 학습 데이터셋 이미지 비율로 직사각 입력([h,w], 32 배수)으로 내보낸다. 848×480 이면 864 학습 → 512×864 —
    /// 정사각 864×864 대비 레터박스 여백 계산이 빠져 CPU 추론이 약 40% 빨라진다(합성곱 모델이라 정확도 동일).
    /// </param>
    public async Task StartExportAsync(int opset = YoloExportOpset, int imgsz = 0, string? modality = null, bool rect = true)
    {
        if (IsBusy) throw new InvalidOperationException("이미 실행 중인 작업이 있습니다.");
        if (modality is not ("rgb" or "ir"))
        {
            var (m, fromRecord) = await TrainedModalityAsync(unet: false);
            modality = m;
            if (!fromRecord) Emit($"[내보내기] 학습 시점 모달리티 기록이 없어(이전 버전 학습) 현재 데이터셋 기준 {m.ToUpperInvariant()} 로 정했습니다 — 다르면 파일명의 _ir/_rgb 를 확인하세요.");
        }
        var paths = await GetPathsAsync() ?? throw new InvalidOperationException("캡처 폴더 미설정");
        var best = Path.Combine(paths.Runs, "hd", "weights", "best.pt");
        if (!File.Exists(best)) throw new FileNotFoundException("학습된 가중치(best.pt)가 없습니다. 먼저 학습을 완료하세요.", best);

        Directory.CreateDirectory(paths.Models);
        var script = Path.Combine(paths.Workspace, "_export.py");
        await File.WriteAllTextAsync(script, ExportPy);

        var py = await GetPythonAsync();
        var kind = await TrainedKindAsync(unet: false);
        var outName = WeldModelNames.YoloExportName(modality, kind, DateTime.Now, await TrainedClaheAsync(unet: false));
        var spec = imgsz.ToString();
        if (rect)
        {
            var size = ReadTrainedMeta(Path.Combine(paths.Runs, "hd")).Size
                       ?? DatasetImageSize(_lastYoloDataset ?? paths.DatasetOf(kind))
                       ?? DatasetImageSize(paths.DatasetOf(LabelKind.Area))
                       ?? DatasetImageSize(paths.DatasetOf(LabelKind.Line));
            if (size is { } sz) spec = $"rect:{imgsz}:{sz.W}x{sz.H}";
            else Emit("[내보내기] 데이터셋 이미지를 찾지 못해 정사각 입력으로 내보냅니다.");
        }
        var args = new[] { script, best, opset.ToString(), spec, paths.Models, outName };
        TrainTotalEpochs = 0; // 내보내기는 epoch 진행 개념 없음.
        Emit($"[내보내기] 시작 — {best} → {outName} (opset {opset}, imgsz {(imgsz > 0 ? imgsz : "학습값")}{(rect ? ", 직사각" : "")})");
        StartProcess(py, args, paths.Workspace, TrainingPhase.Exporting, "내보내기");
    }

    // ── U-Net 학습 / 내보내기 ─────────────────────────────────────────────────
    private const string UnetKindMarker = "_dataset_kind.txt";

    /// <summary>
    /// 마지막 학습(best.pt)이 쓴 라벨 종류 — 내보내기 파일명의 area/line 표식.
    /// YOLO: ultralytics 가 남긴 runs/hd/args.yaml 의 data 경로(dataset_area/dataset_line).
    /// U-Net: 학습 시작 때 남긴 runs/unet/_dataset_kind.txt. 기록이 없으면 YOLO=면, U-Net=선(각 기본 데이터셋).
    /// </summary>
    public async Task<LabelKind> TrainedKindAsync(bool unet)
    {
        var paths = await GetPathsAsync();
        if (paths is null) return unet ? LabelKind.Line : LabelKind.Area;
        try
        {
            if (unet)
            {
                var f = Path.Combine(UnetRunDir(paths), UnetKindMarker);
                if (File.Exists(f)) return (await File.ReadAllTextAsync(f)).Trim() == "area" ? LabelKind.Area : LabelKind.Line;
                return LabelKind.Line;
            }
            var args = Path.Combine(paths.Runs, "hd", "args.yaml");
            if (File.Exists(args))
                foreach (var line in await File.ReadAllLinesAsync(args))
                    if (line.TrimStart().StartsWith("data:", StringComparison.Ordinal))
                    {
                        if (line.Contains(LabelFiles.DatasetFolder(LabelKind.Line))) return LabelKind.Line;
                        if (line.Contains(LabelFiles.DatasetFolder(LabelKind.Area))) return LabelKind.Area;
                    }
        }
        catch (IOException) { /* 기본값 */ }
        return _lastYoloDataset?.EndsWith(LabelFiles.DatasetFolder(LabelKind.Line)) == true ? LabelKind.Line : LabelKind.Area;
    }

    /// <summary>U-Net 학습 산출물 폴더(runs/unet). best.pt / last.pt.</summary>
    public static string UnetRunDir(TrainingPaths p) => Path.Combine(p.Runs, "unet");

    /// <summary>
    /// U-Net 학습(CPU, PyTorch). 데이터셋 변환이 만든 images/·masks/{train,val} 을 쓴다.
    /// 크롭 학습이라 이미지 크기와 무관하고, val Dice 기준으로 best.pt 를 저장·조기 종료한다.
    /// </summary>
    /// <param name="kind">학습할 데이터셋(선 = dataset_line 권장, 면 = dataset_area).</param>
    /// <param name="afterSuccess">정상 종료 시 이어서 실행할 작업(예: 자동 내보내기). 중단·실패면 실행 안 함.</param>
    public async Task StartUnetTrainingAsync(int epochs, int batch, int crop, int patience, int baseChannels,
        LabelKind kind = LabelKind.Line, Func<Task>? afterSuccess = null)
    {
        if (IsBusy) throw new InvalidOperationException("이미 실행 중인 작업이 있습니다.");
        var paths = await GetPathsAsync() ?? throw new InvalidOperationException("캡처 폴더 미설정");
        var dataset = paths.DatasetOf(kind);
        var masksTrain = Path.Combine(dataset, "masks", "train");
        if (!Directory.Exists(masksTrain) || Directory.GetFiles(masksTrain, "*.png").Length == 0)
            throw new FileNotFoundException($"{LabelFiles.KindText(kind)} 데이터셋이 없습니다. ① 데이터셋에서 '{LabelFiles.KindText(kind)}' 으로 변환하세요.", masksTrain);

        var script = await WriteUnetScriptAsync(paths);
        // 이 학습이 쓴 데이터셋 종류 기록 — 내보내기 파일명의 area/line 표식(앱을 다시 켜도 유지).
        Directory.CreateDirectory(UnetRunDir(paths));
        await File.WriteAllTextAsync(Path.Combine(UnetRunDir(paths), UnetKindMarker), WeldModelNames.KindToken(kind));
        CopyPreprocessMarker(dataset, UnetRunDir(paths));
        WriteTrainedMeta(dataset, UnetRunDir(paths));
        var py = await GetPythonAsync();
        // YOLO 학습과 같은 규칙 — macOS 는 Apple GPU(MPS), 그 외는 기존처럼 CPU. 못 쓰면 스크립트가 cpu 로 대체.
        var device = OperatingSystem.IsMacOS() ? "mps" : "cpu";
        var args = new[]
        {
            script, "train", "--data", dataset, "--out", UnetRunDir(paths),
            "--epochs", epochs.ToString(), "--batch", batch.ToString(), "--crop", crop.ToString(),
            "--patience", Math.Max(0, patience).ToString(), "--base", baseChannels.ToString(),
            "--device", device,
        };
        TrainTotalEpochs = epochs;
        TrainCurrentEpoch = 0;
        TrainStartUtc = DateTime.UtcNow;
        TrainEndUtc = null;
        Emit($"[U-Net 학습] 시작 — 데이터셋={LabelFiles.KindText(kind)}({Path.GetFileName(dataset)}) epochs={epochs} batch={batch} crop={crop} patience={(patience > 0 ? patience : "끔")} base={baseChannels} device={device}");
        StartProcess(py, args, paths.Workspace, TrainingPhase.Training, "U-Net 학습", afterSuccess);
    }

    /// <summary>U-Net best.pt → models/weld_unet_{modality}_{area|line}_{yyMMdd_HHmm}.onnx (H/W 동적 축, opset 17).</summary>
    public async Task StartUnetExportAsync(string? modality = null)
    {
        if (IsBusy) throw new InvalidOperationException("이미 실행 중인 작업이 있습니다.");
        if (modality is not ("rgb" or "ir"))
        {
            var (m, fromRecord) = await TrainedModalityAsync(unet: true);
            modality = m;
            if (!fromRecord) Emit($"[U-Net 내보내기] 학습 시점 모달리티 기록이 없어(이전 버전 학습) 현재 데이터셋 기준 {m.ToUpperInvariant()} 로 정했습니다 — 다르면 파일명의 _ir/_rgb 를 확인하세요.");
        }
        var paths = await GetPathsAsync() ?? throw new InvalidOperationException("캡처 폴더 미설정");
        var best = Path.Combine(UnetRunDir(paths), "best.pt");
        if (!File.Exists(best)) throw new FileNotFoundException("U-Net 가중치(best.pt)가 없습니다. 먼저 U-Net 학습을 완료하세요.", best);

        var script = await WriteUnetScriptAsync(paths);
        var py = await GetPythonAsync();
        var kind = await TrainedKindAsync(unet: true);
        var outPath = Path.Combine(paths.Models, WeldModelNames.UnetExportName(modality, kind, DateTime.Now, await TrainedClaheAsync(unet: true)));
        Directory.CreateDirectory(paths.Models);
        TrainTotalEpochs = 0;
        Emit($"[U-Net 내보내기] 시작 — {best} → {Path.GetFileName(outPath)}");
        StartProcess(py, new[] { script, "export", "--weights", best, "--out", outPath }, paths.Workspace,
            TrainingPhase.Exporting, "U-Net 내보내기");
    }

    // ── 중심선 히트맵 학습 / 내보내기 ───────────────────────────────────────────
    /// <summary>중심선 히트맵 학습 산출물 폴더(runs/heatmap_{modality}).</summary>
    public static string HeatmapRunDir(TrainingPaths p, string modality) => Path.Combine(p.Runs, $"heatmap_{modality}");

    /// <summary>
    /// 중심선 히트맵 학습(weld_heatmap.py, ResNet18 사전학습 인코더 U-Net). 데이터셋 변환 없이 캡처 폴더의
    /// <c>{stem}_{modality}.png</c> + <c>_line.json</c> 선 라벨을 바로 읽고, 촬영 세션 단위로 검증셋을 나눈다.
    /// 장치는 스크립트가 고른다(cuda → mps → cpu). <paramref name="pretrained"/> 면 첫 실행 때 ImageNet 가중치(약 45MB)를 내려받는다.
    /// </summary>
    /// <param name="continueFromPrevious">이어 학습(보강) — 이전 best.pt 에서 시작해 기존+새 라벨 전체로 학습한다.
    /// 이전 모델 성능을 먼저 재고 그보다 나아질 때만 best.pt 가 바뀐다.</param>
    public async Task StartHeatmapTrainingAsync(string modality, int epochs, int batch, int patience, bool pretrained,
        bool continueFromPrevious = false)
    {
        if (IsBusy) throw new InvalidOperationException("이미 실행 중인 작업이 있습니다.");
        modality = modality == "ir" ? "ir" : "rgb";
        var paths = await GetPathsAsync() ?? throw new InvalidOperationException("캡처 폴더 미설정");
        if (!Directory.EnumerateFiles(paths.CaptureDir, $"*_{modality}_line.json").Any())
            throw new FileNotFoundException(
                $"{modality.ToUpperInvariant()} 선 라벨(*_{modality}_line.json)이 캡처 폴더에 없습니다 — 수동 비드 입력·라벨 편집으로 먼저 라벨을 만드세요.",
                paths.CaptureDir);

        var runDir = HeatmapRunDir(paths, modality);
        var prevBest = Path.Combine(runDir, "best.pt");
        if (continueFromPrevious && !File.Exists(prevBest))
            throw new FileNotFoundException(
                $"이어 학습할 이전 모델(best.pt)이 없습니다 — 먼저 {modality.ToUpperInvariant()} 히트맵 학습을 한 번 완료하세요.", prevBest);
        // 새 학습은 best.pt 를 덮어쓰므로 이전 것을 prev/ 에 백업한다(이어 학습이면 그 백업에서 시작). 최근 3개만 보관.
        var backup = BackupHeatmapBest(runDir);

        var script = await WriteScriptAsync(paths, "weld_heatmap.py");
        var py = await GetPythonAsync();
        var args = new List<string>
        {
            script, "train", "--captures", paths.CaptureDir, "--modality", modality, "--out", runDir,
            "--epochs", epochs.ToString(), "--batch", batch.ToString(), "--patience", Math.Max(1, patience).ToString(),
            "--device", "auto",
        };
        if (!pretrained) args.Add("--no-pretrained");
        if (continueFromPrevious && backup is not null) { args.Add("--init"); args.Add(backup); }
        if (backup is not null)
            Emit($"[히트맵 학습] 이전 best.pt 백업 → {Path.GetRelativePath(runDir, backup)}");
        TrainTotalEpochs = epochs;
        TrainCurrentEpoch = 0;
        TrainStartUtc = DateTime.UtcNow;
        TrainEndUtc = null;
        Emit($"[히트맵 학습] 시작 — {modality.ToUpperInvariant()} epochs={epochs} batch={batch} patience={patience} " +
             (continueFromPrevious ? "이어 학습(이전 best.pt 에서 시작)" : $"새로 학습(사전학습={(pretrained ? "예" : "아니오")})"));
        StartProcess(py, args.ToArray(), paths.Workspace, TrainingPhase.Training, "히트맵 학습");
    }

    /// <summary>히트맵 이전 best.pt 백업 보관 개수.</summary>
    public const int HeatmapBackupKeep = 3;

    /// <summary>runDir/best.pt 를 runDir/prev/best_yyyyMMdd_HHmmss.pt 로 복사하고 오래된 백업을 정리한다. 없으면 null.</summary>
    internal static string? BackupHeatmapBest(string runDir)
    {
        var best = Path.Combine(runDir, "best.pt");
        if (!File.Exists(best)) return null;
        var prevDir = Path.Combine(runDir, "prev");
        Directory.CreateDirectory(prevDir);
        var dst = Path.Combine(prevDir, $"best_{DateTime.Now:yyyyMMdd_HHmmss}.pt");
        File.Copy(best, dst, overwrite: true);
        foreach (var old in Directory.GetFiles(prevDir, "best_*.pt").OrderByDescending(f => f, StringComparer.Ordinal).Skip(HeatmapBackupKeep))
        {
            try { File.Delete(old); } catch (IOException) { /* 사용 중이면 다음에 정리 */ }
        }
        return dst;
    }

    /// <summary>히트맵 best.pt → models/weld_heatmap_{modality}.onnx (train_width 메타데이터 포함).</summary>
    public async Task StartHeatmapExportAsync(string modality)
    {
        if (IsBusy) throw new InvalidOperationException("이미 실행 중인 작업이 있습니다.");
        modality = modality == "ir" ? "ir" : "rgb";
        var paths = await GetPathsAsync() ?? throw new InvalidOperationException("캡처 폴더 미설정");
        var best = Path.Combine(HeatmapRunDir(paths, modality), "best.pt");
        if (!File.Exists(best))
            throw new FileNotFoundException($"히트맵 가중치(best.pt)가 없습니다. 먼저 {modality.ToUpperInvariant()} 히트맵 학습을 완료하세요.", best);

        var script = await WriteScriptAsync(paths, "weld_heatmap.py");
        var py = await GetPythonAsync();
        var outPath = Path.Combine(paths.Models, WeldModelNames.HeatmapExportName(modality));
        Directory.CreateDirectory(paths.Models);
        TrainTotalEpochs = 0;
        Emit($"[히트맵 내보내기] 시작 — {best} → {Path.GetFileName(outPath)}");
        StartProcess(py, new[] { script, "export", "--weights", best, "--out", outPath }, paths.Workspace,
            TrainingPhase.Exporting, "히트맵 내보내기");
    }

    // 임베드 리소스(weld_unet.py)를 작업 폴더로 꺼낸다(매번 덮어써 앱 버전과 스크립트를 일치시킴).
    private static Task<string> WriteUnetScriptAsync(TrainingPaths paths) => WriteScriptAsync(paths, "weld_unet.py");

    /// <summary>임베드 리소스 스크립트를 작업 폴더의 <c>_{name}</c> 으로 꺼낸다(매번 덮어씀).</summary>
    private static async Task<string> WriteScriptAsync(TrainingPaths paths, string name)
    {
        Directory.CreateDirectory(paths.Workspace);
        var dst = Path.Combine(paths.Workspace, "_" + name);
        await using var src = typeof(WeldTrainingService).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"내장 스크립트({name})를 찾지 못했습니다.");
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

    // ── train/val 판정(④ 추론 목록 표시용) ─────────────────────────────────────
    /// <summary>
    /// 현재 폴더 설정으로 데이터셋을 변환하면 각 촬영본이 train/val 중 어디로 가는지 계산한다(<see cref="ConvertAsync(string, LabelKind, IReadOnlyList{string}, string?, bool)"/> 와 같은 규칙).
    /// 검증 폴더가 있으면 그 폴더 = val, 없으면 학습 폴더 전체를 촬영 세션 단위로 분리한다.
    /// 같은 stem 이 여러 폴더에 있으면 먼저 나온 폴더만, 원본 폴더의 예전 회전본(_cw90/_ccw90)은 제외.
    /// (손상된 선 라벨·빈 마스크처럼 변환 중에야 걸러지는 항목은 반영하지 않으므로 경계 부근이 드물게 다를 수 있다.)
    /// </summary>
    public async Task<SplitView> GetSplitAsync(string modality, LabelKind kind)
    {
        modality = LabelFiles.Mod(modality);
        var src = await GetDatasetSourcesAsync();
        var train = LabelFiles.NormalizeDirs(src.TrainDirs);
        var val = string.IsNullOrWhiteSpace(src.ValDir) ? null : LabelFiles.NormalizeDirs(new[] { src.ValDir })[0];
        if (val is not null) train.RemoveAll(d => LabelFiles.SameDir(d, val));
        bool offOnly = await GetEmitterOffOnlyAsync();

        return await Task.Run(() =>
        {
            var dirs = train.Select(d => (Dir: d, IsVal: false)).ToList();
            if (val is not null) dirs.Add((val, true));
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var found = new List<(string Stem, string Dir, bool IsVal)>();
            foreach (var (dir, isVal) in dirs)
                foreach (var stem in LabelFiles.LabeledStems(dir, modality, kind))
                    if (DatasetSplitter.BaseStem(stem) == stem
                        && !ExcludedByEmitter(modality, offOnly, Path.Combine(dir, LabelFiles.ImageName(stem, modality)))
                        && seen.Add(stem)) found.Add((stem, dir, isVal));

            var split = DatasetSplitter.Split(found.Where(x => !x.IsVal).Select(x => x.Stem));
            var autoVal = split.Val.ToHashSet();
            var items = found.Select(x => new SplitItem(x.Stem, x.Dir, val is not null ? x.IsVal : autoVal.Contains(x.Stem))).ToList();
            return new SplitView(items, val is null && split.SingleSession);
        });
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
        // 학습 run 의 runs/hd/weights/best.onnx 는 내보내기 중간 산출물(models 의 정식 이름 파일과 같은 모델)이라 목록에서 뺀다.
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
    private void StartProcess(string exe, IEnumerable<string> args, string workDir, TrainingPhase phase, string label,
        Func<Task>? afterSuccess = null)
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
        // MPS(Apple GPU)가 지원하지 않는 연산은 조용히 CPU 로 폴백 — device=mps 학습 중 미구현 연산에
        // 부딪혀도 크래시하지 않게 한다. 비-macOS 에는 영향 없음.
        psi.Environment["PYTORCH_ENABLE_MPS_FALLBACK"] = "1";

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
            // 후속 작업(자동 내보내기 등) — 프로세스 정리 후(IsBusy=false) 시작. 화면을 떠나 있어도 진행된다.
            if (code == 0 && afterSuccess is not null)
                _ = Task.Run(async () =>
                {
                    try { await afterSuccess(); }
                    catch (Exception ex)
                    {
                        Phase = TrainingPhase.Error;
                        StatusText = "자동 내보내기 실패";
                        Emit($"[자동 내보내기] 실패: {ex.Message}");
                    }
                });
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

    /// <summary>데이터셋 첫 원본 이미지의 (W,H). 직사각 내보내기 비율 산출용. 없으면 null.</summary>
    private static (int W, int H)? DatasetImageSize(string dataset)
    {
        foreach (var sub in new[] { "train", "val" })
        {
            var dir = Path.Combine(dataset, "images", sub);
            if (!Directory.Exists(dir)) continue;
            // 90° 회전본(_cw90/_ccw90)은 가로세로가 바뀌어 있으므로 원본 이미지로 비율을 잡는다.
            var f = Directory.GetFiles(dir, "*.png").OrderBy(x => x)
                .FirstOrDefault(x => !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(x), @"_c?cw90_"));
            if (f is null) continue;
            if (UsePythonCv)
            {
                // 비-Windows: OpenCvSharp 네이티브가 없으므로 PNG 헤더(IHDR)에서 크기를 직접 읽는다.
                if (PngSize(f) is { } s) return s;
                continue;
            }
            using var m = CvIo.ReadMat(f, ImreadModes.Unchanged);
            if (!m.Empty()) return (m.Width, m.Height);
        }
        return null;
    }

    /// <summary>PNG 파일의 IHDR(시그니처 8B + 길이 4B + "IHDR" 4B 뒤 너비·높이 빅엔디언 4B씩)에서 크기를 읽는다.</summary>
    private static (int W, int H)? PngSize(string path)
    {
        Span<byte> b = stackalloc byte[24];
        using var fs = File.OpenRead(path);
        if (fs.Read(b) < 24) return null;
        if (b[0] != 0x89 || b[1] != (byte)'P' || b[2] != (byte)'N' || b[3] != (byte)'G') return null;
        if (b[12] != (byte)'I' || b[13] != (byte)'H' || b[14] != (byte)'D' || b[15] != (byte)'R') return null;
        int w = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(b[16..20]);
        int h = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(b[20..24]);
        return w > 0 && h > 0 ? (w, h) : null;
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
        "    # translate=0.3: 라벨이 화면 중앙에 몰려 위/아래 가장자리 비드를 놓쳐(선창 151/153) 이동 범위를 넓힘.\n" +
        "    kw.update(mosaic=0.0, close_mosaic=0, mixup=0.0, copy_paste=0.0, erasing=0.0,\n" +
        "              fliplr=0.5, flipud=0.5, degrees=8.0, translate=0.3, scale=0.25,\n" +
        "              shear=0.0, perspective=0.0, hsv_h=0.0, hsv_s=0.0, hsv_v=0.4)\n" +
        "if bright == '1':\n" +
        "    kw.update(hsv_v=0.5, hsv_s=0.3)  # 밝기/채도 변동 강화(조명·노출 편차 대응)\n" +
        "YOLO(model).train(**kw)\n" +
        "print('TRAIN_DONE')\n";

    // 비-Windows 픽셀 연산 ① 마스크 → YOLO-seg 라벨(MaskToYoloLabel 과 동일 알고리즘). 입력: 마스크 경로 JSON 배열,
    // 출력: {경로: {label, count}} JSON. 한글 경로 안전을 위해 np.fromfile + imdecode 로 읽는다.
    private const string LabelsPy =
        "import sys, json\n" +
        "import numpy as np, cv2\n" +
        "src, dst = sys.argv[1], sys.argv[2]\n" +
        "masks = json.load(open(src, encoding='utf-8'))\n" +
        "out = {}\n" +
        "for path in masks:\n" +
        "    data = np.fromfile(path, dtype=np.uint8)\n" +
        "    gray = None if data.size == 0 else cv2.imdecode(data, cv2.IMREAD_GRAYSCALE)\n" +
        "    if gray is None:\n" +
        "        out[path] = {'label': '', 'count': 0}; continue\n" +
        "    h, w = gray.shape[:2]\n" +
        "    _, b = cv2.threshold(gray, 127, 255, cv2.THRESH_BINARY)\n" +
        "    contours, _ = cv2.findContours(b, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)\n" +
        "    min_area = max(16.0, w * h * 0.0002)\n" +
        "    lines = []\n" +
        "    for c in contours:\n" +
        "        if cv2.contourArea(c) < min_area: continue\n" +
        "        poly = cv2.approxPolyDP(c, 1.5, True)\n" +
        "        if len(poly) < 3: continue\n" +
        "        parts = ['0']\n" +
        "        for p in poly:\n" +
        "            parts.append('%.6f' % min(max(int(p[0][0]) / w, 0.0), 1.0))\n" +
        "            parts.append('%.6f' % min(max(int(p[0][1]) / h, 0.0), 1.0))\n" +
        "        lines.append(' '.join(parts) + '\\n')\n" +
        "    out[path] = {'label': ''.join(lines), 'count': len(lines)}\n" +
        "json.dump(out, open(dst, 'w', encoding='utf-8'), ensure_ascii=False)\n" +
        "print('[변환] 마스크 %d개 폴리곤 추출 완료(Python cv2)' % len(masks), flush=True)\n";

    // 비-Windows 픽셀 연산 ② 이미지 90° 회전(Cv2.Rotate 와 동일, 채널·비트심도 그대로). 입력: [{src,dst,cw}] JSON.
    private const string RotateImgPy =
        "import sys, json\n" +
        "import numpy as np, cv2\n" +
        "jobs = json.load(open(sys.argv[1], encoding='utf-8'))\n" +
        "n = 0\n" +
        "for j in jobs:\n" +
        "    data = np.fromfile(j['src'], dtype=np.uint8)\n" +
        "    m = None if data.size == 0 else cv2.imdecode(data, cv2.IMREAD_UNCHANGED)\n" +
        "    if m is None: continue\n" +
        "    r = cv2.rotate(m, cv2.ROTATE_90_CLOCKWISE if j['cw'] else cv2.ROTATE_90_COUNTERCLOCKWISE)\n" +
        "    ok, buf = cv2.imencode('.png', r)\n" +
        "    if ok: buf.tofile(j['dst']); n += 1\n" +
        "print('[변환] 이미지 %d장 90° 회전(Python cv2)' % n, flush=True)\n";

    // imgsz 인자: "0"=학습값 자동, "864"=정사각, "rect:{size}:{W}x{H}"=데이터 비율 직사각([h,w], 32 배수, size 0 이면 학습값).
    private const string ExportPy =
        "import sys, shutil, os, math\n" +
        "from ultralytics import YOLO\n" +
        "best, opset, spec, outdir = sys.argv[1], int(sys.argv[2]), sys.argv[3], sys.argv[4]\n" +
        "outname = sys.argv[5] if len(sys.argv) > 5 else 'weld_yolo.onnx'\n" +
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

/// <summary>작업 폴더 경로들. 작업 폴더(dl/)는 캡처 저장 폴더 기준으로 고정 — 라벨링·데이터 폴더를 바꿔도 모델 경로는 그대로.</summary>
public sealed record TrainingPaths(string CaptureDir, string Workspace, string Runs, string Models)
{
    /// <summary>라벨 종류별 데이터셋 폴더(dl/dataset_area, dl/dataset_line).</summary>
    public string DatasetOf(LabelKind kind) => Path.Combine(Workspace, LabelFiles.DatasetFolder(kind));
}
/// <summary>데이터셋 변환 원본 폴더 구성. ValDir 이 null 이면 세션 단위 자동 분리.</summary>
public sealed record DatasetSources(List<string> TrainDirs, string? ValDir);
/// <summary>촬영본 하나의 판정 결과. <see cref="Dir"/> = 이미지가 있는 폴더.</summary>
public sealed record SplitItem(string Stem, string Dir, bool IsVal);
/// <param name="SingleSession">자동 분리인데 촬영 세션이 1개뿐(val 이 train 과 같은 자리 영상).</param>
public sealed record SplitView(List<SplitItem> Items, bool SingleSession);
public sealed record PythonInfo(bool PythonOk, bool UltralyticsOk, string Exe, string Detail, bool TorchOk = false);
public sealed record ConvertResult(int Used, int Skipped, int Instances, string DataYaml, int Train = 0, int Val = 0, int Sessions = 0,
    LabelKind Kind = LabelKind.Area, int Rotated = 0);
public sealed record ModelInfo(string Path, string Name, long Bytes, DateTime Modified);
