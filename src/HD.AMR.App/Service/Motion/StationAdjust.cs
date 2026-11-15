using System.Globalization;

namespace HD.AMR.App.Service.Motion;

/// <summary>
/// ACS 가 정차마다 허락한 온보드 보정 범위 — `params.stationAdjust`(사양 §4.4.2, N23 초안).
/// 프레임: 전진 = 노드 theta(벽 정면) 방향, 횡 = 그 좌측. 기준은 언제나 원래 nodePosition 이다.
/// </summary>
public sealed record StationAdjustLimits(double MaxForwardMm, double MaxBackwardMm, double MaxLateralMm, double MaxThetaDeg);

/// <summary>
/// 정차 1곳의 용접선 1구간 리치 표본. 둘 다 <b>원래 정차점</b>과 그로부터 시험 후진한 자리에서
/// <see cref="Inspection.SeamBaseTransform"/> 으로 계산한 면 법선거리다(코봇 BASE 기준, mm).
/// </summary>
/// <param name="Label">로그·보고용 식별자(jobRef).</param>
/// <param name="NormalAtNodeMm">원래 정차점에서의 면 법선거리.</param>
/// <param name="NormalAtProbeMm">정차점에서 <see cref="StationAdjustPlanner.ProbeMm"/> 후진한 자리의 면 법선거리.</param>
/// <param name="RequiredMm">필요 법선거리(<see cref="ReachCheck.RequiredNormalDistanceMm"/>).</param>
public sealed record StationAdjustSeam(string Label, double NormalAtNodeMm, double NormalAtProbeMm, double RequiredMm);

/// <summary>
/// ② 리치 부족 수치 — `stationReachShortfall` 보고용(사양 §6.4·§4.4.2, N23 A안). 거리는 mm, 현재 정차 기준.
/// </summary>
/// <param name="NormalMm">현재 정차에서 코봇 BASE→면 법선거리.</param>
/// <param name="RequiredMm">필요 법선거리(② 후퇴 + 공구 + 최소 뻗음).</param>
/// <param name="ExtraBackwardMm">현재 정차에서 더 물러나야 할 거리(여유 포함, 10 mm 올림). −1 = 수평 후진으로 고칠 수 없는 면(바닥·천장).</param>
public sealed record ReachShortfall(double NormalMm, double RequiredMm, double ExtraBackwardMm)
{
    /// <summary>`errorDescription` 의 기계 판독부(공백 구분 key=value).</summary>
    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"normalMm={NormalMm:0} requiredMm={RequiredMm:0} extraBackwardMm={ExtraBackwardMm:0}");
}

/// <summary>정차 보정 계획. <see cref="BackwardMm"/> 0 이면 옮기지 않는다.</summary>
/// <param name="BackwardMm">원래 정차점에서 노드 theta 반대로 물러날 거리 [mm].</param>
/// <param name="Unfixable">허락 범위로는 리치를 만들 수 없는 구간 — 사유 포함. 이 구간은 ②가 종전대로 실패한다.</param>
/// <param name="Summary">운영 로그용 한 줄.</param>
public sealed record StationAdjustPlan(double BackwardMm, IReadOnlyList<string> Unfixable, string Summary);

/// <summary>
/// 정차 온보드 보정 계획(1단계 — 후진만). 현장 실패(2026-09-28)는 전부 <b>법선 거리 부족</b>이었으므로
/// 필요 법선거리에 모자라는 구간이 있으면 정차를 벽에서 물러나게 한다.
///
/// 면 법선거리는 AMR 평면 이동에 선형이라(코봇 BASE 가 차체에 고정) 정차점과 시험 후진점 두 표본의 기울기로
/// 필요 후진량을 정확히 구한다 — 챔퍼·마구리처럼 법선이 수평이 아닌 면도 같은 식으로 다뤄진다.
/// 바닥·천장처럼 수평 이동이 법선거리를 바꾸지 않는 면은 기울기가 0이라 보정 불가로 분류된다.
///
/// 전진(벽 쪽)·횡·각 보정은 아직 쓰지 않는다 — 최대 리치 판정 모델이 없어 전진의 필요를 판정할 수 없고,
/// 횡 오프셋은 실패 원인이 아니었다. 허락 범위로는 받되 계획은 0 으로 둔다.
/// </summary>
public static class StationAdjustPlanner
{
    /// <summary>기울기 측정용 시험 후진 거리 [mm].</summary>
    public const double ProbeMm = 100.0;

