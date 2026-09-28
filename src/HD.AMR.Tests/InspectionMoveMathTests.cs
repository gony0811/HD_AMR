using HD.AMR.App.Communication;
using HD.AMR.App.Service.Inspection;
using HD.AMR.App.Service.Sequence;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>
/// ② 검사위치 이동의 순수 계산부 — 후퇴 거리 결정과 u/v 합성, 그리고 ③ 게이트 기준 선택.
/// 이 세 가지가 2026-09-25 회귀(ACS 자동 실행이 ③에서 전부 막힘)의 진원지였다.
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
    public void UvOffset_은_수직이면_RZ가_마이너스90_이다()
    {
        var o = CobotInspectionMoveStep.UvOffset(Ctx(dir: InspectionMoveDirection.Vertical));

        Assert.Equal(-90, o[5], 9);
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

    // ── ③ 게이트 기준 선택 (세 분기) ───────────────────────────────

    private static readonly double[] Teaching = { 1, 2, 3, 4, 5, 6 };

    private static Task<double[]> TeachingFactory() => Task.FromResult(Teaching);

    [Fact]
    public void 게이트는_②가_남긴_지령_목표를_최우선으로_쓴다()
    {
        var commanded = new[] { 9.0, 8, 7, 6, 5, 4 };
        var c = Ctx();
        c.Bag[WeldSequenceSupport.InspectTargetPoseBagKey] = commanded;

        var (expected, basis, skip) = CameraAlignStep.ResolveGateReference(c, TeachingFactory);

        Assert.Null(skip);
        Assert.Equal(commanded, expected!.Result);
        Assert.Contains("지령", basis);
    }

    [Fact]
    public void 게이트는_UI_단독_실행이면_티칭_기준으로_폴백한다()
    {
        // Bag 없음 + seamStartW 없음 = ②를 건너뛰고 ③만 누르는 기존 세미오토 용법.
        var (expected, basis, skip) = CameraAlignStep.ResolveGateReference(Ctx(), TeachingFactory);

        Assert.Null(skip);
        Assert.Equal(Teaching, expected!.Result);
        Assert.Contains("티칭", basis);
    }

    [Fact]
    public void 게이트는_ACS_경로에_지령_기록이_없으면_실패가_아니라_생략한다()
    {
        // 티칭 기준 재계산은 seam 접근점과 다르므로 비교할 수 없다 — 검증 불가를 실패로 바꾼 것이
        // 바로 2026-09-25 회귀였다.
        var c = Ctx();
        c.SeamStartW = new[] { 6.5, 14.42, 1.1 };

        var (expected, _, skip) = CameraAlignStep.ResolveGateReference(c, TeachingFactory);

        Assert.NotNull(skip);
        Assert.Null(expected);
    }
}
