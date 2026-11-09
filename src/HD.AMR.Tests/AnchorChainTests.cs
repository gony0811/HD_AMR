using HD.AMR.App.Service;
using HD.AMR.App.Service.Inspection;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>
/// 같은 anchor 그룹 연속 task — 캐시 프레임 평행 이동과 복귀 없는 연속 이동 계획.
/// 2026-10-30 SU L4-R03C05: #2·#3 이 #1 프레임을 그대로 써서 #1 끝점 [-1013.9, 266.7, 785.5] 에서 끝났고,
/// task 마다 후퇴 → 홈 위치_2 → 검사 준비 왕복을 반복했다.
/// </summary>
public class AnchorChainTests
{
    // 프레임 Z(면 바깥 법선) = BASE +X 인 자세(Ry 90°). SU 면에서 BASE X 가 면 법선 방향(면까지 법선거리 1461mm).
    private static readonly double[] Frame = { -1057.6, 624.9, 809.4, 0.0, 90.0, 0.0 };

    [Fact]
    public void 캐시_프레임은_시작점_차이의_면_방향_성분만큼_옮긴다()
    {
        // 로그 값: #1 시작 접근점 → #2 시작 접근점. x 37.6mm 는 면 법선 성분이라 버린다.
        var (moved, normalMm) = InternalWorkpieceFrame.TranslateInPlane(
            Frame, new[] { -1033.7, 626.5, 785.5 }, new[] { -996.1, -69.1, 785.5 });

        Assert.Equal(37.6, normalMm, 6);
        Assert.Equal(-1057.6, moved[0], 6);          // 면 거리 유지
        Assert.Equal(624.9 - 695.6, moved[1], 6);
        Assert.Equal(809.4, moved[2], 6);
        Assert.Equal(Frame[3..], moved[3..]);         // 자세 그대로
    }

    [Fact]
    public void 같은_시작점이면_프레임은_그대로()
    {
        var start = new[] { -1033.7, 626.5, 785.5 };
        var (moved, normalMm) = InternalWorkpieceFrame.TranslateInPlane(Frame, start, start);

        Assert.Equal(0.0, normalMm, 9);
        for (var i = 0; i < 6; i++) Assert.Equal(Frame[i], moved[i], 9);
    }

    [Fact]
    public void 면_바깥_법선은_광축의_반대()
    {
        // 툴 −Z 가 광축(현장 설정). Rx 180° 면 툴 Z = BASE −Z → 광축(−Z) = BASE +Z 를 향함 → 바깥 = BASE −Z.
        var n = CobotInspectionMoveStep.WallOutNormal(new[] { 0.0, 0.0, 0.0, 180.0, 0.0, 0.0 }, ToolAxisDir.MinusZ);

        Assert.Equal(0.0, n[0], 9);
        Assert.Equal(0.0, n[1], 9);
        Assert.Equal(-1.0, n[2], 9);
    }

    [Fact]
    public void 면에_standoff_보다_가까우면_법선_방향으로만_들어_올린다()
    {
        var outN = new[] { 1.0, 0.0, 0.0 };   // 벽은 BASE −X 쪽
        // 접근점은 x −1000(면 이격 200mm 평면). 현재는 그보다 벽 쪽으로 150mm(x −1150)이고 옆으로 300mm 떨어져 있다.
        var lift = CobotInspectionMoveStep.PlanChainLift(
            new[] { -1150.0, 300.0, 700.0, 0, 0, 0 }, new[] { -1000.0, -60.0, 785.0 }, outN);

        Assert.NotNull(lift);
        Assert.Equal(new[] { 150.0, 0.0, 0.0, 0.0, 0.0, 0.0 }, lift);
    }

    [Theory]
    [InlineData(-1000.0)]   // 같은 평면
    [InlineData(-1000.8)]   // 1mm 허용 이내
    [InlineData(-900.0)]    // 이미 더 멀리
    public void 이미_standoff_평면_이상이면_들어_올리지_않는다(double x)
    {
        var lift = CobotInspectionMoveStep.PlanChainLift(
            new[] { x, 300.0, 700.0, 0, 0, 0 }, new[] { -1000.0, -60.0, 785.0 }, new[] { 1.0, 0.0, 0.0 });

        Assert.Null(lift);
    }

