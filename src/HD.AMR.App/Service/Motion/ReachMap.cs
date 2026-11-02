using System.Globalization;
using System.Text;
using HD.AMR.App.Models;
using HD.AMR.App.Service.Inspection;

namespace HD.AMR.App.Service.Motion;

/// <summary>
/// 리치 맵 — "AMR 을 여기 세우고 리프트를 이만큼 올리면, 이 높이·가로 위치의 용접선에 팔이 닿는가" 를
/// 격자로 훑어 표로 만든다. 정차 위치 최적화(어디에 설 것인가)의 근거 데이터이자 ACS 에 넘길 리치 모델이다.
///
/// <b>좌표는 AMR 차체 기준이다</b>(코봇 BASE 아님). AMR 이 맵 원점에 벽을 정면으로(+X) 바라보고 섰다고 두고,
/// 용접선 점을 차체 기준 (x 전방, y 좌측, z 바닥 위 높이) 로 놓는다. 그래서 수직벽에서 x 는 곧
/// ACS 의 '정차 이격'(벽 ↔ 로봇 기준점, VDA §4.4)과 같은 값이 된다 — 표를 그대로 정차 계획에 쓸 수 있다.
/// T_A_B(장착 보정)와 텔레스코픽 스트로크는 실제 값을 쓴다.
///
/// 셀마다 운영 시퀀스와 같은 두 자세를 본다:
///  · ② 거친 접근 — 면에서 <see cref="ReachMapSpec.ApproachStandoffMm"/>(기본 400) 물러난 TCP. 팔이 가장 접히는 지점.
///  · 최종 작업 — 면에서 <see cref="ReachMapSpec.FinalStandoffMm"/>(기본 300, ④⁺ 레이저 WD) 물러난 TCP. 가장 뻗는 지점.
/// 둘 다 역기구학이 풀리고 <see cref="PostureLimits"/> 를 만족하며, 둘 사이 관절 경로가 손목 특이점을
/// 지나지 않아야 그 셀을 '도달' 로 본다.
///
/// J6 은 판정에서 뺀다 — 운영은 이동 직전에 J6 을 절대각(수직 180° / 수평 90°)으로 따로 맞추므로
/// IK 가 고른 J6 값은 의미가 없다(<see cref="Sequence.Steps.CobotInspectionMoveStep.TargetJ6Deg"/>).
///
/// 역기구학은 델리게이트로 주입받는다 — 컨트롤러 IK 는 계산만 하는 호출이라 코봇이 움직이지 않으며,
/// 단위 테스트는 가짜 IK 로 같은 경로를 탄다.
/// </summary>
public static class ReachMap
{
    /// <summary>
    /// 격자 전체를 평가한다. 셀 순서는 wall_code → 스트로크 → x → y → z.
    /// </summary>
    /// <param name="inverseKin">BASE pose → 관절각[6]. 도달 불가면 null 또는 예외(예외 메시지는 사유로 남긴다).</param>
    public static async Task<ReachMapResult> BuildAsync(
        ReachMapSpec spec,
        ReachMapRobot robot,
        Func<double[], CancellationToken, Task<double[]?>> inverseKin,
        IProgress<ReachMapProgress>? progress = null,
        CancellationToken ct = default)
    {
        var limits = robot.Limits.Normalized();
        var walls = spec.WallCodes
            .Select(c => (Code: c, Wall: WallCodes.Find(c)))
            .ToList();
        var unknown = walls.Where(w => w.Wall is null).Select(w => w.Code).ToList();
        if (unknown.Count > 0)
            throw new ArgumentException($"미정의 wall_code: {string.Join(", ", unknown)}", nameof(spec));

        var total = walls.Sum(w => spec.StrokesMm.Length * spec.Xs.Length * spec.Ys.Length * ZsFor(spec, w.Wall!).Length);
        var cells = new List<ReachCell>(total);
        var done = 0;
        var ikCalls = 0;

        foreach (var (code, wall) in walls)
        foreach (var h in spec.StrokesMm)
        foreach (var x in spec.Xs)
        foreach (var y in spec.Ys)
        foreach (var z in ZsFor(spec, wall!))
        {
            ct.ThrowIfCancellationRequested();
            var (cell, calls) = await EvaluateAsync(spec, robot, limits, code, h, x, y, z, inverseKin, ct).ConfigureAwait(false);
            cells.Add(cell);
            ikCalls += calls;
            done++;
            progress?.Report(new ReachMapProgress(done, total, ikCalls, cell));
        }

        return new ReachMapResult(spec, robot, cells, ikCalls);
    }

