using HD.AMR.App.Communication;

namespace HD.AMR.App.Service.Motion;

/// <summary>
/// FAIRINO FR 계열(UR 형 6축) 표준 DH 기구학 — 컨트롤러 없이 순·역기구학을 계산한다(오프라인 리치 맵용).
///
/// 관절 규약은 UR 과 같다고 <b>가정</b>한다: 전 관절 0 = 팔이 수평으로 펴진 자세, J3 = 0 팔꿈치 특이점,
/// J5 = 0 손목 특이점(<see cref="PostureLimits"/> 와 같은 전제). 링크 치수는 STEP 실측으로 검증했지만
/// 관절 0점·부호는 컨트롤러 규약이라 도면으로 확인할 수 없다 — 실물에서 <c>GetForwardKin</c> 한 번으로
/// <see cref="Forward"/> 와 대조할 것.
/// </summary>
/// <param name="D1">BASE 장착면 → J2 축 높이 [mm].</param>
/// <param name="A2">상완 길이(J2→J3, UR 부호로 음수) [mm].</param>
/// <param name="A3">전완 길이(J3→J4, 음수) [mm].</param>
/// <param name="D4">J2·J3·J4 평행축 방향의 순 가로 오프셋 [mm].</param>
/// <param name="D5">J4 → J5 [mm].</param>
/// <param name="D6">J5 → 플랜지 면 [mm].</param>
public sealed record Fr5Kinematics(double D1, double A2, double A3, double D4, double D5, double D6)
{
    private const double Deg2Rad = Math.PI / 180.0;
    private const double Rad2Deg = 180.0 / Math.PI;
    private static readonly double[] Alpha = { Math.PI / 2, 0, 0, Math.PI / 2, -Math.PI / 2, 0 };

    /// <summary>
    /// FR5-WML — 장비 STEP(E26003-E-A000-000-0, 2026-10-28)의 관절 축선에서 실측했다.
    /// d4 = 어깨 가로 185 − 팔꿈치 가로 140 + 손목 114. STEP 자세를 역으로 풀어 J2~J5 축을 0.02mm·0.01° 로 재현.
    /// </summary>
    public static Fr5Kinematics Fr5Wml { get; } = new(180, -970, -816, 159, 114, 106);

    /// <summary>
    /// 검사 툴 TCP — 플랜지 기준 [x,y,z,rx,ry,rz]. 렌즈 광축이 J6 축과 동축(STEP 실측 편심 0)이고
    /// z 375mm 는 플랜지 면 → 초점 거리(설계값, 도면으로 측정 불가).
    /// </summary>
    public static double[] InspectionTool { get; } = { 0, 0, 375, 0, 0, 0 };

    /// <summary>
    /// STEP 상 장착 — 코봇 BASE 가 차체 중심 위 1635mm(텔레스코픽 0), 축은 차체와 평행하다고 둔다.
    /// BASE x 방향(J1 0점)은 도면에 없어 0 으로 둔다 — 장착 보정(T_A_B)이 있으면 그 값을 쓸 것.
    /// </summary>
    public static double[] StepMount { get; } = { 0, 0, 1635, 0, 0, 0 };

    private double[] A => new[] { 0, A2, A3, 0, 0, 0 };
    private double[] D => new[] { D1, 0, 0, D4, D5, D6 };

    private double[,] Link(int i, double theta)
    {
        double ca = Math.Cos(Alpha[i]), sa = Math.Sin(Alpha[i]), ct = Math.Cos(theta), st = Math.Sin(theta);
        double a = A[i], d = D[i];
        return new double[,]
        {
            { ct, -st * ca, st * sa, a * ct },
            { st, ct * ca, -ct * sa, a * st },
            { 0, sa, ca, d },
            { 0, 0, 0, 1 },
        };
    }

    /// <summary>순기구학 — 관절각[도] → BASE 기준 플랜지 동차변환(mm).</summary>
    public double[,] ForwardFlange(double[] jointsDeg)
    {
        var m = Link(0, jointsDeg[0] * Deg2Rad);
        for (var i = 1; i < 6; i++) m = PoseMath.Multiply(m, Link(i, jointsDeg[i] * Deg2Rad));
        return m;
    }

    /// <summary>순기구학 — 관절각[도] → BASE 기준 TCP pose [x,y,z,rx,ry,rz](mm·도, <see cref="PoseMath"/> 규약).</summary>
    public double[] Forward(double[] jointsDeg, double[]? toolOffset = null)
    {
        var m = ForwardFlange(jointsDeg);
        if (toolOffset is not null) m = PoseMath.Multiply(m, PoseMath.FromPose(toolOffset));
        return PoseMath.ToPose(m);
    }

    /// <summary>
    /// 역기구학 — BASE 기준 TCP pose → 해 전부(최대 8개, 어깨·팔꿈치·손목 가지)[도, −180~180].
    /// 도달 불가면 빈 목록. 손목 특이점(J5 = 0)에서는 J6 을 0 으로 둔다.
    /// </summary>
    public IReadOnlyList<double[]> InverseAll(double[] tcpPose, double[]? toolOffset = null)
    {
        var t = PoseMath.FromPose(tcpPose);
        if (toolOffset is not null) t = PoseMath.Multiply(t, PoseMath.Inverse(PoseMath.FromPose(toolOffset)));
        return InverseFlange(t);
    }

