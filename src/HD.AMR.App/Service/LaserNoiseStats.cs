using System.Globalization;
using System.Text;

namespace HD.AMR.App.Service;

/// <summary>
/// 레이저 3점 측정 한 스냅샷 — 노이즈 통계 입력. <see cref="Values"/>/<see cref="Valid"/>/<see cref="Warning"/> 은 채널 순(0-based).
/// <see cref="Rx"/>/<see cref="Ry"/> 는 이 스냅샷으로 계산한 틸트(°), 계산 불가면 null.
/// </summary>
public sealed record LaserNoiseSample(
    double TimeS,
    IReadOnlyList<double> Values,
    IReadOnlyList<bool> Valid,
    IReadOnlyList<bool> Warning,
    double? Rx,
    double? Ry);

/// <summary>
/// 채널 하나의 N 샘플 통계. 단위 mm(시간 s).
/// </summary>
/// <param name="Channel">채널 번호(1-based).</param>
/// <param name="Samples">전체 샘플 수.</param>
/// <param name="Invalid">Enabled=false(범위 밖) 샘플 수 — 통계에서 제외.</param>
/// <param name="Warnings">Warning 비트(수광량 등) 샘플 수.</param>
/// <param name="Repeats">직전 유효 샘플과 값이 완전히 같은 횟수 — 많으면 폴링이 센서 갱신보다 빠른 것(같은 값 중복 판독).</param>
/// <param name="Mean">평균.</param>
/// <param name="Std">표본 표준편차.</param>
/// <param name="Median">중앙값.</param>
/// <param name="RobustStd">1.4826 × MAD — 스파이크에 둔감한 σ 추정. Std 와 크게 다르면 가끔 튀는 값이 섞였다는 뜻.</param>
/// <param name="Min">최솟값.</param>
/// <param name="Max">최댓값.</param>
/// <param name="Spikes">|x − 중앙값| &gt; 3 × RobustStd 인 샘플 수(Hampel 기준).</param>
/// <param name="SlopeMmPerS">시간 대비 선형회귀 기울기 — 0 이 아니면 정착 덜 됨(필터 지연)·드리프트.</param>
/// <param name="Lag1">lag-1 자기상관 — 1 에 가까우면 연속 샘플이 독립이 아니다.</param>
/// <param name="EffectiveSamples">유효 샘플 수 n·(1−ρ)/(1+ρ) — 평균의 표준오차 계산용.</param>
public sealed record LaserChannelNoise(
    int Channel,
    int Samples,
    int Invalid,
    int Warnings,
    int Repeats,
    double Mean,
    double Std,
    double Median,
    double RobustStd,
    double Min,
    double Max,
    int Spikes,
    double SlopeMmPerS,
    double Lag1,
    double EffectiveSamples)
{
    /// <summary>유효 샘플 수.</summary>
    public int ValidSamples => Samples - Invalid;

    /// <summary>평균의 표준오차 σ/√n_eff.</summary>
    public double StdError => EffectiveSamples > 0 ? Std / Math.Sqrt(EffectiveSamples) : double.NaN;
}

