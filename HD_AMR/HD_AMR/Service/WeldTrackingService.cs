using System.Runtime.InteropServices;
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
    public double DMm(PeakMeasurement m) => m.DPixel * EffectiveMmPerPixel(m.DepthZ);

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
        ApplyProfile(p);
        Message = $"프로파일 '{name}' 불러옴";
        return true;
    }

    private void ApplyProfile(RoiProfile p)
    {
        ProfileName = p.Name;
        ScaleCorrection = p.ScaleCorrection <= 0 ? 1.0 : p.ScaleCorrection;
        ScaleCorrectionEnabled = p.ScaleCorrectionEnabled;
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
        // Peak(진행축 위치)를 먼저 구해, 오버레이에 자홍색 Peak 선/라벨로 그릴 수 있게 한다.
        PeakInfo? peak = null;
        var depth = _camera.LatestDepth;
        if (depth is not null && PeakRoi is not null)
            peak = DepthPeakAnalyzer.Analyze(depth, PeakRoi, Params.ProgressAxis);

        var r = RunDetect(peak is { Found: true } ? peak.ProgressPos : null, $"P{id}");
        LastDetect = r;
        if (!r.Success)
        {
            Message = $"Peak #{id} 캡처 실패: {r.Message}";
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
        };
        if (id == 1) { M1 = m; State = WeldTrackingState.Peak1Captured; }
        else { M2 = m; State = WeldTrackingState.Peak2Captured; }
        Message = $"Peak #{id} 캡처: d={DMm(m):0.0}mm ({r.DPixel:0.0}px)"
            + (peak is { Found: true } ? $", peak@{peak.ProgressPos:0}px / {peak.DepthValue}mm" : "");

        // Pitch(mm)·두 측정이 있고 스케일이 사용 가능할 때만 자동 각도 산출.
        if (M1 is not null && M2 is not null && Pitch > 0 && ScaleAvailable) ComputeAngle();
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
        double mmpp1 = EffectiveMmPerPixel(M1.DepthZ);
        double mmpp2 = EffectiveMmPerPixel(M2.DepthZ);
        double d1 = M1.DPixel * mmpp1;   // mm
        double d2 = M2.DPixel * mmpp2;   // mm

        double nominal = Pitch;          // mm (로봇 파라미터)
        double effPitch = nominal;
        double peakShift = 0;            // mm
        bool corrected = false;

        // Peak 변위 보정: 보정 Pitch = Pitch + Sign × ΔPeak. ΔPeak 는 두 측정 평균 스케일로 mm 환산.
        if (PitchCorrectionEnabled && M1.Peak is { Found: true } p1 && M2.Peak is { Found: true } p2)
        {
            double dppPx = p2.ProgressPos - p1.ProgressPos;
            peakShift = dppPx * ((mmpp1 + mmpp2) / 2.0);   // mm
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
    private WeldDetectionResult RunDetect(double? peakProgressPos = null, string? peakLabel = null)
    {
        if (!_detector.IsAvailable)
            return WeldDetectionResult.Fail("OpenCV 네이티브가 없어 검출 비활성(Windows에서만 지원).");

        var frame = Params.Mode == WeldImageMode.Ir ? _camera.LatestIr : _camera.LatestColor;
        if (frame is null)
            return WeldDetectionResult.Fail(Params.Mode == WeldImageMode.Ir
                ? "IR 프레임 없음 — 스트림/IR 활성 확인."
                : "컬러 프레임 없음 — 스트림 시작/연결 확인.");

        var weldRoi = WeldRoi ?? RoiRect.Full(frame.Width, frame.Height);

        // d 기준선은 FOV(전체 화면) 센터선으로 고정.
        return _detector.DetectWeld(frame, weldRoi, Params, WeldReferenceMode.FovCenter, null, peakProgressPos, peakLabel);
    }

    private static string Fmt(double dpx) => $"{dpx:0.0}px";   // 1회 검출(튜닝)은 깊이 컨텍스트가 없어 px 로 표시

    private (int w, int h)? FrameSize()
    {
        var f = _camera.LatestColor ?? _camera.LatestIr ?? _camera.LatestDepth;
        return f is null ? null : (f.Width, f.Height);
    }

    // ── 스케일(mm 환산) ─────────────────────────────────────────────
    /// <summary>Depth 자동 mm/px = Z/fx (기본). 2점 보정계수가 켜져 있으면 곱해 보강한다.</summary>
    private double EffectiveMmPerPixel(double depthZ)
    {
        double fx = Fx();
        if (fx <= 0) return 0;
        double z = depthZ > 0 ? depthZ : _settings.DefaultWorkDistanceMm;
        double mmpp = z / fx;                                          // Depth 자동 기본
        return ScaleCorrectionEnabled ? mmpp * ScaleCorrection : mmpp; // 2점 보정 보강
    }

    /// <summary>현재 검출 모드의 fx(px) = (영상폭/2)/tan(HFov/2). FOV·해상도 기반(간이 intrinsic).</summary>
    private double Fx()
    {
        bool ir = Params.Mode == WeldImageMode.Ir;
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
