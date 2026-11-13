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
    /// <param name="approachMm">면에서 물러나는 거리 [mm] — ② 거친 접근 거리(inspection.approachStandoffMm).
    /// 이후 ③·④⁺ 가 툴을 더 들여보내므로 ②가 가장 접힌 자세이고, 판정은 여기서 한다.</param>
    /// <param name="toolLengthMm">플랜지→TCP 병진 길이 [mm].</param>
    /// <param name="minFlangeReachMm">플랜지가 최소한 나가 있어야 하는 거리 [mm].</param>
    public static ReachCheck Check(double normalDistanceMm, double approachMm,
                                   double toolLengthMm, double minFlangeReachMm)
    {
        var flange = normalDistanceMm - approachMm - toolLengthMm;
        var required = approachMm + toolLengthMm + minFlangeReachMm;
        return new ReachCheck(flange >= minFlangeReachMm, flange, required, required - normalDistanceMm);
    }

    /// <summary>
    /// 이 면에 최소 플랜지 뻗음 규칙을 적용하는가 — 천장(T)만 뺀다(2026-11-13).
    ///
    /// 근거가 된 실측(230mm 손목 특이점 3회, 2026-09-28)은 <b>수직벽</b>이었다. 천장은 툴이 위를 향해 ② 자세의
    /// J5 가 90° 근처에 머물러 접힌 팔에서도 손목 특이점이 생기지 않는다(ACS 리치 시뮬레이터 — 리프트를 올려
    /// 뻗음 143mm 까지 |J5| 90°·자세 여유 유지, 그보다 가까우면 상완–리프트 기둥 간섭·가지 전환이 따로 막는다).
    /// 이 규칙이 천장에서는 리프트를 올린 정차만 막아(2026-11-11 리프트 100·200 정차 52건 거부) 먼 용접선의
    /// 팔 뻗음 실패를 풀 수단을 없앴다. 경사면·바닥은 유지(경사면은 단독으로 막는 경우 없음, 바닥은 걸리지 않음).
    /// ACS <c>ReachSimulator.FlangeRuleApplies</c> 와 같은 규칙 — 바꾸면 함께 맞출 것.
    /// </summary>
    public static bool AppliesTo(string? wallCode)
        => !string.Equals(wallCode?.Trim(), "T", StringComparison.OrdinalIgnoreCase);

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
