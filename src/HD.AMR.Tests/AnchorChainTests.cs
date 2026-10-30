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
}
