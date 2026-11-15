using HD.AMR.App.Communication;

namespace HD.AMR.Tests;

/// <summary>
/// 관절값 없이 지령하는 이동(MoveL·MoveJByToolOffset)의 관절 해 규칙 — 컨트롤러 자동 IK 해를 현재 관절 쪽으로
/// 감아 풀고(J6 ±360°), 가지(J3/J5 부호)가 바뀌면 재선택 대상으로 판정한다.
/// </summary>
public class MoveJointResolutionTests
{
    [Fact]
    public void 자동_IK_J6_179는_현재_마이너스181_쪽으로_감아_푼다()
    {
        double[] cur = { 10, -80, 100, -110, 85, -181 };
        double[] auto = { 10, -80, 100, -110, 85, 179 };

        var j = FairinoRpcClient.UnwrapToward(auto, cur);

        Assert.Equal(-181, j[5], 6);
    }

    [Fact]
    public void 현재가_플러스180_4로_보고되면_자동_해_마이너스179는_181로_감는다()
    {
        // 실기 2026-11-03: 같은 물리각을 컨트롤러가 +180.4° 로 보고한 날.
        double[] cur = { 10, -80, 100, -110, 85, 180.4 };
        double[] auto = { 10, -80, 100, -110, 85, -179 };

        var j = FairinoRpcClient.UnwrapToward(auto, cur);

        Assert.Equal(181, j[5], 6);
    }

    [Fact]
    public void 같은_가지면_재선택하지_않는다()
    {
        double[] cur = { 10, -80, 100, -110, 85, -181 };
        double[] j = { 12, -78, 95, -100, 60, -200 };

        Assert.False(FairinoRpcClient.IsBranchChange(cur, j));
    }

    [Fact]
    public void 손목_J5_부호가_바뀌면_다른_가지()
    {
        double[] cur = { 10, -80, 100, -110, 20, -181 };
        double[] flipped = { 10, -80, 100, 70, -20, -1 };

        Assert.True(FairinoRpcClient.IsBranchChange(cur, flipped));
    }

    [Fact]
    public void 팔꿈치_J3_부호가_바뀌면_다른_가지()
    {
        double[] cur = { 10, -80, 100, -110, 20, -181 };
        double[] flipped = { 10, -150, -100, -110, 20, -181 };

        Assert.True(FairinoRpcClient.IsBranchChange(cur, flipped));
    }

    [Fact]
    public void J5가_정확히_0이면_가지_판정을_하지_않는다()
    {
        double[] cur = { 10, -80, 100, -110, 0, -181 };
        double[] j = { 10, -80, 100, -110, -5, -181 };

        Assert.False(FairinoRpcClient.IsBranchChange(cur, j));
    }
}
