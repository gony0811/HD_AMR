using HD.AMR.App.Service.Motion;

namespace HD.AMR.Tests;

/// <summary>
/// 자세 허용 범위 판정 — 툴 장착으로 좁아진 관절 범위와 특이점 여유를 하나의 스칼라(여유 [도])로 합치는
/// 로직. 이 판정이 <see cref="ApproachPlanner"/> 의 목적함수라, 여기가 틀리면 "쓸 수 있는 자세" 자체가
/// 틀린다.
/// </summary>
public class PostureLimitsTests
{
    private static PostureLimits Limits(double wrist = 15, double elbow = 8, double lo = -175, double hi = 175)
        => new(
            JointMinDeg: Enumerable.Repeat(lo, 6).ToArray(),
            JointMaxDeg: Enumerable.Repeat(hi, 6).ToArray(),
            WristMarginDeg: wrist,
            ElbowMarginDeg: elbow);

    private static double[] Joints(double j1 = 0, double j2 = -30, double j3 = 90, double j4 = -60, double j5 = 90, double j6 = 0)
        => new[] { j1, j2, j3, j4, j5, j6 };

    [Fact]
    public void 여유_있는_자세는_통과한다()
    {
        var m = Limits().Evaluate(Joints());

        Assert.True(m.Feasible);
        Assert.True(m.MarginDeg > 0);
        Assert.Equal(90, m.WristDeg, 6);
    }

    [Fact]
    public void 손목_특이점_근방은_거부된다()
    {
        // 현장 관측값 — 이동 중 J5 가 90° 에서 25.5° 까지 떨어졌다. 여유 15° 기준으로는 아직 통과,
        // 여유 30° 기준(툴이 큰 경우)으로는 거부돼야 한다.
        var ok = Limits(wrist: 15).Evaluate(Joints(j5: 25.5));
        var ng = Limits(wrist: 30).Evaluate(Joints(j5: 25.5));

        Assert.True(ok.Feasible);
        Assert.False(ng.Feasible);
        Assert.Contains("손목 특이점", ng.Limiting);
    }

    [Fact]
    public void J5_부호는_상관없다_절대값으로_본다()
    {
        Assert.Equal(
            Limits().Evaluate(Joints(j5: 40)).MarginDeg,
            Limits().Evaluate(Joints(j5: -40)).MarginDeg,
            6);
    }

    [Fact]
    public void 툴로_좁힌_관절한계가_먼저_걸린다()
    {
        // J4 만 툴 간섭으로 ±90° 로 묶은 경우.
        var limits = Limits() with
        {
            JointMinDeg = new[] { -175.0, -175, -160, -90, -175, -175 },
            JointMaxDeg = new[] { 175.0, 175, 160, 90, 175, 175 },
        };

        var m = limits.Evaluate(Joints(j4: -120));

        Assert.False(m.Feasible);
        Assert.Contains("J4", m.Limiting);
    }

    [Fact]
    public void 팔꿈치가_완전히_펴지면_거부된다()
    {
        var m = Limits().Evaluate(Joints(j3: 3));

        Assert.False(m.Feasible);
        Assert.Contains("팔꿈치", m.Limiting);
    }

    [Fact]
    public void 어깨_반경은_목표가_베이스_축에_가까울_때만_본다()
    {
        var limits = Limits() with { ShoulderRadiusMinMm = 180 };

        Assert.True(limits.Evaluate(Joints(), planarRadiusMm: 900).Feasible);

        var near = limits.Evaluate(Joints(), planarRadiusMm: 50);
        Assert.False(near.Feasible);
        Assert.Contains("어깨", near.Limiting);
    }

    [Fact]
    public void 이동량_상한을_넘는_해는_거부된다()
    {
        var limits = Limits() with { MaxJointTravelDeg = 120 };
        var from = Joints();

        Assert.True(limits.Evaluate(Joints(j1: 60), fromJointsDeg: from).Feasible);

        var far = limits.Evaluate(Joints(j1: 160), fromJointsDeg: from);
        Assert.False(far.Feasible);
        Assert.Contains("이동량", far.Limiting);
    }

    [Fact]
    public void min_max_가_뒤집혀_저장돼도_받아준다()
    {
        var swapped = new PostureLimits(
            JointMinDeg: new[] { 175.0, 175, 160, 175, 175, 175 },
            JointMaxDeg: new[] { -175.0, -175, -160, -175, -175, -175 });

        Assert.True(swapped.Evaluate(Joints()).Feasible);
    }

    // ── 관절 경로 안전성 — MoveJ 단조성의 핵심 ──────────────────────

    [Fact]
    public void J5_부호가_같으면_관절경로는_특이점을_지나지_않는다()
    {
        // 관절 보간은 각 축이 두 끝값 사이에서 단조로 변한다 — 양 끝이 같은 부호면 도중에 0 이 없다.
        Assert.True(Limits().JointPathWithin(Joints(j5: 90), Joints(j5: 30)));
    }

    [Fact]
    public void J5_부호가_바뀌면_관절경로가_특이점을_통과한다()
    {
        // 양 끝은 각각 여유가 충분해도(|J5| = 40, 45) 중간에서 0 을 지난다 — MoveL 이 아니라 MoveJ 여도 위험.
        Assert.False(Limits().JointPathWithin(Joints(j5: 40), Joints(j5: -45)));
    }

    [Fact]
    public void 끝점이_한계_밖이면_경로도_불가()
    {
        Assert.False(Limits().JointPathWithin(Joints(j5: 90), Joints(j5: 5)));
    }
}
