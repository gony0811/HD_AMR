using HD.AMR.App.Communication;

namespace HD.AMR.App.Service.Motion;

/// <summary>
/// FAIRINO FR 계열(UR 형 6축) 표준 DH 기구학 — 컨트롤러 없이 순·역기구학을 계산한다(오프라인 리치 맵용).
///
/// 관절 규약은 UR 과 같다: 전 관절 0 = 팔이 수평으로 펴진 자세, J3 = 0 팔꿈치 특이점,
/// J5 = 0 손목 특이점(<see cref="PostureLimits"/> 와 같은 전제). 관절 0점·부호와 d6(J5 → 플랜지 면) 106 mm 는 2026-11-16
/// 실물 컨트롤러 순기구학과 대조해 확인했다. 장비마다 다른 DH 보정값(약 1 mm)은 파라미터로 받는다 — <see cref="ApplyLinks"/>.
/// 다시 대조할 때는 <see cref="Fr5FkCheck"/> 와 tools/fr5_fk_capture.py 를 쓴다.
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

    /// <summary>d6 후보 — 장비 STEP 축선에서 잰 값 [mm]. 실물 대조(2026-11-16)로 이 값이 맞다고 확인했다.</summary>
    public const double D6StepMm = 106;
    /// <summary>d6 후보 — FAIRINO 공식 FR5 DH 표 값 [mm]. 실물과 맞지 않는다(TCP 가 약 55 mm 어긋난다) — 대조 도구의 비교용으로만 남긴다.</summary>
    public const double D6DatasheetMm = 160;

    /// <summary>
    /// FR5-WML 공칭 치수 [d1, a2, a3, d4, d5, d6] mm — 장비 STEP(E26003-E-A000-000-0, 2026-10-28)의 관절 축선에서 실측했다.
    /// d4 = 어깨 가로 185 − 팔꿈치 가로 140 + 손목 114. 2026-11-16 실물 컨트롤러(FR5-WML, v3.9.8) 순기구학과 자세 18개를 대조해
    /// d6 106 과 관절 0점·부호가 맞음을 확인했다(위치 오차 최대 1.34 mm · RMS 0.94 mm, d6 160 이면 최대 55.3 mm).
    /// </summary>
    public static readonly double[] NominalLinksMm = { 180, -970, -816, 159, 114, D6StepMm };

    /// <summary>
    /// 2026-11-16 실물(용접검사 AMR) 컨트롤러의 DH 보정값을 반영한 치수 — 이 값으로 풀면 컨트롤러 순기구학과 18개 자세 모두 0.000 mm 로 맞는다.
    /// 장비마다 다른 값이다. d1 은 보정값을 받지 못해 공칭 그대로다.
    /// </summary>
    public static readonly double[] WeldAmrLinksMm = { 180, -970.317, -815.788, 159.250, 114.077, 105.0 };

    /// <summary>파라미터로 받는 치수가 공칭에서 벗어날 수 있는 최대 [mm] — DH 보정은 1 mm 안팎이다. 넘으면 잘못된 값으로 보고 공칭을 쓴다.</summary>
    public const double MaxLinkDeviationMm = 5;

    /// <summary>링크 치수 파라미터 이름 — 값은 "d1,a2,a3,d4,d5,d6" [mm]. ACS 의 <c>Acs:Reach:LinksMm</c> 와 같게 둘 것.</summary>
    public const string LinksKey = "cobot.kinematics.linksMm";

    /// <summary>
    /// 지금 쓰는 FR5-WML 기구학 — 기본은 공칭 치수이고, 파라미터(<see cref="LinksKey"/>)의 컨트롤러 DH 보정 치수가 있으면 그 값이다
    /// (<see cref="ApplyLinks"/>). 오프라인 리치 맵·도달 사전 점검·자기 간섭이 쓴다.
    /// </summary>
    public static Fr5Kinematics Fr5Wml { get; private set; } = Of(NominalLinksMm);

    private static Fr5Kinematics Of(double[] v) => new(v[0], v[1], v[2], v[3], v[4], v[5]);

    /// <summary>이 기구학의 치수 [d1, a2, a3, d4, d5, d6] mm.</summary>
    public double[] LinksMm => new[] { D1, A2, A3, D4, D5, D6 };

    /// <summary>d6 만 바꾼 기구학(대조 도구용).</summary>
    public Fr5Kinematics WithD6(double d6Mm) => this with { D6 = d6Mm };

    /// <summary>
    /// 설정 치수 [d1, a2, a3, d4, d5, d6] → 기구학. 없으면 공칭(Ok). 6개가 아니거나, 무한대이거나, 공칭에서
    /// <see cref="MaxLinkDeviationMm"/> 넘게 벗어난 값이 있으면 공칭을 쓰고 Ok = false.
    /// </summary>
    public static (Fr5Kinematics Kinematics, bool Ok) FromLinks(IReadOnlyList<double>? linksMm)
    {
        var nominal = Of(NominalLinksMm);
        if (linksMm is null) return (nominal, true);
        if (linksMm.Count != 6) return (nominal, false);
        for (var i = 0; i < 6; i++)
            if (!double.IsFinite(linksMm[i]) || Math.Abs(linksMm[i] - NominalLinksMm[i]) > MaxLinkDeviationMm) return (nominal, false);
        return (Of(linksMm.ToArray()), true);
    }

    /// <summary>파라미터 문자열 "d1,a2,a3,d4,d5,d6" → 숫자 목록. 비어 있으면 null, 숫자가 아닌 칸이 있으면 빈 목록(→ 공칭·Ok = false).</summary>
    public static IReadOnlyList<double>? ParseLinks(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var list = new List<double>(parts.Length);
        foreach (var p in parts)
        {
            if (!double.TryParse(p, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
                return Array.Empty<double>();
            list.Add(v);
        }
        return list;
    }

    /// <summary>치수 → 파라미터 문자열.</summary>
    public static string FormatLinks(IEnumerable<double> linksMm) =>
        string.Join(",", linksMm.Select(v => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>
    /// 파라미터 치수를 <see cref="Fr5Wml"/> 에 적용한다(기동·저장·삭제 때). 잘못된 값이면 공칭을 쓰고 false.
    /// 값이 바뀌면 이미 만들어 둔 오프라인 리치 맵(reachmaps)은 옛 치수로 계산된 것이다(차이는 약 1 mm).
    /// </summary>
    public static bool ApplyLinks(string? text)
    {
        var (kin, ok) = FromLinks(ParseLinks(text));
        Fr5Wml = kin;
        return ok;
    }

    /// <summary>
    /// 검사 툴 초점 — 플랜지 면 기준 [x,y,z,rx,ry,rz]. 렌즈 광축이 J6 축과 동축(STEP 실측 편심 0)이고
    /// z 375mm 는 플랜지 면 → <b>카메라 초점</b> 거리(설계값)다. 컨트롤러에 등록된 공구 #1 은 z 365 mm(플랜지 면 → 팁, 초점 10 mm 제외,
    /// 2026-11-16 실물 확인)라 이 값과 10 mm 다르다 — 컨트롤러 TCP 로 움직이는 경로에는 공구 #1 을, 초점 기준 오프라인 계산에만 이 값을 쓴다.
    /// ACS 는 공구 365 + 초점 자세 이격 10 = 375 로 같은 초점을 본다.
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

    /// <summary>관절 프레임 전부 — [0] = BASE(단위행렬), [i] = 관절 i 뒤, [6] = 플랜지. 링크 위치(자기 간섭 판정)용.</summary>
    public double[][,] Frames(double[] jointsDeg)
    {
        var frames = new double[7][,];
        frames[0] = PoseMath.FromPose(new double[6]);
        for (var i = 0; i < 6; i++) frames[i + 1] = PoseMath.Multiply(frames[i], Link(i, jointsDeg[i] * Deg2Rad));
        return frames;
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
    /// <param name="collides">주면 이 판정이 참인 해(자기 간섭 — <see cref="CobotEnvelope"/>)는 고르지 않는다.
    /// 간섭 없는 해가 하나도 없을 때만 간섭 해를 돌려줘 호출측이 '자기 간섭' 으로 사유를 남기게 한다.</param>
    public double[]? InverseBest(double[] tcpPose, double[]? toolOffset, PostureLimits limits, double[]? seed = null,
        Func<double[], bool>? collides = null)
    {
        double[]? best = null, bestFree = null, nearest = null;
        double bestMargin = double.NegativeInfinity, bestFreeMargin = double.NegativeInfinity, nearestDist = double.PositiveInfinity;
        foreach (var sol in InverseAll(tcpPose, toolOffset))
        {
            var j = limits.NormalizeJoints(sol);
            var m = limits.Evaluate(new[] { j[0], j[1], j[2], j[3], j[4], 0.0 });
            if (m.MarginDeg > bestMargin) { bestMargin = m.MarginDeg; best = j; }
            if (collides?.Invoke(j) == true) continue;
            if (m.MarginDeg > bestFreeMargin) { bestFreeMargin = m.MarginDeg; bestFree = j; }
            if (seed is { Length: >= 5 } && m.Feasible)
            {
                var d = Enumerable.Range(0, 5).Max(i => Math.Abs(j[i] - seed[i]));
                if (d < nearestDist) { nearestDist = d; nearest = j; }
            }
        }
        return nearest ?? bestFree ?? best;
    }

    private static double Wrap(double deg)
    {
        deg %= 360.0;
        if (deg > 180) deg -= 360;
        else if (deg <= -180) deg += 360;
        return deg;
    }
}
