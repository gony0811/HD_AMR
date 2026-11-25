using HD.AMR.App.Data.Entities;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Motion;
using HD.AMR.App.Service.Sequence;
using HD.AMR.App.Service.Sequence.Steps;
using Xunit.Abstractions;
using static HD.AMR.App.Service.Sequence.Steps.SequenceEntry;

namespace HD.AMR.Tests;

/// <summary>
/// 홈 모드 복귀가 막혔을 때 검사 준비 자세(없으면 그 모드 기준 자세)로 돌아가는 대체 경로 —
/// 실기 2026-11-25 12:39 PU L4-R02C24 #5: 벽면 후퇴 300mm 뒤 작업 준비 위치 관절 이동이 면 쪽으로 −139mm 휘고
/// 면 여유 확보 후보 92개가 모두 막혀 팔이 면 앞에 남았다.
/// </summary>
public class HomeReturnInspectReadyFallbackTests(ITestOutputHelper output)
{
    private static TeachingPosition Taught(string key, params double[] j) => new()
    {
        Key = key, Name = key,
        X = 0, Y = 0, Z = 0, Rx = 0, Ry = 0, Rz = 0,
        J1 = j[0], J2 = j[1], J3 = j[2], J4 = j[3], J5 = j[4], J6 = j[5],
    };

    private static SequenceContext Ctx(string? wall, params TeachingPosition[] ps) => new()
    {
        WallCode = wall,
        Positions = ps.ToDictionary(p => p.Key, p => p),
    };

    // 현장 AMR-01 factsheet(2026-11-25) 값 — ready = 일반 면 출발 자세, home2 = 천장 출발 자세.
    private static readonly TeachingPosition Ready = Taught("ready", -0.483, -20.1535, 127.3357, -107.3856, 89.9082, -179.6069);
    private static readonly TeachingPosition Home2 = Taught("home2", -0.4144, -11.9923, 121.9717, -110.1692, 89.975, 180.392);
    private static readonly TeachingPosition Home = Taught("home", 0, -90, 90, 0, 90, 0);
    private static readonly TeachingPosition SlotPu = Taught(TeachingService.WallSlotKey("PU"), 0, -60, 120, -90, 90, -180);
    private static readonly TeachingPosition Home3 = Taught("home3", -3, -21, 102, -81, 88, -180);

    [Fact]
    public void UpperFace_UsesItsSlot_ThenHome2()
    {
        var fb = InspectReadyFallback(Ctx("PU", Home, Ready, Home2, SlotPu));
        Assert.NotNull(fb);
        Assert.Equal(TeachingService.WallSlotKey("PU"), fb!.Value.SlotKey);
        Assert.Equal(PoseMode.Home2, fb.Value.Mode);
    }

    [Fact]
    public void UpperFace_SlotNotTaught_GoesStraightToHome2()
    {
        var fb = InspectReadyFallback(Ctx("PU", Home, Ready, Home2));
        Assert.NotNull(fb);
        Assert.Null(fb!.Value.SlotKey);
        Assert.Equal(PoseMode.Home2, fb.Value.Mode);
    }

    [Fact]
    public void NoWallCode_UsesLastEntryWall()
    {
        Assert.Null(InspectReadyFallback(Ctx(null, Home, Ready, Home2)));
        Assert.Equal("SU", InspectReadyFallback(Ctx(null, Home, Ready, Home2), lastWall: "SU")!.Value.Code);
    }

    [Theory]
    [InlineData("F")]     // 일반 면 — 속한 모드 없음
    [InlineData("SM")]
    public void SideWall_HasNoFallback(string wall)
        => Assert.Null(InspectReadyFallback(Ctx(wall, Home, Ready, Home2, Home3)));

    [Fact]
    public void Home2NotTaught_HasNoFallback()
        => Assert.Null(InspectReadyFallback(Ctx("PU", Home, Ready, SlotPu)));

    [Fact]
    public void LowerFace_UsesHome3()
        => Assert.Equal(PoseMode.Home3, InspectReadyFallback(Ctx("SL", Home, Ready, Home3))!.Value.Mode);

    /// <summary>현장 자세에서 오프라인 기구학으로 — 작업 준비 위치 경로는 면 쪽으로 휘고(현장 −139mm, 모델 −140mm), 현장 홈 위치_2 는
    /// 작업 준비 위치와 거의 같은 관절이라 그 경로도 막힌다(모델 −151mm) — 이 AMR 에서는 PU 검사 준비 슬롯을 티칭해야 대체 경로가 생긴다.</summary>
    [Fact]
    public void FieldPose_ReadyAndHome2Blocked()
    {
        var kin = Fr5Kinematics.Fr5Wml;
        var env = CobotEnvelope.Default;
        // 벽면 후퇴 전 TCP [-952.3, -191.8, 997.1] → 후 [-653, -171.3, 997.1] (수평 300mm) — 면 = 후퇴 전 점, 바깥 = 후퇴 방향.
        var wallPoint = new[] { -952.3, -191.8, 997.1 };
        var d = new[] { -653 + 952.3, -171.3 + 191.8, 0.0 };
        var n = Math.Sqrt(d[0] * d[0] + d[1] * d[1]);
        var wallOut = new[] { d[0] / n, d[1] / n, 0.0 };
        (double, string) Wall(double[] q) => env.WallClearance(kin, q, wallPoint, wallOut);
        string? Self(double[] q)
        {
            var c = env.Evaluate(kin, q, 0, CobotObstacleZone.FallbackMountYawDeg);
            return c.Collides ? c.ToString() : null;
        }

        var cur = new[] { -0.89, -123.56, 141.29, -63.18, 86.62, -176.12 };
        double[] J(TeachingPosition p) => [p.J1!.Value, p.J2!.Value, p.J3!.Value, p.J4!.Value, p.J5!.Value, p.J6!.Value];
        var toReady = CobotInspectionMoveStep.JointPathBlocker(cur, Near(J(Ready), cur), null, Wall);
        var toHome2 = CobotInspectionMoveStep.JointPathBlocker(cur, Near(J(Home2), cur), Self, Wall);
        output.WriteLine($"→ ready: {toReady ?? "통과"}");
        output.WriteLine($"→ home2: {toHome2 ?? "통과"}");
        Assert.NotNull(toReady);
        Assert.NotNull(toHome2);
    }

    private static double[] Near(double[] target, double[] from)
        => target.Select((v, i) => from[i] + (((v - from[i]) % 360.0 + 540.0) % 360.0 - 180.0)).ToArray();
}
