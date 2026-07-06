namespace HD_AMR.Models;

/// <summary>이미지 픽셀 좌표계의 사각형 ROI. 원점=좌상단, x→오른쪽, y→아래.</summary>
public sealed record RoiRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>프레임 크기 안으로 잘라 맞춘 ROI. 완전히 벗어나면 null.</summary>
    public RoiRect? ClampTo(int frameW, int frameH)
    {
        int x = Math.Clamp(X, 0, Math.Max(0, frameW - 1));
        int y = Math.Clamp(Y, 0, Math.Max(0, frameH - 1));
        int w = Math.Clamp(Width, 0, frameW - x);
        int h = Math.Clamp(Height, 0, frameH - y);
        if (w <= 0 || h <= 0) return null;
        return new RoiRect(x, y, w, h);
    }

    public static RoiRect Full(int w, int h) => new(0, 0, w, h);
}

/// <summary>
/// 깊이 ROI 영역 통계. 무효(0) 픽셀은 제외하고 계산. <see cref="ValidCount"/>==0 이면
/// 영역 전체가 무효라 <see cref="MinMm"/>/<see cref="MaxMm"/>/<see cref="AvgMm"/> 는 0.
/// </summary>
public sealed record DepthRoiStats(
    int MinMm, int MaxMm, double AvgMm, int ValidCount, int TotalCount, double ValidRatio,
    double MinU, double MinV);   // 최소 픽셀의 정규화 좌표(전체 프레임 기준 0~1)

/// <summary>학습 데이터 캡처 결과. 저장 폴더·타임스탬프 접두사·생성된 파일 경로 목록.</summary>
public sealed record CaptureResult(string Dir, string Timestamp, IReadOnlyList<string> Files);

/// <summary>검출 입력 이미지 선택.</summary>
public enum WeldImageMode { RgbGrayscale, RgbHsv, Ir }

/// <summary>용접 진행 방향. Horizontal=비드가 좌우로 흐름(경계는 상/하), Vertical=상하로 흐름(경계는 좌/우).</summary>
public enum WeldProgressAxis { Horizontal, Vertical }

/// <summary>이진화/엣지 방식.</summary>
public enum WeldThresholdMethod { Otsu, Adaptive, Canny }

/// <summary>위치 오차 d 의 기준선.</summary>
public enum WeldReferenceMode { FovCenter, PeakLine }

/// <summary>비드 검출 방식. Param=고전 CV 파라미터 이진화, Dl=학습된 YOLOv8-seg 모델 추론.</summary>
public enum WeldDetectionMethod { Param, Dl }

/// <summary>용접라인 검출 파라미터(명세서 8장). 1차 구현에 필요한 핵심 튜너블만.</summary>
public sealed class WeldDetectionParams
{
    public WeldImageMode Mode { get; set; } = WeldImageMode.Ir;
    public WeldProgressAxis ProgressAxis { get; set; } = WeldProgressAxis.Horizontal;
    public WeldThresholdMethod Threshold { get; set; } = WeldThresholdMethod.Otsu;

    /// <summary>CLAHE 대비강화 사용.</summary>
    public bool UseClahe { get; set; } = true;
    public double ClaheClip { get; set; } = 2.0;

    /// <summary>가우시안 블러 커널(홀수, 0=끔).</summary>
    public int BlurKernel { get; set; } = 3;

    /// <summary>비드가 배경보다 어두우면 true(이진화 반전).</summary>
    public bool Invert { get; set; }

    // Adaptive threshold
    public int AdaptiveBlockSize { get; set; } = 35;
    public double AdaptiveC { get; set; } = 5;

    // Canny
    public int CannyLow { get; set; } = 50;
    public int CannyHigh { get; set; } = 150;

    /// <summary>morphology close/open 커널(홀수, 0=끔).</summary>
    public int MorphKernel { get; set; } = 5;

    public int MinBlobArea { get; set; } = 80;
    public int MaxBlobArea { get; set; } = 500000;

    /// <summary>centerline 이동평균 윈도(0/1=끔).</summary>
    public int SmoothingWindow { get; set; } = 5;

    // ── DL 검출(YOLOv8-seg) 전용 ─────────────────────────────────────────────
    // 아래 값들은 WeldDetectionMethod.Dl 에서만 사용된다(고전 CV 경로는 무시).
    /// <summary>DL 검출 confidence 임계값(이 값 미만 후보 제거).</summary>
    public double DlConfidence { get; set; } = 0.25;

    /// <summary>DL 마스크 확률 임계값(proto 마스크 이진화 기준).</summary>
    public double DlMaskThreshold { get; set; } = 0.50;

    /// <summary>사용할 .onnx 경로. null 이면 모달리티(Mode)로 <c>weld_seg_{ir|rgb}.onnx</c> 자동 해석.</summary>
    public string? DlModelPath { get; set; }
}

/// <summary>한 점(이미지 픽셀 좌표).</summary>
public sealed record PixelPoint(double X, double Y);

/// <summary>용접라인 검출 결과(한 프레임).</summary>
public sealed class WeldDetectionResult
{
    public bool Success { get; init; }
    public string? Message { get; init; }
    public double Confidence { get; init; }

    /// <summary>비드 중심선 점들(전체 이미지 좌표). 진행 방향을 따라 정렬.</summary>
    public IReadOnlyList<PixelPoint> Centerline { get; init; } = Array.Empty<PixelPoint>();

