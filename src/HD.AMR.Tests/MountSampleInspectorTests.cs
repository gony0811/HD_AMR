using HD.AMR.App.Service;

namespace HD.AMR.Tests;

/// <summary>
/// 표본 메타데이터 점검. 여기서 잡는 것들은 <b>잔차로는 절대 드러나지 않는</b> 오염이다 —
/// 특히 공구 번호 혼용은 그럴듯하지만 틀린 기울기로 위장된다.
/// </summary>
public class MountSampleInspectorTests
{
    private static MountSample Sample(int i, double x, double y, double yaw,
        int? tool = 1, double bz = -800, DateTime? at = null) => new()
    {
        Index = i, AmrXmm = x, AmrYmm = y, AmrYawDeg = yaw,
        Bx = 900 + 40 * i, By = 100 - 30 * i, Bz = bz,
        Tool = tool, CapturedAtUtc = at ?? DateTime.UtcNow,
    };

    private static List<MountSample> Clean() => new()
    {
        Sample(0, 3000, 1500,   0),
        Sample(1, 3400, 1800,  90),
        Sample(2, 2700, 1200, 180),
        Sample(3, 3100, 2000, -90),
    };

    [Fact]
    public void Inspect_CleanSet_ReturnsNoWarnings()
        => Assert.Empty(MountSampleInspector.Inspect(Clean()));

    [Fact]
    public void Inspect_EmptySet_ReturnsNoWarnings()
        => Assert.Empty(MountSampleInspector.Inspect(new List<MountSample>()));

    [Fact]
    public void Inspect_MixedToolNumbers_Warns()
    {
        var list = Clean();
        list[2].Tool = 0;   // 플랜지 — 나머지는 프로브(tool 1)

        Assert.Contains(MountSampleInspector.Inspect(list), w => w.Contains("공구 번호"));
    }

    [Fact]
    public void Inspect_AllBzZero_WarnsLegacySamples()
    {
        var list = Clean();
        foreach (var s in list) s.Bz = 0.0;

        Assert.Contains(MountSampleInspector.Inspect(list), w => w.Contains("BASE Z"));
    }

    [Fact]
    public void Inspect_DuplicatePose_Warns()
    {
        var list = Clean();
        list.Add(Sample(4, 3000 + 10, 1500 - 15, 1.0));   // #1 과 사실상 동일

        Assert.Contains(MountSampleInspector.Inspect(list), w => w.Contains("같은 AMR 자세"));
    }

    // yaw 가 랩 경계를 넘어도 중복 판정이 동작해야 한다(359.5° 와 −0.4° 는 같은 자세다).
    [Fact]
    public void Inspect_DuplicatePoseAcrossYawWrap_Warns()
    {
        var list = Clean();
        list[0].AmrYawDeg = 359.5;
        list.Add(Sample(4, 3000, 1500, -0.4));

        Assert.Contains(MountSampleInspector.Inspect(list), w => w.Contains("같은 AMR 자세"));
    }

    [Fact]
    public void Inspect_StaleSamples_Warn()
    {
        var old = DateTime.UtcNow.AddDays(-45);
        var list = Clean();
        foreach (var s in list) s.CapturedAtUtc = old;

        Assert.Contains(MountSampleInspector.Inspect(list), w => w.Contains("일 전"));
    }

    [Fact]
    public void Inspect_SamplesSpanningManyDays_Warn()
    {
        var list = Clean();
        list[0].CapturedAtUtc = DateTime.UtcNow.AddDays(-20);

        Assert.Contains(MountSampleInspector.Inspect(list), w => w.Contains("걸쳐"));
    }

    // 구버전 표본은 Tool/CapturedAtUtc 가 null — 그 자체로 경고를 내면 안 된다.
    [Fact]
    public void Inspect_NullProvenanceFields_DoNotWarnByThemselves()
    {
        var list = Clean();
        foreach (var s in list) { s.Tool = null; s.CapturedAtUtc = null; }

        Assert.Empty(MountSampleInspector.Inspect(list));
    }
}
