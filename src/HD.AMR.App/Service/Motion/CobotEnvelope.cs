namespace HD.AMR.App.Service.Motion;

/// <summary>
/// 코봇 자기 간섭 외곽 — 팔 링크·검사 툴이 <b>AMR 차체·리프트 기둥·신호탑</b>에 닿는지 관절각만으로 판정한다
/// (컨트롤러 없이, 탱크 면과 무관). 치수는 장비 STEP(E26003-E-A000-000-0, 2026-11-01 파싱) 실측이다.
///
/// <b>왜 필요한가.</b> 컨트롤러 역기구학은 같은 TCP 에 최대 8개 해를 주고 그중 하나는 상완이 리프트 기둥 옆으로
/// 늘어지는 elbow-down 가지다 — 수직벽 한가운데처럼 흔한 목표에서도 팔꿈치가 기둥 단면 안으로 들어간다.
/// 관절 한계·특이점 여유(<see cref="PostureLimits"/>)는 이것을 거르지 못한다.
///
/// 좌표: AMR 차체 기준 mm(+X 전방·+Y 좌측·z 바닥 위). 코봇 BASE 는 차체 중심 위 <see cref="BaseHeightMm"/> + 스트로크,
/// BASE 축은 차체 축을 z 둘레로 장착 yaw(T_A_B rz)만큼 돌린 것으로 둔다(rx·ry 는 작아 무시).
/// 링크는 캡슐(선분 + 반경), 검사 툴은 몸체 중심부 원통(<see cref="ToolSegments"/>)으로 감싸고, 장애물은 차체 좌표
/// 축정렬 상자다. ACS 리치·간섭 시뮬레이터(HD.Acs.Core.Reach)의 자기 간섭과 같은 모델.
/// </summary>
public sealed record CobotEnvelope
{
    /// <summary>차체 바닥 → 코봇 BASE 면 높이(스트로크 0) [mm].</summary>
    public double BaseHeightMm { get; init; } = 1635;

    public double AmrFrontMm { get; init; } = 395;
    public double AmrRearMm { get; init; } = 365;
    public double AmrHalfWidthMm { get; init; } = 350;
    public double AmrHeightMm { get; init; } = 887;

    /// <summary>리프트 기둥(CTD-LF144) 사각 단면 반폭 [mm] — 200×200, 차체 중심, 차체 윗면 → BASE.</summary>
    public double ColumnHalfMm { get; init; } = 100;

    /// <summary>차체 윗면 신호탑(SWTEL) — 후방 77~225 · 좌우 ±108 · 높이 933 [mm].</summary>
    public double TowerRearFromMm { get; init; } = 77;
    public double TowerRearToMm { get; init; } = 225;
    public double TowerHalfWidthMm { get; init; } = 108;
    public double TowerTopMm { get; init; } = 933;

    /// <summary>J2·J3 축 방향 가로 오프셋 — 어깨(상완 위치)·팔꿈치 이후(전완 위치) [mm].</summary>
    public double ShoulderOffsetMm { get; init; } = 185;
    public double ForearmOffsetMm { get; init; } = 45;

    public double ShoulderRadiusMm { get; init; } = 70;
    public double UpperArmRadiusMm { get; init; } = 60;
    public double ElbowRadiusMm { get; init; } = 55;
    public double ForearmRadiusMm { get; init; } = 50;
    public double WristRadiusMm { get; init; } = 45;

    /// <summary>
    /// 검사 툴 외곽(자기 간섭용) — 플랜지 면에서 광축(+Z) 방향 원통. STEP(E26003-E-A200) 몸체 브래킷(CAMETA BKT)
    /// 축 0~293 · 반경 169 만 쓴다. 한쪽에 치우친 브래킷·D435i(반경 ≤245)·조명 막대(반경 ≤234)는 J6 각에 따라 위치가
    /// 달라 회전 대칭으로 감싸면 실기 홈 자세(툴 수평, 차체 윗면 위 209mm)부터 간섭으로 잘못 거부된다 — 그 부분은
    /// 판정하지 않는다(탱크 면 판정은 ACS 시뮬레이터가 전체 외곽으로 보수적으로 한다).
    /// </summary>
    public IReadOnlyList<(string Name, double FromMm, double ToMm, double RadiusMm)> ToolSegments { get; init; } = new[]
    {
        ("검사 툴(몸체)", 0.0, 293.0, 169.0),
    };

    public static CobotEnvelope Default { get; } = new();

