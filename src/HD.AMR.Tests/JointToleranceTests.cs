using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>
/// 홈/ready 판정(<see cref="SequenceEntry.IsWithinJointTolerance"/>)의 ±360° 감기 비교를 고정한다.
/// +180°/−180° 처럼 한 바퀴 차이로 표현된 같은 물리 자세를 "같은 위치"로 봐야 한다
/// (안 그러면 홈에 있어도 '홈 아님'으로 오판 → 블라인드 벽면 후퇴가 베이스 방향으로 실행).
/// </summary>
public class JointToleranceTests
{
    private static double[] J(double j6, double j1 = 0, double j2 = 0, double j3 = 0, double j4 = 0, double j5 = 0)
        => new[] { j1, j2, j3, j4, j5, j6 };

    [Theory]
    [InlineData(180.0, -180.0, true)]   // 한 바퀴 차 = 같은 자세
    [InlineData(-180.0, 180.0, true)]
    [InlineData(179.9, -180.0, true)]   // 0.1° 차(감기 후) — 허용 내
    [InlineData(0.0, 0.0, true)]
    [InlineData(0.4, 0.0, true)]        // 허용(0.5°) 경계 안
    [InlineData(1.0, 0.0, false)]       // 허용 밖
    [InlineData(179.0, -179.0, false)]  // 감기 후 2° 차 — 허용 밖
    [InlineData(360.0, 0.0, true)]      // 완전 한 바퀴 = 같은 자세
    public void J6_와인딩_차이를_감아서_비교한다(double cur, double target, bool expected)
    {
        Assert.Equal(expected, SequenceEntry.IsWithinJointTolerance(J(cur), J(target)));
    }

    [Fact]
    public void 다른_축이_허용_밖이면_감기와_무관하게_false()
    {
        // J6 는 ±180 로 같은 자세지만 J4 가 3° 벌어지면 홈 아님.
        var cur = J(j6: 180.0, j4: 3.0);
        var target = J(j6: -180.0, j4: 0.0);
        Assert.False(SequenceEntry.IsWithinJointTolerance(cur, target));
    }
}
