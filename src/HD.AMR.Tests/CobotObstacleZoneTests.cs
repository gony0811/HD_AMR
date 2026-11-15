using System.Text.Json;
using HD.AMR.App.Service.Motion;
using Xunit;

namespace HD.AMR.Tests;

/// <summary>주행 중 코봇 반경 장애물 감시의 판정부 — 반경 계산·점 판정·옵션·errors 항목.</summary>
public class CobotObstacleZoneTests
{
    private static readonly CobotEnvelope Env = CobotEnvelope.Default;

    /// <summary>홈 위치_2(상부 면 출발 자세)·작업 준비 위치 — 현장 티칭값.</summary>
    private static readonly double[] Home2 = { 0.158, 59.456, -140.907, 80.848, 90.048, -179.696 };
    private static readonly double[] Ready = { -0.391, -9.414, 119.984, -110.76, 90.001, 180.392 };

    private static CobotObstacleZone.Zone Circle(double radiusMm) => new(null, radiusMm, 0);

    [Fact]
    public void HomeFootprint_ReachesForward_ButStaysNarrow()
    {
        var hull = Env.Footprint(Fr5Kinematics.Fr5Wml, Home2, mountYawDeg: 180);
        // 검사 툴이 차체 전단(395)을 훌쩍 넘어 전방 약 1.03 m 까지 나온다 — 차체만 보는 감지로는 모자란다.
        Assert.InRange(hull.Max(p => p.X), 950, 1100);
        // 좌우는 차체 반폭(350) 안팎 — 원(반경 1.1 m)으로 잡으면 벽 옆에서 매번 걸린다.
        Assert.InRange(hull.Max(p => p.Y), 150, 450);
        Assert.InRange(-hull.Min(p => p.Y), 50, 450);   // 팔이 한쪽으로 치우쳐 있어 반대쪽은 좁다
        Assert.InRange(-hull.Min(p => p.X), 0, 300);
        // 볼록 다각형이고 차체 중심을 품는다.
        Assert.Equal(0, CobotEnvelope.DistanceToConvex(hull, 0, 0), 6);
        Assert.Equal(0, CobotEnvelope.DistanceToConvex(hull, 900, 0), 6);
        Assert.True(CobotEnvelope.DistanceToConvex(hull, 1300, 0) > 150);
        Assert.True(CobotEnvelope.DistanceToConvex(hull, 600, 800) > 250);

        // 장착 yaw 가 0 이면 같은 모양이 뒤(−X)를 향한다.
        var back = Env.Footprint(Fr5Kinematics.Fr5Wml, Home2, mountYawDeg: 0);
        Assert.Equal(hull.Max(p => p.X), -back.Min(p => p.X), 3);
    }

    [Fact]
    public void Zone_UsesFootprint_UnlessARadiusIsGiven()
    {
        var auto = new CobotObstacleZone.Options(true, 0, 100, 3, 400);
        var zone = CobotObstacleZone.ResolveZone(auto, Home2, 180, Env, Fr5Kinematics.Fr5Wml);
        Assert.NotNull(zone.Hull);
        Assert.True(zone.Contains(1100, 0));      // 툴 끝(1.03 m) + 여유 0.1 m 안
        Assert.False(zone.Contains(1200, 0));     // 벽을 보고 선 정차(벽 1.2 m)는 안 걸린다
        Assert.False(zone.Contains(0, 800));      // 옆 0.8 m 의 벽도 안 걸린다
        Assert.Contains("전방 1.0", zone.Describe());

        // 관절값·장착 보정이 없으면 홈 위치_2 · 180° 로 본다.
        var fallback = CobotObstacleZone.ResolveZone(auto, null, null, Env, Fr5Kinematics.Fr5Wml);
        Assert.Equal(zone.ReachMm, fallback.ReachMm, 3);

        var circle = CobotObstacleZone.ResolveZone(auto with { RadiusMm = 900 }, Home2, 180, Env, Fr5Kinematics.Fr5Wml);
        Assert.Null(circle.Hull);
        Assert.True(circle.Contains(0, 990));
        Assert.False(circle.Contains(0, 1010));
        Assert.Equal("반경 1.00m", circle.Describe());
    }

