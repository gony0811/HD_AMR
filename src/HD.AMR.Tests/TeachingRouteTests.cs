using HD.AMR.App.Data.Entities;
using HD.AMR.App.Service;

namespace HD.AMR.Tests;

/// <summary>Teaching 이동의 홈 ↔ 홈 2 자동 경유(2026-11-09) — 바로 MoveJ 하면 J5 가 AMR 차체와 충돌한다.</summary>
public class TeachingRouteTests
{
    private static TeachingPosition Taught(string key, params double[] j) => new()
    {
        Key = key, Name = key,
        X = 0, Y = 0, Z = 0, Rx = 0, Ry = 0, Rz = 0,
        J1 = j[0], J2 = j[1], J3 = j[2], J4 = j[3], J5 = j[4], J6 = j[5],
    };

    private static readonly TeachingPosition Home = Taught("home", 0, -90, 90, 0, 90, 0);
    private static readonly TeachingPosition Ready = Taught("ready", 0, -80, 100, -20, 90, 0);
    private static readonly TeachingPosition H15 = Taught("home15", 20, -95, 40, 0, 90, 0);
    private static readonly TeachingPosition H16 = Taught("home16", 40, -100, -10, 20, 90, 0);
    private static readonly TeachingPosition Home2 = Taught("home2", 60, -100, -40, 30, 90, 0);
    private static readonly TeachingPosition[] All = { Home, Ready, H15, H16, Home2 };

    private static double[] J(TeachingPosition p) => new[] { p.J1!.Value, p.J2!.Value, p.J3!.Value, p.J4!.Value, p.J5!.Value, p.J6!.Value };
    private static string[] Keys(TeachingMove.RoutePlan plan) => plan.Steps.Select(s => s.Key).ToArray();

    [Fact]
    public void HomeToHome2_GoesViaReadyAnd15And16()
    {
        var plan = TeachingMove.PlanRoute(Home2, J(Home), All);
        Assert.Null(plan.Error);
        Assert.Equal(new[] { "ready", "home15", "home16", "home2" }, Keys(plan));
    }

    [Fact]
    public void Home2ToHome_GoesInReverse()
    {
        var plan = TeachingMove.PlanRoute(Home, J(Home2), All);
        Assert.Null(plan.Error);
        Assert.Equal(new[] { "home16", "home15", "ready", "home" }, Keys(plan));
    }

    // 현재 위치는 허용오차(2°) 안이면 그 위치로 본다 — 조그로 살짝 어긋나도 경유.
    [Fact]
    public void SlightlyOffHome_StillRoutes()
    {
        var cur = J(Home); cur[0] += 1.5; cur[5] -= 1.0;
        Assert.Equal(new[] { "ready", "home15", "home16", "home2" }, Keys(TeachingMove.PlanRoute(Home2, cur, All)));
    }

    // ready 는 선택 경유점 — 미티칭이면 건너뛴다.
    [Fact]
    public void ReadyNotTaught_Skipped()
    {
        var plan = TeachingMove.PlanRoute(Home2, J(Home), new[] { Home, H15, H16, Home2 });
        Assert.Equal(new[] { "home15", "home16", "home2" }, Keys(plan));
    }

    // 홈 1.5·1.6 은 필수 — 없으면 움직이지 않는다(바로 가면 충돌).
    [Fact]
    public void MissingWaypoint_Refuses()
    {
        var plan = TeachingMove.PlanRoute(Home2, J(Home), new[] { Home, Ready, H15, Home2 });
        Assert.NotNull(plan.Error);
        Assert.Contains("홈 위치_1.6", plan.Error);
    }

    // 홈·홈 2 가 아닌 곳(경유점)에서 홈 2 는 바로 — 경유는 홈 ↔ 홈 2 사이만.
    [Fact]
    public void FromWaypoint_IsDirect()
    {
        Assert.Equal(new[] { "home2" }, Keys(TeachingMove.PlanRoute(Home2, J(H15), All)));
        Assert.Equal(new[] { "home2" }, Keys(TeachingMove.PlanRoute(Home2, J(Ready), All)));
        Assert.Equal(new[] { "home" }, Keys(TeachingMove.PlanRoute(Home, J(H16), All)));
    }

    // 경유점 목표(작업 준비·1.5·1.6)는 홈에서도 바로.
    [Fact]
    public void WaypointTarget_IsDirect()
    {
        Assert.Equal(new[] { "ready" }, Keys(TeachingMove.PlanRoute(Ready, J(Home), All)));
        Assert.Equal(new[] { "home16" }, Keys(TeachingMove.PlanRoute(H16, J(Home), All)));
    }

    // 검사 자세 등 임의 자세에서 홈 2·홈 은 거부 없이 바로(2026-11-09 운영안).
    [Fact]
    public void OffRoute_IsDirect()
    {
        var inspectPose = new double[] { 5, -85, 95, -10, 90, 0 };
        var p2 = TeachingMove.PlanRoute(Home2, inspectPose, All);
        Assert.Null(p2.Error);
        Assert.Equal(new[] { "home2" }, Keys(p2));
        Assert.Equal(new[] { "home" }, Keys(TeachingMove.PlanRoute(Home, inspectPose, All)));
    }

    // 허용오차(2°) 밖이면 홈으로 보지 않는다.
    [Fact]
    public void JustOutsideTolerance_IsDirect()
    {
        var cur = J(Home); cur[1] += 2.5;
        Assert.Equal(new[] { "home2" }, Keys(TeachingMove.PlanRoute(Home2, cur, All)));
    }

    // 경로 밖 목표(검사 준비 등)와 작업물 좌표계 위치는 종전대로 목표 하나.
    [Fact]
    public void NonRouteTarget_Unchanged()
    {
        var wall = Taught(TeachingService.WallSlotKey("F"), 10, -90, 80, 0, 90, 0);
        Assert.Equal(new[] { wall.Key }, Keys(TeachingMove.PlanRoute(wall, J(Home2), All)));
    }
}
