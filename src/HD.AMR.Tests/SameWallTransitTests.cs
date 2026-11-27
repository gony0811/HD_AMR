using HD.AMR.App.Data.Entities;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Motion;
using HD.AMR.App.Service.Sequence;
using HD.AMR.App.Service.Sequence.Steps;
using Xunit.Abstractions;

namespace HD.AMR.Tests;

/// <summary>
/// 같은 정차·같은 면의 다음 용접선 면 앞 이송과 진입 자세 복원 역주행 —
/// 실기 2026-11-27 15:31 A L4-R01C09: #1 진입(J6 −180°) 뒤 연속 검사 #2~#4 로 팔이 면을 따라 1.4m·J6 −270° 가 된 상태에서
/// #5(V, 새 anchor)가 기준 자세 복귀를 시작 → ② 기록 역주행 1393mm(자세 유지) → 작업 준비 위치 관절 이동 면 여유 82mm &lt; 100mm,
/// 대체 후보 32개 모두 불가로 팔이 갇혔다.
/// </summary>
public class SameWallTransitTests(ITestOutputHelper output)
{
    // 면 A(후벽) 바깥 법선 ≈ BASE +X (현장 벽 정면 방향 −2.2°)
    private static readonly double[] N = Unit(Math.Cos(-2.2 * Math.PI / 180), Math.Sin(-2.2 * Math.PI / 180), 0);
    private static readonly double[] Seam4End = { -1569, 258, -619 };          // #4 종료 TCP
    private static readonly double[] OldEntry = { -1068, -1110, -660 };        // #1 진입점(갇힌 자리)
    private static readonly double[] V05Approach = { -1250.5, -218.5, -1198.8 }; // #5 접근점(면 이격 400mm)

    private static double[] Unit(double x, double y, double z)
    {
        var n = Math.Sqrt(x * x + y * y + z * z);
        return new[] { x / n, y / n, z / n };
    }

    private static PlanarApproachRecord Rec(string? wall = "A", string? stop = "ord|30.224,5.624,-0.039", bool withPose = true) => new(
        N, new[] { -1168.0, -1110, -660 }, 100, OldEntry, wall, 400,
        withPose ? new[] { -1068.0, -1110, -660, 90, 0, 90 } : null,
        withPose ? new[] { 53.0, -3, 75, -72, 139, -180 } : null,
        stop);

    // ── 이송 조건 ─────────────────────────────────────────────────────────
    [Fact]
    public void SameWallSameStop_Transits()
        => Assert.Null(CobotInspectionMoveStep.TransitSkipReason(Rec(), "A", "ord|30.224,5.624,-0.039"));

    [Fact]
    public void NoRecord_OrOtherWall_OrOtherStop_Skips()
    {
        Assert.NotNull(CobotInspectionMoveStep.TransitSkipReason(null, "A", "s"));
        Assert.NotNull(CobotInspectionMoveStep.TransitSkipReason(Rec(), "F", "ord|30.224,5.624,-0.039"));
        Assert.NotNull(CobotInspectionMoveStep.TransitSkipReason(Rec(), "A", "ord2|30.366,5.681,-0.004"));
        Assert.NotNull(CobotInspectionMoveStep.TransitSkipReason(Rec(), "A", null));          // 수동 시퀀스
        Assert.NotNull(CobotInspectionMoveStep.TransitSkipReason(Rec(stop: null), "A", "s"));
        Assert.NotNull(CobotInspectionMoveStep.TransitSkipReason(Rec(withPose: false), "A", "ord|30.224,5.624,-0.039"));
    }

    [Fact]
    public void StopKey_SameOrderAndTarget_Matches_UnknownIsNull()
    {
        var a = new SequenceContext { AcsOrderId = "d2fd", AmrTargetX = 30.224, AmrTargetY = 5.624, AmrTargetThetaRad = -0.039 };
        var b = new SequenceContext { AcsOrderId = "d2fd", AmrTargetX = 30.2241, AmrTargetY = 5.6239, AmrTargetThetaRad = -0.039 };
        var c = new SequenceContext { AcsOrderId = "d2fd", AmrTargetX = 30.366, AmrTargetY = 5.681, AmrTargetThetaRad = -0.004 };
        Assert.Equal(SequenceEntry.StopKeyOf(a), SequenceEntry.StopKeyOf(b));
        Assert.NotEqual(SequenceEntry.StopKeyOf(a), SequenceEntry.StopKeyOf(c));
        Assert.Null(SequenceEntry.StopKeyOf(new SequenceContext()));
    }

    // ── 이송 계획(현장 값) ─────────────────────────────────────────────────
    [Fact]
    public void FromSeam4End_LiftsToRetreatPlane_ThenAbout750mmAlongWall()
    {
        var plan = CobotInspectionMoveStep.PlanSameWallTransit(Seam4End, V05Approach, N, 100, 400)!;
        Assert.NotNull(plan.Lift);
        Assert.Equal(100, plan.LevelMm, 1);
        Assert.InRange(plan.InPlaneMm, 700, 800);
        output.WriteLine($"후퇴 [{string.Join(", ", plan.Lift!.Select(v => v.ToString("0")))}] → 면 방향 {plan.InPlaneMm:0}mm");
        // 들어갈 점 = 접근점 + 100mm 바깥
        for (var i = 0; i < 3; i++) Assert.Equal(V05Approach[i] + 100 * N[i], plan.Plunge[i], 3);
    }

