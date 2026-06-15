using HD_AMR.Communication.Weld;
using HD_AMR.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HD_AMR.Service;

/// <summary>
/// 용접라인 추적 오케스트레이션(명세서 v2). 1차 구현은 <b>수동 2-shot 측정</b>:
/// Peak #1·Peak #2 에서 운영자가 트리거 → 각 한 장에서 비드 centerline·위치오차 d(픽셀)를 구하고,
/// d1·d2·pitch 로 각도 theta 를 산출한다(화면 표시만, 로봇 미동작). 검출은 <see cref="IWeldVisionDetector"/>,
/// Peak 는 <see cref="DepthPeakAnalyzer"/>, ROI 는 <see cref="RoiProfileStore"/>(JSON) 사용.
/// 싱글톤 — 페이지 이동 간 ROI/파라미터/측정 상태가 유지된다.
/// </summary>
public class WeldTrackingService
{
    private readonly CameraService _camera;
    private readonly IWeldVisionDetector _detector;
    private readonly RoiProfileStore _store;
    private readonly WeldTrackingSettings _settings;
    private readonly ILogger<WeldTrackingService> _logger;

    public WeldTrackingService(
        CameraService camera,
        IWeldVisionDetector detector,
        RoiProfileStore store,
        IOptions<WeldTrackingSettings> options,
        ILoggerFactory loggerFactory)
    {
        _camera = camera;
        _detector = detector;
        _store = store;
        _settings = options.Value;
        _logger = loggerFactory.CreateLogger<WeldTrackingService>();
        MmPerPixel = _settings.MmPerPixel;
        PitchCorrectionEnabled = _settings.PitchCorrectionEnabled;
        PitchCorrectionSign = _settings.PitchCorrectionSign >= 0 ? 1 : -1;

        if (!string.IsNullOrWhiteSpace(_settings.AutoLoadProfile))
        {
            var p = _store.Load(_settings.AutoLoadProfile!);
            if (p is not null) ApplyProfile(p);
        }
    }

    // ── 상태(UI 가 읽음) ────────────────────────────────────────────
    public bool DetectorAvailable => _detector.IsAvailable;
    public RoiRect? PeakRoi { get; private set; }
    public RoiRect? WeldRoi { get; private set; }
    public string ProfileName { get; set; } = "default";
    public WeldDetectionParams Params { get; } = new();
    public WeldReferenceMode ReferenceMode { get; set; } = WeldReferenceMode.FovCenter;
    public double Pitch { get; set; }
    public double MmPerPixel { get; set; }

    /// <summary>Peak 변위로 pitch 를 보정할지(로봇 반복정도 오차 교정).</summary>
    public bool PitchCorrectionEnabled { get; set; }
    /// <summary>Peak 변위 보정 부호(+1/−1).</summary>
    public int PitchCorrectionSign { get; set; } = -1;

    public WeldTrackingState State { get; private set; } = WeldTrackingState.Idle;
    public string? Message { get; private set; }
    public WeldDetectionResult? LastDetect { get; private set; }
    public PeakMeasurement? M1 { get; private set; }
    public PeakMeasurement? M2 { get; private set; }
    public AngleResult? Angle { get; private set; }

    public byte[]? LastOverlay => LastDetect?.OverlayJpeg;
    public byte[]? Peak1Overlay => M1?.OverlayJpeg;
    public byte[]? Peak2Overlay => M2?.OverlayJpeg;

    public IReadOnlyList<string> ListProfiles() => _store.List();
    public string ProfileDirectory => _store.Directory_;

    // ── ROI 관리 ────────────────────────────────────────────────────
    public void SetPeakRoi(RoiRect? r) => PeakRoi = r;
    public void SetWeldRoi(RoiRect? r) => WeldRoi = r;
    public void ResetRoi() { PeakRoi = null; WeldRoi = null; Message = "ROI 초기화됨"; }

    public void SaveProfile(string name)
    {
        var size = FrameSize();
        _store.Save(new RoiProfile
        {
            Name = name,
            PeakRoi = PeakRoi,
            WeldRoi = WeldRoi,
            FrameWidth = size?.w ?? 0,
            FrameHeight = size?.h ?? 0,
        });
        ProfileName = name;
        Message = $"프로파일 '{name}' 저장됨";
    }

