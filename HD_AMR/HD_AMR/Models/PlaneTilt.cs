namespace HD_AMR.Models;

/// <summary>
/// 레이저 변위 센서 3점 측정으로 산출한 평면 기울기(cobot base 좌표계, ZYX RPY, 도).
/// 3점 거리만으로는 평면 <b>법선(2 DOF)</b> 만 결정되므로 <see cref="RzDeg"/> 는 항상 0(요각 미관측)이다.
/// </summary>
/// <param name="RxDeg">base X축 둘레 회전(도).</param>
/// <param name="RyDeg">base Y축 둘레 회전(도).</param>
/// <param name="RzDeg">base Z축 둘레 회전(도). 거리 3점으로는 구할 수 없어 항상 0.</param>
/// <param name="Normal">정규화된 평면 법선(base 좌표, +Z=센서쪽).</param>
/// <param name="Valid">3채널이 모두 유효하고 세 점이 일직선이 아니어서 계산이 성립하면 true.</param>
/// <param name="Note">Valid=false 사유 등 안내 메시지(정상이면 null).</param>
public record PlaneTilt(
    double RxDeg,
    double RyDeg,
    double RzDeg,
    double[] Normal,
    bool Valid,
    string? Note)
{
    /// <summary>계산 불가 상태(안내 메시지 포함)를 만드는 헬퍼.</summary>
    public static PlaneTilt Invalid(string note) =>
        new(0, 0, 0, new double[] { 0, 0, 1 }, false, note);
}
