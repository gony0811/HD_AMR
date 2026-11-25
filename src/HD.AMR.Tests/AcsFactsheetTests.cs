using System.Text.Json.Nodes;
using HD.AMR.App.Service;
using Xunit;

namespace HD.AMR.Tests;

/// <summary>ACS factsheet(§7.3) — 면별 ② 출발 자세·리프트 운영 방식·설정 버전·지문.</summary>
public class AcsFactsheetTests
{
    private static readonly double[] Home = { 0, -30, 120, -90, 90, 180 };
    private static readonly double[] Home2 = { 0.158, 59.456, -140.907, 80.848, 90.048, -179.696 };
    private static readonly double[] Home3 = { 1, 2, 3, 4, 5, 6 };
    private static readonly double[] Ready = { -0.391, -9.414, 119.984, -110.76, 90.001, 180.392 };
    private static readonly double[] WallT = { 10, 20, 30, 40, 50, 60 };

    private static AcsFactsheet.Inputs Inputs(Dictionary<string, double[]>? taught = null, bool liftOn = false, double disabledMm = 0,
        double[]? mount = null) => new(
        "SHORT", taught ?? new() { ["home"] = Home, ["home2"] = Home2, ["ready"] = Ready },
        liftOn, disabledMm, 0, 1000, 2.41, 0, mount ?? new double[] { 0, 0, 927, 0, 0, 180 },
        new double[] { 180, -970.317, -815.788, 159.25, 114.077, 105 }, 160);

    [Fact]
    public void 면별_출발_자세는_운영_규칙과_같다()
    {
        var taught = new Dictionary<string, double[]> { ["home2"] = Home2, ["ready"] = Ready, ["wall.T"] = WallT };
        var s = AcsFactsheet.StartJointsByWall(taught, new[] { "T", "PU", "F", "B", "SM" });
        Assert.Equal(WallT, s["T"]);          // 상부 면 = 그 면 검사 준비 슬롯
        Assert.Equal(Ready, s["PU"]);         // 상부 챔퍼는 홈 모드 — 작업 준비 위치(2026-11-24)
        Assert.Equal(Home2, AcsFactsheet.StartJointsByWall(
            new Dictionary<string, double[]> { ["home2"] = Home2 }, new[] { "T" })["T"]);   // 천장 슬롯이 없으면 홈 위치_2
        Assert.Equal(Ready, s["F"]);          // 그 외 = 작업 준비 위치
        Assert.Equal(Ready, s["B"]);          // 홈 위치_3 미티칭이면 하부 면도 ready
        Assert.Equal(Ready, s["SM"]);

        taught["home3"] = Home3;
        Assert.Equal(Home3, AcsFactsheet.StartJointsByWall(taught, new[] { "B" })["B"]);   // 홈 위치_3 이 있으면 하부 면은 그 자세
        taught["home4"] = WallT;
        var low = AcsFactsheet.StartJointsByWall(taught, new[] { "B", "PL" });
        Assert.Equal(WallT, low["B"]);        // 바닥은 홈 위치_4 우선
        Assert.Equal(Home3, low["PL"]);       // 하부 챔퍼는 홈 위치_3

        var none = AcsFactsheet.StartJointsByWall(new Dictionary<string, double[]>(), new[] { "T", "F" });
        Assert.Empty(none);                   // 티칭 없는 면은 싣지 않는다
    }

    [Fact]
    public void 리프트_운영_방식을_ACS_값으로_옮긴다()
    {
        Assert.Equal(("Auto", (double?)null), AcsFactsheet.LiftMode(true, 300));
        Assert.Equal(("Off", (double?)null), AcsFactsheet.LiftMode(false, 0));
        Assert.Equal(("Fixed", (double?)300), AcsFactsheet.LiftMode(false, 300));
    }

    [Fact]
    public void 본문에_네_묶음과_지문이_실린다()
    {
        var body = AcsFactsheet.Build(Inputs(disabledMm: 300), 3, DateTimeOffset.Parse("2026-11-20T01:02:03Z"), new[] { "lift" });
        var hd = body["hdAmr"]!.AsObject();
        Assert.Equal(1, (int)hd["schemaVersion"]!);
        Assert.Equal(3, (int)hd["configVersion"]!);
        Assert.Equal("2026-11-20T01:02:03.000Z", (string)hd["changedAt"]!);
        Assert.Equal("lift", (string)hd["changed"]![0]!);
        Assert.Equal("Fixed", (string)hd["lift"]!["mode"]!);
        Assert.Equal(300, (double)hd["lift"]!["fixedStrokeMm"]!);
        Assert.Equal(927, (double)hd["mount"]!["tAB"]![2]!);
        Assert.Equal(160, (double)hd["cobot"]!["maxEntryJointTravelDeg"]!);
        Assert.Equal(6, hd["teaching"]!["startJointsByWall"]!["T"]!.AsArray().Count);
        Assert.Equal(-140.907, (double)hd["teaching"]!["startJointsByWall"]!["T"]![2]!);
        Assert.Equal(Ready[1], (double)hd["teaching"]!["poses"]!["ready"]![1]!);
        foreach (var s in AcsFactsheet.Sections) Assert.Equal(16, ((string)hd["fingerprints"]![s]!).Length);
        Assert.Equal("HD_AMR", (string)body["typeSpecification"]!["seriesName"]!);

        // 장착 보정이 없으면(모두 0) null
        Assert.Null(AcsFactsheet.Build(Inputs(mount: new double[6]), 1, DateTimeOffset.UtcNow, Array.Empty<string>())["hdAmr"]!["mount"]!["tAB"]);
    }

