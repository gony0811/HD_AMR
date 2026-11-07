using HD.AMR.App.Service;
using HD.AMR.App.Service.Sequence;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>수동 비드 입력 팝업 조그 — 화면 방향 → 툴 평면 오프셋, 설정 기본값·범위, 파라미터 시드.</summary>
public class BeadManualJogTests
{
    [Theory]
    [InlineData(BeadJogDirection.Left, -20, 0)]
    [InlineData(BeadJogDirection.Right, 20, 0)]
    [InlineData(BeadJogDirection.Up, 0, -20)]
    [InlineData(BeadJogDirection.Down, 0, 20)]
    public void 화면_방향은_영상_평면_이동량으로(BeadJogDirection dir, double x, double y)
    {
        Assert.Equal((x, y), BeadManualJogService.ImageShift(dir, 20));
    }

    [Fact]
    public void 기본_매핑이면_툴_XY_평면만_움직이고_광축과_회전은_0()
    {
        var (x, y) = BeadManualJogService.ImageShift(BeadJogDirection.Right, 20);
        var off = WeldSequenceSupport.ImageShiftToToolOffset(ToolAxisDir.PlusX, ToolAxisDir.PlusY, x, y);

        Assert.Equal(new[] { 20.0, 0, 0, 0, 0, 0 }, off);
    }

    [Fact]
    public void 기본값은_속도_20퍼센트_이동량_20mm()
    {
        Assert.Equal(20, BeadManualSettings.DefaultJogVelPct);
        Assert.Equal(20, BeadManualSettings.DefaultJogStepMm);
    }

    [Fact]
    public void 범위_밖_값은_잘라낸다()
    {
        Assert.Equal(1, BeadManualSettings.ClampJogVelPct(0));
        Assert.Equal(100, BeadManualSettings.ClampJogVelPct(250));
        Assert.Equal(0.5, BeadManualSettings.ClampJogStepMm(0));
        Assert.Equal(100, BeadManualSettings.ClampJogStepMm(500));
    }

    [Fact]
    public void 조그_파라미터가_시드에_들어_있다()
    {
        var seeds = BeadManualSettings.SeedDefaults().ToDictionary(r => r.Name, r => r.Value);

        Assert.Equal("20", seeds[BeadManualSettings.JogVelPctKey]);
        Assert.Equal("20", seeds[BeadManualSettings.JogStepMmKey]);
    }
}
