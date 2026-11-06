using System.Runtime.InteropServices;
using HD.AMR.App.Communication.Weld;
using HD.AMR.App.Models;
using HD.AMR.App.Service.Sequence;
using HD.AMR.App.Service.Vision;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace HD.AMR.App.Service;

/// <summary>
/// 용접라인 추적 오케스트레이션(명세서 v2). 1차 구현은 <b>수동 2-shot 측정</b>:
/// Peak #1·Peak #2 에서 운영자가 트리거 → 각 한 장에서 비드 centerline·위치오차 d(픽셀)를 구하고,
/// d1·d2·pitch 로 각도 theta 를 산출한다(화면 표시만, 로봇 미동작). 검출은 <see cref="IWeldVisionDetector"/>,
/// Peak 는 <see cref="DepthPeakAnalyzer"/> 사용.
/// ROI 는 카메라 페이지·시퀀스와 같은 파라미터(<see cref="CameraRoiKeys.Peak"/>=Peak ROI,
/// <see cref="CameraRoiKeys.Bead"/>=Weld ROI, 정규화 0~1)가 원천이다 — 이 서비스에서 바꾸면 DB 에 저장되고,
/// 다른 화면이 바꾸면 <see cref="ParameterService.Changed"/> 로 다시 읽는다. <see cref="RoiProfileStore"/>(JSON)
/// 프로파일은 이름 붙인 스냅샷으로만 쓴다(Load 하면 그 ROI 가 파라미터에 저장된다).
/// 싱글톤 — 페이지 이동 간 ROI/파라미터/측정 상태가 유지된다.
/// </summary>
public class WeldTrackingService
{
    private readonly CameraService _camera;
    private readonly IWeldVisionDetector _cvDetector;
    private readonly IDlWeldVisionDetector _dlDetector;
    private readonly IRidgeWeldVisionDetector _ridgeDetector;
    private readonly IUnetWeldVisionDetector _unetDetector;
    private readonly RoiProfileStore _store;
    private readonly WeldTrackingSettings _settings;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WeldTrackingService> _logger;

    /// <summary>파라미터 ROI 캐시(정규화). 참조 교체로 원자적 갱신 — 여러 스레드(UI·시퀀스·DB 이벤트)가 읽는다.</summary>
    private sealed record RoiState(NormalizedRoi? Peak, NormalizedRoi? Bead);
    private volatile RoiState _roi = new(null, null);

    /// <summary>시퀀스가 넘긴 ROI(픽셀) 일시 적용분. null 이면 파라미터 캐시를 쓴다.</summary>
    private sealed record RoiOverride(RoiRect? Peak, RoiRect? Weld);
    private volatile RoiOverride? _override;

    /// <summary>파라미터 쓰기 직렬화 + 자기 쓰기로 생긴 변경 이벤트 무시용 카운터.</summary>
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private int _selfWrites;

