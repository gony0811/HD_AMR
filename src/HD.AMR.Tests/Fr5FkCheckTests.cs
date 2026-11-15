using HD.AMR.App.Communication;
using HD.AMR.App.Service.Motion;
using Xunit;
using Xunit.Abstractions;

namespace HD.AMR.Tests;

/// <summary>d6(106 / 160)·관절 0점·부호의 실물 대조 — 표본이 오기 전에도 대조 계산 자체가 맞는지 확인한다.</summary>
public class Fr5FkCheckTests
{
    /// <summary>공칭 치수의 기구학 — 정적 Fr5Wml 은 파라미터로 바뀔 수 있어 테스트는 공칭을 명시한다.</summary>
    private static readonly Fr5Kinematics Nominal = Fr5Kinematics.FromLinks(Fr5Kinematics.NominalLinksMm).Kinematics;

    private readonly ITestOutputHelper _out;
    public Fr5FkCheckTests(ITestOutputHelper output) => _out = output;

    /// <summary>현장 캡처 스크립트가 쓰는 관절 자세와 같은 8개(tools/fr5_fk_capture.py).</summary>
    private static readonly double[][] Poses =
    {
        new[] { 0.158, 59.456, -140.907, 80.848, 90.048, -179.696 },   // 홈 위치_2
        new[] { -0.391, -9.414, 119.984, -110.76, 90.001, 180.392 },   // 작업 준비 위치
        new[] { 0.0, -90, 90, -90, 90, 0 },
        new[] { 30.0, -60, 100, -130, 60, 45 },
        new[] { -45.0, -120, -80, -70, -75, -90 },
        new[] { 90.0, -30, 60, -160, 120, 120 },
        new[] { -120.0, -75, 110, 20, 45, -30 },
        new[] { 150.0, 20, -100, -200, -100, 170 },
    };

    private static List<FkSample> Controller(Fr5Kinematics real, JointConvention? conv = null) =>
        Poses.Select(q => new FkSample(q, Fr5FkCheck.ToPose(real.ForwardFlange((conv ?? JointConvention.Identity).Apply(q))))).ToList();

    [Fact]
    public void D6_106과_160은_플랜지를_축_방향으로_54mm_옮기고_자세는_같다()
    {
        var a = Nominal.WithD6(Fr5Kinematics.D6StepMm);
        var b = Nominal.WithD6(Fr5Kinematics.D6DatasheetMm);
        foreach (var q in Poses)
        {
            var (fa, fb) = (a.ForwardFlange(q), b.ForwardFlange(q));
            var along = Enumerable.Range(0, 3).Sum(i => (fb[i, 3] - fa[i, 3]) * fa[i, 2]);
            var dist = Math.Sqrt(Enumerable.Range(0, 3).Sum(i => Math.Pow(fb[i, 3] - fa[i, 3], 2)));
            Assert.Equal(54.0, along, 9);
            Assert.Equal(54.0, dist, 9);
            for (var i = 0; i < 3; i++)
                for (var k = 0; k < 3; k++)
                    Assert.Equal(fa[i, k], fb[i, k], 12);
        }
    }

    [Fact]
    public void 같은_플랜지_목표를_d6_106과_160으로_풀면_관절각이_달라진다()
    {
        var a = Nominal.WithD6(Fr5Kinematics.D6StepMm);
        var b = Nominal.WithD6(Fr5Kinematics.D6DatasheetMm);
        var worst = 0.0;
        foreach (var q in Poses)
        {
            var target = a.ForwardFlange(q);
            // 같은 가지(관절값이 가장 가까운 해)끼리 비교
            double Dist(double[] s) => Enumerable.Range(0, 6).Max(i => Math.Abs(Wrap(s[i] - q[i])));
            var sa = a.InverseFlange(target).MinBy(Dist)!;
            Assert.True(Dist(sa) < 1e-6);   // 자기 d6 로 풀면 원래 관절각이 나온다(역기구학 식 자체는 맞다)
            var sols = b.InverseFlange(target);
            if (sols.Count == 0) continue;
            var sb = sols.MinBy(Dist)!;
            // 푼 해는 그 d6 의 순기구학으로 목표를 재현한다
            var back = b.ForwardFlange(sb);
            for (var i = 0; i < 3; i++) Assert.Equal(target[i, 3], back[i, 3], 6);
            worst = Math.Max(worst, Dist(sb));
        }
        _out.WriteLine($"d6 106 → 160 관절각 최대 차이 {worst:0.0}°");
        Assert.True(worst > 2, $"d6 가 54 mm 달라지면 관절각이 눈에 띄게 달라져야 한다(최대 {worst:0.0}°)");
    }