/// <summary>
/// 레이저 3점 측정 N 샘플의 노이즈 진단 결과 — 채널별 통계, 채널 간 상관, 샘플별 틸트의 흩어짐.
/// </summary>
/// <param name="Channels">채널별 통계.</param>
/// <param name="MeanCorrelation">채널 쌍 피어슨 상관 평균 — 1 에 가까우면 세 채널이 같이 움직임(로봇 Z 진동 등 공통 모드,
/// 틸트에는 거의 안 들어감), 0 근처면 채널마다 독립(센서·표면 잡음 — 틸트에 그대로 들어감). 계산 불가면 NaN.</param>
/// <param name="PoseSamples">틸트 계산이 된 샘플 수.</param>
/// <param name="RxMean">rx 평균(°).</param>
/// <param name="RxStd">rx 표본 표준편차(°) — 한 번 읽은 값이 이만큼 흔들린다.</param>
/// <param name="RyMean">ry 평균(°).</param>
/// <param name="RyStd">ry 표본 표준편차(°).</param>
/// <param name="RxStdError">rx 평균의 표준오차(°, n_eff 반영).</param>
/// <param name="RyStdError">ry 평균의 표준오차(°).</param>
/// <param name="DurationS">첫~마지막 샘플 시간(s).</param>
public sealed record LaserNoiseReport(
    IReadOnlyList<LaserChannelNoise> Channels,
    double MeanCorrelation,
    int PoseSamples,
    double RxMean,
    double RxStd,
    double RyMean,
    double RyStd,
    double RxStdError,
    double RyStdError,
    double DurationS)
{
    /// <summary>한 번 읽은 틸트의 σ(°) 한도 — 넘으면 판정 임계(기본 1°) 근처에서 합격/불합격이 잡음으로 갈린다.</summary>
    public const double MaxAngleStdDeg = 0.3;

    /// <summary>채널 σ(mm) 한도 — 헤드 간격 ~30mm 에서 독립 잡음 0.15mm ≈ 틸트 σ 0.4°.</summary>
    public const double MaxChannelStdMm = 0.15;

    /// <summary>측정 창 동안 선형 드리프트 합(mm) 한도 — 넘으면 정착 덜 된 상태에서 잰 것.</summary>
    public const double MaxDriftMm = 0.1;

    /// <summary>Warning 비트 비율 한도.</summary>
    public const double MaxWarningRatio = 0.2;

    /// <summary>
    /// 측정을 믿기 어려운 사유 목록(비었으면 안정). 판정 로직에는 쓰지 않고 진단·실패 메시지 분류용.
    /// </summary>
    public IReadOnlyList<string> InstabilityReasons()
    {
        var reasons = new List<string>();
        foreach (var c in Channels)
        {
            if (c.Invalid > 0)
                reasons.Add($"CH{c.Channel} 범위 밖 {c.Invalid}/{c.Samples}");
            if (c.Samples > 0 && (double)c.Warnings / c.Samples >= MaxWarningRatio)
                reasons.Add($"CH{c.Channel} Warning {c.Warnings}/{c.Samples}");
            if (c.ValidSamples >= 2 && c.Std > MaxChannelStdMm)
                reasons.Add($"CH{c.Channel} σ={F(c.Std, "0.###")}mm");
            if (c.ValidSamples >= 2 && Math.Abs(c.SlopeMmPerS * DurationS) > MaxDriftMm)
                reasons.Add($"CH{c.Channel} 드리프트 {F(c.SlopeMmPerS * DurationS, "+0.###;-0.###")}mm/{F(DurationS, "0.0")}s");
        }
        if (PoseSamples >= 2 && (RxStd > MaxAngleStdDeg || RyStd > MaxAngleStdDeg))
            reasons.Add($"틸트 σ rx={F(RxStd, "0.##")}°/ry={F(RyStd, "0.##")}°");
        return reasons;
    }

    /// <summary>안정 여부(<see cref="InstabilityReasons"/> 가 비었는가).</summary>
    public bool IsStable => InstabilityReasons().Count == 0;

    /// <summary>한 줄 요약 — 진행 로그·결과 메시지용.</summary>
    public string ShortText()
    {
        var sb = new StringBuilder();
        sb.Append($"노이즈 n={Channels.FirstOrDefault()?.Samples ?? 0}/{F(DurationS, "0.0")}s ");
        sb.Append($"σrx={F(RxStd, "0.###")}° σry={F(RyStd, "0.###")}° ");
        sb.Append("σCH=").Append(string.Join("/", Channels.Select(c => F(c.Std, "0.###")))).Append("mm ");
        sb.Append($"상관={F(MeanCorrelation, "0.##")}");
        return sb.ToString();
    }

    /// <summary>채널별 상세(여러 줄) — 로그용.</summary>
    public string DetailText()
    {
        var sb = new StringBuilder();
        sb.Append(ShortText());
        sb.Append($" | rx={F(RxMean, "0.###")}±{F(RxStdError, "0.###")}° ry={F(RyMean, "0.###")}±{F(RyStdError, "0.###")}° (평균±SE)");
        foreach (var c in Channels)
        {
            sb.AppendLine();
            sb.Append($"  CH{c.Channel}: mean={F(c.Mean, "0.###")} med={F(c.Median, "0.###")} " +
                      $"σ={F(c.Std, "0.####")} σMAD={F(c.RobustStd, "0.####")} range=[{F(c.Min, "0.###")}, {F(c.Max, "0.###")}] " +
                      $"slope={F(c.SlopeMmPerS, "+0.####;-0.####")}mm/s ρ1={F(c.Lag1, "0.##")} n_eff={F(c.EffectiveSamples, "0.#")} " +
                      $"spike={c.Spikes} repeat={c.Repeats} invalid={c.Invalid} warn={c.Warnings}");
        }
        return sb.ToString();
    }

    private static string F(double v, string fmt)
        => double.IsNaN(v) ? "n/a" : v.ToString(fmt, CultureInfo.InvariantCulture);
}

/// <summary>레이저 3점 노이즈 통계 계산(순수 함수).</summary>
public static class LaserNoiseAnalyzer
{
    /// <summary>Hampel 스파이크 판정 배수.</summary>
    internal const double SpikeSigma = 3.0;

    /// <summary>MAD → σ 환산 계수(정규분포).</summary>
    internal const double MadToSigma = 1.4826;

    public static LaserNoiseReport Analyze(IReadOnlyList<LaserNoiseSample> samples, int channelCount = 3)
    {
        var channels = new List<LaserChannelNoise>(channelCount);
        for (var c = 0; c < channelCount; c++)
            channels.Add(AnalyzeChannel(samples, c));

        var rx = samples.Where(s => s.Rx.HasValue).Select(s => s.Rx!.Value).ToArray();
        var ry = samples.Where(s => s.Ry.HasValue).Select(s => s.Ry!.Value).ToArray();

        var duration = samples.Count >= 2 ? samples[^1].TimeS - samples[0].TimeS : 0;
        return new LaserNoiseReport(
            channels,
            MeanPairCorrelation(samples, channelCount),
            rx.Length,
            Mean(rx), Std(rx),
            Mean(ry), Std(ry),
            StdError(rx), StdError(ry),
            duration);
    }

    private static LaserChannelNoise AnalyzeChannel(IReadOnlyList<LaserNoiseSample> samples, int c)
    {
        var t = new List<double>();
        var v = new List<double>();
        int invalid = 0, warnings = 0, repeats = 0;
        double? prev = null;
        foreach (var s in samples)
        {
            if (c < s.Warning.Count && s.Warning[c]) warnings++;
            if (c >= s.Values.Count || c >= s.Valid.Count || !s.Valid[c]) { invalid++; continue; }
            var x = s.Values[c];
            if (prev is { } p && p == x) repeats++;
            prev = x;
            t.Add(s.TimeS);
            v.Add(x);
        }

        var arr = v.ToArray();
        var median = Median(arr);
        var mad = Median(arr.Select(x => Math.Abs(x - median)).ToArray());
        var robust = MadToSigma * mad;
        var spikes = robust > 0 ? arr.Count(x => Math.Abs(x - median) > SpikeSigma * robust) : 0;
        var rho = Lag1(arr);

        return new LaserChannelNoise(
            c + 1, samples.Count, invalid, warnings, repeats,
            Mean(arr), Std(arr), median, robust,
            arr.Length > 0 ? arr.Min() : double.NaN,
            arr.Length > 0 ? arr.Max() : double.NaN,
            spikes,
            Slope(t.ToArray(), arr),
            rho,
            EffectiveN(arr.Length, rho));
    }

    internal static double Mean(double[] x) => x.Length > 0 ? x.Average() : double.NaN;

    /// <summary>표본 표준편차(n−1). 샘플 1개 이하면 NaN.</summary>
    internal static double Std(double[] x)
    {
        if (x.Length < 2) return double.NaN;
        var m = x.Average();
        return Math.Sqrt(x.Sum(a => (a - m) * (a - m)) / (x.Length - 1));
    }

    internal static double Median(double[] x)
    {
        if (x.Length == 0) return double.NaN;
        var s = x.OrderBy(a => a).ToArray();
        var mid = s.Length / 2;
        return s.Length % 2 == 1 ? s[mid] : (s[mid - 1] + s[mid]) / 2;
    }

    /// <summary>lag-1 자기상관. 변동이 없거나 샘플 2개 미만이면 0.</summary>
    internal static double Lag1(double[] x)
    {
        if (x.Length < 3) return 0;
        var m = x.Average();
        double num = 0, den = 0;
        for (var i = 0; i < x.Length; i++)
        {
            var d = x[i] - m;
            den += d * d;
            if (i + 1 < x.Length) num += d * (x[i + 1] - m);
        }
        return den > 0 ? num / den : 0;
    }

    /// <summary>AR(1) 근사 유효 샘플 수 n·(1−ρ)/(1+ρ), [1, n] 로 제한(ρ≤0 이면 n).</summary>
    internal static double EffectiveN(int n, double rho)
    {
        if (n == 0) return 0;
        if (rho <= 0) return n;
        return Math.Clamp(n * (1 - rho) / (1 + rho), 1, n);
    }

    /// <summary>평균의 표준오차 σ/√n_eff.</summary>
    internal static double StdError(double[] x)
    {
        var s = Std(x);
        if (double.IsNaN(s)) return double.NaN;
        return s / Math.Sqrt(EffectiveN(x.Length, Lag1(x)));
    }

    /// <summary>최소제곱 기울기 dy/dt. 시간 분산이 없으면 0.</summary>
    internal static double Slope(double[] t, double[] y)
    {
        if (t.Length < 2) return 0;
        var mt = t.Average();
        var my = y.Average();
        double num = 0, den = 0;
        for (var i = 0; i < t.Length; i++)
        {
            num += (t[i] - mt) * (y[i] - my);
            den += (t[i] - mt) * (t[i] - mt);
        }
        return den > 0 ? num / den : 0;
    }

    /// <summary>모든 채널이 유효한 샘플에서 채널 쌍 피어슨 상관의 평균. 계산 가능한 쌍이 없으면 NaN.</summary>
    internal static double MeanPairCorrelation(IReadOnlyList<LaserNoiseSample> samples, int channelCount)
    {
        var rows = samples
            .Where(s => s.Values.Count >= channelCount && s.Valid.Count >= channelCount &&
                        Enumerable.Range(0, channelCount).All(c => s.Valid[c]))
            .ToList();
        if (rows.Count < 3) return double.NaN;

        double sum = 0;
        int pairs = 0;
        for (var a = 0; a < channelCount; a++)
        for (var b = a + 1; b < channelCount; b++)
        {
            var r = Pearson(rows.Select(s => s.Values[a]).ToArray(), rows.Select(s => s.Values[b]).ToArray());
            if (double.IsNaN(r)) continue;
            sum += r;
            pairs++;
        }
        return pairs > 0 ? sum / pairs : double.NaN;
    }

    internal static double Pearson(double[] x, double[] y)
    {
        var mx = x.Average();
        var my = y.Average();
        double sxy = 0, sxx = 0, syy = 0;
        for (var i = 0; i < x.Length; i++)
        {
            sxy += (x[i] - mx) * (y[i] - my);
            sxx += (x[i] - mx) * (x[i] - mx);
            syy += (y[i] - my) * (y[i] - my);
        }
        return sxx > 0 && syy > 0 ? sxy / Math.Sqrt(sxx * syy) : double.NaN;
    }
}

/// <summary>정착 판정 결과. <see cref="Reasons"/> 는 미정착 사유(채널별 드리프트·범위 밖), 정착이면 빈 목록.</summary>
public sealed record LaserSettleResult(bool Settled, IReadOnlyList<string> Reasons);

/// <summary>
/// 모션 후 정착 판정 — 창 안 채널별 선형회귀 기울기가 <b>물리적으로 크고(&gt; maxSlope) 통계적으로도 유의(&gt; k·SE)</b>할 때만
/// "아직 흘러가는 중"으로 본다. 종전 기준(3샘플 평균 간 |Δ|&lt;0.05mm)은 잡음 σ 0.2mm 채널이 있으면 평균 간 차이 자체가
/// σ≈0.17mm 라 거의 만족할 수 없어 매번 한도까지 기다린 뒤 정착 여부와 무관하게 측정했다(11-09 10:40 CH2).
/// 기울기 SE 는 추세 제거 잔차의 σ 와 lag-1 자기상관(앰프 평균화로 연속 샘플이 독립이 아님)을 반영한다.
/// </summary>
public static class LaserSettleAnalyzer
{
    public static LaserSettleResult Assess(IReadOnlyList<LaserNoiseSample> window, double maxSlopeMmPerS,
        double significance, int channelCount = 3)
    {
        var reasons = new List<string>();
        for (var c = 0; c < channelCount; c++)
        {
            var t = new List<double>();
            var v = new List<double>();
            foreach (var s in window)
            {
                if (c >= s.Values.Count || c >= s.Valid.Count || !s.Valid[c]) continue;
                t.Add(s.TimeS);
                v.Add(s.Values[c]);
            }
            if (v.Count < Math.Max(3, window.Count / 2))
            {
                reasons.Add($"CH{c + 1} 유효 {v.Count}/{window.Count}");
                continue;
            }

            var (slope, se) = SlopeWithError(t.ToArray(), v.ToArray());
            if (Math.Abs(slope) > maxSlopeMmPerS && Math.Abs(slope) > significance * se)
                reasons.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"CH{c + 1} 드리프트 {slope:+0.###;-0.###}mm/s(±{se:0.###})"));
        }
        return new LaserSettleResult(reasons.Count == 0, reasons);
    }

    /// <summary>최소제곱 기울기와 그 표준오차. SE = σ_잔차/√Σ(t−t̄)² × √(n/n_eff), n_eff 는 잔차 lag-1 로 계산.</summary>
    internal static (double Slope, double StdError) SlopeWithError(double[] t, double[] y)
    {
        var n = t.Length;
        if (n < 3) return (0, double.PositiveInfinity);
        var mt = t.Average();
        var my = y.Average();
        double sxx = 0, sxy = 0;
        for (var i = 0; i < n; i++)
        {
            sxx += (t[i] - mt) * (t[i] - mt);
            sxy += (t[i] - mt) * (y[i] - my);
        }
        if (sxx <= 0) return (0, double.PositiveInfinity);
        var slope = sxy / sxx;
        var resid = new double[n];
        for (var i = 0; i < n; i++) resid[i] = y[i] - (my + slope * (t[i] - mt));
        var sigma = Math.Sqrt(resid.Sum(r => r * r) / (n - 2));
        var neff = LaserNoiseAnalyzer.EffectiveN(n, LaserNoiseAnalyzer.Lag1(resid));
        var se = sigma / Math.Sqrt(sxx) * Math.Sqrt(n / Math.Max(1, neff));
        return (slope, se);
    }
}