    /// <summary>기준선 위치(픽셀, cross-axis 좌표). FovCenter면 ROI 중앙, PeakLine이면 Peak 기준.</summary>
    public double ReferencePos { get; init; }

    /// <summary>타깃(FOV 중심) 진행좌표에서의 비드 중심선 cross-axis 위치(픽셀).</summary>
    public double WeldCenterAtTarget { get; init; }

    /// <summary>위치 오차 d (픽셀) = WeldCenterAtTarget − ReferencePos.</summary>
    public double DPixel { get; init; }

    /// <summary>타깃 지점의 비드 중심점(전체 이미지 픽셀). 깊이 샘플링·스케일 환산용.</summary>
    public PixelPoint? WeldPoint { get; init; }

    /// <summary>타깃 지점의 기준선 점(전체 이미지 픽셀).</summary>
    public PixelPoint? RefPoint { get; init; }

    /// <summary>주석(overlay) JPEG. UI 표시용. 없으면 null.</summary>
    public byte[]? OverlayJpeg { get; init; }

    /// <summary>진행축 슬라이스별 비드 cross 구간(전체 이미지 좌표). DL 라벨 초안 마스크를 굽는 데 쓴다.</summary>
    public IReadOnlyList<BeadSpan>? BeadSpans { get; init; }

    public static WeldDetectionResult Fail(string msg) => new() { Success = false, Message = msg };
}

/// <summary>비드 한 슬라이스의 cross 구간. Progress=진행축 좌표, [CrossStart,CrossEnd]=비드 폭(모두 전체 이미지 픽셀).</summary>
public readonly record struct BeadSpan(int Progress, int CrossStart, int CrossEnd);

/// <summary>Depth 기반 Peak 정보(명세서 7장). 1차에서는 진행축 상의 위치/깊이만.</summary>
public sealed class PeakInfo
{
    public bool Found { get; init; }
    public double ProgressPos { get; init; }   // 진행축 좌표(픽셀)
    public int DepthValue { get; init; }        // mm
    public double Confidence { get; init; }

    // Peak 슬라이스에서 depth-최소부(비드)의 cross 축 구간. 급격한 depth 점프/무효 픽셀에서 끊긴다.
    // 자홍 Peak 선을 이 구간만큼만 그리는 데 쓴다. ProgressPos 와 같은 프레임 좌표(IR=깊이, RGB=재투영).
    public bool HasCrossSpan { get; init; }
    public double CrossStart { get; init; }
    public double CrossEnd { get; init; }
}

/// <summary>Peak #1/#2 한 곳의 측정 스냅샷(수동 트리거 결과).</summary>
public sealed class PeakMeasurement
{
    public int PeakId { get; init; }            // 1 또는 2
    public double DPixel { get; init; }
    public double Confidence { get; init; }
    public PeakInfo? Peak { get; init; }
    public DateTime At { get; init; }
    public byte[]? OverlayJpeg { get; init; }

    /// <summary>비드 중심점 위치의 깊이(mm, 0=무효). Depth 자동 스케일(mm/px=Z/fx) 환산용.</summary>
    public double DepthZ { get; init; }
}

/// <summary>스케일 모드. AutoDepth=깊이로 mm/px 자동, Fixed=고정 mm/px(2점 보정/수동).</summary>
public enum WeldScaleMode { AutoDepth, Fixed }

/// <summary>스케일 검증 결과 — 아는 길이를 보정 전(Depth 자동)/후(현재 스케일)로 측정해 비교.</summary>
public sealed class ScaleValidationResult
{
    public double TrueMm { get; init; }
    public double PixelDist { get; init; }
    public double AutoMm { get; init; }       // Depth 자동 스케일로 측정
    public double FixedMm { get; init; }      // 현재 고정 스케일(2점/수동)로 측정
    public bool FixedAvailable { get; init; } // 고정 스케일이 설정돼 있는지
    public double AutoErr => AutoMm - TrueMm;
    public double FixedErr => FixedMm - TrueMm;
    public double AutoErrPct => TrueMm != 0 ? AutoErr / TrueMm * 100 : 0;
    public double FixedErrPct => TrueMm != 0 ? FixedErr / TrueMm * 100 : 0;
}

/// <summary>d1,d2,pitch 로 산출한 각도(명세서 11장).</summary>
public sealed class AngleResult
{
    public double D1 { get; init; }
    public double D2 { get; init; }
    /// <summary>각도 계산에 실제 사용된 pitch(보정이 적용됐다면 보정값).</summary>
    public double Pitch { get; init; }
    /// <summary>운영자가 입력한 공칭 pitch.</summary>
    public double NominalPitch { get; init; }
    /// <summary>Peak 변위 ΔPeak = (ProgressPos2 − ProgressPos1) 를 pitch 단위로 환산한 값(부호 적용 전).</summary>
    public double PeakShift { get; init; }
    /// <summary>Peak 변위 보정이 적용됐는지.</summary>
    public bool Corrected { get; init; }
    public double ThetaRad { get; init; }
    public double ThetaDeg { get; init; }
    public string Unit { get; init; } = "px";   // 현재는 픽셀 기준
}

/// <summary>추적 상태 머신(명세서 12장)의 경량 버전 — 수동 2-shot 흐름.</summary>
public enum WeldTrackingState
{
    Idle,
    WeldDetected,      // 1회 검출 성공
    Peak1Captured,
    Peak2Captured,
    ThetaComputed,
    DetectUnavailable, // 검출기 미가용(네이티브 없음 등)
}
