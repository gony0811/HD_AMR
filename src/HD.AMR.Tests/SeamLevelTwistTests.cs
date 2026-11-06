using HD.AMR.App.Communication;
using HD.AMR.App.Models;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Inspection;
using HD.AMR.App.Service.Sequence;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>
/// ② 용접선 영상 가로 정렬 — 계산한 광축 둘레 회전을 적용하면 용접선이 영상 X 축과 나란해지는지(영상 Y 성분 0),
/// 광축 방향·위치는 바뀌지 않는지, 진행축이 영상 가로로 바뀌는지를 고정한다.
/// </summary>
public class SeamLevelTwistTests
{
    private static double[] Unit(ToolAxisDir a)
    {
        var v = new double[3];
        v[(int)a / 2] = (int)a % 2 == 0 ? 1.0 : -1.0;
        return v;
    }

    /// <summary>BASE 벡터 d 의 툴 프레임 성분을 영상 축(ex, ey)에 사영.</summary>
    private static (double X, double Y) InImage(double[] pose, double[] d, ToolAxisDir imageX, ToolAxisDir imageY)
    {
        var m = FrameMath.PoseToMatrix(pose);
        var t = new double[3];
        for (var j = 0; j < 3; j++) t[j] = m[0, j] * d[0] + m[1, j] * d[1] + m[2, j] * d[2];
        var ex = Unit(imageX);
        var ey = Unit(imageY);
        return (t[0] * ex[0] + t[1] * ex[1] + t[2] * ex[2], t[0] * ey[0] + t[1] * ey[1] + t[2] * ey[2]);
    }

    private static double[] Normalize(double[] v)
    {
        var n = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
        return new[] { v[0] / n, v[1] / n, v[2] / n };
    }

