using HD.AMR.App.Communication;
using HD.AMR.App.Service.Inspection;

namespace HD.AMR.Tests;

/// <summary>작업물 좌표계 기준 경유점 자세 합성(<see cref="WObjAttitude"/>) — 프레임 X 가 툴 X 와 반대(rz0≈−180)인
/// 실물 케이스에서 rz=0 절대 명령 대신 rz0 를 유지하고 틸트 부호를 뒤집는지 검증.</summary>
public class WObjAttitudeTests
{
    // 코봇 페이지 실물 교시: 점1 (-790.3, 58.8, -903.1), 점2 는 베이스 -Y 방향 → 프레임 rz=-90.
    private static readonly double[] Frame = { -790.3, 58.8, -903.1, 0.0, 0.0, -90.0 };

    [Fact]
    public void ToolOppositeToFrameX_KeepsRz180_AndFlipsTilt()
    {
        // 점1 캡처 자세: 툴 +X 가 베이스 +Y(프레임 X 의 반대) → 프레임 기준 rz ≈ ±180.
        var cur = FrameMath.FromFrame(new[] { 0.0, 0.0, 0.0, 0.0, 0.0, -180.0 }, Frame);
        var a = WObjAttitude.FromCurrent(cur, Frame);

        Assert.Equal(180.0, Math.Abs(a.Rz0), 3);
        Assert.Equal(-1.0, a.TiltSign);

        var pose = a.Pose(100, 0, 50, 10, 0);
        Assert.Equal(100.0, pose[0]); Assert.Equal(0.0, pose[1]); Assert.Equal(50.0, pose[2]);
        Assert.Equal(0.0, pose[3]);
        Assert.Equal(-10.0, pose[4], 6);              // 틸트 부호 반전
        Assert.Equal(180.0, Math.Abs(pose[5]), 3);    // rz 유지 — 0 을 명령하지 않는다
    }

    [Fact]
    public void ToolAlignedWithFrameX_IsIdentityLike()
    {
        var cur = FrameMath.FromFrame(new[] { 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 }, Frame);
        var a = WObjAttitude.FromCurrent(cur, Frame);

        Assert.Equal(0.0, a.Rz0, 6);
        Assert.Equal(1.0, a.TiltSign);
        var pose = a.Pose(1, 2, 3, 7.5, 90);
        Assert.Equal(7.5, pose[4], 6);
        Assert.Equal(90.0, pose[5], 6);
    }

    [Fact]
    public void 절대모드_역방향_검사는_광축_둘레_180도로_시작_비틀림을_유지한다()
    {
        // 시작 자세 툴 X 가 프레임 X(검사 이동 방향)와 반대 — 절대 rz=0 이면 J6 가 180° 뒤집힌다.
        var cur = FrameMath.FromFrame(new[] { 0.0, 0.0, 0.0, 0.0, 0.0, -180.0 }, Frame);
        var a = WObjAttitude.FromCurrent(cur, Frame);
        Assert.Equal(180.0, a.AbsoluteTwistDeg);

        var absolute = FrameMath.FromFrame(new[] { 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 }, Frame);
        var kept = FrameMath.PoseToMatrix(a.KeepTwist(absolute));
        var mCur = FrameMath.PoseToMatrix(cur);
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 4; c++)
                Assert.Equal(mCur[r, c], kept[r, c], 6);   // 시작 자세와 같은 회전·위치
    }

    [Fact]
    public void 절대모드_정방향_검사는_보정하지_않는다()
    {
        var cur = FrameMath.FromFrame(new[] { 0.0, 0.0, 0.0, 0.0, 0.0, 5.0 }, Frame);
        var a = WObjAttitude.FromCurrent(cur, Frame);
        var p = new[] { 1.0, 2.0, 3.0, 4.0, 5.0, 6.0 };

        Assert.Equal(0.0, a.AbsoluteTwistDeg);
        Assert.Same(p, a.KeepTwist(p));
    }

    [Fact]
    public void Identity_PassesThrough()
    {
        var pose = WObjAttitude.Identity.Pose(1, 2, 3, -4, 5);
        Assert.Equal(new[] { 1.0, 2.0, 3.0, 0.0, -4.0, 5.0 }, pose);
    }

    [Fact]
    public void ComposedPose_RoundTripsThroughFrame_ToTaughtAttitude()
    {
        // rz0 유지 pose 를 베이스로 되돌리면 교시 자세(툴 Z=프레임 Z, X 반대)와 같은 회전이어야 한다.
        var taught = FrameMath.FromFrame(new[] { 0.0, 0.0, 0.0, 0.0, 0.0, -180.0 }, Frame);
        var a = WObjAttitude.FromCurrent(taught, Frame);
        var back = FrameMath.FromFrame(a.Pose(0, 0, 0, 0, 0), Frame);
        var mA = FrameMath.PoseToMatrix(taught);
        var mB = FrameMath.PoseToMatrix(back);
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                Assert.Equal(mA[r, c], mB[r, c], 6);
    }
}
