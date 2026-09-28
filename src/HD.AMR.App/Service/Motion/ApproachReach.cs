namespace HD.AMR.App.Service.Motion;

/// <summary>
/// 접근점이 "팔을 접지 않고" 닿는 자리인지 — <see cref="PostureLimits"/> 와는 다른 축의 판정이다.
///
/// <see cref="PostureLimits"/> 는 역기구학 해가 나온 <b>뒤</b>의 관절각을 보고, 이쪽은 역기구학을 부르기
/// <b>전</b>에 배치만으로 거른다. 뻗음이 모자라면 IK 는 해를 주지만 그 해가 손목 특이점(|J5|≈0)이거나
/// 손목 플립이라 뒤에서 터진다 — 현장 3회 실측이 전부 그랬다(플랜지 뻗음 230mm).
///
/// 판정은 <b>면 법선축 하나의 스칼라</b>로 한다. 수평 반경으로 보면 바닥·천장(wall_code B/T)에서
/// 반경이 작은 것이 정상인데 오탐이 난다. 법선축은 면 자세와 무관하게 성립한다.
///
/// <code>
///   플랜지 뻗음   = 면까지 법선거리(코봇 BASE 기준) − 후퇴 거리 − 공구 길이
///   필요 법선거리 = 후퇴 거리 + 공구 길이 + 최소 뻗음
/// </code>
/// </summary>
public static class ApproachReach
{
    /// <summary>
    /// 도달성 판정.
    /// </summary>
    /// <param name="normalDistanceMm">코봇 BASE 에서 검사면까지의 법선 방향 거리 [mm]
    /// (<see cref="Inspection.SeamBaseTarget.NormalDistanceMm"/>).</param>
    /// <param name="approachMm">면에서 물러나는 거리 [mm] — ③ 카메라 목표거리.</param>
    /// <param name="toolLengthMm">플랜지→TCP 병진 길이 [mm].</param>
    /// <param name="minFlangeReachMm">플랜지가 최소한 나가 있어야 하는 거리 [mm].</param>
    public static ReachCheck Check(double normalDistanceMm, double approachMm,
                                   double toolLengthMm, double minFlangeReachMm)
    {
        var flange = normalDistanceMm - approachMm - toolLengthMm;
        var required = approachMm + toolLengthMm + minFlangeReachMm;
        return new ReachCheck(flange >= minFlangeReachMm, flange, required, required - normalDistanceMm);
    }

    /// <summary>플랜지→TCP 오프셋의 병진 길이 [mm]. 공구 #0(플랜지)·미설정이면 0.</summary>
    public static double ToolLengthMm(double[]? toolCoord)
        => toolCoord is { Length: >= 3 }
            ? Math.Sqrt(toolCoord[0] * toolCoord[0] + toolCoord[1] * toolCoord[1] + toolCoord[2] * toolCoord[2])
            : 0.0;
}

/// <summary>도달성 판정 결과. 거리는 전부 mm.</summary>
/// <param name="Ok">플랜지 뻗음이 최소값 이상인가.</param>
/// <param name="FlangeReachMm">플랜지가 실제로 나가는 거리.</param>
/// <param name="RequiredNormalDistanceMm">이 구성에서 필요한 최소 면 법선거리.</param>
/// <param name="ShortfallMm">부족분 — AMR 을 벽에서 이만큼 더 떨어뜨려야 한다(음수면 여유).</param>
public sealed record ReachCheck(bool Ok, double FlangeReachMm,
                                double RequiredNormalDistanceMm, double ShortfallMm);
