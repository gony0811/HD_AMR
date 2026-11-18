using HD.AMR.App.Data.Entities;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.Tests;

/// <summary>검사 순회 경유점별 레이저 WD 요약 로그 — 초점 어긋남 분석용(2026-11-17).</summary>
public class InspectionWdLogTests
{
    private static readonly List<InspectionWaypoint> Wps = new()
    {
        new(0, 33.8, 0, false),
        new(5, 33.4, 10, false),
        new(45, 0, 0, false),
        new(90, 0, 0, false),
    };

    [Fact]
    public void 기준은_첫_평면점_차이는_평면_경사_따로()
    {
        var wd = new Dictionary<int, double?> { [0] = 343.0, [1] = 340.5, [2] = 343.4, [3] = 345.1 };
        var s = InspectionRunStep.FormatWdLog(Wps, wd)!;

        Assert.StartsWith("기준 343.0mm (#1)", s);
        Assert.Contains("평면 Δ 0.0~+2.1", s);
        Assert.Contains("경사 Δ -2.5~-2.5", s);
        Assert.Contains("#2 θ10 Z33.4 -2.5", s);
        Assert.Contains("#4 θ0 Z0 +2.1", s);
    }

    [Fact]
    public void 레이저_없는_점은_없음_전부_없으면_null()
    {
        var wd = new Dictionary<int, double?> { [0] = null, [1] = 341.0, [2] = 342.0 };
        var s = InspectionRunStep.FormatWdLog(Wps, wd)!;
        Assert.StartsWith("기준 342.0mm (#3)", s);
        Assert.Contains("#1 θ0 Z33.8 없음", s);

        Assert.Null(InspectionRunStep.FormatWdLog(Wps, new Dictionary<int, double?> { [0] = null }));
    }
}
