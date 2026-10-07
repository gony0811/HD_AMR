using HD.AMR.App.Communication;
using HD.AMR.App.Service.Inspection;
using HD.AMR.App.Service.Sequence;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>
/// ② 검사위치 이동의 순수 계산부 — 후퇴 거리 결정과 u/v 합성, 접근 앵커 선택.
/// </summary>
public class InspectionMoveMathTests
{
    private static SequenceContext Ctx(double cameraTarget = 400, double? acsStandoff = null,
                                       double u = 0, double v = 0,
                                       InspectionMoveDirection dir = InspectionMoveDirection.Horizontal)
        => new()
        {
            CameraTargetDistanceMm = cameraTarget,
            StandoffMmOverride = acsStandoff,
            InspectionOffsetU = u,
            InspectionOffsetV = v,
            InspectionDirection = dir,
        };

    // ── 후퇴 거리 ───────────────────────────────────────────────────

    [Fact]
    public void 후퇴_거리는_카메라_목표거리를_쓴다()
    {
        Assert.Equal(300, CobotInspectionMoveStep.ResolveApproachDistanceMm(Ctx(cameraTarget: 300)), 6);
    }

    [Fact]
    public void ACS_standoffMm_은_후퇴_거리에_영향을_주지_않는다()
    {
        // 2026-09-25 회귀를 직접 잠근다 — ACS 가 800 을 보내도 ②는 카메라 목표거리로 물러난다.
        var c = Ctx(cameraTarget: 300, acsStandoff: 800);

        Assert.Equal(300, CobotInspectionMoveStep.ResolveApproachDistanceMm(c), 6);
    }

    [Fact]
    public void 카메라_목표거리가_0_이면_전역_기본값으로_떨어진다()
    {
        Assert.Equal(SeamBaseTransform.DefaultStandoffMm,
                     CobotInspectionMoveStep.ResolveApproachDistanceMm(Ctx(cameraTarget: 0)), 6);
    }

    // ── u/v 합성 ───────────────────────────────────────────────────

    [Fact]
    public void UvOffset_은_수평이면_RZ가_0_이다()
    {
        var o = CobotInspectionMoveStep.UvOffset(Ctx(u: 10, v: -20));

        Assert.Equal(10, o[0], 9);
        Assert.Equal(-20, o[1], 9);
        Assert.Equal(0, o[5], 9);
    }

    [Fact]
    public void UvOffset_은_수직이어도_RZ를_싣지_않는다()
    {
        // 광축 둘레 회전은 J6 절대각 맞춤(AlignTwistToJ6Async)이 담당한다.
        var o = CobotInspectionMoveStep.UvOffset(Ctx(dir: InspectionMoveDirection.Vertical));

        Assert.Equal(0, o[5], 9);
    }

    [Theory]
    [InlineData(InspectionMoveDirection.Vertical, 180.0)]
    [InlineData(InspectionMoveDirection.Horizontal, 90.0)]
    public void J6_목표는_검사방향으로만_정해진다(InspectionMoveDirection dir, double expected)
    {
        Assert.Equal(expected, CobotInspectionMoveStep.TargetJ6Deg(Ctx(dir: dir)), 9);
    }

    [Fact]
    public void IK_해는_기준_관절_쪽으로_감긴다()
    {
        var r = FairinoRpcClient.UnwrapToward(
            new[] { 0.0, 0, 0, 0, 0, -179.0 }, new[] { 0.0, 0, 0, 0, 0, 180.4 });

        Assert.Equal(181.0, r[5], 9);
    }

    [Fact]
    public void ComposeUvTarget_은_툴프레임_합성과_같다()
    {
        // ③ 게이트가 이 값을 기준으로 판정하므로 MoveJByToolOffsetAsync 내부식과 갈라지면 안 된다.
        var anchor = new[] { 100.0, 200.0, 300.0, 0.0, 0.0, 30.0 };
        var c = Ctx(u: 15, v: 25);

        var composed = CobotInspectionMoveStep.ComposeUvTarget(anchor, c);
        var expected = FrameMath.FromFrame(CobotInspectionMoveStep.UvOffset(c), anchor);

        for (var i = 0; i < 6; i++) Assert.Equal(expected[i], composed[i], 9);
    }

    [Fact]
    public void ComposeUvTarget_은_오프셋이_0_이면_앵커_그대로다()
    {
        var anchor = new[] { 100.0, 200.0, 300.0, 10.0, 20.0, 30.0 };

        var composed = CobotInspectionMoveStep.ComposeUvTarget(anchor, Ctx());

        for (var i = 0; i < 6; i++) Assert.Equal(anchor[i], composed[i], 6);
    }

    // ── 하이브리드 접근 앵커 선택 (위치=seam, 자세=계산법선/티칭) ─────

    private static readonly double[] TaughtPose = { 1, 2, 3, 40, 50, 60 };
    private static readonly double[] SeamBase = { 700, 800, 900 };
    private static readonly double[] ComputedPose = { 700, 800, 900, 11, 22, 33 };

    [Fact]
    public void 앵커는_측위_없으면_티칭_포즈로_폴백한다()
    {
        // seamBase == null → 위치·자세 모두 티칭(종전 동작).
        var (anchor, used) = CobotInspectionMoveStep.BuildApproachAnchor(
            TaughtPose, seamBase: null, computedPose: ComputedPose, useComputed: true);

        Assert.False(used);
        for (var i = 0; i < 6; i++) Assert.Equal(TaughtPose[i], anchor[i], 9);
    }

    [Fact]
    public void 앵커는_토글_ON_이고_계산자세_있으면_위치는_seam_자세는_계산법선()
    {
        var (anchor, used) = CobotInspectionMoveStep.BuildApproachAnchor(
            TaughtPose, SeamBase, ComputedPose, useComputed: true);

        Assert.True(used);
        Assert.Equal(SeamBase[0], anchor[0], 9);   // 위치 = seam 접근점
        Assert.Equal(SeamBase[1], anchor[1], 9);
        Assert.Equal(SeamBase[2], anchor[2], 9);
        Assert.Equal(ComputedPose[3], anchor[3], 9);   // 자세 = 계산 법선
        Assert.Equal(ComputedPose[4], anchor[4], 9);
        Assert.Equal(ComputedPose[5], anchor[5], 9);
    }

    [Fact]
    public void 앵커는_토글_OFF_면_위치는_seam_자세는_티칭()
    {
        var (anchor, used) = CobotInspectionMoveStep.BuildApproachAnchor(
            TaughtPose, SeamBase, ComputedPose, useComputed: false);

        Assert.False(used);
        Assert.Equal(SeamBase[0], anchor[0], 9);   // 위치는 seam
        Assert.Equal(TaughtPose[3], anchor[3], 9);   // 자세는 티칭
        Assert.Equal(TaughtPose[4], anchor[4], 9);
        Assert.Equal(TaughtPose[5], anchor[5], 9);
    }

    [Fact]
    public void 앵커는_토글_ON_이라도_계산자세_없으면_자세는_티칭()
    {
        // wall_code 미지정/미정의 → computedPose == null. 위치는 seam, 자세는 티칭 폴백.
        var (anchor, used) = CobotInspectionMoveStep.BuildApproachAnchor(
            TaughtPose, SeamBase, computedPose: null, useComputed: true);

        Assert.False(used);
        Assert.Equal(SeamBase[0], anchor[0], 9);
        Assert.Equal(TaughtPose[3], anchor[3], 9);
        Assert.Equal(TaughtPose[5], anchor[5], 9);
    }
}