    /// <summary>
    /// 관절각 한 벌의 자기 간섭 여유 — 팔 부품마다 차체·신호탑·기둥까지 최단거리 − 반경의 최소 [mm].
    /// 음수면 그만큼 파고든다. 기둥에 붙은 어깨는 기둥과 재지 않는다(BASE 는 기둥 위라 아예 제외).
    /// </summary>
    /// <param name="mountYawDeg">장착 yaw(T_A_B rz) [도] — BASE +X 가 차체 +X 에서 반시계로 돈 각.</param>
    public SelfClearance Evaluate(Fr5Kinematics kin, double[] jointsDeg, double strokeMm, double mountYawDeg)
    {
        var fr = kin.Frames(jointsDeg);
        var baseZ = BaseHeightMm + strokeMm;
        var yaw = mountYawDeg * Math.PI / 180;
        double cy = Math.Cos(yaw), sy = Math.Sin(yaw);

        double[] Amr(double[] p) => new[] { cy * p[0] - sy * p[1], sy * p[0] + cy * p[1], p[2] + baseZ };
        double[] O(int i) => new[] { fr[i][0, 3], fr[i][1, 3], fr[i][2, 3] };
        double[] Z(int i) => new[] { fr[i][0, 2], fr[i][1, 2], fr[i][2, 2] };
        double[] Along(double[] p, double[] axis, double mm) => new[] { p[0] + axis[0] * mm, p[1] + axis[1] * mm, p[2] + axis[2] * mm };

        var z1 = Z(1);
        var z6 = Z(6);
        var s1 = Along(O(1), z1, ShoulderOffsetMm);
        var e1 = Along(O(2), z1, ShoulderOffsetMm);
        var e2 = Along(O(2), z1, ForearmOffsetMm);
        var w1 = Along(O(3), z1, ForearmOffsetMm);

        var parts = new List<(string Name, double[] A, double[] B, double R, bool NearColumn)>
        {
            ("어깨(J2)", O(1), s1, ShoulderRadiusMm, true),
            ("상완", s1, e1, UpperArmRadiusMm, false),
            ("팔꿈치(J3)", e1, e2, ElbowRadiusMm, false),
            ("전완", e2, w1, ForearmRadiusMm, false),
            ("손목1(J4)", w1, O(4), WristRadiusMm, false),
            ("손목2(J5)", O(4), O(5), WristRadiusMm, false),
            ("손목3(J6)", O(5), O(6), WristRadiusMm, false),
        };
        foreach (var t in ToolSegments)
            parts.Add((t.Name, Along(O(6), z6, t.FromMm), Along(O(6), z6, t.ToMm), t.RadiusMm, false));

        var boxes = new (string Name, double[] Min, double[] Max)[]
        {
            ("AMR 차체", new[] { -AmrRearMm, -AmrHalfWidthMm, 0 }, new[] { AmrFrontMm, AmrHalfWidthMm, AmrHeightMm }),
            ("신호탑", new[] { -TowerRearToMm, -TowerHalfWidthMm, AmrHeightMm }, new[] { -TowerRearFromMm, TowerHalfWidthMm, TowerTopMm }),
            ("리프트 기둥", new[] { -ColumnHalfMm, -ColumnHalfMm, AmrHeightMm }, new[] { ColumnHalfMm, ColumnHalfMm, baseZ }),
        };

        var best = new SelfClearance(double.PositiveInfinity, "", "");
        foreach (var p in parts)
        {
            var a = Amr(p.A);
            var b = Amr(p.B);
            foreach (var box in boxes)
            {
                if (p.NearColumn && box.Name == "리프트 기둥") continue;
                var cl = SegmentBoxDistanceMm(a, b, box.Min, box.Max) - p.R;
                if (cl < best.ClearanceMm) best = new SelfClearance(cl, p.Name, box.Name);
            }
        }
        return best;
    }

    /// <summary>선분–축정렬 상자 최단거리 [mm]. 선분 위 점에서 상자까지 거리는 볼록이라 삼분 탐색으로 찾는다.</summary>
    public static double SegmentBoxDistanceMm(double[] a, double[] b, double[] min, double[] max)
    {
        double At(double t)
        {
            var s = 0.0;
            for (var i = 0; i < 3; i++)
            {
                var p = a[i] + (b[i] - a[i]) * t;
                var d = p < min[i] ? min[i] - p : p > max[i] ? p - max[i] : 0;
                s += d * d;
            }
            return Math.Sqrt(s);
        }
        double lo = 0, hi = 1;
        for (var k = 0; k < 40 && hi - lo > 1e-4; k++)
        {
            double m1 = lo + (hi - lo) / 3, m2 = hi - (hi - lo) / 3;
            if (At(m1) <= At(m2)) hi = m2; else lo = m1;
        }
        return Math.Min(At((lo + hi) / 2), Math.Min(At(0), At(1)));
    }
}

/// <summary>자기 간섭 여유 — 가장 가까운 부품과 장애물. ClearanceMm &lt; 0 이면 간섭.</summary>
public sealed record SelfClearance(double ClearanceMm, string Part, string Obstacle)
{
    public bool Collides => ClearanceMm < 0;

    public override string ToString() => $"{Part} ↔ {Obstacle} {ClearanceMm:0}mm";
}