    /// <summary>바닥은 높이가 0 하나뿐이다 — z 격자를 훑지 않는다. 천장은 z 를 천장 높이로 읽는다.</summary>
    public static double[] ZsFor(ReachMapSpec spec, WallCodeInfo wall)
        => wall.Orientation == SurfaceOrientation.Floor ? new[] { 0.0 } : spec.Zs;

    private static async Task<(ReachCell Cell, int IkCalls)> EvaluateAsync(
        ReachMapSpec spec, ReachMapRobot robot, PostureLimits limits,
        string code, double h, double x, double y, double z,
        Func<double[], CancellationToken, Task<double[]?>> inverseKin, CancellationToken ct)
    {
        var approach = SeamBaseTransform.Resolve(Input(robot, code, h, x, y, z, spec.ApproachStandoffMm));
        var final = SeamBaseTransform.Resolve(Input(robot, code, h, x, y, z, spec.FinalStandoffMm));
        var reach = ApproachReach.Check(approach.NormalDistanceMm, spec.ApproachStandoffMm,
                                        robot.ToolLengthMm, limits.MinFlangeReachMm);

        ReachCell Cell(ReachStatus status, string reason, double? margin = null,
                       double[]? jA = null, double[]? jF = null)
            => new(code, h, x, y, z, status, reason, margin, reach.FlangeReachMm,
                   approach.NormalDistanceMm, jA, jF);

        // 면이 코봇 뒤에 있거나 좌표가 팔 규모를 크게 벗어나면 IK 를 부르지 않는다 — 컨트롤러 왕복 절약.
        if (approach.NormalDistanceMm < SeamBaseTransform.MinNormalDistanceMm)
            return (Cell(ReachStatus.OutOfRange, $"면이 코봇 BASE 뒤(법선거리 {approach.NormalDistanceMm:0}mm)"), 0);
        var far = Math.Max(approach.DistanceMm, final.DistanceMm);
        if (far > spec.MaxBaseDistanceMm)
            return (Cell(ReachStatus.OutOfRange, $"BASE→TCP {far:0}mm > {spec.MaxBaseDistanceMm:0}mm"), 0);

        var calls = 0;
        double[]? jA, jF;
        string? ikError;
        (jA, ikError) = await SolveAsync(approach, inverseKin, ct).ConfigureAwait(false);
        calls++;
        if (jA is null) return (Cell(ReachStatus.IkFailed, $"② IK 실패{ikError}"), calls);
        (jF, ikError) = await SolveAsync(final, inverseKin, ct).ConfigureAwait(false);
        calls++;
        if (jF is null) return (Cell(ReachStatus.IkFailed, $"최종 IK 실패{ikError}", jA: jA), calls);

        jA = WithoutJ6(limits.NormalizeJoints(jA));
        jF = WithoutJ6(limits.NormalizeJoints(jF));

        var mA = limits.Evaluate(jA, approach.PlanarDistanceMm);
        var mF = limits.Evaluate(jF, final.PlanarDistanceMm);
        var margin = Math.Min(mA.MarginDeg, mF.MarginDeg);
        if (!mA.Feasible) return (Cell(ReachStatus.PostureLimit, $"② {mA.Limiting}", margin, jA, jF), calls);
        if (!mF.Feasible) return (Cell(ReachStatus.PostureLimit, $"최종 {mF.Limiting}", margin, jA, jF), calls);

        var path = limits.CheckJointPath(jA, jF);
        if (!path.Ok) return (Cell(ReachStatus.PathUnsafe, $"②→최종 {path.Reason}", margin, jA, jF), calls);

        // IK 는 풀렸어도 운영 ② 의 리치 사전 점검이 막는다 — 잠정 최소 뻗음 검증용으로 관절 결과는 남긴다.
        if (!reach.Ok)
            return (Cell(ReachStatus.FlangeShort,
                $"플랜지 뻗음 {reach.FlangeReachMm:0}mm < 최소 {limits.MinFlangeReachMm:0}mm (IK·자세는 통과)", margin, jA, jF), calls);

        return margin < spec.GoodMarginDeg
            ? (Cell(ReachStatus.Tight, $"여유 {margin:0.0}° < {spec.GoodMarginDeg:0}°", margin, jA, jF), calls)
            : (Cell(ReachStatus.Ok, "도달", margin, jA, jF), calls);
    }

