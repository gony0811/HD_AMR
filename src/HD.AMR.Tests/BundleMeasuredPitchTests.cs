using HD.AMR.App.Communication;
using HD.AMR.App.Service.Inspection;
using HD.AMR.App.Service.Sequence;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>
/// 묶음 팔로워 줄을 리더가 교시로 잰 점1~점2 간격에 맞춰 놓는다 — ACS 도면 거리(줄당 360mm)로 옮기면 실제 코루게이션 간격이
/// 다를 때 뒤 줄일수록 밀린다(실기 2026-11-24 M2: 도면 1080mm·실측 1064.8mm — 4번째 줄이 14.5mm 더 감).
/// </summary>
public class BundleMeasuredPitchTests
{
    [Fact]
    public void 실기_값은_뒤_줄을_실측_간격에_비례해_놓는다()
    {
        // 2026-11-24 12:58 M2: 팔로워 ACS 이동 359.4 / 719.4 / 1079.3mm, 점1~점2 실측 1064.8mm, 도면 1080mm.
        var l2 = InternalWorkpieceFrame.MeasuredPitchAlong(359.4, 360.0, 1064.8, 1080.0).AlongMm!.Value;
        var l3 = InternalWorkpieceFrame.MeasuredPitchAlong(719.4, 720.0, 1064.8, 1080.0).AlongMm!.Value;
        var l4 = InternalWorkpieceFrame.MeasuredPitchAlong(1079.3, 1080.0, 1064.8, 1080.0).AlongMm!.Value;

        Assert.Equal(1064.8 / 3, l2, 6);
        Assert.Equal(1064.8 * 2 / 3, l3, 6);
        Assert.Equal(1064.8, l4, 6);          // 마지막 줄 원점 = 점2(실제로 Peak 를 찾은 자리)
        Assert.InRange(l4 - 1079.3, -14.6, -14.4);
    }

    [Fact]
    public void 방향은_ACS_이동_부호를_따른다()
    {
        var back = InternalWorkpieceFrame.MeasuredPitchAlong(-719.4, 720.0, 1064.8, 1080.0).AlongMm!.Value;
        Assert.Equal(-1064.8 * 2 / 3, back, 6);
        // 도면 거리의 부호는 보지 않는다(크기만).
        Assert.Equal(-1064.8 * 2 / 3, InternalWorkpieceFrame.MeasuredPitchAlong(-719.4, -720.0, 1064.8, 1080.0).AlongMm!.Value, 6);
    }

    [Theory]
    [InlineData(1040.0, 1080.0)]   // −3.7% — Peak 오검출 의심
    [InlineData(1120.0, 1080.0)]   // +3.7%
    [InlineData(200.0, 200.0)]     // 도면 거리가 너무 짧다
    [InlineData(double.NaN, 1080.0)]
    public void 간격_비가_이상하면_쓰지_않는다(double measured, double nominal)
    {
        var (mm, reject) = InternalWorkpieceFrame.MeasuredPitchAlong(359.4, 360.0, measured, nominal);
        Assert.Null(mm);
        Assert.False(string.IsNullOrEmpty(reject));
    }

    [Fact]
    public void 실측_구간_밖의_줄에는_쓰지_않는다()
    {
        // 점2 를 피치 2회 자리(720mm)에 잡았는데 도면 1080mm 떨어진 줄 — 늘여 쓰지 않는다.
        Assert.Null(InternalWorkpieceFrame.MeasuredPitchAlong(1079.3, 1080.0, 709.9, 720.0).AlongMm);
        Assert.NotNull(InternalWorkpieceFrame.MeasuredPitchAlong(719.4, 720.0, 709.9, 720.0).AlongMm);
    }

    [Theory]
    [InlineData(null, true, true)]     // 비움 — 가스 켬
    [InlineData(null, false, false)]   // 비움 — 비전 끔
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    public void 사용_여부는_비우면_센서_모드를_따른다(bool? value, bool usesGas, bool expected)
        => Assert.Equal(expected, InternalWorkpieceFrame.ResolveFollowMeasuredPitch(value, usesGas));

    [Fact]
    public void 리더가_남긴_점1_점2로_실측_간격을_구한다()
    {
        // 좌표계 X = BASE −Y(수평 줄 진행 방향), 점1 = 원점, 점2 는 X 로 1064.8mm.
        var p1 = new[] { -1533.0, 785.2, 730.4, 90.0, 0.4, 89.4 };
        var p2 = new[] { -1537.4, -279.5, 733.4, 90.0, 0.4, 89.4 };
        var frame = InternalWorkpieceFrame.Compute(p1, p2, corrugationTopHeightMm: 0);
        var context = new SequenceContext();
        context.Bag[WObjPointStep.PointPoseBagKey(1)] = p1;
        context.Bag[WObjPointStep.PointPoseBagKey(2)] = p2;

        // 묶음으로 점2 를 잡지 않았으면(도면 거리 표식 없음) 없음.
        Assert.Null(WeldInspectionOrchestrator.MeasuredBundleSpan(context, frame));

        context.Bag[BundleTeach.Point2NominalMmKey] = 1080.0;
        var span = WeldInspectionOrchestrator.MeasuredBundleSpan(context, frame);
        Assert.NotNull(span);
        Assert.Equal(1080.0, span!.Value.NominalMm, 9);
        var expected = Math.Sqrt(4.4 * 4.4 + 1064.7 * 1064.7 + 3.0 * 3.0);
        Assert.Equal(expected, span.Value.MeasuredMm, 3);
    }

    [Fact]
    public void 다른_줄로_넘어갈_때_되돌리면_종전_위치와_같다()
    {
        // 팔로워 원점 = 리더 좌표계 + ACS 거리, 실측 맞춤 뒤 = 리더 좌표계 + 실측 비례 거리. 그 차이를 빼면 ACS 거리 기준 자리.
        var p1 = new[] { -1533.0, 785.2, 730.4, 90.0, 0.4, 89.4 };
        var p2 = new[] { -1537.4, -279.5, 733.4, 90.0, 0.4, 89.4 };
        var frame = InternalWorkpieceFrame.Compute(p1, p2, corrugationTopHeightMm: 0);
        var byAcs = InternalWorkpieceFrame.ShiftAlongX(frame, 1079.3);
        var placed = InternalWorkpieceFrame.ShiftAlongX(frame, 1064.8);
        var shift = new[] { placed[0] - byAcs[0], placed[1] - byAcs[1], placed[2] - byAcs[2] };

        for (var i = 0; i < 3; i++) Assert.Equal(byAcs[i], placed[i] - shift[i], 9);
        Assert.Equal(14.5, Math.Sqrt(shift[0] * shift[0] + shift[1] * shift[1] + shift[2] * shift[2]), 6);
    }
}
