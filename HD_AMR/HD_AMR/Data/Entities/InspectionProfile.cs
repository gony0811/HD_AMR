namespace HD_AMR.Data.Entities;

/// <summary>
/// 한 도면에 대해 사용자가 Inspection 페이지에서 조정한 설정 한 세트(이름붙여 저장).
/// 솎기/코봇 파라미터와 경유점(수동 편집 포함)을 묶어 보관해, 나중에 그대로 복원한다.
/// </summary>
public class InspectionProfile
{
    public int Id { get; set; }
    public int DrawingId { get; set; }
    public string Name { get; set; } = "";

    // 솎기 파라미터
    public double SpacingMm { get; set; }
    public double CorrugThresholdDeg { get; set; }
    public double CorrugStepDeg { get; set; }

    // 코봇 실행 파라미터
    public int RunTool { get; set; }
    public int RunUser { get; set; }
    public int RunVel { get; set; }
    public double DelaySec { get; set; }
    public double ThMax { get; set; }
    public double SettleDelaySec { get; set; }   // 이동 후 진동 흡수 대기(초)
    public bool MoveHomeFirst { get; set; }

    /// <summary>경유점 목록(<see cref="InspectionWaypoint"/>)을 JSON 직렬화한 문자열.</summary>
    public string WaypointsJson { get; set; } = "[]";

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Drawing? Drawing { get; set; }
}

/// <summary>
/// 저장용 경유점 한 점. X=로봇 x, Z=로봇 z(=DXF Y), Theta=적용 θ(도),
/// ThetaManual=θ가 자동 계산값이 아니라 수동 입력값인지 여부.
/// </summary>
public record InspectionWaypoint(double X, double Z, double Theta, bool ThetaManual);
