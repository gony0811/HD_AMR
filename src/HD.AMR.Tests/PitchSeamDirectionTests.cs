using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>피치 이동 방향 — 툴 진행축이 어느 쪽으로 정렬됐든 용접선 끝점 쪽으로 간다.</summary>
public class PitchSeamDirectionTests
{
    [Fact]
    public void 실기_F벽_가로_용접선은_끝점_반대쪽_이동을_뒤집는다()
    {
        // 2026-11-13 F L4-R01C04 H02: 피치 이동 BASE [-19.5, -359.5, 0] (차체 왼쪽 = 좌현),
        // 용접선은 시작점에서 차체 오른쪽(BASE +Y, 장착 yaw 180°)으로 간다.
        var move = new[] { -19.5, -359.5, 0.0 };
        var seam = new[] { 0.054, 0.9985, 0.0 };
        Assert.True(WeldSequenceSupport.PitchMoveOpposesSeam(move, seam, pitchDir: 1));
        Assert.False(WeldSequenceSupport.PitchMoveOpposesSeam(new[] { 19.5, 359.5, 0.0 }, seam, pitchDir: 1));
    }

    [Fact]
    public void 음의_피치는_끝점_반대쪽이_맞는_방향이다()
    {
        var seam = new[] { 0.0, 1.0, 0.0 };
        Assert.False(WeldSequenceSupport.PitchMoveOpposesSeam(new[] { 0.0, -360.0, 0.0 }, seam, pitchDir: -1));
        Assert.True(WeldSequenceSupport.PitchMoveOpposesSeam(new[] { 0.0, 360.0, 0.0 }, seam, pitchDir: -1));
    }

    [Fact]
    public void 용접선과_거의_수직인_이동이나_영벡터는_판단하지_않는다()
    {
        var seam = new[] { 0.0, 1.0, 0.0 };
        Assert.False(WeldSequenceSupport.PitchMoveOpposesSeam(new[] { 360.0, -30.0, 0.0 }, seam, 1));
        Assert.False(WeldSequenceSupport.PitchMoveOpposesSeam(new[] { 0.0, 0.0, 0.0 }, seam, 1));
        Assert.False(WeldSequenceSupport.PitchMoveOpposesSeam(new[] { 0.0, -360.0, 0.0 }, new[] { 0.0, 0.0, 0.0 }, 1));
    }

    [Fact]
    public void 툴_이동량을_자세에_맞춰_BASE_벡터로_바꾼다()
    {
        // 자세 회전 없음 → 그대로. rz 180° → x·y 가 뒤집힌다.
        var same = WeldSequenceSupport.ToolOffsetInBase(new[] { 0.0, 0, 0, 0, 0, 0 }, new[] { 360.0, 0, 0 });
        Assert.Equal(360.0, same[0], 6);
        var turned = WeldSequenceSupport.ToolOffsetInBase(new[] { 0.0, 0, 0, 0, 0, 180 }, new[] { 360.0, 0, 0 });
        Assert.Equal(-360.0, turned[0], 6);
        Assert.Equal(0.0, turned[1], 6);
    }
}
