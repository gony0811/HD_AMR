namespace HD_AMR.Communication.Weld;

/// <summary>
/// 용접라인 추적 설정. <c>appsettings.json</c> 의 <c>WeldTracking</c> 섹션에 매핑.
/// 1차 구현은 수동 2-shot 측정 + 픽셀 단위 기준이며, 추후 mm 캘리브레이션으로 확장한다.
/// </summary>
public class WeldTrackingSettings
{
    /// <summary>ROI 프로파일 JSON 저장 폴더(콘텐츠 루트 상대 경로).</summary>
    public string ProfileDirectory { get; set; } = "RoiProfiles";

    /// <summary>시작 시 자동 Load 할 프로파일명. 비우면 자동 로드 안 함.</summary>
    public string? AutoLoadProfile { get; set; } = "default";

    /// <summary>
    /// Peak 변위 기반 pitch 보정 사용. 로봇 위치 반복정도 오차를 영상으로 본 Peak 변위로 교정한다.
    /// 보정 pitch = 공칭 pitch + Sign × (ProgressPos2 − ProgressPos1). (연속 Peak 가정, 명세서 §7.5)
    /// </summary>
    public bool PitchCorrectionEnabled { get; set; } = true;

    /// <summary>Peak 변위 보정 부호(+1 또는 −1). 진행/장착 방향 규약에 맞춰 설정.</summary>
    public int PitchCorrectionSign { get; set; } = -1;

    // ── 스케일(mm 환산) ─────────────────────────────────────────────
    /// <summary>IR/Depth 센서 수평 화각(°). fx = (영상폭/2)/tan(HFov/2) 산출에 사용. RealSense D435 기본 90°.</summary>
    public double IrHFovDeg { get; set; } = 90.0;

    /// <summary>컬러(RGB) 센서 수평 화각(°). RealSense D435 기본 69°.</summary>
    public double ColorHFovDeg { get; set; } = 69.0;

    /// <summary>깊이 값이 없을 때 Depth 자동 스케일에 쓰는 기본 작업거리(mm).</summary>
    public double DefaultWorkDistanceMm { get; set; } = 500.0;
}
