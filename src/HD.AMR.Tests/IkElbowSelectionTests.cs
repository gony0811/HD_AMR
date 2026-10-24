using HD.AMR.App.Communication;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>
/// 천장(T) 접근의 팔꿈치 가지 선택 — 같은 어깨·손목 가지 후보 추리기, J3 섭동 원의 중심(팔꿈치 축) 계산,
/// wall_code 별 적용 여부를 고정한다.
/// </summary>
public class IkElbowSelectionTests
{
    [Fact]
    public void ElbowSiblings는_J1_J5_J6가_같은_해만_남긴다()
    {
        var keep = new[] { 10.0, -60, 100, -130, 85, 180 };
        var candidates = new (int, double[])[]
        {
            (0, keep),
            (1, new[] { 10.4, -150, -100, 30, 85.5, 179.2 }),   // 팔꿈치만 다른 해
            (2, new[] { -170.0, -120, 100, -50, -85, 0 }),      // 어깨 가지 다름
            (3, new[] { 10.0, -60, 100, 50, -85, 0 }),          // 손목 가지 다름
        };

        var s = FairinoRpcClient.ElbowSiblings(candidates, keep);

        Assert.Equal(new[] { 0, 1 }, s.Select(c => c.Config).ToArray());
    }

    [Fact]
    public void ArcCenter는_원_위_세_점의_중심을_돌려준다()
    {
        // 중심 (100, -50, 400), 반지름 300, Y 축 둘레 원 — J3 축이 수평인 경우.
        double[] At(double deg)
        {
            var t = deg * Math.PI / 180;
            return new[] { 100 + 300 * Math.Cos(t), -50.0, 400 + 300 * Math.Sin(t), 0, 0, 0 };
        }

        var c = FairinoRpcClient.ArcCenter(At(40), At(55), At(25));

        Assert.NotNull(c);
        Assert.Equal(100, c![0], 6);
        Assert.Equal(-50, c[1], 6);
        Assert.Equal(400, c[2], 6);
    }

    [Fact]
    public void ArcCenter는_한_직선_위_점이면_null()
    {
        Assert.Null(FairinoRpcClient.ArcCenter(
            new[] { 0.0, 0, 0 }, new[] { 1.0, 1, 1 }, new[] { 2.0, 2, 2 }));
    }

    [Fact]
    public void 팔꿈치_회피는_천장에만_적용한다()
    {
        Assert.Equal(new[] { 0.0, 0, 1 }, CobotInspectionMoveStep.ElbowAwayDirection("T"));
        Assert.Null(CobotInspectionMoveStep.ElbowAwayDirection("PM"));
        Assert.Null(CobotInspectionMoveStep.ElbowAwayDirection("B"));
        Assert.Null(CobotInspectionMoveStep.ElbowAwayDirection(null));
    }
}