    [Fact]
    public void 바뀐_묶음만_버전을_올린다()
    {
        var a = AcsFactsheet.Fingerprints(AcsFactsheet.BuildSections(Inputs()));
        Assert.Equal(a, AcsFactsheet.Fingerprints(AcsFactsheet.BuildSections(Inputs())));   // 같은 값 = 같은 지문

        var first = AcsFactsheet.Advance(0, null, a);                                           // 처음 = 버전 1, 변경 없음
        Assert.Equal(1, first.Version);
        Assert.Empty(first.Changed);
        var same = AcsFactsheet.Advance(4, a, a);
        Assert.Equal(4, same.Version);
        Assert.Empty(same.Changed);

        var lift = AcsFactsheet.Fingerprints(AcsFactsheet.BuildSections(Inputs(disabledMm: 300)));
        var r = AcsFactsheet.Advance(4, a, lift);
        Assert.Equal(5, r.Version);
        Assert.Equal(new[] { "lift" }, r.Changed);

        // 홈 위치_2 티칭 변경 = teaching 묶음, 장착 보정 변경 = mount 묶음
        var teach = AcsFactsheet.Fingerprints(AcsFactsheet.BuildSections(Inputs(
            taught: new() { ["home"] = Home, ["home2"] = WallT, ["ready"] = Ready }, mount: new double[] { 0, 0, 930, 0, 0, 180 })));
        Assert.Equal(new[] { "teaching", "mount" }, AcsFactsheet.Advance(5, a, teach).Changed);
    }

    [Fact]
    public void J6_안전_범위를_티칭_묶음에_싣고_바뀌면_다시_보고한다()
    {
        static AcsFactsheet.Inputs WithJ6(double lo, double hi, double sb) =>
            Inputs() with { SafeJ6MinDeg = lo, SafeJ6MaxDeg = hi, SafeJ6StandbyDeg = sb };
        var weld = AcsFactsheet.BuildSections(WithJ6(-320, -140, -180));
        var j6 = weld["teaching"]["safeJ6"]!;
        Assert.Equal(-320, j6["minDeg"]!.GetValue<double>());
        Assert.Equal(-140, j6["maxDeg"]!.GetValue<double>());
        Assert.Equal(-180, j6["standbyDeg"]!.GetValue<double>());
        Assert.False(AcsFactsheet.BuildSections(Inputs())["teaching"].ContainsKey("safeJ6"));   // 값 없으면 싣지 않는다

        // 대기 자세를 0° 로 바꾸면 teaching 묶음만 바뀐다 → 버전 +1
        var a = AcsFactsheet.Fingerprints(weld);
        var gas = AcsFactsheet.Fingerprints(AcsFactsheet.BuildSections(WithJ6(-140, 40, 0)));
        Assert.Equal(new[] { "teaching" }, AcsFactsheet.Advance(3, a, gas).Changed);

        // 실제 본문의 hdAmr.teaching.safeJ6 경로
        var body = AcsFactsheet.Build(WithJ6(-140, 40, 0), 4, DateTimeOffset.UnixEpoch, new[] { "teaching" });
        Assert.Equal(0, body["hdAmr"]!["teaching"]!["safeJ6"]!["standbyDeg"]!.GetValue<double>());
    }

    [Fact]
    public void 코봇_묶음에_관절_사용_범위를_싣는다()
    {
        var lo = new double[] { -175, -175, -160, -175, -175, -180 };
        var hi = new double[] { 175, 175, 160, 175, 175, 180 };
        var cobot = AcsFactsheet.Build(Inputs() with { JointMinDeg = lo, JointMaxDeg = hi }, 1, DateTimeOffset.UtcNow, Array.Empty<string>())
            ["hdAmr"]!["cobot"]!;
        Assert.Equal(lo, cobot["jointMinDeg"]!.AsArray().Select(v => v!.GetValue<double>()));
        Assert.Equal(160, cobot["jointMaxDeg"]![2]!.GetValue<double>());

        // 범위가 바뀌면 cobot 지문이 바뀌어 ACS 에 다시 보고된다.
        var a = AcsFactsheet.Fingerprints(AcsFactsheet.BuildSections(Inputs() with { JointMinDeg = lo, JointMaxDeg = hi }));
        var b = AcsFactsheet.Fingerprints(AcsFactsheet.BuildSections(Inputs()));
        Assert.NotEqual(a["cobot"], b["cobot"]);
        Assert.Equal(a["teaching"], b["teaching"]);

        // 없거나 6요소가 아니면 싣지 않는다(구버전 보고와 같다).
        Assert.Null(AcsFactsheet.Build(Inputs(), 1, DateTimeOffset.UtcNow, Array.Empty<string>())["hdAmr"]!["cobot"]!["jointMinDeg"]);
        Assert.Null(AcsFactsheet.Build(Inputs() with { JointMinDeg = new double[5], JointMaxDeg = hi }, 1, DateTimeOffset.UtcNow,
            Array.Empty<string>())["hdAmr"]!["cobot"]!["jointMaxDeg"]);
    }
}