    [Fact]
    public void Detect_CountsPointsInsideTheZone_InTheChassisFrame()
    {
        // 로봇은 맵 (10, 5) 에서 +Y 를 본다(rz = 90°). 정면 0.6 m 에 점 3개, 2 m 밖에 벽.
        var rz = Math.PI / 2;
        var pts = new List<(double, double)>
        {
            (10.00, 5.60), (10.02, 5.61), (9.98, 5.62),
            (10, 7), (10.5, 7), (9.5, 7),
        };
        var hit = CobotObstacleZone.Detect(10, 5, rz, pts, Circle(800), Env, minPoints: 3);

        Assert.NotNull(hit);
        Assert.Equal(3, hit!.Count);
        Assert.Equal(600, hit.NearestMm, 0);
        Assert.Equal(600, hit.XMm, 0);       // 차체 +X = 전방
        Assert.Equal(0, hit.YMm, 0);
        Assert.Contains("전방 0.60m", hit.Describe());

        // 점이 모자라면(잡음) 장애물이 아니다.
        Assert.Null(CobotObstacleZone.Detect(10, 5, rz, pts, Circle(800), Env, minPoints: 4));
        // 영역을 줄이면 안 잡힌다.
        Assert.Null(CobotObstacleZone.Detect(10, 5, rz, pts, Circle(550), Env, minPoints: 3));

        // 코봇 외곽 영역 — 정면 0.6 m(툴 아래)는 잡고, 같은 거리의 옆은 잡지 않는다.
        var zone = CobotObstacleZone.ResolveZone(new(true, 0, 100, 3, 400), Home2, 180, Env, Fr5Kinematics.Fr5Wml);
        Assert.NotNull(CobotObstacleZone.Detect(10, 5, rz, pts, zone, Env, 3));
        var side = new List<(double, double)> { (10.60, 5.0), (10.61, 5.02), (10.62, 4.98) };
        Assert.Null(CobotObstacleZone.Detect(10, 5, rz, side, zone, Env, 3));
    }

    [Fact]
    public void Detect_IgnoresPointsOnTheChassisItself_AndTellsLeftFromRight()
    {
        // 차체 상자(전방 395 · 반폭 350) 안쪽 점은 자기 반사 — 버린다.
        var self = new List<(double, double)> { (0.30, 0.10), (0.35, -0.20), (-0.30, 0.30) };
        Assert.Null(CobotObstacleZone.Detect(0, 0, 0, self, Circle(800), Env, 1));

        var left = new List<(double, double)> { (0.0, 0.55) };
        var hit = CobotObstacleZone.Detect(0, 0, 0, left, Circle(800), Env, 1);
        Assert.NotNull(hit);
        Assert.Equal(90, hit!.BearingDeg, 0);
        Assert.Contains("좌측", hit.Describe());
        Assert.Contains("우측", CobotObstacleZone.Detect(0, 0, 0, new List<(double, double)> { (0.0, -0.55) }, Circle(800), Env, 1)!.Describe());
    }

    [Fact]
    public void Options_DefaultOff_AndOutOfRangeFallsBack()
    {
        var none = CobotObstacleZone.Options.From(null, null, null, null, null);
        Assert.False(none.Enabled);
        Assert.Equal((0, CobotObstacleZone.DefaultMarginMm, CobotObstacleZone.DefaultMinPoints, CobotObstacleZone.DefaultPollMs),
            (none.RadiusMm, none.MarginMm, none.MinPoints, none.PollMs));

        var bad = CobotObstacleZone.Options.From(true, -5, 5000, 0, 10);
        Assert.True(bad.Enabled);
        Assert.Equal((0, CobotObstacleZone.DefaultMarginMm, CobotObstacleZone.DefaultMinPoints, CobotObstacleZone.DefaultPollMs),
            (bad.RadiusMm, bad.MarginMm, bad.MinPoints, bad.PollMs));

        var seeds = CobotObstacleZone.SeedDefaults().ToDictionary(s => s.Name, s => s.Value);
        Assert.Equal("false", seeds[CobotObstacleZone.EnabledKey]);
        Assert.Equal(5, seeds.Count);
    }

    [Fact]
    public void ParsesPoseAndLidar_AndBuildsTheWarning()
    {
        using var doc = JsonDocument.Parse(
            """{"x":5.94,"y":13.61,"rz":1.5708,"lidar":[{"x":4.39,"y":12.25},{"x":"bad"},{"x":6.0,"y":14.2}],"fit":0.71}""");
        Assert.True(CobotObstacleZone.TryParsePose(doc.RootElement, out var x, out var y, out var rz, out var lidar));
        Assert.Equal((5.94, 13.61, 1.5708), (x, y, rz));
        Assert.Equal(2, lidar.Count);

        using var noPose = JsonDocument.Parse("""{"lidar":[]}""");
        Assert.False(CobotObstacleZone.TryParsePose(noPose.RootElement, out _, out _, out _, out _));

        var err = CobotObstacleZone.ToError(new CobotObstacleAlert(new CobotObstacleZone.Hit(4, 620, 600, 150), Circle(760), DateTime.UtcNow));
        Assert.Equal("cobotZoneObstacle", err.ErrorType);
        Assert.Equal("WARNING", err.ErrorLevel);
        Assert.Contains("0.62m", err.ErrorDescription);
        Assert.Contains(err.ErrorReferences!, r => r.ReferenceKey == "distanceMm" && r.ReferenceValue == "620");
    }
}
