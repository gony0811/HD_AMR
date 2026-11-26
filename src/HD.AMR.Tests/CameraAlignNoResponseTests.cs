using HD.AMR.App.Service;
using HD.AMR.App.Service.Inspection;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>③ 카메라 거리 정렬 깊이 무응답 넘김 + 묶음 TASK 순회 실패 뒤 연속 이동 기준 유지.</summary>
public class CameraAlignNoResponseTests
{
    private static DepthDistanceMoveResult NoResponse(double before, double after, double cmd, double? moved) =>
        new(false, after, null, $"광축 {cmd:+0.#}mm 이동했는데 거리가 {before:0}→{after:0}mm 로 기대만큼 변하지 않았습니다",
            NoResponse: true, CommandedMm: cmd, ActualMovedMm: moved);

    // 실기 2026-11-26 13:04 T L4-R09C24 #4 — 415→560mm, 코봇 +114.6mm. 이동 후 값이 기대(680−114.6)와 맞아 다시 접근한다.
    [Fact]
    public void 로봇이_움직였고_이동후_값이_도면과_맞으면_다시_접근한다()
    {
        var w = CameraAlignStep.AcceptNoResponse(NoResponse(415, 560, 114.6, 114.6), 680, 300, 300);
        Assert.NotNull(w);
        Assert.Equal(565.4, w!.Value.ExpectedAfterMm, 6);
        Assert.StartsWith("⚠", w.Value.Message);
    }

    [Theory]
    [InlineData(560, 50.0)]     // 로봇이 거의 안 움직임
    [InlineData(260, 114.6)]    // 이동 후 가장 가까운 물체가 목표 − 20mm 보다 가까움
    public void 조건을_못_맞추면_종전대로_실패(double after, double moved)
        => Assert.Null(CameraAlignStep.AcceptNoResponse(NoResponse(415, after, 114.6, moved), 680, 300, 300));

    [Fact]
    public void 도면_기대가_없거나_벗어나면_넘기지_않는다()
    {
        Assert.Null(CameraAlignStep.AcceptNoResponse(NoResponse(415, 560, 114.6, 114.6), null, 300, 300));
        Assert.Null(CameraAlignStep.AcceptNoResponse(NoResponse(415, 990, 114.6, 114.6), 680, 300, 300));
    }

    [Fact]
    public void 깊이_무응답이_아닌_실패는_넘기지_않는다()
        => Assert.Null(CameraAlignStep.AcceptNoResponse(
            new DepthDistanceMoveResult(false, 560, null, "접근 이동 실패 (rc=112)"), 680, 300, 300));

    // 실기 2026-11-26 10:12 — 팔로워가 비전 0/25 로 순회 실패해도 다음 TASK 는 그 자리에서 이어 간다.
    [Fact]
    public void 묶음_순회_실패면_연속_이동_기준만_지금_자리로()
    {
        var frame = new[] { 1.0, 2, 3, 0, 0, 0 };
        var start = new[] { 4.0, 5, 6 };
        var oldEnd = new[] { 130.7, 579, 1284, 180, -1, 18.3 };
        var now = new[] { -207.7, 467, 1277.7, 180, -1, 18.3 };

        var next = WeldInspectionOrchestrator.AfterFailedBundleTask((frame, start, oldEnd), "inspectionRun", now);
        Assert.NotNull(next);
        Assert.Equal(frame, next!.Value.Frame);
        Assert.Equal(start, next.Value.Start);
        Assert.Equal(now, next.Value.EndTcp);
    }

    [Fact]
    public void 순회_외_실패나_공유_좌표계_없으면_그대로()
    {
        var shared = (new[] { 1.0, 2, 3, 0, 0, 0 }, (double[]?)null, (double[]?)null);
        var now = new[] { 0.0, 0, 0, 0, 0, 0 };
        Assert.Null(WeldInspectionOrchestrator.AfterFailedBundleTask(shared, "cobotInspection", now));
        Assert.Null(WeldInspectionOrchestrator.AfterFailedBundleTask(null, "inspectionRun", now));
        Assert.Null(WeldInspectionOrchestrator.AfterFailedBundleTask(shared, "inspectionRun", null));
    }
}
