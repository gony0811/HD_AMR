using HD.AMR.App.Service.Sequence;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>
/// ⑤~⑫ pitch 결정 — 챔퍼(SL/PL/SU/PU) 경사 방향만 도면 피치 254.6mm, 그 외는 파라미터 pitch.
/// </summary>
public class CorrugationPitchTests
{
    private static SequenceContext Ctx(int surfaceId, InspectionMoveDirection dir)
        => new() { InspectionSurfaceId = surfaceId, InspectionDirection = dir };

    [Theory]
    [InlineData(0x07)]   // PL
    [InlineData(0x08)]   // SL
    [InlineData(0x09)]   // PU
    [InlineData(0x0A)]   // SU
    public void 챔퍼_경사방향은_도면_피치(int surfaceId)
    {
        var pitch = WeldSequenceSupport.ResolvePitchMm(360, Ctx(surfaceId, InspectionMoveDirection.Vertical));
        Assert.Equal(254.6, pitch, 6);
    }

    // 챔퍼라도 선체 길이 방향(수평)은 360 그대로.
    [Theory]
    [InlineData(0x09)]
    [InlineData(0x0A)]
    public void 챔퍼_수평방향은_파라미터_피치(int surfaceId)
    {
        var pitch = WeldSequenceSupport.ResolvePitchMm(360, Ctx(surfaceId, InspectionMoveDirection.Horizontal));
        Assert.Equal(360, pitch, 6);
    }

    // 챔퍼가 아닌 면은 방향과 무관하게 파라미터 pitch(현장 튜닝값 포함).
    [Theory]
    [InlineData(0x01, InspectionMoveDirection.Vertical)]   // B
    [InlineData(0x02, InspectionMoveDirection.Vertical)]   // T
    [InlineData(0x04, InspectionMoveDirection.Vertical)]   // SM
    [InlineData(0x03, InspectionMoveDirection.Horizontal)] // PM
    [InlineData(0xFF, InspectionMoveDirection.Vertical)]   // 미정의 Surface ID
    public void 챔퍼가_아니면_파라미터_피치(int surfaceId, InspectionMoveDirection dir)
    {
        var pitch = WeldSequenceSupport.ResolvePitchMm(362, Ctx(surfaceId, dir));
        Assert.Equal(362, pitch, 6);
    }
}