    private static SeamBaseInput Input(ReachMapRobot robot, string code, double h, double x, double y, double z, double standoff)
        => new(
            SeamStartW: new[] { x / 1000.0, y / 1000.0, z / 1000.0 },
            SeamEndW: null,
            AmrXm: 0, AmrYm: 0, AmrYawRad: 0,
            MountAtHome: robot.MountAtHome,
            TelescopicStrokeMm: h,
            ZDatumOffsetMm: 0,
            StandoffMm: standoff,
            WallFacingThetaRad: 0,
            WallCode: code,
            OpticalAxis: robot.OpticalAxis,
            ToolSpinDeg: robot.ToolSpinDeg,
            RollRef: ToolRollRef.WallHorizontal);

    private static async Task<(double[]? Joints, string? Error)> SolveAsync(
        SeamBaseTarget target, Func<double[], CancellationToken, Task<double[]?>> inverseKin, CancellationToken ct)
    {
        if (target.TargetPoseBase is null) return (null, " — 목표 자세 없음");
        try
        {
            var j = await inverseKin(target.TargetPoseBase, ct).ConfigureAwait(false);
            return j is { Length: 6 } ? (j, null) : (null, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return (null, $" — {FirstLine(ex.Message)}"); }
    }

    private static double[] WithoutJ6(double[] j) => new[] { j[0], j[1], j[2], j[3], j[4], 0.0 };

    private static string FirstLine(string s)
    {
        var i = s.IndexOfAny(new[] { '.', '\n' });
        return i > 0 ? s[..i] : s;
    }
}

/// <summary>리치 맵 격자. 거리는 전부 mm, AMR 차체 기준.</summary>
/// <param name="WallCodes">훑을 면(정본 10코드 중).</param>
/// <param name="StrokesMm">텔레스코픽 스트로크 h [mm, 완전 하강 = 0].</param>
/// <param name="Xs">용접선 점의 차체 전방 거리 — 수직벽에서는 '벽 ↔ 로봇 기준점' 정차 이격.</param>
/// <param name="Ys">용접선 점의 차체 좌측 가로 오프셋(+ = 좌).</param>
/// <param name="Zs">용접선 점의 바닥 위 높이. 바닥(B)은 무시하고 0, 천장(T)은 천장 높이로 읽는다.</param>
/// <param name="ApproachStandoffMm">② 거친 접근 거리 — TCP 의 면 후퇴(inspection.approachStandoffMm).</param>
/// <param name="FinalStandoffMm">최종 작업거리 — ④⁺ 레이저 WD(Sequence.Laser.WorkingDistanceMm).</param>
/// <param name="GoodMarginDeg">이 여유 미만이면 '빠듯' 으로 분류 [도].</param>
/// <param name="MaxBaseDistanceMm">BASE→TCP 거리가 이보다 멀면 IK 없이 범위 밖으로 친다.
/// FR5-WML: 어깨 180 + 리치 1900 + 툴 ≈ 2.45 m.</param>
public sealed record ReachMapSpec(
    string[] WallCodes,
    double[] StrokesMm,
    double[] Xs,
    double[] Ys,
    double[] Zs,
    double ApproachStandoffMm = 400,
    double FinalStandoffMm = 300,
    double GoodMarginDeg = 10,
    double MaxBaseDistanceMm = 2500)
{
    /// <summary>[from, to] 를 step 간격으로 — 끝값 포함. step ≤ 0 이면 from 하나.</summary>
    public static double[] Range(double from, double to, double step)
    {
        if (step <= 0 || to < from) return new[] { from };
        var n = (int)Math.Floor((to - from) / step + 1e-9);
        return Enumerable.Range(0, n + 1).Select(i => from + i * step).ToArray();
    }

    /// <summary>셀 수(바닥은 z 1개로 센다) — IK 호출은 셀당 최대 2회.</summary>
    public int CellCount => WallCodes
        .Select(Models.WallCodes.Find)
        .Sum(w => StrokesMm.Length * Xs.Length * Ys.Length
                  * (w?.Orientation == SurfaceOrientation.Floor ? 1 : Zs.Length));
}

/// <summary>로봇 쪽 고정 입력 — 장착 보정·공구·자세 한계·광축.</summary>
/// <param name="MountAtHome">T_A_B [x,y,z,rx,ry,rz] — 스트로크 0 기준.</param>
/// <param name="ToolLengthMm">플랜지→TCP 병진 길이 [mm].</param>
/// <param name="Limits">자세 허용 범위(최소 플랜지 뻗음 포함).</param>
/// <param name="OpticalAxis">광축 툴축 — 카메라 페이지 설정값.</param>
/// <param name="ToolSpinDeg">광축 둘레 초기 회전 — 시험 화면과 같은 기준(벽면 수평 + 180°).</param>
public sealed record ReachMapRobot(
    double[] MountAtHome,
    double ToolLengthMm,
    PostureLimits Limits,
    ToolAxisDir OpticalAxis = ToolAxisDir.PlusZ,
    double ToolSpinDeg = 180.0);

/// <summary>셀 판정 — 위에서부터 먼저 걸린 사유 하나.</summary>
public enum ReachStatus
{
    /// <summary>두 자세 모두 도달, 여유 충분.</summary>
    Ok,
    /// <summary>도달하지만 여유가 <see cref="ReachMapSpec.GoodMarginDeg"/> 미만.</summary>
    Tight,
    /// <summary>IK·자세는 통과했지만 플랜지 뻗음이 최소값 미만 — 운영 ② 리치 사전 점검이 막는다.</summary>
    FlangeShort,
    /// <summary>②→최종 관절 경로가 손목 특이점(J5 부호 반전)을 지난다.</summary>
    PathUnsafe,
    /// <summary>IK 해가 관절 한계·특이점 여유를 벗어난다.</summary>
    PostureLimit,
    /// <summary>역기구학 해 없음.</summary>
    IkFailed,
    /// <summary>배치만으로 불가(면이 뒤, 너무 멂) — IK 를 부르지 않았다.</summary>
    OutOfRange,
}

/// <summary>셀 하나의 결과. 좌표는 AMR 차체 기준 mm.</summary>
public sealed record ReachCell(
    string WallCode,
    double StrokeMm,
    double Xmm,
    double Ymm,
    double Zmm,
    ReachStatus Status,
    string Reason,
    double? MarginDeg,
    double FlangeReachMm,
    double NormalDistanceMm,
    double[]? ApproachJointsDeg,
    double[]? FinalJointsDeg)
{
    /// <summary>운영 시퀀스가 실제로 수행할 수 있는 셀인가(빠듯 포함).</summary>
    public bool Usable => Status is ReachStatus.Ok or ReachStatus.Tight;
}

/// <summary>진행 보고.</summary>
public sealed record ReachMapProgress(int Done, int Total, int IkCalls, ReachCell Last);

/// <summary>리치 맵 결과 — CSV 와 요약을 만든다.</summary>
public sealed record ReachMapResult(
    ReachMapSpec Spec,
    ReachMapRobot Robot,
    IReadOnlyList<ReachCell> Cells,
    int IkCalls)
{
    /// <summary>
    /// 면·스트로크·x 별 도달 셀 수. 수직벽에서 x 는 정차 이격이므로, 같은 면·높이에서 도달 셀이 가장 많은 x 가
    /// "한 정차에서 가장 넓게 닿는 정차 거리" 다.
    /// </summary>
    public IReadOnlyList<ReachSummaryRow> Summary() => Cells
        .GroupBy(c => (c.WallCode, c.StrokeMm, c.Xmm))
        .Select(g => new ReachSummaryRow(g.Key.WallCode, g.Key.StrokeMm, g.Key.Xmm,
            g.Count(), g.Count(c => c.Status == ReachStatus.Ok), g.Count(c => c.Status == ReachStatus.Tight),
            g.GroupBy(c => c.Status).Where(s => s.Key is not (ReachStatus.Ok or ReachStatus.Tight))
             .OrderByDescending(s => s.Count()).Select(s => s.Key).Cast<ReachStatus?>().FirstOrDefault()))
        .ToList();

    /// <summary>사람이 읽는 요약 — 면·스트로크마다 도달 셀이 가장 많은 x(정차 거리)를 짚는다.</summary>
    public string SummaryText()
    {
        var sb = new StringBuilder();
        var counts = Cells.GroupBy(c => c.Status).ToDictionary(g => g.Key, g => g.Count());
        sb.AppendLine($"셀 {Cells.Count}개 · IK {IkCalls}회 · " +
                      string.Join(" · ", Enum.GetValues<ReachStatus>()
                          .Where(counts.ContainsKey).Select(s => $"{Label(s)} {counts[s]}")));

        foreach (var g in Summary().GroupBy(r => (r.WallCode, r.StrokeMm)))
        {
            var best = g.OrderByDescending(r => r.Usable).ThenBy(r => r.Xmm).First();
            var row = string.Join("  ", g.Select(r => $"x{r.Xmm:0}:{r.Usable}/{r.Total}"));
            sb.AppendLine(best.Usable > 0
                ? $"[{g.Key.WallCode} h={g.Key.StrokeMm:0}] 최다 도달 x={best.Xmm:0}mm ({best.Usable}/{best.Total}) — {row}"
                : $"[{g.Key.WallCode} h={g.Key.StrokeMm:0}] 도달 셀 없음 — {row}");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>셀 전체를 CSV 로(엑셀·ACS 전달용). 관절각은 J6 을 뺀 J1~J5.</summary>
    public string ToCsv()
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine($"# reach map  approachStandoff={Spec.ApproachStandoffMm.ToString(ci)}mm  " +
                      $"finalStandoff={Spec.FinalStandoffMm.ToString(ci)}mm  tool={Robot.ToolLengthMm.ToString("0.#", ci)}mm  " +
                      $"minFlange={Robot.Limits.MinFlangeReachMm.ToString(ci)}mm  wrist={Robot.Limits.WristMarginDeg.ToString(ci)}deg  " +
                      $"mount=[{string.Join(" ", Robot.MountAtHome.Select(v => v.ToString("0.##", ci)))}]");
        sb.AppendLine("wall_code,stroke_mm,x_mm,y_mm,z_mm,status,usable,margin_deg,flange_reach_mm,normal_dist_mm," +
                      "a_j1,a_j2,a_j3,a_j4,a_j5,f_j1,f_j2,f_j3,f_j4,f_j5,reason");
        foreach (var c in Cells)
        {
            static string J(double[]? j, int i, CultureInfo ci) => j is { Length: 6 } ? j[i].ToString("0.0", ci) : "";
            sb.Append(c.WallCode).Append(',')
              .Append(c.StrokeMm.ToString("0", ci)).Append(',')
              .Append(c.Xmm.ToString("0", ci)).Append(',')
              .Append(c.Ymm.ToString("0", ci)).Append(',')
              .Append(c.Zmm.ToString("0", ci)).Append(',')
              .Append(c.Status).Append(',')
              .Append(c.Usable ? 1 : 0).Append(',')
              .Append(c.MarginDeg?.ToString("0.0", ci) ?? "").Append(',')
              .Append(c.FlangeReachMm.ToString("0", ci)).Append(',')
              .Append(c.NormalDistanceMm.ToString("0", ci));
            for (var i = 0; i < 5; i++) sb.Append(',').Append(J(c.ApproachJointsDeg, i, ci));
            for (var i = 0; i < 5; i++) sb.Append(',').Append(J(c.FinalJointsDeg, i, ci));
            sb.Append(",\"").Append(c.Reason.Replace("\"", "'")).AppendLine("\"");
        }
        return sb.ToString();
    }

    public static string Label(ReachStatus s) => s switch
    {
        ReachStatus.Ok => "도달",
        ReachStatus.Tight => "빠듯",
        ReachStatus.FlangeShort => "뻗음부족",
        ReachStatus.PathUnsafe => "경로특이점",
        ReachStatus.PostureLimit => "자세한계",
        ReachStatus.IkFailed => "IK실패",
        _ => "범위밖",
    };
}

/// <summary>면·스트로크·x 한 줄 요약.</summary>
public sealed record ReachSummaryRow(
    string WallCode, double StrokeMm, double Xmm, int Total, int Ok, int Tight, ReachStatus? MainFailure)
{
    public int Usable => Ok + Tight;
}
