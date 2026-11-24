using HD.AMR.App.Communication;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Inspection;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>
/// Peak 센터링 뒤 남은 진행축 잔차를 작업물 좌표계 점 기록에 반영한다 — 실기 2026-11-24 M2: 잔차가 늘 −4mm 안팎으로 남아
/// (−3.7 / −4.6 / −4.4mm) 좌표계 원점이 그만큼 진행 방향으로 밀리고 구역 전체가 약 5mm 더 갔다.
/// </summary>
public class WObjPeakResidualCorrectionTests
{
    [Fact]
    public void 오프셋은_센터링_이동과_같은_규약()
    {
        // 실기 15:20 Peak2: 잔차 −6.1mm 를 "툴-X −6.1mm" 이동으로 보정해 +1.5mm 가 됐다 — 같은 축·부호로 기록을 옮긴다.
        var move = new double[6];
        FlatSurfaceCenteringService.ApplyAxis(move, ToolAxisDir.MinusX, -6.1);
        Assert.Equal(move, WObjPointStep.AlongCorrectionOffset(ToolAxisDir.MinusX, -6.1));

        Assert.Equal(new[] { 4.4, 0.0, 0, 0, 0, 0 }, WObjPointStep.AlongCorrectionOffset(ToolAxisDir.MinusX, -4.4));
        Assert.Equal(new[] { -4.4, 0.0, 0, 0, 0, 0 }, WObjPointStep.AlongCorrectionOffset(ToolAxisDir.PlusX, -4.4));
        Assert.Equal(new[] { 0.0, 2.0, 0, 0, 0, 0 }, WObjPointStep.AlongCorrectionOffset(ToolAxisDir.PlusY, 2.0));
    }

    [Theory]
    [InlineData(10.5)]    // 상한 밖 — 옆 Peak 를 잡았을 수 있다
    [InlineData(-58.0)]
    [InlineData(double.NaN)]
    public void 큰_잔차는_보정하지_않는다(double residual)
        => Assert.Null(WObjPointStep.AlongCorrectionOffset(ToolAxisDir.MinusX, residual));

    [Fact]
    public void 음수_잔차는_원점을_진행_반대쪽으로_되돌린다()
    {
        // 15:19 M2 첫 구역: 진행 방향 = BASE −Y(좌표계 X), 툴 X 는 좌표계 X 와 반대(RZ 유지 −179.8°), 진행축 = 툴 −X.
        // Peak1 잔차 −4.4mm = Peak 가 툴보다 진행 반대쪽에 4.4mm — 원점이 4.4mm 더 간 상태.
        var p1 = new[] { -1531.7, 783.6, 728.4, 90.0, 0.0, 90.0 };
        var p2 = new[] { -1531.7, -275.2, 728.4, 90.0, 0.0, 90.0 };
        var frame = InternalWorkpieceFrame.Compute(p1, p2, corrugationTopHeightMm: 0);
        // 툴 X 가 좌표계 X 와 반대인 자세.
        var tool = FrameMath.FromFrame(new[] { 0.0, 0.0, 0.0, 0.0, 0.0, 180.0 }, frame);

        var corrected = WObjPointStep.ApplyCorrection(tool, WObjPointStep.AlongCorrectionOffset(ToolAxisDir.MinusX, -4.4)!);

        var (along, height) = InternalWorkpieceFrame.AlongAndHeight(frame, corrected);
        Assert.Equal(-4.4, along, 6);        // 진행 반대쪽으로 4.4mm — 실제 Peak 위
        Assert.Equal(0, height, 6);
        for (var i = 3; i < 6; i++) Assert.Equal(tool[i], corrected[i], 6);   // 자세 유지
    }

    [Fact]
    public void 두_점의_잔차가_다르면_교시_간격이_그만큼_바뀐다()
    {
        // 점1 잔차 −4.4mm, 점2 잔차 +1.5mm → 실제 Peak 간격은 기록 간격보다 5.9mm 길다.
        var p1 = new[] { -1531.7, 783.6, 728.4, 90.0, 0.0, 90.0 };
        var p2 = new[] { -1531.7, -275.2, 728.4, 90.0, 0.0, 90.0 };
        var frame = InternalWorkpieceFrame.Compute(p1, p2, corrugationTopHeightMm: 0);
        var t1 = FrameMath.FromFrame(new[] { 0.0, 0.0, 0.0, 0.0, 0.0, 180.0 }, frame);
        var t2 = FrameMath.FromFrame(new[] { 1058.8, 0.0, 0.0, 0.0, 0.0, 180.0 }, frame);

        var c1 = WObjPointStep.ApplyCorrection(t1, WObjPointStep.AlongCorrectionOffset(ToolAxisDir.MinusX, -4.4)!);
        var c2 = WObjPointStep.ApplyCorrection(t2, WObjPointStep.AlongCorrectionOffset(ToolAxisDir.MinusX, 1.5)!);

        var span = InternalWorkpieceFrame.AlongAndHeight(frame, c2).Along - InternalWorkpieceFrame.AlongAndHeight(frame, c1).Along;
        Assert.Equal(1058.8 + 5.9, span, 6);
    }

    [Theory]
    [InlineData(null, true, true)]     // 비움 — 가스 켬
    [InlineData(null, false, false)]   // 비움 — 비전 끔
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    public void 사용_여부는_비우면_센서_모드를_따른다(bool? value, bool usesGas, bool expected)
        => Assert.Equal(expected, WObjPointStep.ResolvePeakResidualCorrection(value, usesGas));
}
