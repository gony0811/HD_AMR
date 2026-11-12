using HD.AMR.App.Service.Inspection;

namespace HD.AMR.Tests;

/// <summary>검사 프로파일 편집 — 포인트 사이 행 삽입의 자리와 초기값.</summary>
public class InspectionPointInsertTests
{
    private static readonly double[][] Poses =
    {
        new[] { 0.0, 0, 33.8, 0, -45, 0 },
        new[] { 20.0, 0, 20.0, 0, -30, 0 },
        new[] { 60.0, 4, 0.0, 2, 0, 10 },
    };

    [Theory]
    [InlineData(3, 1, true, 1)]     // 2번 행 위 → 자리 1
    [InlineData(3, 1, false, 2)]    // 2번 행 아래 → 자리 2
    [InlineData(3, 0, true, 0)]     // 맨 앞
    [InlineData(3, 2, false, 3)]    // 맨 뒤
    [InlineData(3, -1, true, 3)]    // 기준 없음 → 맨 끝
    [InlineData(0, 0, true, 0)]
    public void 기준_행의_위나_아래_자리를_돌려준다(int count, int index, bool above, int expected)
        => Assert.Equal(expected, InspectionPointInsert.InsertIndex(count, index, above));

    [Fact]
    public void 사이에_넣는_행은_앞뒤_행의_중간값이다()
    {
        var mid = InspectionPointInsert.NewPose(Poses, 1)!;
        Assert.Equal(new[] { 10.0, 0, 26.9, 0, -37.5, 0 }, mid.Select(v => Math.Round(v, 6)).ToArray());
        var mid2 = InspectionPointInsert.NewPose(Poses, 2)!;
        Assert.Equal(new[] { 40.0, 2, 10.0, 1, -15, 5 }, mid2);
    }

    [Fact]
    public void 맨_앞이나_맨_뒤는_이웃_행을_복사하고_원본을_건드리지_않는다()
    {
        var first = InspectionPointInsert.NewPose(Poses, 0)!;
        Assert.Equal(Poses[0], first);
        Assert.NotSame(Poses[0], first);
        var last = InspectionPointInsert.NewPose(Poses, 3)!;
        Assert.Equal(Poses[2], last);
        Assert.NotSame(Poses[2], last);
    }

    [Fact]
    public void 빈_목록이면_초기값이_없다()
        => Assert.Null(InspectionPointInsert.NewPose(Array.Empty<double[]>(), 0));

    [Theory]
    [InlineData(3, 0, 0)]    // 맨 앞 → 뒤 행(첫 행)
    [InlineData(3, 1, 0)]
    [InlineData(3, 3, 2)]
    [InlineData(0, 0, -1)]
    public void 숫자가_아닌_값은_앞_행에서_물려받는다(int count, int insertAt, int expected)
        => Assert.Equal(expected, InspectionPointInsert.InheritFrom(count, insertAt));
}
