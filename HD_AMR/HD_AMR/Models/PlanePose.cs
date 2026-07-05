namespace HD_AMR.Models;

/// <summary>
/// 레이저 변위 센서 3점 측정으로 산출한 삼각형 평면 중심의 pose(로봇 <b>툴 좌표계</b>, ZYX RPY).
/// 위치 <see cref="X"/>/<see cref="Y"/>/<see cref="Z"/> 는 mm, 회전 <see cref="Rx"/>/<see cref="Ry"/>/
/// <see cref="Rz"/> 는 도(°). 헤드가 툴에 강체 고정되므로 로봇 자세(FK) 없이 계산된다.
/// </summary>
/// <param name="X">중심 X(mm, 툴).</param>
/// <param name="Y">중심 Y(mm, 툴).</param>
/// <param name="Z">중심 Z(mm, 툴) = standoff + 변위평균.</param>
/// <param name="Rx">툴 X축 둘레 회전(도).</param>
/// <param name="Ry">툴 Y축 둘레 회전(도).</param>
/// <param name="Rz">툴 Z축 둘레 회전(도). 삼각형 X축(Ch1→Ch3)으로 결정됨.</param>
/// <param name="Normal">정규화된 평면 법선(툴 좌표).</param>
/// <param name="Valid">3채널이 모두 유효하고 세 점이 일직선이 아니면 true.</param>
/// <param name="Note">Valid=false 사유(정상이면 null).</param>
public record PlanePose(
    double X,
    double Y,
    double Z,
    double Rx,
    double Ry,
    double Rz,
    double[] Normal,
    bool Valid,
    string? Note)
{
    /// <summary>계산 불가 상태(안내 메시지 포함) 헬퍼.</summary>
    public static PlanePose Invalid(string note) =>
        new(0, 0, 0, 0, 0, 0, new double[] { 0, 0, 1 }, false, note);
}