    [Fact]
    public void 컨트롤러가_d6_160이면_대조가_160을_가리킨다()
    {
        var samples = Controller(Nominal.WithD6(160));
        var v = Fr5FkCheck.Check(Nominal.WithD6(106), samples);
        _out.WriteLine(v.Summary());
        Assert.False(v.Configured.Matches);
        Assert.Equal(54.0, v.Configured.PosMaxMm, 6);
        Assert.Equal(160.0, v.EstimatedD6Mm, 6);
        Assert.Equal(160.0, v.MatchingCandidateMm);
        Assert.True(v.AtEstimated.Matches);
        Assert.True(v.Convention.IsIdentity);
    }

    [Fact]
    public void 컨트롤러가_d6_106이면_설정값이_일치한다()
    {
        var v = Fr5FkCheck.Check(Nominal.WithD6(106), Controller(Nominal.WithD6(106)));
        Assert.True(v.Configured.Matches);
        Assert.Equal(106.0, v.MatchingCandidateMm);
    }

    [Fact]
    public void 관절_0점과_부호가_다르면_진단이_찾아낸다()
    {
        // 컨트롤러 규약: J2 0점이 90° 다르고 J3 부호가 반대, d6 는 160
        var conv = new JointConvention(new[] { 1, 1, -1, 1, 1, 1 }, new double[] { 0, -90, 0, 0, 0, 0 });
        var samples = Controller(Nominal.WithD6(160), conv);
        var v = Fr5FkCheck.Check(Nominal, samples);
        _out.WriteLine(v.Summary());
        Assert.False(v.Configured.Matches);
        Assert.Null(v.MatchingCandidateMm);
        Assert.False(v.AtEstimated.Matches);       // d6 만 바꿔서는 맞지 않는다
        Assert.True(v.AtConvention.Matches);
        Assert.Equal(conv.Sign, v.Convention.Sign);
        Assert.Equal(conv.OffsetDeg, v.Convention.OffsetDeg);
        Assert.Equal(160.0, v.ConventionD6Mm, 4);
    }