    public bool LoadProfile(string name)
    {
        var p = _store.Load(name);
        if (p is null) { Message = $"프로파일 '{name}' 없음"; return false; }
        ApplyProfile(p);
        Message = $"프로파일 '{name}' 불러옴";
        return true;
    }

    private void ApplyProfile(RoiProfile p)
    {
        ProfileName = p.Name;
        var size = FrameSize();
        if (size is { } s)
        {
            PeakRoi = RoiProfileStore.ValidateAndClamp(p.PeakRoi, s.w, s.h, out var c1);
            WeldRoi = RoiProfileStore.ValidateAndClamp(p.WeldRoi, s.w, s.h, out var c2);
            if (c1 || c2) Message = "Load 된 ROI 가 현재 해상도에 맞게 보정되었습니다.";
        }
        else
        {
            PeakRoi = p.PeakRoi;
            WeldRoi = p.WeldRoi;
        }
    }

    // ── 검출/측정 ───────────────────────────────────────────────────
    /// <summary>현재 프레임 한 장 검출(파라미터 튜닝용). 측정 슬롯에는 저장하지 않음.</summary>
    public WeldDetectionResult DetectOnce()
    {
        var r = RunDetect();
        LastDetect = r;
        State = r.Success ? WeldTrackingState.WeldDetected
            : (_detector.IsAvailable ? State : WeldTrackingState.DetectUnavailable);
        Message = r.Success ? $"검출 성공 (d={Fmt(r.DPixel)}, conf={r.Confidence:P0})" : $"검출 실패: {r.Message}";
        return r;
    }

    /// <summary>Peak #1 또는 #2 에서 한 장을 캡처해 d 와 Peak 정보를 측정 슬롯에 저장.</summary>
    public void CapturePeak(int id)
    {
        var r = RunDetect();
        LastDetect = r;
        if (!r.Success)
        {
            Message = $"Peak #{id} 캡처 실패: {r.Message}";
            return;
        }

        PeakInfo? peak = null;
        var depth = _camera.LatestDepth;
        if (depth is not null && PeakRoi is not null)
            peak = DepthPeakAnalyzer.Analyze(depth, PeakRoi, Params.ProgressAxis);

        var m = new PeakMeasurement
        {
            PeakId = id,
            DPixel = r.DPixel,
            Confidence = r.Confidence,
            Peak = peak,
            At = DateTime.Now,
            OverlayJpeg = r.OverlayJpeg,
        };
        if (id == 1) { M1 = m; State = WeldTrackingState.Peak1Captured; }
        else { M2 = m; State = WeldTrackingState.Peak2Captured; }
        Message = $"Peak #{id} 캡처: d={Fmt(r.DPixel)}" + (peak is { Found: true } ? $", peak@{peak.ProgressPos:0}px / {peak.DepthValue}mm" : "");

        // Pitch(mm)·두 측정이 있고 mm/px(환산계수)가 설정됐을 때만 자동 각도 산출.
        if (M1 is not null && M2 is not null && Pitch > 0 && MmPerPixel > 0) ComputeAngle();
    }