    public static IEnumerable<object[]> Cases()
    {
        // 현장 매핑(광축 −Z) · 기본 매핑(광축 +Z) · 왼손계 매핑, 여러 자세·용접선 기울기
        var poses = new[]
        {
            new[] { 600.0, 100.0, 400.0, 90.0, 0.0, 90.0 },
            new[] { 550.0, -200.0, 800.0, -95.0, 12.0, -170.0 },
            new[] { 400.0, 300.0, 1200.0, 180.0, 0.0, 33.0 },   // 천장 — 광축 연직
        };
        var dirs = new[]
        {
            new[] { 0.0, 1.0, 0.0 },
            new[] { 0.3, 0.9, 0.25 },
            new[] { 1.0, -0.4, 0.0 },
            new[] { 0.0, 0.2, 1.0 },
        };
        var maps = new[]
        {
            (ToolAxisDir.MinusZ, ToolAxisDir.PlusX, ToolAxisDir.PlusY),
            (ToolAxisDir.PlusZ, ToolAxisDir.PlusX, ToolAxisDir.PlusY),
            (ToolAxisDir.MinusZ, ToolAxisDir.PlusY, ToolAxisDir.PlusX),
            (ToolAxisDir.PlusZ, ToolAxisDir.MinusY, ToolAxisDir.PlusX),
        };
        foreach (var p in poses)
            foreach (var d in dirs)
                foreach (var (u, x, y) in maps)
                    yield return new object[] { p, d, u, x, y };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Twist_MakesSeamHorizontalInImage(double[] pose, double[] dir, ToolAxisDir optical, ToolAxisDir imageX, ToolAxisDir imageY)
    {
        var d = Normalize(dir);
        var phi = CobotInspectionMoveStep.SeamLevelTwistDeg(pose, d, optical, imageX, imageY);
        if (phi is null) return;   // 용접선이 광축과 거의 나란한 조합 — 정렬 대상 아님

        foreach (var twist in new[] { phi.Value, phi.Value + 180.0 })
        {
            var rotated = CobotInspectionMoveStep.RotateAboutToolAxis(pose, optical, twist);
            var (x, y) = InImage(rotated, d, imageX, imageY);
            Assert.True(Math.Abs(y) < 1e-6, $"twist {twist:0.###}° 후 영상 Y 성분 {y:0.######} (X {x:0.###})");
            Assert.True(Math.Abs(x) > 0.1);

            // 위치·광축 방향은 그대로
            for (var i = 0; i < 3; i++) Assert.Equal(pose[i], rotated[i], 6);
            var m0 = FrameMath.PoseToMatrix(pose);
            var m1 = FrameMath.PoseToMatrix(rotated);
            var col = (int)optical / 2;
            for (var i = 0; i < 3; i++) Assert.Equal(m0[i, col], m1[i, col], 6);
        }
    }

    [Fact]
    public void Twist_AlreadyHorizontal_IsZero()
    {
        var pose = new[] { 600.0, 0.0, 500.0, 90.0, 0.0, 90.0 };
        var m = FrameMath.PoseToMatrix(pose);
        // 툴 +X 의 BASE 방향 = 영상 X(+X 매핑)
        var d = new[] { m[0, 0], m[1, 0], m[2, 0] };
        var phi = CobotInspectionMoveStep.SeamLevelTwistDeg(pose, d, ToolAxisDir.MinusZ, ToolAxisDir.PlusX, ToolAxisDir.PlusY);
        Assert.NotNull(phi);
        Assert.True(Math.Abs(phi!.Value) < 1e-6);
    }

    [Fact]
    public void Twist_OverlappingAxisMap_ReturnsNull()
    {
        var pose = new[] { 600.0, 0.0, 500.0, 90.0, 0.0, 90.0 };
        Assert.Null(CobotInspectionMoveStep.SeamLevelTwistDeg(
            pose, new[] { 0.0, 1.0, 0.0 }, ToolAxisDir.MinusZ, ToolAxisDir.PlusZ, ToolAxisDir.PlusY));
    }

    [Fact]
    public void Twist_SeamAlongOpticalAxis_ReturnsNull()
    {
        var pose = new[] { 600.0, 0.0, 500.0, 0.0, 0.0, 0.0 };   // 툴 Z = BASE Z
        Assert.Null(CobotInspectionMoveStep.SeamLevelTwistDeg(
            pose, new[] { 0.0, 0.0, 1.0 }, ToolAxisDir.MinusZ, ToolAxisDir.PlusX, ToolAxisDir.PlusY));
    }

    [Theory]
    [InlineData(InspectionMoveDirection.Horizontal, false, WeldProgressAxis.Vertical)]
    [InlineData(InspectionMoveDirection.Vertical, false, WeldProgressAxis.Horizontal)]
    [InlineData(InspectionMoveDirection.Horizontal, true, WeldProgressAxis.Horizontal)]
    [InlineData(InspectionMoveDirection.Vertical, true, WeldProgressAxis.Horizontal)]
    public void ProgressAxis_IsImageHorizontal_WhenSeamLeveled(
        InspectionMoveDirection direction, bool leveled, WeldProgressAxis expected)
    {
        var c = new SequenceContext { InspectionDirection = direction, SeamLeveledInImage = leveled };
        Assert.Equal(expected, WeldSequenceSupport.GetProgressAxis(c));
    }

    [Fact]
    public void SeamBaseTransform_ExposesSeamDirectionInBase()
    {
        // 맵 +Y 방향 수평 용접선, AMR yaw 0 · 장착 yaw 0 → BASE 에서도 +Y.
        var input = new SeamBaseInput(
            new[] { 2.0, 1.0, 1.5 }, new[] { 2.0, 3.0, 1.5 },
            0.0, 0.0, 0.0, new double[] { 0, 0, 500, 0, 0, 0 }, 0, 0, 400, 0.0, "PM");
        var t = SeamBaseTransform.Resolve(input);
        Assert.NotNull(t.SeamDirectionBase);
        Assert.Equal(0.0, t.SeamDirectionBase![0], 6);
        Assert.Equal(1.0, t.SeamDirectionBase[1], 6);
        Assert.Equal(0.0, t.SeamDirectionBase[2], 6);

        Assert.Null(SeamBaseTransform.Resolve(input with { SeamEndW = null }).SeamDirectionBase);
    }

    // 벽을 보는 자세: 툴 −Z(광축) = BASE +X(벽 쪽), 툴 +X = BASE +Z(위), 툴 +Y = BASE +Y. 영상 X→툴+X, Y→툴+Y.
    private static readonly double[] WallPose = { 600.0, 0.0, 500.0, 0.0, -90.0, 0.0 };

    [Theory]
    [InlineData(0.0, 0.0, 1.0, WeldProgressAxis.Horizontal, 0.0)]    // 수직 용접선 → 영상 X(툴 X=수직)를 따라 진행
    [InlineData(0.0, 1.0, 0.0, WeldProgressAxis.Vertical, 90.0)]     // 수평 용접선 → 영상 Y(툴 Y=수평)
    [InlineData(0.0, 0.5, 0.8660254, WeldProgressAxis.Horizontal, 30.0)]
    [InlineData(0.0, 0.8660254, 0.5, WeldProgressAxis.Vertical, 60.0)]
    [InlineData(0.0, 0.0, -1.0, WeldProgressAxis.Horizontal, 0.0)]   // 방향 부호와 무관
    public void ProgressAxisFromSeam_FollowsSeamInImage(double dx, double dy, double dz, WeldProgressAxis expected, double tilt)
    {
        var optical = CobotInspectionMoveStep.ToolAxisInBase(WallPose, ToolAxisDir.MinusZ);
        Assert.Equal(1.0, optical[0], 6);   // 자세 가정 확인: 광축 = BASE +X

        var rec = new SeamOrientationRecord(new[] { dx, dy, dz }, optical, "TM");
        var axis = WeldSequenceSupport.ProgressAxisFromSeam(WallPose, rec,
            ToolAxisDir.MinusZ, ToolAxisDir.PlusX, ToolAxisDir.PlusY, out var t, out var reject);
        Assert.Null(reject);
        Assert.Equal(expected, axis);
        Assert.Equal(tilt, t, 3);
    }

    [Fact]
    public void ProgressAxisFromSeam_HorizontalSeamLeveledByTwist_IsHorizontal()
    {
        // 수평 용접선을 가로로 돌린 자세(광축 둘레 90°)면 영상 가로 → Horizontal.
        var leveled = CobotInspectionMoveStep.RotateAboutToolAxis(WallPose, ToolAxisDir.MinusZ, 90.0);
        var rec = new SeamOrientationRecord(new[] { 0.0, 1.0, 0.0 },
            CobotInspectionMoveStep.ToolAxisInBase(WallPose, ToolAxisDir.MinusZ), "TM");
        var axis = WeldSequenceSupport.ProgressAxisFromSeam(leveled, rec,
            ToolAxisDir.MinusZ, ToolAxisDir.PlusX, ToolAxisDir.PlusY, out var t, out _);
        Assert.Equal(WeldProgressAxis.Horizontal, axis);
        Assert.Equal(0.0, t, 3);
    }

    [Fact]
    public void ProgressAxisFromSeam_OtherFace_ReturnsNull()
    {
        var rec = new SeamOrientationRecord(new[] { 0.0, 0.0, 1.0 }, new[] { 0.0, 1.0, 0.0 }, "PM");   // 광축 90° 다름
        Assert.Null(WeldSequenceSupport.ProgressAxisFromSeam(WallPose, rec,
            ToolAxisDir.MinusZ, ToolAxisDir.PlusX, ToolAxisDir.PlusY, out _, out var reject));
        Assert.Contains("다른 면", reject);
    }

    [Fact]
    public void ArmTravel_IgnoresJ6()
    {
        var from = new[] { 10.0, -90.0, 100.0, 60.0, 90.0, -180.0 };
        var to = new[] { 12.0, -91.0, 103.0, 58.0, 90.0, -290.0 };
        Assert.Equal(3.0, CobotInspectionMoveStep.MaxArmTravelExceptJ6(from, to), 9);
    }
}
