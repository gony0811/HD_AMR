using HD.AMR.App.Communication;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>
/// 연속 이동 자세 = 실측 작업물 좌표계 면 법선 — 실기 2026-11-17 16:31 B L1-R04C13 #3→#4 에서 모델 법선(수평)으로 맞춰
/// 툴 Rx/Ry −1.3/+1.7° → 0/0 으로 돌았다가 ⑱ 첫 점에서 다시 기울어졌다. 값은 그 현장 로그.
/// </summary>
public class ChainMeasuredAttitudeTests
{
    // #3 교시로 만든 내부 작업물 좌표계(#4 시작점으로 옮긴 원점) — RPY [1.32, −1.67, −0.68]
    private static readonly double[] Frame = { -179.9, -1107.3, -1011.6, 1.32, -1.67, -0.68 };
    private static readonly double[] V3End = { -180.78, -1108.07, -977.75, -1.32, 1.67, 179.32 };
    private static readonly double[] Up = { 0, 0, 1 };   // 바닥(B) 모델 면 바깥 법선

    private static double[] OpticalAxis(double[] pose)
    {
        var m = FrameMath.PoseToMatrix(pose);
        return new[] { -m[0, 2], -m[1, 2], -m[2, 2] };   // 광축 = 툴 −Z
    }

    [Fact]
    public void 실측_법선은_모델_쪽_부호로_약간_기울어진다()
    {
        var n = CobotInspectionMoveStep.MeasuredWallOut(Frame, Up);
        Assert.NotNull(n);
        Assert.True(n![2] > 0.99);
        var deg = CobotInspectionMoveStep.AngleDeg(n, Up);
        Assert.InRange(deg, 1.5, 3.0);
    }

    [Fact]
    public void 직전_종료가_이미_실측_자세면_자세를_바꾸지_않는다()
    {
        var n = CobotInspectionMoveStep.MeasuredWallOut(Frame, Up)!;
        var o = CobotInspectionMoveStep.AlignOpticalAxisToDirection(V3End, new[] { -n[0], -n[1], -n[2] }, ToolAxisDir.MinusZ);

        Assert.True(CobotInspectionMoveStep.AngleDeg(OpticalAxis(o), OpticalAxis(V3End)) < 0.2);
    }

    [Fact]
    public void 수평_자세에서도_실측_기울기로_맞춘다()
    {
        var n = CobotInspectionMoveStep.MeasuredWallOut(Frame, Up)!;
        var level = new[] { -180.1, -1107.4, -977.7, 0.0, 0.0, 179.33 };
        var o = CobotInspectionMoveStep.AlignOpticalAxisToDirection(level, new[] { -n[0], -n[1], -n[2] }, ToolAxisDir.MinusZ);

        Assert.True(CobotInspectionMoveStep.AngleDeg(OpticalAxis(o), new[] { -n[0], -n[1], -n[2] }) < 1e-6);
        Assert.True(CobotInspectionMoveStep.AngleDeg(OpticalAxis(o), OpticalAxis(V3End)) < 0.2);   // #3 종료 자세와 같은 기울기
        Assert.Equal(179.33, o[5], 0);                                                              // 비틀림 유지
    }

    [Fact]
    public void 모델과_크게_다르면_실측_법선을_쓰지_않는다()
    {
        var tilted = new[] { 0.0, 0, 0, 30, 0, 0 };   // 30° 기울어진 좌표계 — 잘못 잡힌 것으로 본다
        Assert.Null(CobotInspectionMoveStep.MeasuredWallOut(tilted, Up));
    }

    [Fact]
    public void 모델_법선_정렬은_종전과_같다()
    {
        var computed = new[] { 0.0, 0, 0, 180, 0, 0 };   // 광축(툴 −Z)이 위 — 임의 목표
        var a = CobotInspectionMoveStep.AlignOpticalAxisKeepTwist(V3End, computed, ToolAxisDir.MinusZ);
        Assert.True(CobotInspectionMoveStep.AngleDeg(OpticalAxis(a), OpticalAxis(computed)) < 1e-6);
    }
}
