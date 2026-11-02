using HD.AMR.App.Service;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>
/// ④⁺⁺ Depth 카메라 센터 평행이동과 ⑦⁺⑪⁺ 검사캠 시프트는 같은 변환을 반대 부호로 쓴다 —
/// 둘을 합치면 정확히 0 이어야 센터링 후 타깃이 다시 TCP(검사캠 초점) 아래로 돌아온다.
/// </summary>
public class DepthCamCenterShiftTests
{
    [Theory]
    [InlineData(ToolAxisDir.PlusX, ToolAxisDir.PlusY, 12.5, -80.0)]
    [InlineData(ToolAxisDir.MinusY, ToolAxisDir.PlusX, -40.0, 75.0)]
    [InlineData(ToolAxisDir.PlusY, ToolAxisDir.MinusX, 0.0, 120.0)]
    public void CenterShift_IsExactInverseOfInspectCamShift(ToolAxisDir axX, ToolAxisDir axY, double offX, double offY)
    {
        var toCenter = WeldSequenceSupport.ImageShiftToToolOffset(axX, axY, offX, offY);
        var toInspectCam = WeldSequenceSupport.ImageShiftToToolOffset(axX, axY, -offX, -offY);

        for (var i = 0; i < 6; i++)
            Assert.Equal(0.0, toCenter[i] + toInspectCam[i], 9);
    }

    // 영상 +X → 툴 −Y, 영상 +Y → 툴 +X 매핑에서 영상 (10, 20) 이동은 툴 (20, −10). Z·회전은 0 — WD·평행도 유지.
    [Fact]
    public void ImageShift_MapsThroughToolAxes_AndLeavesDepthAndRotationAlone()
    {
        var offset = WeldSequenceSupport.ImageShiftToToolOffset(ToolAxisDir.MinusY, ToolAxisDir.PlusX, 10, 20);

        Assert.Equal(new[] { 20.0, -10.0, 0, 0, 0, 0 }, offset);
    }
}