    /// <summary>필요 법선거리에 더하는 여유 [mm] — 실제 정차가 원래 정차점에서 허용 오차만큼 벗어나는 몫.</summary>
    public const double MarginMm = 20.0;

    /// <summary>후진량을 이 단위로 올림 [mm].</summary>
    public const double StepMm = 10.0;

    /// <summary>1 mm 후진당 법선거리 증가가 이보다 작으면 수평 이동으로 고칠 수 없는 면으로 본다.</summary>
    public const double MinSlope = 0.05;

    public static StationAdjustPlan Plan(StationAdjustLimits limits, IReadOnlyList<StationAdjustSeam> seams)
    {
        var unfixable = new List<string>();
        double backward = 0;
        int shortCount = 0;

        foreach (var s in seams)
        {
            var shortfall = s.RequiredMm + MarginMm - s.NormalAtNodeMm;
            if (shortfall <= 0) continue;
            shortCount++;

            if (NeededBackwardMm(s) is not { } need)
            {
                var slope = (s.NormalAtProbeMm - s.NormalAtNodeMm) / ProbeMm;
                unfixable.Add($"{s.Label}: 법선거리 {shortfall:0}mm 부족 — 이 면은 수평 후진으로 법선거리가 늘지 않음(기울기 {slope:0.00})");
                continue;
            }

            if (need > limits.MaxBackwardMm)
            {
                unfixable.Add($"{s.Label}: 후진 {need:0}mm 필요 — 허락 {limits.MaxBackwardMm:0}mm 초과");
                continue;
            }
            backward = Math.Max(backward, need);
        }

        var summary = shortCount == 0
            ? $"용접선 {seams.Count}구간 모두 리치 충분 — 정차 보정 없음"
            : backward > 0
                ? $"용접선 {seams.Count}구간 중 {shortCount}구간 법선거리 부족 → 후진 {backward:0}mm" +
                  (unfixable.Count > 0 ? $" (보정 불가 {unfixable.Count}구간)" : "")
                : $"용접선 {seams.Count}구간 중 {shortCount}구간 법선거리 부족 — 허락 범위로 보정 불가, 원래 정차점 유지";
        return new StationAdjustPlan(backward, unfixable, summary);
    }

    /// <summary>
    /// 표본 1건이 필요 법선거리(+여유)에 닿으려면 표본 기준점에서 물러나야 할 거리 [mm, 10 mm 올림].
    /// 이미 충분하면 0, 수평 후진으로 법선거리가 늘지 않는 면이면 null.
    /// </summary>
    public static double? NeededBackwardMm(StationAdjustSeam s)
    {
        var shortfall = s.RequiredMm + MarginMm - s.NormalAtNodeMm;
        if (shortfall <= 0) return 0;
        var slope = (s.NormalAtProbeMm - s.NormalAtNodeMm) / ProbeMm;
        if (slope < MinSlope) return null;
        return Math.Ceiling(shortfall / slope / StepMm) * StepMm;
    }

    /// <summary>원래 정차점에서 theta 반대로 <paramref name="backwardMm"/> 물러난 맵 좌표 (m).</summary>
    public static (double X, double Y) BackedOff(double nodeXm, double nodeYm, double thetaRad, double backwardMm)
        => (nodeXm - backwardMm / 1000.0 * Math.Cos(thetaRad), nodeYm - backwardMm / 1000.0 * Math.Sin(thetaRad));

    /// <summary>
    /// 보정 결과 보고 토큰(사양 §4.4.2 규칙 6) — 보정한 정차의 첫 액션 resultDescription 앞머리에 붙인다.
    /// dF 전진(+)/후진(−) mm, dL 좌(+)/우(−) mm, dTh deg(CCW +).
    /// </summary>
    public static string Token(double forwardMm, double lateralMm, double thetaDeg)
        => string.Create(CultureInfo.InvariantCulture,
            $"stationAdjusted dF={forwardMm:+0;-0;0} dL={lateralMm:+0;-0;0} dTh={thetaDeg:+0.0;-0.0;0.0};");
}