    [Fact]
    public void FromOldEntry_KeepsHeight_DoesNotGoTowardWallWhileTravelling()
    {
        var plan = CobotInspectionMoveStep.PlanSameWallTransit(OldEntry, V05Approach, N, 100, 400)!;
        Assert.Null(plan.Lift);                      // 이미 후퇴 평면보다 바깥
        Assert.True(plan.LevelMm > 100);
        Assert.InRange(plan.InPlaneMm, 950, 1100);
    }

    [Fact]
    public void NotInFrontOfWall_OrTooFar_IsNull()
    {
        var behind = new[] { V05Approach[0] - 500, V05Approach[1], V05Approach[2] };    // 면 뒤
        var away = new[] { V05Approach[0] + 1000, V05Approach[1], V05Approach[2] };     // 후퇴 평면 앞 900mm
        var far = new[] { V05Approach[0], V05Approach[1] + 2500, V05Approach[2] };      // 면 방향 2.5m
        Assert.Null(CobotInspectionMoveStep.PlanSameWallTransit(behind, V05Approach, N, 100, 400));
        Assert.Null(CobotInspectionMoveStep.PlanSameWallTransit(away, V05Approach, N, 100, 400));
        Assert.Null(CobotInspectionMoveStep.PlanSameWallTransit(far, V05Approach, N, 100, 400));
    }

    // ── 역주행 진입 자세 복원 ──────────────────────────────────────────────
    [Fact]
    public void RetraceSamples_EndAtEntryOrientation_StepUnder10Deg()
    {
        var from = new[] { -1150.0, 258, -619, 90, 0, 0 };
        var to = new[] { -1068.0, -1110, -660, 90, 0, 90 };
        var pts = SequenceEntry.RetracePlanarSamples(from, to);
        Assert.Equal(to, pts[^1]);
        var prev = from;
        foreach (var p in pts)
        {
            Assert.True(SequenceEntry.RotationAngleDeg(prev, p) <= SequenceEntry.ClearSampleRotDeg + 0.5);
            prev = p;
        }
    }

    [Fact]
    public void EntryJointMismatch_IgnoresWinding_FlagsOtherBranch()
    {
        var entry = new[] { 53.0, -3, 75, -72, 139, -180 };
        Assert.Null(SequenceEntry.RetraceEntryJointMismatch(new[] { 54.0, -2, 74, -73, 138, 180 }, entry));   // J6 +180 = −180
        Assert.NotNull(SequenceEntry.RetraceEntryJointMismatch(new[] { 53.0, -3, 75, -72, 139, -270 }, entry)); // 손목 90° 돈 채
    }

    // ── 홈 모드 면 검사 준비 경유 대체 복귀 ────────────────────────────────
    private static TeachingPosition Taught(string key, params double[] j) => new()
    {
        Key = key, Name = key, X = 0, Y = 0, Z = 0, Rx = 0, Ry = 0, Rz = 0,
        J1 = j[0], J2 = j[1], J3 = j[2], J4 = j[3], J5 = j[4], J6 = j[5],
    };

    // 현장 15:34 '검사 준비 — 후벽' 도착 관절(홈 이동 시작 관절)
    private static readonly TeachingPosition SlotA = Taught(TeachingService.WallSlotKey("A"), -0.4, -9.4, 120.0, -110.8, 90.0, -179.6);
    private static readonly TeachingPosition Home = Taught("home", 0, -90, 90, 0, 90, 0);

    [Fact]
    public void HomeModeWall_WithSlot_HasSlotFallback()
    {
        var ctx = new SequenceContext { WallCode = "A", Positions = new() { ["home"] = Home, [SlotA.Key] = SlotA } };
        Assert.Equal(TeachingService.WallSlotKey("A"), SequenceEntry.HomeWallSlotFallback(ctx));
        Assert.Equal(TeachingService.WallSlotKey("A"),
            SequenceEntry.HomeWallSlotFallback(new SequenceContext { Positions = ctx.Positions }, lastWall: "A"));
        Assert.Null(SequenceEntry.HomeWallSlotFallback(new SequenceContext { WallCode = "A", Positions = new() { ["home"] = Home } }));
        Assert.Null(SequenceEntry.HomeWallSlotFallback(new SequenceContext { WallCode = "F", Positions = ctx.Positions }));
    }

    /// <summary>현장 갇힌 관절(15:34 수동 복구 시작 관절)에서 '검사 준비 — 후벽' 으로 가는 관절 이동 — 진단 출력만.
    /// 복귀 판정은 실제 면이 아니라 접근점 평면(면에서 400mm 앞)을 기준으로 해서 보수적이다: 이 근사로는 16% 지점 48mm &lt; 53mm 로
    /// 걸리지만 운영자는 실제로 이 경로로 빼냈다. 그래서 갇힌 뒤의 대체 경로보다 갇히지 않게(면 앞 이송·진입 자세 복원) 하는 쪽이 핵심이다.</summary>
    [Fact]
    public void FieldStuckPose_SlotAPath_Report()
    {
        var kin = Fr5Kinematics.Fr5Wml;
        var env = CobotEnvelope.Default;
        var wallPoint = Rec().ApproachPoint;
        (double, string) Wall(double[] q) => env.WallClearance(kin, q, wallPoint, N);
        var stuck = new[] { 53.4, -2.8, 75.0, -72.1, 139.1, -270.4 };
        var slot = new[] { -0.4, -9.4, 120.0, -110.8, 90.0, -179.6 };
        var why = CobotInspectionMoveStep.JointPathBlocker(stuck, slot, null, Wall);
        output.WriteLine($"갇힌 자세 → 검사 준비(A): {why ?? "통과"}, 출발 여유 {Wall(stuck).Item1:0}mm");
    }
}
