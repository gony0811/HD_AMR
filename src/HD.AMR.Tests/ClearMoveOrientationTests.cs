using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>
/// 면 여유 확보 이동의 자세 보간(<see cref="SequenceEntry.BlendOrientation"/>) — 실기 2026-11-24 11:03 SU 복귀:
/// 툴 자세를 유지한 채 800mm 내려가 J4 가 크게 돌았다. 경유점 자세 쪽으로 같이 돌리는 후보의 자세 계산을 고정한다.
/// </summary>
public class ClearMoveOrientationTests
{
    // 11:03 후퇴 끝 TCP 자세와 작업 준비 위치(ready) 자세.
    private static readonly double[] From = { -700.5, -645.4, 805.1, 179.99, 44.99, 175.36 };
    private static readonly double[] Ready = { -1142.2, -144.8, -374.5, -117.58, 89.56, -117.97 };

    [Fact]
    public void 비율_0은_출발_자세_1은_목표_자세()
    {
        var a = SequenceEntry.BlendOrientation(From, Ready, 0);
        Assert.True(SequenceEntry.RotationAngleDeg(From, new[] { 0, 0, 0, a[0], a[1], a[2] }) < 1e-6);
        var b = SequenceEntry.BlendOrientation(From, Ready, 1);
        Assert.True(SequenceEntry.RotationAngleDeg(Ready, new[] { 0, 0, 0, b[0], b[1], b[2] }) < 1e-6);
    }

    [Theory]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(0.75)]
    public void 보간은_최단_회전을_비율만큼_나눈다(double t)
    {
        var total = SequenceEntry.RotationAngleDeg(From, Ready);
        var r = SequenceEntry.BlendOrientation(From, Ready, t);
        var mid = new[] { 0, 0, 0, r[0], r[1], r[2] };
        Assert.Equal(total * t, SequenceEntry.RotationAngleDeg(From, mid), 6);
        Assert.Equal(total * (1 - t), SequenceEntry.RotationAngleDeg(mid, Ready), 6);
    }

    [Fact]
    public void 자세_유지보다_목표_쪽을_먼저_본다()
    {
        Assert.Equal(0.75, SequenceEntry.ClearOrientationBlends[0]);
        Assert.Equal(0.0, SequenceEntry.ClearOrientationBlends[^1]);   // 종전 동작은 마지막 후보(J4 가드 적용)
    }
}
