using HD.AMR.App.Communication;
using HD.AMR.App.Models;
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
        double stroke = 0, double zDatum = 0, double standoff = 0, double? facing = null,
        string? wallCode = null, ToolAxisDir optical = ToolAxisDir.PlusZ, double spinDeg = 0)
        => new(seamStart, seamEnd, amrX, amrY, yawRad, Mount, stroke, zDatum, standoff, facing,
               wallCode, optical, spinDeg);

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

    // ── 면 법선 · TOOL 자세 (wall_code) ──────────────────────────────

    [Fact]
    public void SurfaceNormal_VerticalWall_IsHorizontal_AlongFacing()
    {
        var n = SeamBaseTransform.SurfaceNormal(WallCodes.Find("SM")!.Orientation, 0.0);

        Assert.Equal(1.0, n[0], 9);   // facing=0 → +X 로 벽을 향한다
        Assert.Equal(0.0, n[1], 9);
        Assert.Equal(0.0, n[2], 9);
    }

    [Theory]
    [InlineData("B", -1.0)]   // 바닥 — 연직 아래
    [InlineData("T", 1.0)]    // 천장 — 연직 위
    public void SurfaceNormal_FloorCeiling_IsVertical_RegardlessOfFacing(string code, double expectedZ)
    {
        var n = SeamBaseTransform.SurfaceNormal(WallCodes.Find(code)!.Orientation, 1.234);

        Assert.Equal(0.0, n[0], 9);
        Assert.Equal(0.0, n[1], 9);
        Assert.Equal(expectedZ, n[2], 9);
    }

    [Theory]
    [InlineData("SL", -1)]   // 하부 챔퍼 — 45° 아래
    [InlineData("PU", +1)]   // 상부 챔퍼 — 45° 위
    public void SurfaceNormal_Chamfers_AreTiltedFortyFive(string code, int zSign)
    {
        var n = SeamBaseTransform.SurfaceNormal(WallCodes.Find(code)!.Orientation, 0.0);

        const double r = 0.70710678118654752;
        Assert.Equal(r, n[0], 9);
        Assert.Equal(0.0, n[1], 9);
        Assert.Equal(zSign * r, n[2], 9);
    }

    [Fact]
    public void Resolve_Floor_BacksOffUpward_NotHorizontally()
    {
        // 바닥 용접선은 수평으로 물러나면 면에서 떨어지지 않는다 — +Z 로 떠야 한다.
        var t = SeamBaseTransform.Resolve(
            Input(new[] { 13.0, 5.0, 0.0 }, yawRad: 0, standoff: 400, wallCode: "B"));

        Assert.Equal(13000.0, t.ApproachMapMm[0], 6);
        Assert.Equal(5000.0, t.ApproachMapMm[1], 6);
        Assert.Equal(400.0, t.ApproachMapMm[2], 6);   // 바닥에서 400mm 위
        Assert.Equal(SurfaceOrientation.Floor, t.Surface);
    }

    [Fact]
    public void Resolve_Ceiling_BacksOffDownward()
    {
        var t = SeamBaseTransform.Resolve(
            Input(new[] { 13.0, 5.0, 3.0 }, yawRad: 0, standoff: 400, wallCode: "T"));

        Assert.Equal(3000.0 - 400.0, t.ApproachMapMm[2], 6);
    }

    [Fact]
    public void Resolve_LowerChamfer_BacksOffDiagonally()
    {
        var t = SeamBaseTransform.Resolve(
            Input(new[] { 13.0, 5.0, 1.0 }, yawRad: 0, standoff: 400, wallCode: "SL"));

        const double r = 0.70710678118654752;
        Assert.Equal(13000.0 - 400.0 * r, t.ApproachMapMm[0], 6);
        Assert.Equal(1000.0 + 400.0 * r, t.ApproachMapMm[2], 6);   // 아래를 보는 면 → 위로 물러난다
    }

    [Fact]
    public void Resolve_WallCode_ProducesPose_WithOpticalAxisAlongNormal()
    {
        var t = SeamBaseTransform.Resolve(
            Input(new[] { 13.0, 5.0, 1.0 }, yawRad: 0, standoff: 400, wallCode: "SM"));

        Assert.NotNull(t.TargetPoseBase);
        // 목표 자세의 툴 +Z 축(BASE 기준)을 맵으로 되돌리면 면 법선과 일치해야 한다.
        var axisMap = ToolAxisInMap(t, t.TargetPoseBase!, ToolAxisDir.PlusZ);
        AssertVectorEqual(t.SurfaceNormalMap, axisMap);
    }

    [Fact]
    public void Resolve_FloorPose_PointsToolDown()
    {
        var t = SeamBaseTransform.Resolve(
            Input(new[] { 13.0, 5.0, 0.0 }, yawRad: 0, standoff: 400, wallCode: "B"));

        var axisMap = ToolAxisInMap(t, t.TargetPoseBase!, ToolAxisDir.PlusZ);
        AssertVectorEqual(new[] { 0.0, 0.0, -1.0 }, axisMap);
    }

    [Fact]
    public void Resolve_NonDefaultOpticalAxis_IsTheAxisAligned()
    {
        // 광축이 툴 −Y 로 설정된 설비 — 정렬되는 축도 −Y 여야 한다.
        var t = SeamBaseTransform.Resolve(
            Input(new[] { 13.0, 5.0, 1.0 }, yawRad: 0, standoff: 400, wallCode: "SM",
                  optical: ToolAxisDir.MinusY));

        AssertVectorEqual(t.SurfaceNormalMap, ToolAxisInMap(t, t.TargetPoseBase!, ToolAxisDir.MinusY));
    }

    [Fact]
    public void Resolve_Spin_RotatesAboutNormal_KeepingOpticalAxis()
    {
        var a = SeamBaseTransform.Resolve(
            Input(new[] { 13.0, 5.0, 1.0 }, yawRad: 0, standoff: 400, wallCode: "SM"));
        var b = SeamBaseTransform.Resolve(
            Input(new[] { 13.0, 5.0, 1.0 }, yawRad: 0, standoff: 400, wallCode: "SM", spinDeg: 90));

        // 광축은 그대로 법선을 향하고,
        AssertVectorEqual(a.SurfaceNormalMap, ToolAxisInMap(b, b.TargetPoseBase!, ToolAxisDir.PlusZ));
        // 툴 X 축은 90° 돌아 원래 X 와 직교해야 한다.
        var ax = ToolAxisInMap(a, a.TargetPoseBase!, ToolAxisDir.PlusX);
        var bx = ToolAxisInMap(b, b.TargetPoseBase!, ToolAxisDir.PlusX);
        Assert.Equal(0.0, ax[0] * bx[0] + ax[1] * bx[1] + ax[2] * bx[2], 6);
    }

    [Fact]
    public void Resolve_NoWallCode_LeavesPoseNull_AndWarns()
    {
        var t = SeamBaseTransform.Resolve(Input(new[] { 13.0, 5.0, 1.0 }, yawRad: 0, standoff: 400));

        Assert.Null(t.TargetPoseBase);
        Assert.Null(t.Surface);
        Assert.Contains(t.Notes, n => n.Contains("wall_code 미지정"));
    }

    [Fact]
    public void Resolve_UnknownWallCode_Warns_AndFallsBackToHorizontal()
    {
        var t = SeamBaseTransform.Resolve(
            Input(new[] { 13.0, 5.0, 1.0 }, yawRad: 0, standoff: 400, wallCode: "W03"));

        Assert.Null(t.TargetPoseBase);
        Assert.Contains(t.Notes, n => n.Contains("미정의 wall_code"));
        Assert.Equal(12600.0, t.ApproachMapMm[0], 6);
    }

    // ── 벽 정면 방향 검증 ────────────────────────────────────────────

    [Fact]
    public void Resolve_FacingFarFromSeamAzimuth_Warns()
    {
        // 실기 사례: AMR 이 벽과 나란히(+Y) 서 있는데 theta 없이 AMR yaw 로 폴백 → 법선이 90° 돌아간다.
        // 용접선은 BASE 에서 +X 쪽(방위 21°)에 있는데 가정한 벽 정면은 89° — 이 어긋남을 잡아야 한다.
        var t = SeamBaseTransform.Resolve(
            Input(new[] { 7.000, 13.920, 1.000 }, amrX: 5.688, amrY: 13.420,
                  yawRad: 89.49 * Math.PI / 180.0, standoff: 400, wallCode: "PM"));

        Assert.Contains(t.Notes, n => n.Contains("어긋납니다"));
    }

    [Fact]
    public void Resolve_FacingTowardSeam_DoesNotWarn()
    {
        // 같은 배치에서 벽 정면(노드 theta)을 +X 로 주면 경고가 사라진다.
        var t = SeamBaseTransform.Resolve(
            Input(new[] { 7.000, 13.920, 1.000 }, amrX: 5.688, amrY: 13.420,
                  yawRad: 89.49 * Math.PI / 180.0, standoff: 400, wallCode: "PM", facing: 0.0));

        Assert.DoesNotContain(t.Notes, n => n.Contains("어긋납니다"));
    }

    [Fact]
    public void Resolve_FloorCeiling_SkipFacingCheck()
    {
        // 바닥은 법선이 연직이라 방위각 비교가 의미 없다 — 같은 배치라도 경고하지 않는다.
        var t = SeamBaseTransform.Resolve(
            Input(new[] { 7.000, 13.920, 0.000 }, amrX: 5.688, amrY: 13.420,
                  yawRad: 89.49 * Math.PI / 180.0, standoff: 400, wallCode: "B"));

        Assert.DoesNotContain(t.Notes, n => n.Contains("어긋납니다"));
    }

    [Theory]
    [InlineData(ToolAxisDir.PlusZ, 90.0)]    // 광축 +Z — spin 부호가 툴 RZ 와 같다
    [InlineData(ToolAxisDir.MinusZ, -90.0)]  // 광축 −Z — spin 부호가 툴 RZ 와 반대다
    public void Resolve_Spin_EqualsToolRzCcw90(ToolAxisDir optical, double spinDeg)
    {
        // "툴 좌표계 RZ 반시계 90°" 는 광축 +Z 면 spin +90, −Z 면 spin −90 으로 얻는다.
        // 두 경우 모두 spin 0 대비 상대 회전(툴 프레임 기준)이 Rz(+90) 이어야 한다.
        var at0 = SeamBaseTransform.Resolve(
            Input(new[] { 13.0, 5.0, 1.0 }, yawRad: 0, standoff: 400, wallCode: "SM", optical: optical));
        var spun = SeamBaseTransform.Resolve(
            Input(new[] { 13.0, 5.0, 1.0 }, yawRad: 0, standoff: 400, wallCode: "SM",
                  optical: optical, spinDeg: spinDeg));

        var rel = RelativeToolRotation(at0.TargetPoseBase!, spun.TargetPoseBase!);

        // Rz(+90) = [[0,-1,0],[1,0,0],[0,0,1]]
        double[,] rz90 = { { 0, -1, 0 }, { 1, 0, 0 }, { 0, 0, 1 } };
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
                Assert.Equal(rz90[i, j], rel[i, j], 6);
    }

    /// <summary>두 pose 사이의 상대 회전을 <b>앞 pose 의 툴 프레임 기준</b>으로 반환 — R_fromᵀ·R_to.</summary>
    private static double[,] RelativeToolRotation(double[] poseFrom, double[] poseTo)
    {
        var a = FrameMath.PoseToMatrix(poseFrom);
        var b = FrameMath.PoseToMatrix(poseTo);
        var r = new double[3, 3];
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
            {
                double sum = 0;
                for (var k = 0; k < 3; k++) sum += a[k, i] * b[k, j];
                r[i, j] = sum;
            }
        return r;
    }

    /// <summary>목표 pose(BASE)의 툴축을 맵 프레임 단위벡터로 되돌린다 — 법선 정렬 검증용.
    /// 자세 검증 테스트는 모두 AMR (12,5) yaw 0 을 쓰므로 그 값으로 T_W_A 를 재구성한다.</summary>
    private static double[] ToolAxisInMap(SeamBaseTarget t, double[] poseBase, ToolAxisDir axis)
    {
        var amrPose = MapCalibration.AmrPoseToMmDeg(12.0, 5.0, 0.0);
        var tWB = FrameMath.Multiply(FrameMath.PoseToMatrix(amrPose), FrameMath.PoseToMatrix(t.MountUsed));
        var rTool = FrameMath.PoseToMatrix(poseBase);

        var a = new double[3];
        a[(int)axis / 2] = (int)axis % 2 == 0 ? 1.0 : -1.0;

        // 툴축 → BASE → 맵 (회전만)
        var inBase = new double[3];
        for (var i = 0; i < 3; i++)
            inBase[i] = rTool[i, 0] * a[0] + rTool[i, 1] * a[1] + rTool[i, 2] * a[2];

        var inMap = new double[3];
        for (var i = 0; i < 3; i++)
            inMap[i] = tWB[i, 0] * inBase[0] + tWB[i, 1] * inBase[1] + tWB[i, 2] * inBase[2];
        return inMap;
    }

    private static void AssertVectorEqual(double[] expected, double[] actual)
    {
        for (var i = 0; i < 3; i++)
            Assert.Equal(expected[i], actual[i], 6);
    }
}