    [Fact]
    public void 직전_종료_자세_판정_위치_경계()
    {
        var prev = new[] { -1013.9, 266.7, 785.5, 135.0, 0.0, 93.1 };

        Assert.True(CobotInspectionMoveStep.IsSamePose(
            new[] { -1013.9, 266.8, 785.4, 135.0, 0.0, 93.1 }, prev, 5.0, 2.0));   // 로그상 #3 시작(0.1mm 차)
        Assert.True(CobotInspectionMoveStep.IsSamePose(
            new[] { -1009.0, 266.7, 785.5, 135.0, 0.0, 93.1 }, prev, 5.0, 2.0));   // 4.9mm
        Assert.False(CobotInspectionMoveStep.IsSamePose(
            new[] { -1008.0, 266.7, 785.5, 135.0, 0.0, 93.1 }, prev, 5.0, 2.0));   // 5.9mm
    }

    [Fact]
    public void 직전_종료_자세_판정_회전_경계()
    {
        var prev = new[] { -1013.9, 266.7, 785.5, 135.0, 0.0, 93.1 };

        Assert.True(CobotInspectionMoveStep.IsSamePose(
            new[] { -1013.9, 266.7, 785.5, 135.0, 0.0, 94.9 }, prev, 5.0, 2.0));   // 1.8°
        Assert.False(CobotInspectionMoveStep.IsSamePose(
            new[] { -1013.9, 266.7, 785.5, 135.0, 0.0, 95.3 }, prev, 5.0, 2.0));   // 2.2°
        // 표현만 다른 같은 자세(rz ±180 감기)는 같다.
        Assert.True(CobotInspectionMoveStep.IsSamePose(
            new[] { 0.0, 0.0, 0.0, -180.0, 45.0, -178.2 }, new[] { 0.0, 0.0, 0.0, 180.0, 45.0, 181.8 }, 5.0, 2.0));
    }

    // 실기 2026-11-09 16:37 L4-R08C08 #2: #1 이 면에 붙은 검사 자세로 끝났고 #2 시작점이 바로 옆이라, 접근점(400mm)까지
    // 409mm 들어 올렸다가 ⑱ 첫 경유점으로 다시 들어왔다. 연속 이동은 짧은 이격(기본 100mm)까지만 띄운다.
    [Fact]
    public void 연속_이동은_첫_진입_접근점까지_빠지지_않고_짧은_이격까지만_들어_올린다()
    {
        var outN = new[] { 1.0, 0.0, 0.0 };                       // 벽은 BASE −X 쪽, 면은 x = −1400
        var approach = new[] { -1000.0, -60.0, 785.0 };           // 첫 진입 접근점(면 이격 400mm)
        var cur = new[] { -1409.0, -60.0, 785.0, 0, 0, 0 };       // #1 종료 — 접근점 평면보다 409mm 면 쪽

        var chain = CobotInspectionMoveStep.ChainTargetBase(approach, outN, 400.0, 100.0);
        Assert.Equal(new[] { -1300.0, -60.0, 785.0 }, chain);     // 면 이격 100mm, 옆 위치는 그대로

        var lift = CobotInspectionMoveStep.PlanChainLift(cur, chain, outN);
        Assert.NotNull(lift);
        Assert.Equal(109.0, lift![0], 9);                         // 예전 409mm → 109mm
        Assert.Equal(0.0, lift[1], 9);
        Assert.Equal(0.0, lift[2], 9);

        // 예전 동작(접근점까지)과 비교 — 같은 입력에서 409mm 였다.
        Assert.Equal(409.0, CobotInspectionMoveStep.PlanChainLift(cur, approach, outN)![0], 9);
    }

    [Fact]
    public void 연속_이동_목표는_면_법선을_따라서만_당긴다()
    {
        var outN = new[] { 0.0, 0.6, 0.8 };                       // 비스듬한 면
        var chain = CobotInspectionMoveStep.ChainTargetBase(new[] { 10.0, 20.0, 30.0 }, outN, 400.0, 150.0);
        Assert.Equal(10.0, chain[0], 9);
        Assert.Equal(20.0 - 250.0 * 0.6, chain[1], 9);
        Assert.Equal(30.0 - 250.0 * 0.8, chain[2], 9);
        // 같은 이격이면 접근점 그대로
        Assert.Equal(new[] { 10.0, 20.0, 30.0 }, CobotInspectionMoveStep.ChainTargetBase(new[] { 10.0, 20.0, 30.0 }, outN, 400.0, 400.0));
    }

    [Theory]
    [InlineData(null, 400.0, 100.0, true)]     // 미설정 → 기본 100 (경고 없음)
    [InlineData(60.0, 400.0, 60.0, true)]
    [InlineData(30.0, 400.0, 30.0, true)]      // 하한
    [InlineData(400.0, 400.0, 400.0, true)]    // 첫 진입과 같게 = 예전 동작
    [InlineData(10.0, 400.0, 100.0, false)]    // 하한 미만 → 기본
    [InlineData(500.0, 400.0, 100.0, false)]   // 첫 진입보다 큼 → 기본
    [InlineData(null, 80.0, 80.0, true)]       // 첫 진입 이격이 기본보다 작으면 그 값
    public void 연속_이동_이격_파라미터_해석(double? raw, double approach, double expected, bool ok)
    {
        var (value, valid) = CobotInspectionMoveStep.ResolveChainStandoffMm(raw, approach);
        Assert.Equal(expected, value);
        Assert.Equal(ok, valid);
    }

