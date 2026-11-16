using HD.AMR.App.Data.Entities;
using HD.AMR.App.Service.Sequence;
using HD.AMR.App.Service.Sequence.Steps;
using static HD.AMR.App.Service.Sequence.Steps.SequenceEntry;

namespace HD.AMR.Tests;

/// <summary>
/// 실기 2026-11-16 10:29 — cancelOrder 로 홈 18mm 앞(홈 → 작업 준비 위치 이동 도중)에 멈춘 팔을 다음 지령의 주행 전 복귀가
/// "벽 앞 자세"로 보고 300mm 벽면 후퇴시켜, 전완이 차체 중앙 리프트 기둥에 충돌했다(코봇 결함 main=4, sub=2).
/// 관절값은 그날 로그·티칭 값 그대로다.
/// </summary>
public class RetractSelfCollisionTests
{
    private static readonly double[] HomeJ = { -0.389, 9.754, 137.425, -147.369, 90.006, -179.608 };
    private static readonly double[] ReadyJ = { -0.391, -9.414, 119.984, -110.760, 90.001, -179.607 };
    private static readonly double[] FaultJ = { -0.4, 23.07, 140.26, -163.89, 90.02, -179.61 };   // 결함 발생 순간
    private static readonly double[] WallFrontJ = { 26.07, 26.29, 26.75, -53.04, 116.07, -179.61 };   // 같은 날 SM 벽 앞 자세

    private static TeachingPosition Taught(string key, double[] j) => new()
    {
        Key = key, Name = key,
        X = 0, Y = 0, Z = 0, Rx = 0, Ry = 0, Rz = 0,
        J1 = j[0], J2 = j[1], J3 = j[2], J4 = j[3], J5 = j[4], J6 = j[5],
    };

    private static SequenceContext Ctx() => new()
    {
        Positions = new[] { Taught("home", HomeJ), Taught("ready", ReadyJ) }.ToDictionary(p => p.Key, p => p),
    };

    private static double[] Between(double[] a, double[] b, double t) => a.Zip(b, (x, y) => x + (y - x) * t).ToArray();

    [Theory]
    [InlineData(0.05)]   // 그날 멈춘 자리 — 홈에서 조금 출발한 곳
    [InlineData(0.5)]
    [InlineData(0.95)]
    public void 홈과_작업_준비_위치_사이에서_멈춘_팔은_경로_위로_본다(double t)
    {
        var stopped = Between(HomeJ, ReadyJ, t);
        Assert.False(IsAt(stopped, Ctx(), "home"));    // 경유점 판정(0.5°)에는 들지 않는다
        Assert.False(IsAt(stopped, Ctx(), "ready"));
        Assert.Equal("홈 ↔ 작업 준비 위치", RouteSegmentOf(stopped, Ctx(), PoseMode.Home));
    }

    [Fact]
    public void 벽_앞에_뻗은_자세는_경로_위가_아니다()
    {
        Assert.Null(RouteSegmentOf(WallFrontJ, Ctx(), PoseMode.Home));
        // 경로에서 한 축만 4° 벗어나도 경로 위가 아니다(허용 3°)
        var off = Between(HomeJ, ReadyJ, 0.5);
        off[0] += 4;
        Assert.Null(RouteSegmentOf(off, Ctx(), PoseMode.Home));
        // 홈을 지나쳐 더 접힌 자세(충돌 자세)는 구간 밖이다
        Assert.Null(RouteSegmentOf(FaultJ, Ctx(), PoseMode.Home));
    }

    [Fact]
    public void 관절_직선까지의_편차()
    {
        Assert.Equal(0, JointSegmentDeviation(Between(HomeJ, ReadyJ, 0.3), HomeJ, ReadyJ), 6);
        Assert.Equal(0, JointSegmentDeviation(HomeJ, HomeJ, ReadyJ), 6);
        // J6 가 한 바퀴 감긴 표현(+180.4° ↔ −179.6°)이어도 같은 자세로 본다
        var wound = (double[])HomeJ.Clone();
        wound[5] += 360;
        Assert.Equal(0, JointSegmentDeviation(wound, HomeJ, ReadyJ), 6);
        // 두 끝이 같으면 그 점까지의 거리
        Assert.Equal(5, JointSegmentDeviation(new[] { 5.0, 0, 0, 0, 0, 0 }, new double[6], new double[6]), 6);
    }

    [Fact]
    public void 그날_충돌한_자세는_후퇴_경로_검증이_막는다()
    {
        var start = Between(HomeJ, ReadyJ, 0.05);
        var why = RetractSelfBlocker(start, FaultJ);
        Assert.NotNull(why);
        Assert.Contains("리프트 기둥", why);

        // 홈에서 충돌 자세로 접어 가는 도중에 이미 걸린다 — 닿기 전에 멈춘다
        var blockedAt = Enumerable.Range(1, 20).Select(i => i / 20.0)
            .First(t => RetractSelfBlocker(start, Between(HomeJ, FaultJ, t)) is not null);
        Assert.True(blockedAt < 0.8, $"t={blockedAt}");
    }

    [Fact]
    public void 벽에서_물러나는_정상_후퇴는_막지_않는다()
    {
        // 벽 앞 자세와 그 자리에서 작업 준비 위치 쪽으로 물러난 자세 — 차체·기둥에서 멀다
        Assert.Null(RetractSelfBlocker(WallFrontJ, WallFrontJ));
        Assert.Null(RetractSelfBlocker(WallFrontJ, ReadyJ));
        // 홈 자세는 툴이 차체 위 39mm — 여유(30mm) 안이라 막지 않는다
        Assert.Null(RetractSelfBlocker(HomeJ, HomeJ));
    }

    [Fact]
    public void 목표에_닿지_못하고_멈춘_이동은_완료가_아니다()
    {
        Assert.Null(ArrivalShortfall(ReadyJ, ReadyJ, "작업 준비 위치"));
        Assert.Null(ArrivalShortfall(ReadyJ.Select(v => v + 0.4).ToArray(), ReadyJ, "작업 준비 위치"));

        var why = ArrivalShortfall(Between(HomeJ, ReadyJ, 0.05), ReadyJ, "작업 준비 위치");   // 그날: "이동 완료"로 기록됐던 자리
        Assert.NotNull(why);
        Assert.Contains("도중에 멈췄습니다", why);
    }
}
