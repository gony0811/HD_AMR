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
    /// 픽셀↔mm 환산계수(mm per pixel). 0이면 픽셀 단위로만 표시. 값이 있으면 d/theta 를 mm 기반으로
    /// 환산해 표시한다(간이 — 정식 캘리브레이션 전 임시 브리지).
    /// </summary>
    public double MmPerPixel { get; set; }

    /// <summary>
    /// Peak 변위 기반 pitch 보정 사용. 로봇 위치 반복정도 오차를 영상으로 본 Peak 변위로 교정한다.
    /// 보정 pitch = 공칭 pitch + Sign × (ProgressPos2 − ProgressPos1). (연속 Peak 가정, 명세서 §7.5)
    /// </summary>
    public bool PitchCorrectionEnabled { get; set; } = true;

    /// <summary>Peak 변위 보정 부호(+1 또는 −1). 진행/장착 방향 규약에 맞춰 설정.</summary>
    public int PitchCorrectionSign { get; set; } = -1;
}