    // 실기 2026-11-09 17:46 L4-R08C08 V01: 비드 두 점으로 잰 프레임(X = 비드 방향 −91.97°)과 ACS 시작점 차이
    // [-12.9, -359.8, -0.1](−92.05°). 종전에는 ACS 차이를 그대로 써서 LINE 마다 옆으로 0.55mm 씩 밀렸다(V01→V04 위로 시프트).
    private static readonly double[] BeadFrame = { -561.0, 1522.8, 1223.9, -179.54, 0.01, -91.97 };

    [Fact]
    public void 연속_LINE_프레임은_비드_방향으로만_옮긴다()
    {
        var (moved, lateral, normal, alongX) = InternalWorkpieceFrame.TranslateAlongX(
            BeadFrame, new[] { 0.0, 0.0, 0.0 }, new[] { -12.9, -359.8, -0.1 });

        Assert.True(alongX);
        Assert.InRange(Math.Abs(lateral), 0.4, 0.7);                       // 버린 ACS 옆 성분 ≈ 0.55mm
        var m = HD.AMR.App.Communication.FrameMath.PoseToMatrix(BeadFrame);
        var d = new[] { moved[0] - BeadFrame[0], moved[1] - BeadFrame[1], moved[2] - BeadFrame[2] };
        double Comp(int col) => d[0] * m[0, col] + d[1] * m[1, col] + d[2] * m[2, col];
        Assert.InRange(Comp(0), 359.9, 360.1);                              // 진행 거리 유지
        Assert.Equal(0.0, Comp(1), 9);                                      // 옆(프레임 Y) 이동 없음
        Assert.Equal(0.0, Comp(2), 9);                                      // 면 거리 유지
        Assert.Equal(BeadFrame[3..], moved[3..]);                           // 자세 그대로
    }

    [Fact]
    public void 세_번_옮겨도_옆으로_누적되지_않는다()
    {
        var frame = BeadFrame;
        var m = HD.AMR.App.Communication.FrameMath.PoseToMatrix(BeadFrame);
        for (var i = 0; i < 3; i++)
            frame = InternalWorkpieceFrame.TranslateAlongX(frame, new[] { 0.0, 0.0, 0.0 }, new[] { -12.9, -359.8, -0.1 }).Frame;
        var d = new[] { frame[0] - BeadFrame[0], frame[1] - BeadFrame[1], frame[2] - BeadFrame[2] };
        Assert.Equal(0.0, d[0] * m[0, 1] + d[1] * m[1, 1] + d[2] * m[2, 1], 9);   // V04 도 V01 비드선 위
        Assert.InRange(d[0] * m[0, 0] + d[1] * m[1, 0] + d[2] * m[2, 0], 1079.5, 1080.5);
    }

    [Fact]
    public void 옆으로_크게_떨어진_task_는_종전_면_평행_이동으로_폴백한다()
    {
        // 프레임 X = BASE −Y 근처. BASE X 로 50mm 옆 성분 → 버리지 않는다.
        var to = new[] { -12.9 + 50.0, -359.8, -0.1 };
        var (moved, lateral, _, alongX) = InternalWorkpieceFrame.TranslateAlongX(BeadFrame, new[] { 0.0, 0.0, 0.0 }, to);
        var (inPlane, _) = InternalWorkpieceFrame.TranslateInPlane(BeadFrame, new[] { 0.0, 0.0, 0.0 }, to);

        Assert.False(alongX);
        Assert.True(Math.Abs(lateral) > InternalWorkpieceFrame.MaxDroppedLateralMm);
        for (var i = 0; i < 6; i++) Assert.Equal(inPlane[i], moved[i], 9);
    }

    [Fact]
    public void 같은_시작점이면_비드_방향_이동도_그대로()
    {
        var start = new[] { -543.7, 1507.3, 807.5 };
        var (moved, lateral, normal, alongX) = InternalWorkpieceFrame.TranslateAlongX(BeadFrame, start, start);
        Assert.True(alongX);
        Assert.Equal(0.0, lateral, 9);
        Assert.Equal(0.0, normal, 9);
        for (var i = 0; i < 6; i++) Assert.Equal(BeadFrame[i], moved[i], 9);
    }
}
