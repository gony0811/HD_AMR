using HD.AMR.App.Service;
using HD.AMR.App.Service.Inspection;
using HD.AMR.App.Service.Sequence;

namespace HD.AMR.Tests;

/// <summary>
/// 용접선(맵 좌표) → 코봇 BASE 환산. <see cref="MapCalibration.BasePointToMap"/> 과의 왕복,
/// standoff 방향, 텔레스코픽 스트로크 반영, z 기준 보정을 고정한다.
/// </summary>
public class SeamBaseTransformTests
{
    // 완전 하강 기준 T_A_B — 차체 앞쪽 300mm, 좌측 120mm, 높이 700mm, yaw 15°.
    private static readonly double[] Mount = { 300, 120, 700, 0, 0, 15 };

    private static SeamBaseInput Input(
        double[] seamStart, double[]? seamEnd = null,
        double amrX = 12.0, double amrY = 5.0, double yawRad = 1.5707963267948966,
        double stroke = 0, double zDatum = 0, double standoff = 0, double? facing = null)
        => new(seamStart, seamEnd, amrX, amrY, yawRad, Mount, stroke, zDatum, standoff, facing);

    [Fact]
    public void Resolve_RoundTripsThroughBasePointToMap()
    {
        // standoff 0 이면 접근점 = seam 점 — BASE 로 내린 값을 다시 맵으로 올리면 원점으로 돌아와야 한다.
        var input = Input(new[] { 12.51, 5.98, 1.42 });

        var t = SeamBaseTransform.Resolve(input);

        var amrPose = MapCalibration.AmrPoseToMmDeg(input.AmrXm, input.AmrYm, input.AmrYawRad);
        var (bx, by) = MapCalibration.BasePointToMap(amrPose, t.MountUsed, t.SeamStartBaseMm);

        Assert.Equal(12510.0, bx, 6);
        Assert.Equal(5980.0, by, 6);
        Assert.Equal(t.SeamStartBaseMm, t.ApproachBaseMm);
    }

    [Fact]
    public void Resolve_Standoff_BacksOffAlongWallNormal()
    {
        // AMR 은 (12,5)에서 +X(θ=0)로 벽을 바라보고, 용접선은 1m 앞 벽면 위에 있다.
        // 접근점은 seam 에서 −X 로 400mm 물러난 지점이어야 한다.
        var input = Input(new[] { 13.0, 5.0, 1.0 }, yawRad: 0, standoff: 400);

        var t = SeamBaseTransform.Resolve(input);

        Assert.Equal(13000.0 - 400.0, t.ApproachMapMm[0], 6);
        Assert.Equal(5000.0, t.ApproachMapMm[1], 6);
        Assert.Equal(t.SeamStartMapMm[2], t.ApproachMapMm[2], 6);

        // 코봇 BASE 에서 봐도 seam 보다 400mm 가까워야 한다.
        var dSeam = Norm(t.SeamStartBaseMm);
        var dApproach = Norm(t.ApproachBaseMm);
        Assert.True(dApproach < dSeam, $"접근점이 더 멀다 (seam={dSeam:0.0}, approach={dApproach:0.0})");
    }

    [Fact]
    public void Resolve_NodeTheta_OverridesAmrYaw()
    {
        // AMR yaw 는 +Y 를 보고 있지만 노드 theta 는 +X — standoff 는 노드 theta 를 따라야 한다.
        var t = SeamBaseTransform.Resolve(
            Input(new[] { 13.0, 5.0, 1.0 }, yawRad: Math.PI / 2, standoff: 400, facing: 0));

        Assert.Equal(12600.0, t.ApproachMapMm[0], 6);
        Assert.Equal(5000.0, t.ApproachMapMm[1], 6);
    }

    [Fact]
    public void Resolve_TelescopicStroke_RaisesBaseAndLowersTargetZ()
    {
        var down = SeamBaseTransform.Resolve(Input(new[] { 12.0, 5.0, 1.0 }));
        var up = SeamBaseTransform.Resolve(Input(new[] { 12.0, 5.0, 1.0 }, stroke: 500));

        // BASE 가 500mm 올라갔으므로 같은 점의 BASE 기준 z 는 500mm 낮아진다.
        Assert.Equal(down.SeamStartBaseMm[2] - 500.0, up.SeamStartBaseMm[2], 6);
        Assert.Equal(Mount[2] + 500.0, up.MountUsed[2], 6);
        // 수평 성분은 스트로크와 무관.
        Assert.Equal(down.SeamStartBaseMm[0], up.SeamStartBaseMm[0], 6);
        Assert.Equal(down.SeamStartBaseMm[1], up.SeamStartBaseMm[1], 6);
    }

    [Fact]
    public void Resolve_ZDatumOffset_SubtractsLevelFloorHeight()
    {
        // L2(층 바닥 3.2m) 벽면의 바닥 기준 1.3m 용접선 = 도면 전역 z 4.5m.
        var t = SeamBaseTransform.Resolve(Input(new[] { 12.0, 5.0, 4.5 }, zDatum: 3200));

        Assert.Equal(1300.0, t.SeamStartMapMm[2], 6);
        Assert.DoesNotContain(t.Notes, n => n.Contains("z 보정 0"));
    }

    [Fact]
    public void Resolve_WithoutZDatumOffset_WarnsAboutDrawingGlobalZ()
    {
        var t = SeamBaseTransform.Resolve(Input(new[] { 12.0, 5.0, 4.5 }));

        Assert.Equal(4500.0, t.SeamStartMapMm[2], 6);
        Assert.Contains(t.Notes, n => n.Contains("z 보정 0"));
    }

    [Fact]
    public void Resolve_UncalibratedMount_Warns()
    {
        var input = new SeamBaseInput(
            new[] { 12.0, 5.0, 1.0 }, null, 12.0, 5.0, 0, new double[6], 0, 0, 400, null);

        var t = SeamBaseTransform.Resolve(input);

        Assert.Contains(t.Notes, n => n.Contains("T_A_B"));
    }

    [Fact]
    public void Resolve_SmallStandoff_Warns()
    {
        var t = SeamBaseTransform.Resolve(Input(new[] { 12.0, 5.0, 1.0 }, standoff: 50));

        Assert.Contains(t.Notes, n => n.Contains("간섭"));
    }

    [Fact]
    public void Resolve_SeamEnd_ReportsInspectionDirection()
    {
        // 벽 정면 +X(θ=0) → 벽면 수평 탄젠트는 ±Y. seam 이 +Y 로 800mm 면 수평.
        var horizontal = SeamBaseTransform.Resolve(
            Input(new[] { 12.0, 5.0, 1.0 }, new[] { 12.0, 5.8, 1.0 }, yawRad: 0));
        Assert.Equal(InspectionMoveDirection.Horizontal, horizontal.Direction);

        // seam 이 z 로 800mm 면 수직.
        var vertical = SeamBaseTransform.Resolve(
            Input(new[] { 12.0, 5.0, 1.0 }, new[] { 12.0, 5.0, 1.8 }, yawRad: 0));
        Assert.Equal(InspectionMoveDirection.Vertical, vertical.Direction);
    }

    [Fact]
    public void Resolve_NoSeamEnd_LeavesDirectionNull()
    {
        var t = SeamBaseTransform.Resolve(Input(new[] { 12.0, 5.0, 1.0 }));

        Assert.Null(t.Direction);
        Assert.Null(t.DirectionReason);
    }

    private static double Norm(double[] p) => Math.Sqrt(p[0] * p[0] + p[1] * p[1] + p[2] * p[2]);
}