    /// <summary>역기구학 — BASE 기준 플랜지 동차변환 → 해 전부[도].</summary>
    public IReadOnlyList<double[]> InverseFlange(double[,] t)
    {
        var sols = new List<double[]>(8);
        // 손목 중심(J5 축 위) = 플랜지에서 z6 반대로 d6.
        var p5x = t[0, 3] - D6 * t[0, 2];
        var p5y = t[1, 3] - D6 * t[1, 2];
        var r = Math.Sqrt(p5x * p5x + p5y * p5y);
        if (r < Math.Abs(D4)) return sols;   // 손목 중심이 J1 축 가로 오프셋 원통 안 — 해 없음
        var inv = PoseMath.Inverse(t);

        foreach (var s1 in new[] { 1.0, -1.0 })
        {
            var q1 = Math.Atan2(p5y, p5x) + s1 * Math.Acos(D4 / r) + Math.PI / 2;
            double s1v = Math.Sin(q1), c1v = Math.Cos(q1);
            var c5 = (t[0, 3] * s1v - t[1, 3] * c1v - D4) / D6;
            if (Math.Abs(c5) > 1 + 1e-12) continue;
            c5 = Math.Clamp(c5, -1, 1);

            foreach (var s5 in new[] { 1.0, -1.0 })
            {
                var q5 = s5 * Math.Acos(c5);
                var sin5 = Math.Sin(q5);
                var q6 = Math.Abs(sin5) < 1e-9
                    ? 0.0
                    : Math.Atan2((-inv[1, 0] * s1v + inv[1, 1] * c1v) / sin5, (inv[0, 0] * s1v - inv[0, 1] * c1v) / sin5);

                // T14 = T01⁻¹ · T06 · (T45·T56)⁻¹ — 평면 2링크(J2·J3)만 남는다.
                var t14 = PoseMath.Multiply(PoseMath.Multiply(PoseMath.Inverse(Link(0, q1)), t),
                    PoseMath.Inverse(PoseMath.Multiply(Link(4, q5), Link(5, q6))));
                double px = t14[0, 3], py = t14[1, 3];
                var c3 = (px * px + py * py - A2 * A2 - A3 * A3) / (2 * A2 * A3);
                if (Math.Abs(c3) > 1 + 1e-12) continue;
                c3 = Math.Clamp(c3, -1, 1);

                foreach (var s3 in new[] { 1.0, -1.0 })
                {
                    var q3 = s3 * Math.Acos(c3);
                    var q2 = -Math.Atan2(py, -px)
                             + Math.Asin(Math.Clamp(A3 * Math.Sin(q3) / Math.Sqrt(px * px + py * py), -1, 1));
                    var t34 = PoseMath.Multiply(PoseMath.Inverse(PoseMath.Multiply(Link(1, q2), Link(2, q3))), t14);
                    var q4 = Math.Atan2(t34[1, 0], t34[0, 0]);
                    sols.Add(new[] { q1, q2, q3, q4, q5, q6 }.Select(q => Wrap(q * Rad2Deg)).ToArray());
                }
            }
        }
        return sols;
    }

    /// <summary>
    /// 자세 허용 범위 기준 여유가 가장 큰 해 하나. 해가 없을 때만 null — 전부 범위 밖이면 그중 덜 나쁜 해를
    /// 돌려줘 <see cref="ReachMap"/> 이 'IK 실패' 가 아닌 '자세한계' 로 사유를 남기게 한다.
    /// J6 은 운영이 이동 직전에 절대각으로 따로 맞추므로 판정에서 뺀다(<see cref="ReachMap"/> 와 같은 규칙).
    /// </summary>
    /// <param name="seed">주면 허용 범위 안의 해 중 이 관절각(J1~J5)에 가장 가까운 해를 고른다 — 연속 이동처럼
    /// 같은 가지를 유지해, ②→최종처럼 가까운 두 자세가 서로 다른 가지로 갈라져 경로가 휘두르는 것을 막는다.
    /// 범위 안의 해가 없으면 seed 없이 여유 최대 해로 돌아간다.</param>
    public double[]? InverseBest(double[] tcpPose, double[]? toolOffset, PostureLimits limits, double[]? seed = null)
    {
        double[]? best = null, nearest = null;
        double bestMargin = double.NegativeInfinity, nearestDist = double.PositiveInfinity;
        foreach (var sol in InverseAll(tcpPose, toolOffset))
        {
            var j = limits.NormalizeJoints(sol);
            var m = limits.Evaluate(new[] { j[0], j[1], j[2], j[3], j[4], 0.0 });
            if (m.MarginDeg > bestMargin) { bestMargin = m.MarginDeg; best = j; }
            if (seed is { Length: >= 5 } && m.Feasible)
            {
                var d = Enumerable.Range(0, 5).Max(i => Math.Abs(j[i] - seed[i]));
                if (d < nearestDist) { nearestDist = d; nearest = j; }
            }
        }
        return nearest ?? best;
    }

    private static double Wrap(double deg)
    {
        deg %= 360.0;
        if (deg > 180) deg -= 360;
        else if (deg <= -180) deg += 360;
        return deg;
    }
}