    /// <summary>
    /// 시퀀스(자동)와 Weld 페이지(수동)의 동시 접근 직렬화. 신규 *Async 메서드에서만 획득한다.
    /// 기존 동기 메서드는 UI 단독 사용 전제라 그대로 둔다.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WeldTrackingService(
        CameraService camera,
        IWeldVisionDetector detector,
        IDlWeldVisionDetector dlDetector,
        IRidgeWeldVisionDetector ridgeDetector,
        IUnetWeldVisionDetector unetDetector,
        RoiProfileStore store,
        IOptions<WeldTrackingSettings> options,
        IServiceScopeFactory scopeFactory,
        ILoggerFactory loggerFactory)
    {
        _camera = camera;
        _cvDetector = detector;
        _dlDetector = dlDetector;
        _ridgeDetector = ridgeDetector;
        _unetDetector = unetDetector;
        _store = store;
        _settings = options.Value;
        _scopeFactory = scopeFactory;
        _logger = loggerFactory.CreateLogger<WeldTrackingService>();

        // 자동 로드 프로파일은 스케일 보정값만 적용한다 — ROI 는 파라미터가 원천이라, 여기서 ROI 까지
        // 적용하면 재시작마다 카메라 페이지에서 저장한 ROI 를 옛 프로파일로 덮어쓰게 된다.
        if (!string.IsNullOrWhiteSpace(_settings.AutoLoadProfile))
        {
            var p = _store.Load(_settings.AutoLoadProfile!);
            if (p is not null) ApplyProfile(p, applyRoi: false);
        }

        ParameterService.Changed += OnParameterChanged;
        _ = ReloadRoiAsync();
        _ = ReloadDlThresholdsAsync();
        _ = ReloadModeThenDetectionAsync();
    }

    // 모드를 먼저 복원하고 검출 설정(모델 경로 → 방식·모드 맞춤)을 그 뒤에 적용한다 — 순서가 뒤바뀌면
    // 모델에 맞춰 바꾼 모드를 늦게 도착한 저장 모드가 덮어쓴다.
    private async Task ReloadModeThenDetectionAsync()
    {
        await ReloadModeAsync();
        await ReloadDetectionSettingsAsync(changedKey: null);
    }

    /// <summary>검출 입력 영상(IR/RGB) 파라미터. 없으면 <see cref="WeldDetectionParams.Mode"/> 기본값(IR).</summary>
    public const string ImageModeKey = "Weld.ImageMode";

    /// <summary>DL 검출 confidence 임계값 파라미터. 없으면 <see cref="WeldDetectionParams.DlConfidence"/> 기본값.</summary>
    public const string DlConfidenceKey = "Weld.Dl.Confidence";

    /// <summary>DL 마스크 확률 임계값 파라미터. 없으면 <see cref="WeldDetectionParams.DlMaskThreshold"/> 기본값.</summary>
    public const string DlMaskThresholdKey = "Weld.Dl.MaskThreshold";

    // 조정 범위 — 이 밖의 값은 검출이 사실상 전부/전무가 되므로 저장·로드 모두 이 범위로 자른다.
    public const double DlConfidenceMin = 0.05, DlConfidenceMax = 0.90;
    public const double DlMaskThresholdMin = 0.30, DlMaskThresholdMax = 0.70;

    // ── 상태(UI 가 읽음) ────────────────────────────────────────────
    /// <summary>비드 검출 방식. 기본 DL(학습 모델). 파라미터(고전 CV)는 폴백/비교용으로 유지.
    /// 화면에서 바꾸면 <see cref="SaveDetectionSettingsAsync"/> 로 DB(<see cref="WeldDetectionSettingsStore.MethodKey"/>)에
    /// 저장되고 기동 시 복원된다.</summary>
    public WeldDetectionMethod Method { get; set; } = WeldDetectionMethod.Dl;

    /// <summary>현재 방식에 해당하는 검출기.</summary>
    private IWeldVisionDetector Detector => GetDetector(Method);

    /// <summary>방식별 검출기. 비교평가(<c>WeldDetectionBenchmarkService</c>)가 같은 인스턴스를 공유한다.
    /// 새 검출 방식을 추가하면 여기만 늘리면 운영·비교평가 양쪽에 반영된다.</summary>
    public IWeldVisionDetector GetDetector(WeldDetectionMethod method) => method switch
    {
        WeldDetectionMethod.Dl => _dlDetector,
        WeldDetectionMethod.Ridge => _ridgeDetector,
        WeldDetectionMethod.Unet => _unetDetector,
        _ => _cvDetector,
    };

    public bool DetectorAvailable => Detector.IsAvailable;

    /// <summary>Peak ROI(검출 프레임 픽셀). 원천은 <see cref="CameraRoiKeys.Peak"/> 파라미터.</summary>
    public RoiRect? PeakRoi => _override is { } o ? o.Peak : ToPixel(_roi.Peak);

    /// <summary>Weld(비드) ROI(검출 프레임 픽셀). 원천은 <see cref="CameraRoiKeys.Bead"/> 파라미터.</summary>
    public RoiRect? WeldRoi => _override is { } o ? o.Weld : ToPixel(_roi.Bead);
    public string ProfileName { get; set; } = "default";
    public WeldDetectionParams Params { get; } = new();
    public double Pitch { get; set; }

    /// <summary>2점 보정으로 산출한 스케일 보정계수. Depth 자동(mm/px=Z/fx)에 곱해 체계적 오차를 보강. 1=보정 없음.</summary>
    public double ScaleCorrection { get; set; } = 1.0;

    /// <summary>2점 보정계수 적용 여부. 끄면 순수 Depth 자동.</summary>
    public bool ScaleCorrectionEnabled { get; set; }

    /// <summary>스케일 사용 가능 여부 — fx(해상도·FOV) 만 있으면 Depth 자동으로 항상 가능.</summary>
    public bool ScaleAvailable => Fx() > 0;

    /// <summary>최근 검증(Validate) 결과. 없으면 null.</summary>
    public ScaleValidationResult? LastValidation { get; private set; }

    /// <summary>측정 d 를 현재 스케일(Depth 자동 ± 보정)로 mm 환산.</summary>
    public double DMm(PeakMeasurement m) => m.DPixel * EffectiveMmPerPixel(m.DepthZ, m.IrGeometry);

    /// <summary>
    /// 시퀀스 호출 중(<see cref="FindPeakAsync"/>/<see cref="CapturePeakAsync"/>) 입력이 RGB 이면 true —
    /// 비드 검출만 컬러로 하고 위치·거리 기준(Peak 오프셋, d, mm 환산)은 IR(=Depth) 좌표로 맞춘다.
    /// 센터링 이동량이 IR 모드와 같아져 IR 기준으로 교시된 ROI·검사캠 오프셋을 그대로 쓸 수 있다.
    /// </summary>
    private volatile bool _seqIrGeometry;

    /// <summary>Peak 선호 위치 — 진행축 FOV 센터 기준 IR 픽셀. 시퀀스 호출 중에만 0 이 아니다(<see cref="FindPeakAsync"/>).</summary>
    private double _peakPreferOffsetPx;

    /// <summary>위치·거리 계산 좌표계가 IR(=Depth)인지. IR 모드이거나 시퀀스 RGB 모드면 true.</summary>
    private bool GeomIr => Params.Mode == WeldImageMode.Ir || _seqIrGeometry;

    public WeldTrackingState State { get; private set; } = WeldTrackingState.Idle;
    public string? Message { get; private set; }
    public WeldDetectionResult? LastDetect { get; private set; }

    /// <summary>① Peak 찾기 최근 결과(진행축 FOV 센터 거리). 없으면 null.</summary>
    public PeakFindResult? LastPeakFind { get; private set; }

    public PeakMeasurement? M1 { get; private set; }
    public PeakMeasurement? M2 { get; private set; }
    public AngleResult? Angle { get; private set; }

    // 최근 오버레이 — Peak 찾기/1회 검출/비드 찾기 어느 단계든 마지막 결과를 여기에 담는다.
    private byte[]? _lastOverlay;
    public byte[]? LastOverlay => _lastOverlay;
    public byte[]? Peak1Overlay => M1?.OverlayJpeg;
    public byte[]? Peak2Overlay => M2?.OverlayJpeg;

    public IReadOnlyList<string> ListProfiles() => _store.List();
    public string ProfileDirectory => _store.Directory_;

    // ── ROI 관리 ────────────────────────────────────────────────────
    // 설정 즉시 캐시에 반영하고(화면이 바로 읽음) 파라미터 저장은 백그라운드로 한다 — 시퀀스도 같은 값을 쓴다.
    // 입력 r 은 현재 검출 프레임(IR 또는 컬러) 픽셀. 저장은 항상 IR 기준이라 RGB 화면에서 그린 ROI 는 IR 로 역투영한다.
    public void SetPeakRoi(RoiRect? r)
    {
        if (TryToNormalized(r, out var n)) SetRois(n, _roi.Bead);
        else Message = RoiConvertFailMessage("Peak");
    }

    public void SetWeldRoi(RoiRect? r)
    {
        if (TryToNormalized(r, out var n)) SetRois(_roi.Peak, n);
        else Message = RoiConvertFailMessage("Bead");
    }

    private static string RoiConvertFailMessage(string label) =>
        $"{label} ROI 저장 안 됨 — RGB 화면에서 그린 ROI 를 IR 기준으로 바꾸려면 Depth 스트림과 Depth↔컬러 캘리브레이션이 필요합니다. " +
        "카메라 연결을 확인하거나 IR 모드에서 지정하세요.";

    /// <summary>Peak·Weld ROI 사용 해제 — 파라미터 Enabled=false(좌표 보존). 시퀀스는 다른 ROI 로 폴백한다.</summary>
    public void ResetRoi() { SetRois(null, null); Message = "ROI 초기화됨 (Peak/Bead ROI 사용 해제)"; }

    private void SetRois(NormalizedRoi? peak, NormalizedRoi? bead)
    {
        var prev = _roi;
        _roi = new RoiState(peak, bead);
        if (prev.Peak != peak) _ = PersistRoiAsync(CameraRoiKeys.Peak, peak);
        if (prev.Bead != bead) _ = PersistRoiAsync(CameraRoiKeys.Bead, bead);
    }

    private async Task PersistRoiAsync(CameraRoiKeys keys, NormalizedRoi? roi)
    {
        await _persistGate.WaitAsync();
        Interlocked.Increment(ref _selfWrites);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var param = scope.ServiceProvider.GetRequiredService<ParameterService>();
            await CameraRoiParameters.WriteAsync(param, keys, roi);
            _logger.LogInformation("용접 추적 ROI 저장: {Label} ROI = {Roi}", keys.Label,
                roi is { } r ? $"({r.X:0.000}, {r.Y:0.000}, {r.W:0.000}, {r.H:0.000})" : "사용 해제");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "용접 추적 ROI 저장 실패: {Label}", keys.Label);
            Message = $"{keys.Label} ROI 저장 실패: {ex.Message}";
        }
        finally
        {
            Interlocked.Decrement(ref _selfWrites);
            _persistGate.Release();
        }
    }

    /// <summary>다른 화면(카메라 페이지·Parameter 화면)이 ROI 파라미터를 바꾸면 캐시를 다시 읽는다.</summary>
    private void OnParameterChanged(string name)
    {
        if (Volatile.Read(ref _selfWrites) > 0) return;
        if (CameraRoiKeys.Peak.Contains(name) || CameraRoiKeys.Bead.Contains(name))
            _ = ReloadRoiAsync();
        else if (WeldDetectionSettingsStore.IsKey(name))
            _ = ReloadDetectionSettingsAsync(name);
        else if (name is DlConfidenceKey or DlMaskThresholdKey)
            _ = ReloadDlThresholdsAsync();
        else if (name == ImageModeKey)
            _ = ReloadModeAsync();
    }

    // ── 검출 입력 영상(모드) ────────────────────────────────────────
    // UI 가 Params.Mode 를 바꾼 뒤 호출 — 재시작 후에도 같은 모드(IR/RGB)로 복원되도록 파라미터에 저장한다.
    /// <summary>현재 <see cref="WeldDetectionParams.Mode"/> 를 파라미터로 저장(백그라운드).</summary>
    public void SaveMode() => _ = PersistModeAsync(Params.Mode);

    private async Task PersistModeAsync(WeldImageMode mode)
    {
        await _persistGate.WaitAsync();
        Interlocked.Increment(ref _selfWrites);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var param = scope.ServiceProvider.GetRequiredService<ParameterService>();
            await param.SetAsync(ImageModeKey, mode.ToString(), "용접 추적 검출 입력 영상 (Ir / RgbGrayscale / RgbHsv)");
            _logger.LogInformation("용접 추적 입력 모드 저장: {Mode}", mode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "용접 추적 입력 모드 저장 실패");
            Message = $"입력 모드 저장 실패: {ex.Message}";
        }
        finally
        {
            Interlocked.Decrement(ref _selfWrites);
            _persistGate.Release();
        }
    }

    private async Task ReloadModeAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var param = scope.ServiceProvider.GetRequiredService<ParameterService>();
            if (await param.GetAsync(ImageModeKey) is { } s
                && Enum.TryParse<WeldImageMode>(s, ignoreCase: true, out var mode)
                && Enum.IsDefined(mode))
                Params.Mode = mode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "용접 추적 입력 모드 파라미터 읽기 실패 — 이전 값 유지");
        }
    }

    /// <summary>
    /// 저장된 검출 방식·Ridge 파라미터·모델 경로를 읽어 적용한다. 키가 없거나 DB 미준비면 현재 값 유지.
    /// 그 뒤 모델 파일명에 맞춰 방식·입력 모드를 맞춘다 — 모델 경로 키가 바뀐 거면 그 모델에, 아니면(기동·방식 변경)
    /// 현재 방식의 모델에.
    /// </summary>
    private async Task ReloadDetectionSettingsAsync(string? changedKey = null)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var param = scope.ServiceProvider.GetRequiredService<ParameterService>();
            var values = new Dictionary<string, string?>();
            foreach (var key in WeldDetectionSettingsStore.Keys)
                values[key] = await param.GetAsync(key);
            if (WeldDetectionSettingsStore.Apply(k => values.GetValueOrDefault(k), Params) is { } m)
                Method = m;

            var msg = changedKey switch
            {
                WeldDetectionSettingsStore.DlModelPathKey => AlignToModel(Params.DlModelPath, WeldDetectionMethod.Dl),
                WeldDetectionSettingsStore.UnetModelPathKey => AlignToModel(Params.UnetModelPath, WeldDetectionMethod.Unet),
                _ => AlignToActiveModel(),
            };
            if (msg is not null) Message = msg;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "용접 추적 검출 설정 읽기 실패 — 현재 값 유지");
        }
    }

    // ── 모델 파일명 → 검출 방식·입력 모드 자동 맞춤 ─────────────────────
    // 모델 파일명에 종류(weld_yolo/weld_seg=DL, weld_unet=U-Net)와 모달리티(ir/rgb)가 들어 있으므로, 모델을 고르면
    // (용접 추적 드롭다운·Parameter 화면·시퀀스) 검출 방식과 입력 모드를 그 모델에 맞춰 바꾸고 저장한다.
    // → 'IR 입력에 RGB 모델' 같은 불일치가 생기지 않는다. 모델이 '자동'(빈값)이면 아무것도 바꾸지 않는다.

    /// <summary>현재 검출 방식(DL/U-Net)의 모델에 방식·모드를 맞춘다. 다른 방식이거나 '자동'이면 null.</summary>
    public string? AlignToActiveModel() => Method switch
    {
        WeldDetectionMethod.Dl => AlignToModel(Params.DlModelPath, WeldDetectionMethod.Dl),
        WeldDetectionMethod.Unet => AlignToModel(Params.UnetModelPath, WeldDetectionMethod.Unet),
        _ => null,
    };

    /// <summary>
    /// 모델 파일명에 맞춰 검출 방식·입력 모드를 바꾸고 저장한다. 바뀐 게 있으면 안내 문구, 없으면 null.
    /// </summary>
    /// <param name="implied">파일명에 종류 표식이 없을 때의 종류(그 모델이 들어 있던 파라미터 기준).</param>
    public string? AlignToModel(string? modelPath, WeldDetectionMethod implied)
    {
        if (string.IsNullOrWhiteSpace(modelPath)) return null;
        var path = modelPath.Trim().Trim('"').Trim();
        var name = Path.GetFileName(path);
        var kind = WeldModelNames.KindOf(path, implied);
        var changes = new List<string>();

        // 다른 종류 파라미터에 잘못 넣은 모델이면(예: DL 경로에 weld_unet) 맞는 쪽 경로로 옮겨 저장.
        if (kind != implied)
        {
            if (kind == WeldDetectionMethod.Unet) Params.UnetModelPath = path; else Params.DlModelPath = path;
            _ = PersistModelPathAsync(kind, path);
        }
        if (Method != kind)
        {
            Method = kind;
            _ = SaveDetectionSettingsAsync();
            changes.Add($"검출 방식 {(kind == WeldDetectionMethod.Unet ? "U-Net" : "DL")}");
        }
        if (WeldModelNames.ModalityOf(path) is { } mod)
        {
            var target = WeldModelNames.ModeFor(mod, Params.Mode);
            if (Params.Mode != target)
            {
                Params.Mode = target;
                SaveMode();
                changes.Add($"입력 {(target == WeldImageMode.Ir ? "IR" : "RGB")}");
            }
        }
        if (changes.Count == 0) return null;
        _logger.LogInformation("모델 {Model} 에 맞춰 변경: {Changes}", name, string.Join(", ", changes));
        return $"모델 {name} 에 맞춰 {string.Join(", ", changes)} 로 변경";
    }

    private async Task PersistModelPathAsync(WeldDetectionMethod kind, string path)
    {
        var key = kind == WeldDetectionMethod.Unet
            ? WeldDetectionSettingsStore.UnetModelPathKey
            : WeldDetectionSettingsStore.DlModelPathKey;
        await _persistGate.WaitAsync();
        Interlocked.Increment(ref _selfWrites);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var param = scope.ServiceProvider.GetRequiredService<ParameterService>();
            await param.SetAsync(key, path, WeldDetectionSettingsStore.Describe(key));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "모델 경로 저장 실패: {Key}", key);
        }
        finally
        {
            Interlocked.Decrement(ref _selfWrites);
            _persistGate.Release();
        }
    }

    /// <summary>
    /// 지정된 모델(현재 방식)의 모달리티가 현재 입력 모드와 다르면 경고 문구, 같거나 '자동'이면 null.
    /// 사용자가 입력 모드만 손으로 바꿨을 때 안내용(시퀀스는 비드 찾기 전에 모델 기준으로 다시 맞춘다).
    /// </summary>
    public string? ModelModeMismatch()
    {
        var path = Method switch
        {
            WeldDetectionMethod.Dl => Params.DlModelPath,
            WeldDetectionMethod.Unet => Params.UnetModelPath,
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(path) || WeldModelNames.ModalityOf(path) is not { } mod) return null;
        bool modelIr = mod == "ir", inputIr = Params.Mode == WeldImageMode.Ir;
        return modelIr == inputIr ? null
            : $"⚠ 선택된 모델({Path.GetFileName(path.Trim().Trim('"'))})은 {(modelIr ? "IR" : "RGB")}용인데 입력이 {(inputIr ? "IR" : "RGB")}입니다 — 모델을 바꾸거나 '자동'으로 두세요.";
    }

    /// <summary>현재 검출 방식·Ridge 파라미터를 DB 에 저장한다(재시작 후 복원, 다른 화면·시퀀스와 공유).</summary>
    public async Task SaveDetectionSettingsAsync()
    {
        var values = WeldDetectionSettingsStore.ToValues(Method, Params);
        await _persistGate.WaitAsync();
        Interlocked.Increment(ref _selfWrites);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var param = scope.ServiceProvider.GetRequiredService<ParameterService>();
            foreach (var (key, value) in values)
                if (await param.GetAsync(key) != value)
                    await param.SetAsync(key, value, WeldDetectionSettingsStore.Describe(key));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "용접 추적 검출 설정 저장 실패");
            Message = $"검출 설정 저장 실패: {ex.Message}";
        }
        finally
        {
            Interlocked.Decrement(ref _selfWrites);
            _persistGate.Release();
        }
    }

    // ── DL 임계값 ───────────────────────────────────────────────────
    // ROI 와 같은 방식 — 즉시 Params 에 반영하고(시퀀스도 같은 Params 를 쓴다) 저장은 백그라운드로 한다.
    /// <summary>DL conf/mask 임계값 설정 + 파라미터 저장. 범위 밖 값은 잘라낸다.</summary>
    public void SetDlThresholds(double confidence, double maskThreshold)
    {
        confidence = Math.Round(Math.Clamp(confidence, DlConfidenceMin, DlConfidenceMax), 2);
        maskThreshold = Math.Round(Math.Clamp(maskThreshold, DlMaskThresholdMin, DlMaskThresholdMax), 2);
        if (Params.DlConfidence == confidence && Params.DlMaskThreshold == maskThreshold) return;
        Params.DlConfidence = confidence;
        Params.DlMaskThreshold = maskThreshold;
        _ = PersistDlThresholdsAsync(confidence, maskThreshold);
    }

    private async Task PersistDlThresholdsAsync(double confidence, double maskThreshold)
    {
        await _persistGate.WaitAsync();
        Interlocked.Increment(ref _selfWrites);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var param = scope.ServiceProvider.GetRequiredService<ParameterService>();
            await param.SetDoubleAsync(DlConfidenceKey, confidence, "DL 비드 검출 confidence 임계값");
            await param.SetDoubleAsync(DlMaskThresholdKey, maskThreshold, "DL 비드 마스크 확률 임계값");
            _logger.LogInformation("용접 추적 DL 임계값 저장: conf={Conf:0.00}, mask={Mask:0.00}", confidence, maskThreshold);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "용접 추적 DL 임계값 저장 실패");
            Message = $"DL 임계값 저장 실패: {ex.Message}";
        }
        finally
        {
            Interlocked.Decrement(ref _selfWrites);
            _persistGate.Release();
        }
    }

    private async Task ReloadDlThresholdsAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var param = scope.ServiceProvider.GetRequiredService<ParameterService>();
            if (await param.GetDoubleAsync(DlConfidenceKey) is { } conf)
                Params.DlConfidence = Math.Clamp(conf, DlConfidenceMin, DlConfidenceMax);
            if (await param.GetDoubleAsync(DlMaskThresholdKey) is { } mask)
                Params.DlMaskThreshold = Math.Clamp(mask, DlMaskThresholdMin, DlMaskThresholdMax);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "용접 추적 DL 임계값 파라미터 읽기 실패 — 이전 값 유지");
        }
    }

    private async Task ReloadRoiAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var param = scope.ServiceProvider.GetRequiredService<ParameterService>();
            _roi = new RoiState(
                await CameraRoiParameters.ReadAsync(param, CameraRoiKeys.Peak),
                await CameraRoiParameters.ReadAsync(param, CameraRoiKeys.Bead));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "용접 추적 ROI 파라미터 읽기 실패 — 이전 값 유지");
        }
    }

    // ── ROI 좌표계 ──────────────────────────────────────────────────
    // 저장(파라미터 Camera.Peak/Bead.Roi)은 입력 모드와 무관하게 항상 IR(=Depth) 화면 기준 정규화 좌표다 — 시퀀스가
    // IR 기준으로 읽기 때문. RGB 화면에서는 그 ROI 를 ROI 위치의 깊이에서 컬러 좌표로 정확 투영해 보여 주고(표시·검출),
    // RGB 화면에서 그린 ROI 는 IR 좌표로 역투영해 저장한다 → IR·RGB 어느 화면에서 그려도 시퀀스와 같은 위치가 된다.
    // 두 센서가 떨어져 있어(시차) 컬러 표시 위치는 거리에 따라 조금 달라진다 — 그린 시점의 작업거리에서 정확.

    /// <summary>IR 프레임 크기(=ROI 저장 기준). 스트림이 없으면 설정 해상도.</summary>
    private (int w, int h) IrFrameSize() => _camera.LatestIr is { } ir
        ? (ir.Width, ir.Height)
        : (_camera.Settings.IrWidth, _camera.Settings.IrHeight);

    /// <summary>저장된 정규화 ROI → IR 픽셀.</summary>
    private RoiRect? IrPixel(NormalizedRoi? roi)
    {
        if (roi is not { } r) return null;
        var (w, h) = IrFrameSize();
        return r.ToPixel(w, h);
    }

    /// <summary>저장된 정규화 ROI(IR 기준) → 현재 검출 프레임 픽셀(IR 모드는 IR, RGB 모드는 컬러로 투영).</summary>
    private RoiRect? ToPixel(NormalizedRoi? roi)
    {
        var ir = IrPixel(roi);
        if (ir is null || Params.Mode == WeldImageMode.Ir) return ir;
        if (TryIrRoiToColor(ir, out var color)) return color;   // 컬러 화각 밖이면 null(표시·검출 안 함)
        // Depth/캘리브레이션이 없어 투영 불가 — 비율로 근사 표시(위치가 정확하지 않음).
        var (cw, ch) = DetectionFrameSize();
        return roi!.Value.ToPixel(cw, ch);
    }

    /// <summary>현재 검출 프레임 픽셀 ROI → IR 기준 정규화. RGB 화면 ROI 를 IR 로 바꿀 수 없으면 false(저장하지 않음).</summary>
    private bool TryToNormalized(RoiRect? r, out NormalizedRoi? n)
    {
        n = null;
        if (r is null) return true;
        RoiRect? ir = Params.Mode == WeldImageMode.Ir ? r : ColorRoiToIr(r);
        if (ir is null) return false;
        var (w, h) = IrFrameSize();
        var nn = NormalizedRoi.FromPixel(ir, w, h);
        n = nn.IsValid ? nn : null;
        return true;
    }

    /// <summary>IR 픽셀 ROI → 컬러 픽셀 ROI(ROI 중앙 깊이에서 정확 투영). 투영 수단(Depth·컬러·캘리브레이션)이 없으면 false.</summary>
    private bool TryIrRoiToColor(RoiRect ir, out RoiRect? color)
    {
        color = null;
        var depth = _camera.LatestDepth;
        var c = _camera.LatestColor;
        if (depth is null || c is null || BuildMapper(depth) is not { } mapper) return false;
        var (iw, ih) = IrFrameSize();
        var d = ScaleRoi(ir, (double)depth.Width / iw, (double)depth.Height / ih, depth.Width, depth.Height);
        if (d is null) return true;
        double z = SampleDepthMm(depth, d.X + d.Width / 2.0, d.Y + d.Height / 2.0);
        if (z <= 0) z = _settings.DefaultWorkDistanceMm;
        color = mapper.MapDepthRoiToColor(d, z, c.Width, c.Height);
        return true;
    }

    /// <summary>컬러 픽셀 ROI → IR 픽셀 ROI(ROI 중앙 깊이에서 정확 역투영). 투영 수단이 없거나 실패하면 null.</summary>
    private RoiRect? ColorRoiToIr(RoiRect color)
    {
        var depth = _camera.LatestDepth;
        if (depth is null || _camera.LatestColor is null || BuildMapper(depth) is not { } mapper) return null;

        // ROI 중앙의 깊이: 근사 위치에서 샘플 → 그 깊이로 정확 역투영한 위치에서 한 번 더 샘플(시차 보정).
        double cu = color.X + color.Width / 2.0, cv = color.Y + color.Height / 2.0;
        var guess = mapper.ColorToDepthApprox(cu, cv);
        double z = SampleDepthMm(depth, guess.u, guess.v);
        if (z <= 0) z = _settings.DefaultWorkDistanceMm;
        if (mapper.ColorToDepth(cu, cv, z) is { } exact && SampleDepthMm(depth, exact.u, exact.v) is var z2 && z2 > 0) z = z2;

        if (mapper.MapColorRoiToDepthAt(color, z, depth.Width, depth.Height) is not { } d) return null;
        var (iw, ih) = IrFrameSize();
        return ScaleRoi(d, (double)iw / depth.Width, (double)ih / depth.Height, iw, ih);
    }

    private static RoiRect? ScaleRoi(RoiRect r, double sx, double sy, int w, int h)
        => sx == 1 && sy == 1
            ? r.ClampTo(w, h)
            : new RoiRect((int)Math.Round(r.X * sx), (int)Math.Round(r.Y * sy),
                (int)Math.Round(r.Width * sx), (int)Math.Round(r.Height * sy)).ClampTo(w, h);

    // 프로파일 ROI 도 저장 기준과 같게 IR 픽셀로 보관한다(어느 모드에서 저장·로드해도 같은 위치).
    public void SaveProfile(string name)
    {
        var (w, h) = IrFrameSize();
        _store.Save(new RoiProfile
        {
            Name = name,
            PeakRoi = IrPixel(_roi.Peak),
            WeldRoi = IrPixel(_roi.Bead),
            FrameWidth = w,
            FrameHeight = h,
            ScaleCorrection = ScaleCorrection,
            ScaleCorrectionEnabled = ScaleCorrectionEnabled,
        });
        ProfileName = name;
        Message = $"프로파일 '{name}' 저장됨";
    }

    public bool LoadProfile(string name)
    {
        var p = _store.Load(name);
        if (p is null) { Message = $"프로파일 '{name}' 없음"; return false; }
        Message = $"프로파일 '{name}' 불러옴 (ROI 는 Peak/Bead ROI 파라미터에 저장)";
        ApplyProfile(p, applyRoi: true);
        return true;
    }

    private void ApplyProfile(RoiProfile p, bool applyRoi)
    {
        ProfileName = p.Name;
        ScaleCorrection = p.ScaleCorrection <= 0 ? 1.0 : p.ScaleCorrection;
        ScaleCorrectionEnabled = p.ScaleCorrectionEnabled;
        if (!applyRoi) return;

        // 프로파일 ROI 는 IR 픽셀(SaveProfile) — 화면 모드와 무관하게 IR 크기로 정규화한다.
        var (w, h) = IrFrameSize();
        var peak = RoiProfileStore.ValidateAndClamp(p.PeakRoi, w, h, out var c1);
        var weld = RoiProfileStore.ValidateAndClamp(p.WeldRoi, w, h, out var c2);
        if (c1 || c2) Message = "Load 된 ROI 가 현재 해상도에 맞게 보정되었습니다.";
        NormalizedRoi? Norm(RoiRect? r) => r is null ? null
            : NormalizedRoi.FromPixel(r, w, h) is { IsValid: true } n ? n : null;
        SetRois(Norm(peak), Norm(weld));
    }

    // ── 검출/측정 ───────────────────────────────────────────────────
    /// <summary>
    /// ① Peak 찾기 — Depth 에서 코루게이션 Peak 를 찾아, <b>진행축</b> FOV 센터로부터의 거리(px·mm)를
    /// 구한다. 비드 검출은 하지 않는다. 운영자는 이 거리만큼 로봇을 이동해 Peak 를 FOV 센터에 맞춘 뒤
    /// ② 비드 찾기(<see cref="CapturePeak"/>)를 수행한다.
    /// </summary>
    public PeakFindResult FindPeak()
    {
        // Peak 는 Depth 로만 찾는다. 시퀀스 RGB 모드는 GeomIr=true 라 IR 좌표·IR fx 로 오프셋을 낸다(센터링 기준 IR).
        bool geomIr = GeomIr;
        var frame = geomIr ? _camera.LatestIr : _camera.LatestColor;
        if (frame is null)
            return SetPeakFind(PeakFindResult.Fail(geomIr
                ? "IR 프레임 없음 — 스트림/IR 활성 확인."
                : "컬러 프레임 없음 — 스트림 시작/연결 확인."));
        if (PeakRoi is null)
            return SetPeakFind(PeakFindResult.Fail("Peak ROI 를 먼저 지정하세요."));

        var peak = ComputePeak();

        OpenCvSharp.Mat? bgr = null, gray = null;
        try
        {
            (bgr, gray) = WeldFrameDecoder.Decode(frame, geomIr ? WeldImageMode.Ir : Params.Mode);
            gray?.Dispose();
            gray = null;
            if (bgr is null || bgr.Empty())
                return SetPeakFind(PeakFindResult.Fail("프레임 디코드 실패"));

            bool horiz = Params.ProgressAxis == WeldProgressAxis.Horizontal;
            var weldRoi = WeldRoi ?? RoiRect.Full(bgr.Width, bgr.Height);

            if (peak is not { Found: true })
            {
                _lastOverlay = WeldMaskAnalyzer.EncodePeakOverlay(bgr, weldRoi, Params, null, null, PeakRoi);
                return SetPeakFind(PeakFindResult.Fail("Peak 미검출 — Peak ROI 위치/Depth 스트림을 확인하세요."));
            }

            // 진행축 FOV 센터(= 회색 센터선)로부터의 부호 있는 거리.
            double fovCenter = (horiz ? bgr.Width : bgr.Height) / 2.0;
            double offsetPx = peak.ProgressPos - fovCenter;
            double mmpp = EffectiveMmPerPixel(peak.DepthValue, geomIr);

            _lastOverlay = WeldMaskAnalyzer.EncodePeakOverlay(
                bgr, weldRoi, Params, peak.ProgressPos, "PEAK", PeakRoi,
                peak.HasCrossSpan ? peak.CrossStart : null,
                peak.HasCrossSpan ? peak.CrossEnd : null);

            var r = new PeakFindResult
            {
                Found = true,
                ProgressPos = peak.ProgressPos,
                OffsetPx = offsetPx,
                OffsetMm = offsetPx * mmpp,
                DepthMm = peak.DepthValue,
                Confidence = peak.Confidence,
                PeakCount = peak.PeakCount,
                ScaleAvailable = mmpp > 0,
            };
            LastPeakFind = r;
            string axis = horiz ? "X" : "Y";
            Message = $"① Peak 찾음: {axis} 거리 {offsetPx:+0.0;-0.0}px"
                + (r.ScaleAvailable ? $" ({r.OffsetMm:+0.0;-0.0}mm)" : " (스케일 없음 — px만)")
                + $", depth {peak.DepthValue}mm";
            return r;
        }
        finally
        {
            bgr?.Dispose();
            gray?.Dispose();
        }
    }

    private PeakFindResult SetPeakFind(PeakFindResult r)
    {
        LastPeakFind = r;
        Message = $"① Peak 찾기 실패: {r.Message}";
        return r;
    }

    /// <summary>현재 프레임 한 장 검출(파라미터 튜닝용). 측정 슬롯에는 저장하지 않음.</summary>
    public WeldDetectionResult DetectOnce()
    {
        var r = RunDetect();
        LastDetect = r;
        _lastOverlay = r.OverlayJpeg;
        State = r.Success ? WeldTrackingState.WeldDetected
            : (Detector.IsAvailable ? State : WeldTrackingState.DetectUnavailable);
        Message = r.Success ? $"검출 성공 (d={Fmt(r.DPixel)}, conf={r.Confidence:P0})" : $"검출 실패: {r.Message}";
        return r;
    }

    /// <summary>Peak #1 또는 #2 에서 한 장을 캡처해 d 와 Peak 정보를 측정 슬롯에 저장.</summary>
    public void CapturePeak(int id)
    {
        if (_seqIrGeometry) { CapturePeakRgbIrGeometry(id); return; }

        // Peak(진행축 위치)를 먼저 구해, 오버레이에 자홍색 Peak 선/라벨로 그릴 수 있게 한다.
        // Peak 는 Depth 에서 찾으므로, 검출 프레임이 IR 이면 1:1, RGB 면 공장 캘리브레이션으로
        // Depth↔Color 좌표를 맞춰(방법 ②) ProgressPos 를 검출 프레임 좌표로 변환한다.
        PeakInfo? peak = ComputePeak();

        var r = RunDetect(
            peak is { Found: true } ? peak.ProgressPos : null, $"P{id}",
            peak is { Found: true, HasCrossSpan: true } ? peak.CrossStart : null,
            peak is { Found: true, HasCrossSpan: true } ? peak.CrossEnd : null);
        LastDetect = r;
        _lastOverlay = r.OverlayJpeg;
        if (!r.Success)
        {
            Message = $"② 비드 찾기 #{id} 실패: {r.Message}";
            return;
        }

        // 비드 중심점 위치의 깊이(mm) — Depth 자동 스케일(Z/fx)용. 없으면 peak 깊이로 대체.
        double depthZ = r.WeldPoint is { } wp ? SampleDepthAtDetectionPoint(wp) : 0;
        if (depthZ <= 0 && peak is { Found: true }) depthZ = peak.DepthValue;

        var m = new PeakMeasurement
        {
            PeakId = id,
            DPixel = r.DPixel,
            Confidence = r.Confidence,
            Peak = peak,
            At = DateTime.Now,
            OverlayJpeg = r.OverlayJpeg,
            DepthZ = depthZ,
            IrGeometry = Params.Mode == WeldImageMode.Ir,
        };
        StoreMeasurement(id, m, peak);
    }

    private void StoreMeasurement(int id, PeakMeasurement m, PeakInfo? peak)
    {
        if (id == 1) { M1 = m; State = WeldTrackingState.Peak1Captured; }
        else { M2 = m; State = WeldTrackingState.Peak2Captured; }
        Message = $"② 비드 찾기 #{id}: d={DMm(m):0.0}mm ({m.DPixel:0.0}px{(m.IrGeometry ? "" : ", 컬러")})"
            + (peak is { Found: true } ? $", peak@{peak.ProgressPos:0}px / {peak.DepthValue}mm" : "");

        // Pitch(mm)·두 측정이 있고 스케일이 사용 가능할 때만 자동 각도 산출.
        if (M1 is not null && M2 is not null && Pitch > 0 && ScaleAvailable) ComputeAngle();
    }

    /// <summary>
    /// 시퀀스 RGB 모드 비드 찾기 — <b>검출은 컬러 영상</b>, <b>위치·거리 기준은 IR(=Depth) 좌표</b>.
    /// Peak ROI·Bead ROI(<see cref="_override"/>)는 IR 픽셀이다. Peak 는 Depth 에서 직접 구하고, 그 깊이 Z 에서
    /// Peak 선·IR 화면 중심선·ROI 를 공장 캘리브레이션(Depth→Color)으로 컬러에 정확히 투영해 검출한다.
    /// d 의 기준선을 'IR 화면 중심선의 투영'으로 두므로, 컬러 d 를 IR 픽셀로 환산하면 IR 모드에서 잰 d 와 같은 값이 된다
    /// → 센터링 이동량·각도가 IR 모드와 같은 기준이 되고, IR 기준으로 교시된 ROI·검사캠 오프셋을 그대로 쓴다.
    /// </summary>
    private void CapturePeakRgbIrGeometry(int id)
    {
        WeldDetectionResult Fail(string msg)
        {
            var f = WeldDetectionResult.Fail(msg);
            LastDetect = f;
            Message = $"② 비드 찾기 #{id} 실패: {msg}";
            return f;
        }

        if (!Detector.IsAvailable) { Fail("검출기가 비활성 상태입니다(OpenCV 네이티브 없음 — Windows에서만 지원)."); return; }
        var depth = _camera.LatestDepth;
        var color = _camera.LatestColor;
        if (depth is null) { Fail("Depth 프레임 없음 — 스트림 확인."); return; }
        if (color is null) { Fail("컬러 프레임 없음 — RGB 모드는 컬러 스트림이 필요합니다."); return; }
        if (PeakRoi is not { } peakRoiIr || WeldRoi is not { } weldRoiIr) { Fail("Peak/Bead ROI 없음."); return; }
        if (BuildMapper(depth) is not { } mapper) { Fail("Depth↔컬러 캘리브레이션 없음 — IR 모드로 전환하거나 카메라 연결을 확인하세요."); return; }

        bool horiz = Params.ProgressAxis == WeldProgressAxis.Horizontal;
        var dp = DepthPeakAnalyzer.Analyze(depth, peakRoiIr, Params.ProgressAxis);

        // 투영 깊이 Z: Peak 깊이 → Bead ROI 중앙 깊이 → 기본 작업거리.
        double z = dp.Found ? dp.DepthValue : 0;
        if (z <= 0) z = SampleDepthMm(depth, weldRoiIr.X + weldRoiIr.Width / 2.0, weldRoiIr.Y + weldRoiIr.Height / 2.0);
        if (z <= 0) z = _settings.DefaultWorkDistanceMm;

        // 측정 진행좌표(IR) = Peak 위치(없으면 Bead ROI 중앙), 기준 cross 좌표(IR) = IR 화면 중심선.
        double progIr = dp.Found ? dp.ProgressPos
            : horiz ? weldRoiIr.X + weldRoiIr.Width / 2.0 : weldRoiIr.Y + weldRoiIr.Height / 2.0;
        double centerCrossIr = (horiz ? depth.Height : depth.Width) / 2.0;
        if (mapper.DepthToColor(horiz ? progIr : centerCrossIr, horiz ? centerCrossIr : progIr, z) is not { } refC)
        { Fail("IR 중심선을 컬러로 투영하지 못했습니다(캘리브레이션 확인)."); return; }
        double progC = horiz ? refC.u : refC.v;
        double refCrossC = horiz ? refC.v : refC.u;

        if (mapper.MapDepthRoiToColor(weldRoiIr, z, color.Width, color.Height) is not { } weldRoiC)
        { Fail("Bead ROI 를 컬러 영상으로 투영하지 못했습니다 — ROI 위치/거리를 확인하세요."); return; }
        var peakRoiC = mapper.MapDepthRoiToColor(peakRoiIr, z, color.Width, color.Height);

        // 오버레이용 Peak cross 구간도 컬러로 투영.
        double? cStart = null, cEnd = null;
        if (dp is { Found: true, HasCrossSpan: true }
            && mapper.DepthToColor(horiz ? dp.ProgressPos : dp.CrossStart, horiz ? dp.CrossStart : dp.ProgressPos, z) is { } ca
            && mapper.DepthToColor(horiz ? dp.ProgressPos : dp.CrossEnd, horiz ? dp.CrossEnd : dp.ProgressPos, z) is { } cb)
        {
            cStart = horiz ? ca.v : ca.u;
            cEnd = horiz ? cb.v : cb.u;
        }

        // 기준선 = 투영된 IR 중심선(회색 선이 이 위치에 그려진다), 측정 위치 = 투영된 Peak 선.
        var r = Detector.DetectWeld(color, weldRoiC, Params, WeldReferenceMode.PeakLine, refCrossC,
            progC, $"P{id}", peakRoiC, cStart, cEnd);
        LastDetect = r;
        _lastOverlay = r.OverlayJpeg;
        if (!r.Success) { Message = $"② 비드 찾기 #{id} 실패: {r.Message}"; return; }

        // 컬러 d(px) → IR d(px). 같은 깊이에서 두 카메라 초점거리 비율(축 거의 평행).
        double dIr = r.DPixel * mapper.CrossScaleColorToDepth(horiz);
        double wu = horiz ? progIr : centerCrossIr + dIr;
        double wv = horiz ? centerCrossIr + dIr : progIr;
        double depthZ = SampleDepthMm(depth, wu, wv);
        if (depthZ <= 0) depthZ = z;

        var m = new PeakMeasurement
        {
            PeakId = id,
            DPixel = dIr,
            Confidence = r.Confidence,
            Peak = dp.Found ? dp : null,
            At = DateTime.Now,
            OverlayJpeg = r.OverlayJpeg,
            DepthZ = depthZ,
            IrGeometry = true,
        };
        StoreMeasurement(id, m, m.Peak);
        Message += $" [RGB 검출·IR 기준, 컬러 d={r.DPixel:0.0}px]";
    }

    /// <summary>
    /// Depth 에서 Peak 를 찾아 <see cref="PeakInfo.ProgressPos"/> 를 <b>검출 프레임 좌표</b>로 반환한다.
    /// IR 모드: Depth=IR 동일 좌표라 그대로. RGB 모드: 공장 캘리브레이션으로 Depth→Color 재투영(방법 ②).
    /// 캘리브레이션/컬러 프레임이 없거나 재투영 실패면 Peak 미발견으로 처리(자홍선 미표시).
    /// </summary>
    private PeakInfo? ComputePeak()
    {
        var depth = _camera.LatestDepth;
        if (depth is null || PeakRoi is null) return null;

        // ROI 에 Peak 가 여럿이면 선호 위치(기본 FOV 센터, 센터링 재측정은 이동 후 예상 위치)에 가장 가까운 것을 고른다.
        bool horizAxis = Params.ProgressAxis == WeldProgressAxis.Horizontal;
        double preferDepth = (horizAxis ? depth.Width : depth.Height) / 2.0 + _peakPreferOffsetPx;

        // IR 모드·시퀀스 RGB 모드(IR 기준)는 Peak ROI 가 IR(=Depth) 픽셀이라 그대로 분석.
        if (GeomIr)
            return DepthPeakAnalyzer.Analyze(depth, PeakRoi, Params.ProgressAxis, preferPos: preferDepth);

        // RGB 모드: Peak ROI(Depth 좌표)에서 분석 → peak(Depth)→컬러 재투영.
        var mapper = BuildMapper(depth);
        if (mapper is null) return null;

        // 저장된 ROI 는 IR(=Depth) 기준이라 그대로 Depth 좌표로 쓴다(컬러 투영→역투영 왕복 오차 없음).
        // 일시 적용 ROI(_override)만 있으면 컬러 좌표로 보고 근사 매핑한다.
        var (iw, ih) = IrFrameSize();
        var depthRoi = (_override is null && IrPixel(_roi.Peak) is { } irPeak
                ? ScaleRoi(irPeak, (double)depth.Width / iw, (double)depth.Height / ih, depth.Width, depth.Height)
                : null)
            ?? mapper.MapColorRoiToDepth(PeakRoi, depth.Width, depth.Height);
        var dp = DepthPeakAnalyzer.Analyze(depth, depthRoi, Params.ProgressAxis, preferPos: preferDepth);
        if (!dp.Found) return dp;

        bool horiz = Params.ProgressAxis == WeldProgressAxis.Horizontal;
        double crossCenter = horiz ? depthRoi.Y + depthRoi.Height / 2.0 : depthRoi.X + depthRoi.Width / 2.0;
        double du = horiz ? dp.ProgressPos : crossCenter;
        double dv = horiz ? crossCenter : dp.ProgressPos;
        if (mapper.DepthToColor(du, dv, dp.DepthValue) is not { } cc)
            return new PeakInfo { Found = false };
        double colorProgress = horiz ? cc.u : cc.v;

        // 비드 cross 구간 양 끝점도 컬러로 재투영(자홍선을 그 구간만큼만 그리기 위해).
        bool hasSpan = false; double cStart = 0, cEnd = 0;
        if (dp.HasCrossSpan
            && mapper.DepthToColor(horiz ? dp.ProgressPos : dp.CrossStart, horiz ? dp.CrossStart : dp.ProgressPos, dp.DepthValue) is { } ca
            && mapper.DepthToColor(horiz ? dp.ProgressPos : dp.CrossEnd, horiz ? dp.CrossEnd : dp.ProgressPos, dp.DepthValue) is { } cb)
        {
            cStart = horiz ? ca.v : ca.u;
            cEnd = horiz ? cb.v : cb.u;
            hasSpan = true;
        }

        return new PeakInfo
        {
            Found = true, ProgressPos = colorProgress, DepthValue = dp.DepthValue, Confidence = dp.Confidence,
            PeakCount = dp.PeakCount, HasCrossSpan = hasSpan, CrossStart = cStart, CrossEnd = cEnd,
        };
    }

    /// <summary>Depth↔Color 좌표 매퍼 생성. 캘리브레이션·컬러 프레임이 없으면 null.</summary>
    private DepthColorMapper? BuildMapper(CameraFrame depth)
    {
        var color = _camera.LatestColor;
        if (color is null) return null;
        var p = _camera.GetD2CParams();
        if (p is null || !p.IsValid) return null;
        return new DepthColorMapper(p, depth.Width, depth.Height, color.Width, color.Height);
    }

    /// <summary>
    /// d1·d2·Pitch 로 각도 theta 산출. <b>Pitch 는 항상 mm</b>(로봇 파라미터). d 는 픽셀 측정이라
    /// Depth 자동 스케일(mm/px=Z/fx, ±2점 보정계수)로 mm 환산해 단위를 맞춘다.
    /// </summary>
    public void ComputeAngle()
    {
        if (M1 is null || M2 is null) { Message = "Peak #1, #2 측정이 모두 필요합니다."; return; }
        if (Pitch <= 0) { Message = "Pitch(mm) 값을 입력하세요(>0)."; return; }
        if (!ScaleAvailable)
        {
            Angle = null;
            Message = "⚠ theta 계산 불가: 해상도/FOV 를 알 수 없어 스케일(fx)을 만들 수 없습니다.";
            return;
        }

        // 측정마다 그 지점의 스케일로 d 를 mm 로 환산. 자동이면 Z/fx, 고정이면 mm/px.
        double mmpp1 = EffectiveMmPerPixel(M1.DepthZ, M1.IrGeometry);
        double mmpp2 = EffectiveMmPerPixel(M2.DepthZ, M2.IrGeometry);
        double d1 = M1.DPixel * mmpp1;   // mm
        double d2 = M2.DPixel * mmpp2;   // mm

        // 이동거리가 항상 FOV 보다 커 두 샷의 겹침이 없으므로 영상 기반 Peak 변위 보정은 쓰지 않는다.
        // 분모 Pitch 는 로봇 명령값(mm)을 그대로 사용.
        double pitch = Pitch;            // mm (로봇 파라미터, 명령값)

        double thetaRad = Math.Atan2(d2 - d1, pitch);
        Angle = new AngleResult
        {
            D1 = d1,
            D2 = d2,
            Pitch = pitch,
            NominalPitch = pitch,
            PeakShift = 0,
            Corrected = false,
            ThetaRad = thetaRad,
            ThetaDeg = thetaRad * 180.0 / Math.PI,
            Unit = "mm",
        };
        State = WeldTrackingState.ThetaComputed;
        Message = $"theta = {Angle.ThetaDeg:0.00}° (d1={d1:0.0}, d2={d2:0.0} mm, Pitch={pitch:0.#})";
    }

    public void ResetMeasurements()
    {
        M1 = M2 = null; Angle = null; LastPeakFind = null;
        State = WeldTrackingState.Idle; Message = "측정 초기화됨";
    }

    // ── 시퀀스용 비동기 API ──────────────────────────────────────────
    // 시퀀스는 ROI 를 스스로 계산해 넘긴다(같은 파라미터 + 평탄면/중앙 폴백). 여기서는 그 ROI 를
    // 일시 적용(_override)만 하고 finally 에서 해제한다 — 파라미터에는 쓰지 않는다.
    // 검출 자체는 수백 ms 걸릴 수 있어 Task.Run 으로 Blazor 서킷 스레드를 막지 않는다.
    // (기존 동기 메서드 내부는 취소할 수 없다. 한 호출이 수 초 이내라 허용 범위로 본다.)

    /// <summary>시퀀스용 Peak 찾기. <paramref name="peakRoi"/> 를 일시 적용해 <see cref="FindPeak"/> 수행.</summary>
    /// <param name="preferOffsetPx">ROI 에 Peak 가 여럿일 때 고를 위치 — 진행축 FOV 센터 기준 IR 픽셀(0=센터).
    /// 센터링 재측정은 이동 후 예상 이격을 넣어 옆 Peak 를 잡지 않게 한다.</param>
    public async Task<PeakFindResult> FindPeakAsync(RoiRect peakRoi, CancellationToken ct = default, double preferOffsetPx = 0)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // 시퀀스 ROI 는 IR 픽셀 — RGB 여도 IR 기준(오버레이도 IR 영상에 그리므로 Bead ROI 도 IR 픽셀로).
            _override = new RoiOverride(peakRoi, IrPixel(_roi.Bead));
            _seqIrGeometry = Params.Mode != WeldImageMode.Ir;
            _peakPreferOffsetPx = preferOffsetPx;
            return await Task.Run(FindPeak, ct);
        }
        finally
        {
            _peakPreferOffsetPx = 0;
            _seqIrGeometry = false;
            _override = null;
            _gate.Release();
        }
    }

    /// <summary>
    /// 시퀀스용 비드 찾기. ROI 를 일시 적용해 <see cref="CapturePeak"/> 를 수행하고 측정 슬롯
    /// (<see cref="M1"/>/<see cref="M2"/>)과 검출 결과를 함께 반환한다. 오버레이는 UI 에서 확인 가능하다.
    /// </summary>
    public async Task<(PeakMeasurement? Measurement, WeldDetectionResult? Detect)> CapturePeakAsync(
        int id, RoiRect peakRoi, RoiRect weldRoi, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _override = new RoiOverride(peakRoi, weldRoi);
            _seqIrGeometry = Params.Mode != WeldImageMode.Ir;   // RGB 면 컬러로 검출하되 d·mm 는 IR 기준
            await Task.Run(() => CapturePeak(id), ct);
            return (id == 1 ? M1 : M2, LastDetect);
        }
        finally
        {
            _seqIrGeometry = false;
            _override = null;
            _gate.Release();
        }
    }

    // ── 시퀀스 수동 비드 입력 ───────────────────────────────────────
    /// <summary>
    /// 수동 비드 입력용 스냅샷 — 현재 IR·컬러·Depth 프레임을 고정하고, 각 영상 좌표의 가이드(Peak 선·기준선·ROI·
    /// 자동 검출 선)를 계산한다. 사람이 이 스냅샷 위에 선을 그리고, 같은 프레임이 학습 데이터로 저장된다.
    /// ROI 는 시퀀스 규약대로 IR 픽셀. IR 프레임이 없으면 null.
    /// </summary>
    /// <param name="auto">직전 자동 검출 결과(있으면 그 중심선을 초안으로 표시). 좌표는 현재 입력 모드 영상 기준.</param>
    public async Task<ManualBeadSnapshot?> PrepareManualBeadAsync(
        int id, RoiRect peakRoi, RoiRect beadRoi, WeldDetectionResult? auto, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await Task.Run(() => BuildManualSnapshot(id, peakRoi, beadRoi, auto), ct);
        }
        finally { _gate.Release(); }
    }

    private ManualBeadSnapshot? BuildManualSnapshot(int id, RoiRect peakRoi, RoiRect beadRoi, WeldDetectionResult? auto)
    {
        var ir = _camera.LatestIr;
        var depth = _camera.LatestDepth;
        var color = _camera.LatestColor;
        if (ir is null || depth is null) return null;

        bool horiz = Params.ProgressAxis == WeldProgressAxis.Horizontal;
        double preferDepth = (horiz ? depth.Width : depth.Height) / 2.0;
        var dp = DepthPeakAnalyzer.Analyze(depth, peakRoi, Params.ProgressAxis, preferPos: preferDepth);

        // 측정 진행좌표(IR) = Peak 위치(없으면 Bead ROI 중앙), 기준 cross(IR) = FOV 센터선 — 자동 검출과 같은 규약.
        double progIr = dp.Found ? dp.ProgressPos
            : horiz ? beadRoi.X + beadRoi.Width / 2.0 : beadRoi.Y + beadRoi.Height / 2.0;
        double centerCrossIr = (horiz ? ir.Height : ir.Width) / 2.0;

        // 자동 검출 중심선: 피팅에 성공한 결과만 초안으로 쓴다. 좌표는 검출 입력 모드 영상 기준.
        string autoMod = Params.Mode == WeldImageMode.Ir ? "ir" : "rgb";
        IReadOnlyList<PixelPoint>? autoLine = auto is { Success: true, LineFitOk: true, Centerline.Count: >= 2 }
            ? auto.Centerline.ToList() : null;

        var irGuide = new ManualBeadGuide("ir", ir.Width, ir.Height, horiz, progIr, centerCrossIr, dp.Found,
            dp is { Found: true, HasCrossSpan: true } ? dp.CrossStart : null,
            dp is { Found: true, HasCrossSpan: true } ? dp.CrossEnd : null,
            beadRoi, peakRoi, autoMod == "ir" ? autoLine : null);

        // 컬러 가이드: Depth→Color 투영(자동 RGB 검출과 같은 깊이 Z·같은 매핑). 캘리브레이션이 없으면 컬러 입력 불가.
        double z = dp.Found ? dp.DepthValue : 0;
        if (z <= 0) z = SampleDepthMm(depth, beadRoi.X + beadRoi.Width / 2.0, beadRoi.Y + beadRoi.Height / 2.0);
        if (z <= 0) z = _settings.DefaultWorkDistanceMm;

        ManualBeadGuide? colorGuide = null;
        double crossScale = 0;
        byte[]? colorPng = null;
        if (color is not null && BuildMapper(depth) is { } mapper
            && mapper.DepthToColor(horiz ? progIr : centerCrossIr, horiz ? centerCrossIr : progIr, z) is { } refC)
        {
            double? cs = null, ce = null;
            if (dp is { Found: true, HasCrossSpan: true }
                && mapper.DepthToColor(horiz ? dp.ProgressPos : dp.CrossStart, horiz ? dp.CrossStart : dp.ProgressPos, z) is { } ca
                && mapper.DepthToColor(horiz ? dp.ProgressPos : dp.CrossEnd, horiz ? dp.CrossEnd : dp.ProgressPos, z) is { } cb)
            {
                cs = horiz ? ca.v : ca.u;
                ce = horiz ? cb.v : cb.u;
            }
            colorGuide = new ManualBeadGuide("rgb", color.Width, color.Height, horiz,
                horiz ? refC.u : refC.v, horiz ? refC.v : refC.u, dp.Found, cs, ce,
                mapper.MapDepthRoiToColor(beadRoi, z, color.Width, color.Height),
                mapper.MapDepthRoiToColor(peakRoi, z, color.Width, color.Height),
                autoMod == "rgb" ? autoLine : null);
            crossScale = mapper.CrossScaleColorToDepth(horiz);
            try { colorPng = CameraService.EncodeColorPng(color); }
            catch (Exception ex) { _logger.LogWarning(ex, "수동 비드 입력: 컬러 PNG 인코딩 실패 — IR 만 제공"); colorGuide = null; }
        }

        return new ManualBeadSnapshot
        {
            PeakId = id,
            Horizontal = horiz,
            Ir = ir,
            Depth = depth,
            Color = colorGuide is null ? null : color,
            IrPng = CameraService.EncodeIrPng(ir),
            ColorPng = colorPng,
            Peak = dp.Found ? dp : null,
            ProjectionZ = z,
            CrossScaleColorToIr = crossScale,
            IrGuide = irGuide,
            ColorGuide = colorGuide,
            AutoModality = autoMod,
        };
    }

    /// <summary>
    /// 사람이 그린 비드선(<paramref name="modality"/> 영상 픽셀)으로 측정을 만들어 <see cref="M1"/>/<see cref="M2"/> 에 저장한다.
    /// d 는 자동 검출과 같은 규약 — Peak 진행위치에서 비드선 cross 좌표 − FOV 센터선, IR 픽셀 기준. 컬러에 그린 선은
    /// 투영된 IR 중심선 기준으로 잰 뒤 초점거리 비율로 IR 픽셀로 환산한다(RGB 자동 검출과 동일).
    /// 선이 진행축으로 펼쳐지지 않았으면 null.
    /// </summary>
    public async Task<PeakMeasurement?> ApplyManualBeadAsync(
        ManualBeadSnapshot s, string modality, IReadOnlyList<PixelPoint> points, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            bool rgb = modality == "rgb";
            var guide = rgb ? s.ColorGuide : s.IrGuide;
            if (guide is null) return null;
            if (ManualBeadGeometry.CrossAt(points, s.Horizontal, guide.ProgressPos) is not { } cross) return null;

            double dIr = rgb ? (cross - guide.RefCross) * s.CrossScaleColorToIr : cross - guide.RefCross;
            double progIr = s.IrGuide.ProgressPos, centerIr = s.IrGuide.RefCross;
            double depthZ = SampleDepthMm(s.Depth,
                s.Horizontal ? progIr : centerIr + dIr,
                s.Horizontal ? centerIr + dIr : progIr);
            if (depthZ <= 0) depthZ = s.ProjectionZ;

            var overlay = await Task.Run(() => ManualOverlayJpeg(rgb ? s.ColorPng : s.IrPng, guide, points, cross), ct);
            var m = new PeakMeasurement
            {
                PeakId = s.PeakId,
                DPixel = dIr,
                Confidence = 1.0,
                Peak = s.Peak,
                At = DateTime.Now,
                OverlayJpeg = overlay,
                DepthZ = depthZ,
                IrGeometry = true,
                Manual = true,
            };
            if (overlay is not null) _lastOverlay = overlay;
            StoreMeasurement(s.PeakId, m, s.Peak);
            Message += rgb ? $" [수동 입력·RGB, 컬러 d={cross - guide.RefCross:0.0}px]" : " [수동 입력]";
            return m;
        }
        finally { _gate.Release(); }
    }

    /// <summary>수동 측정 오버레이(Weld 페이지·모니터 표시용). OpenCV 네이티브가 없으면 null.</summary>
    private byte[]? ManualOverlayJpeg(byte[]? png, ManualBeadGuide g, IReadOnlyList<PixelPoint> pts, double cross)
    {
        if (png is null) return null;
        try
        {
            using var src = OpenCvSharp.Cv2.ImDecode(png, OpenCvSharp.ImreadModes.Color);
            if (src.Empty()) return null;
            OpenCvSharp.Point P(double x, double y) => new((int)Math.Round(x), (int)Math.Round(y));
            OpenCvSharp.Point Pc(double prog, double c) => g.Horizontal ? P(prog, c) : P(c, prog);
            int len = g.Horizontal ? g.Width : g.Height, crossLen = g.Horizontal ? g.Height : g.Width;

            if (g.BeadRoi is { } br)
                OpenCvSharp.Cv2.Rectangle(src, new OpenCvSharp.Rect(br.X, br.Y, br.Width, br.Height), new OpenCvSharp.Scalar(0, 200, 255), 1);
            // 회색 기준선(FOV 센터), 자홍 Peak 선, 초록 수동 비드선, 빨간 교점.
            OpenCvSharp.Cv2.Line(src, Pc(0, g.RefCross), Pc(len, g.RefCross), new OpenCvSharp.Scalar(160, 160, 160), 1);
            double c0 = g.PeakCrossStart ?? 0, c1 = g.PeakCrossEnd ?? crossLen;
            OpenCvSharp.Cv2.Line(src, Pc(g.ProgressPos, c0), Pc(g.ProgressPos, c1), new OpenCvSharp.Scalar(255, 0, 255), 2);
            for (int i = 0; i + 1 < pts.Count; i++)
                OpenCvSharp.Cv2.Line(src, P(pts[i].X, pts[i].Y), P(pts[i + 1].X, pts[i + 1].Y), new OpenCvSharp.Scalar(0, 255, 0), 2);
            OpenCvSharp.Cv2.Circle(src, Pc(g.ProgressPos, cross), 5, new OpenCvSharp.Scalar(0, 0, 255), -1);
            OpenCvSharp.Cv2.PutText(src, "MANUAL", new OpenCvSharp.Point(8, 22), OpenCvSharp.HersheyFonts.HersheySimplex, 0.7,
                new OpenCvSharp.Scalar(0, 255, 255), 2);
            OpenCvSharp.Cv2.ImEncode(".jpg", src, out var jpg);
            return jpg;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "수동 비드 오버레이 생성 실패(OpenCV 없음?) — 오버레이 없이 진행");
            return null;
        }
    }

    // ── 내부 ────────────────────────────────────────────────────────
    private WeldDetectionResult RunDetect(double? peakProgressPos = null, string? peakLabel = null,
        double? peakCrossStart = null, double? peakCrossEnd = null)
    {
        if (!Detector.IsAvailable)
            return WeldDetectionResult.Fail("검출기가 비활성 상태입니다(OpenCV 네이티브 없음 — Windows에서만 지원).");

        var frame = Params.Mode == WeldImageMode.Ir ? _camera.LatestIr : _camera.LatestColor;
        if (frame is null)
            return WeldDetectionResult.Fail(Params.Mode == WeldImageMode.Ir
                ? "IR 프레임 없음 — 스트림/IR 활성 확인."
                : "컬러 프레임 없음 — 스트림 시작/연결 확인.");

        var weldRoi = WeldRoi ?? RoiRect.Full(frame.Width, frame.Height);

        // d 기준선은 FOV(전체 화면) 센터선으로 고정. 자홍 Peak 선은 depth 기반 비드 cross 구간만큼 그린다.
        return Detector.DetectWeld(frame, weldRoi, Params, WeldReferenceMode.FovCenter, null,
            peakProgressPos, peakLabel, PeakRoi, peakCrossStart, peakCrossEnd);
    }

    /// <summary>
    /// 현재 프레임에서 DL 라벨 <b>초안</b> 마스크(비드=흰색, 배경=검정)를 굽는다. 검출기가 찾은 비드
    /// cross 구간을 채워 만든다(=경계 채우기). 검출 실패·신뢰도 미달이면 null(→ 사람이 처음부터 라벨).
    /// 반환 Modality 는 검출 모드에 맞춘 "ir"/"rgb" (저장 이미지와 정렬).
    /// </summary>
    public (byte[] Png, string Modality, double Confidence)? TryBuildLabelMask(double minConfidence = 0.3)
    {
        // 라벨 초안은 항상 고전 CV 로 생성한다(아직 DL 모델이 없을 때도 초안을 만들어 라벨링을 부트스트랩).
        if (!_cvDetector.IsAvailable) return null;
        var frame = Params.Mode == WeldImageMode.Ir ? _camera.LatestIr : _camera.LatestColor;
        if (frame is null) return null;

        var weldRoi = WeldRoi ?? RoiRect.Full(frame.Width, frame.Height);
        var r = _cvDetector.DetectWeld(frame, weldRoi, Params, WeldReferenceMode.FovCenter);
        if (!r.Success || r.BeadSpans is not { Count: > 0 } spans || r.Confidence < minConfidence)
            return null;

        bool horiz = Params.ProgressAxis == WeldProgressAxis.Horizontal;
        var png = RasterizeMaskPng(spans, frame.Width, frame.Height, horiz);
        string modality = Params.Mode == WeldImageMode.Ir ? "ir" : "rgb";
        return (png, modality, r.Confidence);
    }

    /// <summary>비드 span 들을 채워 L8(1채널) PNG 마스크로 래스터화. 비드=255, 배경=0.</summary>
    private static byte[] RasterizeMaskPng(IReadOnlyList<BeadSpan> spans, int w, int h, bool horiz)
    {
        var buf = new byte[w * h];
        foreach (var sp in spans)
        {
            if (horiz)
            {
                int x = sp.Progress;
                if ((uint)x >= (uint)w) continue;
                int y0 = Math.Clamp(Math.Min(sp.CrossStart, sp.CrossEnd), 0, h - 1);
                int y1 = Math.Clamp(Math.Max(sp.CrossStart, sp.CrossEnd), 0, h - 1);
                for (int y = y0; y <= y1; y++) buf[y * w + x] = 255;
            }
            else
            {
                int y = sp.Progress;
                if ((uint)y >= (uint)h) continue;
                int x0 = Math.Clamp(Math.Min(sp.CrossStart, sp.CrossEnd), 0, w - 1);
                int x1 = Math.Clamp(Math.Max(sp.CrossStart, sp.CrossEnd), 0, w - 1);
                int rowBase = y * w;
                for (int x = x0; x <= x1; x++) buf[rowBase + x] = 255;
            }
        }
        using var img = Image.LoadPixelData<L8>(buf, w, h);
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    private static string Fmt(double dpx) => $"{dpx:0.0}px";   // 1회 검출(튜닝)은 깊이 컨텍스트가 없어 px 로 표시

    /// <summary>
    /// 검출 프레임 크기(ROI 픽셀 좌표계) — IR 모드는 IR(=Depth 해상도, 시퀀스와 동일 기준), RGB 모드는 컬러.
    /// 스트림이 없으면 설정 해상도.
    /// </summary>
    private (int w, int h) DetectionFrameSize()
    {
        if (Params.Mode == WeldImageMode.Ir)
            return _camera.LatestIr is { } ir
                ? (ir.Width, ir.Height)
                : (_camera.Settings.IrWidth, _camera.Settings.IrHeight);
        return _camera.LatestColor is { } c
            ? (c.Width, c.Height)
            : (_camera.Settings.ColorWidth, _camera.Settings.ColorHeight);
    }

    // ── 스케일(mm 환산) ─────────────────────────────────────────────
    /// <summary>Depth 자동 mm/px = Z/fx (기본). 2점 보정계수가 켜져 있으면 곱해 보강한다.</summary>
    /// <param name="ir">픽셀 좌표계가 IR(=Depth)이면 true, 컬러면 false — 해당 카메라의 fx 를 쓴다.</param>
    private double EffectiveMmPerPixel(double depthZ, bool ir)
    {
        double fx = Fx(ir);
        if (fx <= 0) return 0;
        double z = depthZ > 0 ? depthZ : _settings.DefaultWorkDistanceMm;
        double mmpp = z / fx;                                          // Depth 자동 기본
        return ScaleCorrectionEnabled ? mmpp * ScaleCorrection : mmpp; // 2점 보정 보강
    }

    /// <summary>현재 검출 모드의 fx(px). 2점 보정·검증처럼 화면(검출 프레임) 좌표를 다루는 곳에서 쓴다.</summary>
    private double Fx() => Fx(Params.Mode == WeldImageMode.Ir);

    /// <summary>IR 또는 컬러 카메라의 fx(px) = (영상폭/2)/tan(HFov/2). FOV·해상도 기반(간이 intrinsic).</summary>
    private double Fx(bool ir)
    {
        var frame = ir ? _camera.LatestIr : _camera.LatestColor;
        int w = frame?.Width ?? (ir ? _camera.Settings.IrWidth : _camera.Settings.ColorWidth);
        double hfov = ir ? _settings.IrHFovDeg : _settings.ColorHFovDeg;
        if (w <= 0 || hfov <= 0) return 0;
        return (w / 2.0) / Math.Tan(hfov * Math.PI / 180.0 / 2.0);
    }

    /// <summary>검출 프레임 좌표 (u,v) 위치의 깊이(mm). 깊이 해상도가 다르면 비율로 스케일. 무효면 0.</summary>
    private double SampleDepthAtDetectionPoint(PixelPoint p)
    {
        var depth = _camera.LatestDepth;
        if (depth is null) return 0;
        bool ir = Params.Mode == WeldImageMode.Ir;
        var frame = ir ? _camera.LatestIr : _camera.LatestColor;
        int fw = frame?.Width ?? depth.Width, fh = frame?.Height ?? depth.Height;
        double sx = (double)depth.Width / fw, sy = (double)depth.Height / fh;
        return SampleDepthMm(depth, p.X * sx, p.Y * sy);
    }

    private static double SampleDepthMm(CameraFrame depth, double u, double v)
    {
        int x = (int)Math.Round(u), y = (int)Math.Round(v);
        var span = MemoryMarshal.Cast<byte, ushort>(depth.Pixels);
        var vals = new List<int>(25);
        for (int dy = -2; dy <= 2; dy++)
        for (int dx = -2; dx <= 2; dx++)
        {
            int xx = x + dx, yy = y + dy;
            if (xx < 0 || yy < 0 || xx >= depth.Width || yy >= depth.Height) continue;
            int idx = yy * depth.Width + xx;
            if ((uint)idx >= (uint)span.Length) continue;
            int mm = span[idx];
            if (mm > 0) vals.Add(mm);
        }
        if (vals.Count == 0) return 0;
        vals.Sort();
        return vals[vals.Count / 2];
    }

    // ── 2점 보정(보강) / 검증 ───────────────────────────────────────
    /// <summary>
    /// 클릭한 두 점과 아는 거리(mm)로 <b>보정계수</b>를 산출해 Depth 자동에 곱한다(보강).
    /// 보정계수 = 참값 ÷ (Depth 자동만으로 잰 값). 거리 자동대응은 유지하면서 체계적 오차만 잡는다.
    /// </summary>
    public void Calibrate2Point(PixelPoint p1, PixelPoint p2, double knownMm)
    {
        double dist = Distance(p1, p2);
        if (dist < 1 || knownMm <= 0) { Message = "두 점이 너무 가깝거나 실제 거리(mm)가 0입니다."; return; }
        double fx = Fx();
        double z = SampleDepthAtDetectionPoint(new PixelPoint((p1.X + p2.X) / 2, (p1.Y + p2.Y) / 2));
        if (z <= 0) z = _settings.DefaultWorkDistanceMm;
        double autoMeasured = fx > 0 ? dist * (z / fx) : 0;     // Depth 자동만으로 잰 길이
        if (autoMeasured <= 0) { Message = "Depth 자동 스케일을 만들 수 없습니다(해상도·FOV 확인)."; return; }
        ScaleCorrection = knownMm / autoMeasured;
        ScaleCorrectionEnabled = true;
        Message = $"2점 보정: Depth 자동 {autoMeasured:0.0}mm → 참값 {knownMm:0.#}mm, 보정계수 ×{ScaleCorrection:0.0000} 적용";
    }

    /// <summary>아는 길이를 보정 전(Depth 자동)/후(×보정계수)로 측정해 비교(검증).</summary>
    public ScaleValidationResult ValidateScale(PixelPoint p1, PixelPoint p2, double knownMm)
    {
        double dist = Distance(p1, p2);
        double fx = Fx();
        double zMid = SampleDepthAtDetectionPoint(new PixelPoint((p1.X + p2.X) / 2, (p1.Y + p2.Y) / 2));
        if (zMid <= 0) zMid = _settings.DefaultWorkDistanceMm;
        double baseMm = fx > 0 ? dist * (zMid / fx) : 0;        // 보정 전(Depth 자동)
        var res = new ScaleValidationResult
        {
            TrueMm = knownMm,
            PixelDist = dist,
            AutoMm = baseMm,
            FixedMm = baseMm * ScaleCorrection,                 // 보정 후(×보정계수)
            FixedAvailable = ScaleCorrectionEnabled,
        };
        LastValidation = res;
        Message = $"검증: 참값 {knownMm:0.#}mm / Depth자동 {res.AutoMm:0.0}mm ({res.AutoErrPct:+0.0;-0.0}%)"
            + (res.FixedAvailable ? $" / 보정후 {res.FixedMm:0.0}mm ({res.FixedErrPct:+0.0;-0.0}%)" : "");
        return res;
    }

    private static double Distance(PixelPoint a, PixelPoint b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

/// <summary>
/// 수동 비드 입력 스냅샷(<see cref="WeldTrackingService.PrepareManualBeadAsync"/>). 사람이 본 프레임 그대로를
/// 측정·학습 데이터 저장에 쓴다. <see cref="IrGuide"/>·<see cref="ColorGuide"/> 는 각 영상 픽셀 좌표.
/// </summary>
public sealed class ManualBeadSnapshot
{
    public int PeakId { get; init; }
    public bool Horizontal { get; init; }
    public CameraFrame Ir { get; init; } = null!;
    public CameraFrame Depth { get; init; } = null!;
    /// <summary>컬러 프레임 — 캘리브레이션 투영이 가능할 때만(아니면 null, 컬러 입력 불가).</summary>
    public CameraFrame? Color { get; init; }
    public byte[] IrPng { get; init; } = [];
    public byte[]? ColorPng { get; init; }
    public PeakInfo? Peak { get; init; }
    public double ProjectionZ { get; init; }
    public double CrossScaleColorToIr { get; init; }
    public ManualBeadGuide IrGuide { get; init; } = null!;
    public ManualBeadGuide? ColorGuide { get; init; }
    /// <summary>자동 검출 입력 모드("ir"/"rgb") — 자동 선 초안이 이 영상 좌표다.</summary>
    public string AutoModality { get; init; } = "ir";

    public ManualBeadGuide? GuideFor(string modality) => modality == "rgb" ? ColorGuide : IrGuide;
}
