using HD.AMR.App.Service;
using HD.AMR.App.Service.Motion;

namespace HD.AMR.Tests;

/// <summary>
/// 리치 맵. 역기구학을 델리게이트로 주입받으므로 하드웨어 없이 판정 순서·좌표 규약·출력을 검증한다.
///
/// 가상 로봇 모델: 코봇 BASE 가 차체 원점 위 1650mm(텔레스코픽 0), 어깨는 BASE 위 180mm.
/// <b>TCP 가 어깨에서 2000mm 안이면 IK 가 풀린다</b> — FR5-WML 리치(1900)+여유를 흉내 낸 구 모델이다.
/// </summary>
public class ReachMapTests
{
    private static readonly double[] Mount = { 0, 0, 1650, 0, 0, 0 };

    private static ReachMapRobot Robot(double minFlange = 350) => new(
        MountAtHome: Mount,
        ToolLengthMm: 370,
        Limits: new PostureLimits(
            JointMinDeg: Enumerable.Repeat(-175.0, 6).ToArray(),
            JointMaxDeg: Enumerable.Repeat(175.0, 6).ToArray(),
            MinFlangeReachMm: minFlange),
        OpticalAxis: ToolAxisDir.MinusZ);

    private static ReachMapSpec Spec(string wall, double[] xs, double[]? ys = null, double[]? zs = null, double[]? hs = null)
        => new(new[] { wall }, hs ?? new[] { 0.0 }, xs, ys ?? new[] { 0.0 }, zs ?? new[] { 1830.0 });

    /// <summary>TCP 가 어깨에서 <paramref name="reach"/> 안이면 고정 관절각을 돌려준다. J6 은 일부러 한계 밖.</summary>
    private static Func<double[], CancellationToken, Task<double[]?>> SphereIk(double reach = 2000, List<double[]>? queried = null)
        => (pose, _) =>
        {
            queried?.Add(pose);
            var dz = pose[2] - 180.0;
            var d = Math.Sqrt(pose[0] * pose[0] + pose[1] * pose[1] + dz * dz);
            return Task.FromResult<double[]?>(d <= reach ? new[] { 0.0, -60, 90, -120, 40, 300 } : null);
        };

    [Fact]
    public void 범위는_끝값을_포함한다()
    {
        Assert.Equal(new[] { 800.0, 1000, 1200 }, ReachMapSpec.Range(800, 1200, 200));
        Assert.Equal(new[] { 500.0 }, ReachMapSpec.Range(500, 900, 0));
    }

    [Fact]
    public async Task 벽에_가까우면_뻗음부족_적당하면_도달_멀면_IK실패()
    {
        var r = await ReachMap.BuildAsync(Spec("SM", new[] { 900.0, 1300, 2600 }), Robot(), SphereIk());

        Assert.Equal(ReachStatus.FlangeShort, r.Cells[0].Status);   // 900 − 400 − 370 = 130 < 350
        Assert.Equal(130, r.Cells[0].FlangeReachMm, 6);
        Assert.Equal(ReachStatus.Ok, r.Cells[1].Status);
        Assert.Equal(ReachStatus.IkFailed, r.Cells[2].Status);
    }

    [Fact]
    public async Task 수직벽의_x_는_정차_이격과_같다()
    {
        // BASE 가 차체 원점 위에 있으므로 면까지 법선거리 = x.
        var r = await ReachMap.BuildAsync(Spec("SM", new[] { 1200.0 }), Robot(), SphereIk());
        Assert.Equal(1200, r.Cells[0].NormalDistanceMm, 6);
    }

    [Fact]
    public async Task J6_은_판정에서_뺀다()
    {
        // 가짜 IK 의 J6=300° 는 ±175 한계 밖이지만 운영은 J6 을 따로 절대각으로 맞추므로 도달이어야 한다.
        var r = await ReachMap.BuildAsync(Spec("SM", new[] { 1300.0 }), Robot(), SphereIk());
        Assert.Equal(ReachStatus.Ok, r.Cells[0].Status);
        Assert.Equal(0, r.Cells[0].FinalJointsDeg![5]);
    }

    [Fact]
    public async Task 바닥은_높이를_훑지_않는다()
    {
        var spec = Spec("B", new[] { 800.0, 1000 }, zs: new[] { 0.0, 1000, 2000 });
        var r = await ReachMap.BuildAsync(spec, Robot(), SphereIk());

        Assert.Equal(2, spec.CellCount);
        Assert.Equal(2, r.Cells.Count);
        Assert.All(r.Cells, c => Assert.Equal(0, c.Zmm));
    }

    [Fact]
    public async Task 너무_멀면_IK_를_부르지_않는다()
    {
        var queried = new List<double[]>();
        var r = await ReachMap.BuildAsync(Spec("SM", new[] { 3500.0 }), Robot(), SphereIk(queried: queried));

        Assert.Equal(ReachStatus.OutOfRange, r.Cells[0].Status);
        Assert.Empty(queried);
        Assert.Equal(0, r.IkCalls);
    }

    [Fact]
    public async Task IK_예외는_사유로_남는다()
    {
        var r = await ReachMap.BuildAsync(Spec("SM", new[] { 1300.0 }), Robot(),
            (_, _) => throw new InvalidOperationException("역기구학(GetInverseKin) 실패 (errcode=38). 상세"));

        Assert.Equal(ReachStatus.IkFailed, r.Cells[0].Status);
        Assert.Contains("errcode=38", r.Cells[0].Reason);
    }

    [Fact]
    public async Task 요약은_도달이_가장_많은_x_를_짚는다()
    {
        var spec = Spec("SM", new[] { 900.0, 1300, 2600 }, ys: new[] { -400.0, 0, 400 });
        var r = await ReachMap.BuildAsync(spec, Robot(), SphereIk());

        Assert.Contains("최다 도달 x=1300mm (3/3)", r.SummaryText());
        var csv = r.ToCsv().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2 + r.Cells.Count, csv.Length);   // 주석 1줄 + 머리글 1줄 + 셀
    }

    [Fact]
    public async Task 미정의_wall_code_는_거부한다()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            ReachMap.BuildAsync(Spec("ZZ", new[] { 1300.0 }), Robot(), SphereIk()));
    }
}