    /// <summary>
    /// d1·d2·Pitch 로 각도 theta 산출. <b>Pitch 는 항상 mm</b>(로봇 파라미터)이고 d·ΔPeak 는 픽셀
    /// 측정이므로, 둘을 같은 단위로 맞추려면 <see cref="MmPerPixel"/>(환산계수)가 반드시 필요하다.
    /// mm/px 가 0 이면 단위 불일치라 각도를 계산하지 않고 <see cref="Angle"/> 를 비우고 경고만 남긴다.
    /// </summary>
    public void ComputeAngle()
    {
        if (M1 is null || M2 is null) { Message = "Peak #1, #2 측정이 모두 필요합니다."; return; }
        if (Pitch <= 0) { Message = "Pitch(mm) 값을 입력하세요(>0)."; return; }
        if (MmPerPixel <= 0)
        {
            // 사고 방지: 픽셀 d 와 mm Pitch 를 섞어 계산하지 않는다.
            Angle = null;
            Message = "⚠ theta 계산 불가: mm/px(환산계수)가 0 입니다. d 는 픽셀, Pitch 는 mm 라 단위가 달라 " +
                      "각도를 계산할 수 없습니다. mm/px 값을 입력하세요.";
            return;
        }

        double mmpp = MmPerPixel;
        double d1 = M1.DPixel * mmpp;   // mm
        double d2 = M2.DPixel * mmpp;   // mm

        double nominal = Pitch;          // mm (로봇 파라미터)
        double effPitch = nominal;
        double peakShift = 0;            // mm
        bool corrected = false;

        // Peak 변위 보정: 보정 Pitch = Pitch + Sign × ΔPeak (ΔPeak = (ProgressPos2 − ProgressPos1)×mm/px).
        if (PitchCorrectionEnabled && M1.Peak is { Found: true } p1 && M2.Peak is { Found: true } p2)
        {
            double dppPx = p2.ProgressPos - p1.ProgressPos;
            peakShift = dppPx * mmpp;    // mm
            int sign = PitchCorrectionSign >= 0 ? 1 : -1;
            effPitch = nominal + sign * peakShift;
            corrected = true;

            if (effPitch <= 0)
            {
                Message = $"보정 Pitch({effPitch:0.#}mm)가 0 이하 — 부호(+/−)/값 확인. Pitch 값으로 계산합니다.";
                effPitch = nominal;
                corrected = false;
            }
        }

        double thetaRad = Math.Atan2(d2 - d1, effPitch);
        Angle = new AngleResult
        {
            D1 = d1,
            D2 = d2,
            Pitch = effPitch,
            NominalPitch = nominal,
            PeakShift = peakShift,
            Corrected = corrected,
            ThetaRad = thetaRad,
            ThetaDeg = thetaRad * 180.0 / Math.PI,
            Unit = "mm",
        };
        State = WeldTrackingState.ThetaComputed;
        Message = corrected
            ? $"theta = {Angle.ThetaDeg:0.00}° · 보정 Pitch={effPitch:0.#}mm (Pitch {nominal:0.#} {(PitchCorrectionSign >= 0 ? "+" : "−")} ΔPeak {Math.Abs(peakShift):0.0})"
            : $"theta = {Angle.ThetaDeg:0.00}° (d1={d1:0.0}, d2={d2:0.0} mm, Pitch={effPitch:0.#})";
    }

    public void ResetMeasurements()
    {
        M1 = M2 = null; Angle = null; State = WeldTrackingState.Idle; Message = "측정 초기화됨";
    }

    // ── 내부 ────────────────────────────────────────────────────────
    private WeldDetectionResult RunDetect()
    {
        if (!_detector.IsAvailable)
            return WeldDetectionResult.Fail("OpenCV 네이티브가 없어 검출 비활성(Windows에서만 지원).");

        var frame = Params.Mode == WeldImageMode.Ir ? _camera.LatestIr : _camera.LatestColor;
        if (frame is null)
            return WeldDetectionResult.Fail(Params.Mode == WeldImageMode.Ir
                ? "IR 프레임 없음 — 스트림/IR 활성 확인."
                : "컬러 프레임 없음 — 스트림 시작/연결 확인.");

        var weldRoi = WeldRoi ?? RoiRect.Full(frame.Width, frame.Height);

        double? peakRef = null;
        if (ReferenceMode == WeldReferenceMode.PeakLine && PeakRoi is { } pr)
            peakRef = Params.ProgressAxis == WeldProgressAxis.Horizontal
                ? pr.Y + pr.Height / 2.0
                : pr.X + pr.Width / 2.0;

        return _detector.DetectWeld(frame, weldRoi, Params, ReferenceMode, peakRef);
    }

    private string Fmt(double dpx) => MmPerPixel > 0 ? $"{dpx * MmPerPixel:0.0}mm" : $"{dpx:0.0}px";

    private (int w, int h)? FrameSize()
    {
        var f = _camera.LatestColor ?? _camera.LatestIr ?? _camera.LatestDepth;
        return f is null ? null : (f.Width, f.Height);
    }
}