    [Fact]
    public void 공구와_작업물_좌표계가_걸린_값을_BASE_플랜지로_되돌린다()
    {
        var kin = Nominal;
        var tool = new[] { 5.3, -3.2, 365, 180, 0, 90.0 };
        var wobj = new[] { 100.0, -200, 300, 10, 20, 30 };
        var q = Poses[3];
        var flange = kin.ForwardFlange(q);
        // 컨트롤러가 주는 값 = T_wobj⁻¹ · T_flange · T_tool
        var fk = PoseMath.Multiply(PoseMath.Multiply(PoseMath.Inverse(Fr5FkCheck.FromPose(wobj)), flange), Fr5FkCheck.FromPose(tool));
        var back = Fr5FkCheck.FlangeInBase(Fr5FkCheck.ToPose(fk), tool, wobj);
        for (var i = 0; i < 3; i++)
            for (var k = 0; k < 4; k++)
                Assert.Equal(flange[i, k], back[i, k], 6);

        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            tool = new { id = 1, pose = tool },
            wobj = new { id = 5, pose = wobj },
            samples = new[] { new { jointsDeg = q, fk = Fr5FkCheck.ToPose(fk) } },
        });
        var loaded = Fr5FkCheck.LoadSamples(json);
        Assert.Single(loaded);
        Assert.True(Fr5FkCheck.Residual(kin, loaded).Matches);
    }

    [Fact]
    public void 설정_링크_치수_해석()
    {
        Assert.Equal((Nominal, true), Fr5Kinematics.FromLinks(null));
        var (cal, ok) = Fr5Kinematics.FromLinks(Fr5Kinematics.WeldAmrLinksMm);
        Assert.True(ok);
        Assert.Equal(Fr5Kinematics.WeldAmrLinksMm, cal.LinksMm);
        Assert.Equal((Nominal, false), Fr5Kinematics.FromLinks(new[] { 180.0, -970, -816, 159, 114 }));
        Assert.Equal((Nominal, false), Fr5Kinematics.FromLinks(new[] { 180.0, -970, -816, 159, 114, 160 }));   // d6 160 은 받지 않는다
        Assert.Equal((Nominal, false), Fr5Kinematics.FromLinks(new[] { 180.0, -970, double.NaN, 159, 114, 106 }));
    }

    [Fact]
    public void 실물_보정_치수는_공칭과_1mm_안팎_차이다()
    {
        // 2026-11-16 실물 대조: 공칭 치수는 컨트롤러와 최대 1.34 mm(RMS 0.94) 차이였고, DH 보정값을 넣으면 0.000 mm 였다.
        // 보정값을 '컨트롤러'로 놓고 공칭을 대조하면 같은 크기의 오차가 나와야 한다(d6 160 은 50 mm 넘게 어긋난다).
        var (cal, _) = Fr5Kinematics.FromLinks(Fr5Kinematics.WeldAmrLinksMm);
        var v = Fr5FkCheck.Check(Nominal, Controller(cal));
        _out.WriteLine(v.Summary());
        Assert.True(v.Configured.Matches);                       // 공칭도 판정 기준(2 mm·0.5°) 안
        Assert.InRange(v.Configured.PosMaxMm, 0.5, 2.0);
        Assert.Equal(Fr5Kinematics.D6StepMm, v.MatchingCandidateMm);
        Assert.False(v.Candidates[Fr5Kinematics.D6DatasheetMm].Matches);
        Assert.True(v.Candidates[Fr5Kinematics.D6DatasheetMm].PosMaxMm > 50);
        Assert.True(v.Convention.IsIdentity);                    // 관절 0점·부호는 컨트롤러와 같다
        Assert.True(Fr5FkCheck.Residual(cal, Controller(cal)).PosMaxMm < 1e-9);
    }

    [Fact]
    public void 파라미터_문자열을_읽고_쓴다()
    {
        Assert.Equal("180,-970.317,-815.788,159.25,114.077,105", Fr5Kinematics.FormatLinks(Fr5Kinematics.WeldAmrLinksMm));
        Assert.Equal(Fr5Kinematics.WeldAmrLinksMm, Fr5Kinematics.ParseLinks(" 180, -970.317,-815.788 ,159.25,114.077,105.0 "));
        Assert.Null(Fr5Kinematics.ParseLinks(""));
        Assert.Empty(Fr5Kinematics.ParseLinks("180,abc,1")!);
        Assert.False(Fr5Kinematics.FromLinks(Fr5Kinematics.ParseLinks("180,abc,1")).Ok);
        Assert.True(Fr5Kinematics.FromLinks(Fr5Kinematics.ParseLinks(null)).Ok);
    }

    /// <summary>
    /// 현장 표본(Data/fr5_fk_samples.json)과 공칭 치수를 대조한다. 표본 파일이 비어 있으면 건너뛴다
    /// (2026-11-16 대조는 다른 PC 에서 했고 그 표본 파일은 이 저장소에 없다 — 결과 수치는 <see cref="Fr5Kinematics.NominalLinksMm"/> 주석).
    /// 다른 장비·펌웨어에서 다시 받은 표본을 넣었을 때 실패하면 요약에 나온 d6·관절 규약을 확인할 것.
    /// </summary>
    [Fact]
    public void 현장_표본이_있으면_기본_d6와_관절_규약이_실물과_맞아야_한다()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "fr5_fk_samples.json");
        var samples = Fr5FkCheck.LoadSamples(File.ReadAllText(path));
        if (samples.Count == 0)
        {
            _out.WriteLine("표본 파일 없음 — 대조를 건너뛴다(2026-11-16 실물 대조 결과: d6 106 mm 확인).");
            return;
        }
        Assert.True(samples.Count >= 5, $"표본이 {samples.Count}개 — 서로 다른 자세 5개 이상이 필요하다");
        var v = Fr5FkCheck.Check(Nominal, samples);
        _out.WriteLine(v.Summary());
        Assert.True(v.Configured.Matches, "공칭 치수·관절 규약이 실물과 다르다:" + Environment.NewLine + v.Summary());
    }

    private static double Wrap(double d)
    {
        d %= 360;
        return d > 180 ? d - 360 : d < -180 ? d + 360 : d;
    }
}
