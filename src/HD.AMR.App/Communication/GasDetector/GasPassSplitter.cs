namespace HD.AMR.App.Communication.GasDetector;

/// <summary>
/// 무정지 연속 측정(경유점에 멈추지 않고 여러 구간을 한 번에 지나가며 샘플링)의 순수 계산 — 샘플을 구간별로 나누고,
/// 경유점 블렌딩 반경을 구간 길이에 맞춰 줄인다.
/// </summary>
public static class GasPassSplitter
{
    /// <summary>블렌딩 반경이 인접 구간 길이에서 차지할 수 있는 최대 비율 — 앞뒤 경유점의 블렌딩이 겹치지 않게 절반 미만으로 둔다.</summary>
    public const double MaxBlendFraction = 0.45;

    /// <summary>
    /// 한 번의 무정지 이동 중에 읽은 샘플을 구간별로 나눈다. 샘플 시각에서 스니퍼 응답 지연을 뺀 뒤 이동 시간에 대한
    /// 비율을 경로 길이 비율로 본다(등속 가정 — 시작·끝 가감속은 무시한 추정). 이동 전·후(응답 지연 꼬리) 샘플은
    /// 첫·마지막 구간에 넣는다. 샘플 간격보다 빨리 지나가 샘플이 없는 구간은 시간상 가장 가까운 샘플 1개를 빌린다.
    /// </summary>
    /// <param name="lengthsMm">구간 길이(순서대로). 모두 0 이면 같은 길이로 본다.</param>
    public static IReadOnlyList<IReadOnlyList<(DateTime At, double Value)>> Split(
        IReadOnlyList<(DateTime At, double Value)> samples, DateTime moveStartUtc, DateTime moveEndUtc,
        TimeSpan responseDelay, IReadOnlyList<double> lengthsMm)
    {
        var n = lengthsMm.Count;
        var parts = Enumerable.Range(0, n).Select(_ => new List<(DateTime At, double Value)>()).ToList();
        if (n == 0 || samples.Count == 0) return parts;

        // 구간 끝의 누적 길이 비율(마지막 = 1).
        var lengths = lengthsMm.Select(l => Math.Max(0, l)).ToArray();
        var total = lengths.Sum();
        var bounds = new double[n];
        var acc = 0.0;
        for (var k = 0; k < n; k++)
        {
            acc += total > 0 ? lengths[k] / total : 1.0 / n;
            bounds[k] = acc;
        }
        bounds[n - 1] = 1.0;

        var durationMs = (moveEndUtc - moveStartUtc).TotalMilliseconds;
        double FractionOf(DateTime at) => durationMs <= 0
            ? 1.0
            : Math.Clamp((at - responseDelay - moveStartUtc).TotalMilliseconds / durationMs, 0, 1);

        var fractions = samples.Select(s => FractionOf(s.At)).ToArray();
        for (var i = 0; i < samples.Count; i++)
        {
            var k = Array.FindIndex(bounds, b => fractions[i] <= b);
            parts[k < 0 ? n - 1 : k].Add(samples[i]);
        }

        for (var k = 0; k < n; k++)
        {
            if (parts[k].Count > 0) continue;
            var mid = ((k == 0 ? 0 : bounds[k - 1]) + bounds[k]) / 2;
            var nearest = 0;
            for (var i = 1; i < samples.Count; i++)
                if (Math.Abs(fractions[i] - mid) < Math.Abs(fractions[nearest] - mid))
                    nearest = i;
            parts[k].Add(samples[nearest]);
        }
        return parts;
    }

    /// <summary>
    /// 중간 경유점(구간 사이)마다 쓸 블렌딩 반경 [mm] — 설정 반경을 넘지 않고, 앞뒤 구간 중 짧은 쪽의
    /// <see cref="MaxBlendFraction"/> 를 넘지 않는다. 개수 = 구간 수 − 1.
    /// </summary>
    public static double[] BlendRadii(IReadOnlyList<double> lengthsMm, double radiusMm)
    {
        var radii = new double[Math.Max(0, lengthsMm.Count - 1)];
        for (var k = 0; k < radii.Length; k++)
            radii[k] = Math.Max(0, Math.Min(radiusMm, MaxBlendFraction * Math.Min(lengthsMm[k], lengthsMm[k + 1])));
        return radii;
    }

    /// <summary>두 pose([x,y,z,…]) 사이 위치 거리 [mm].</summary>
    public static double Distance(double[] a, double[] b)
        => Math.Sqrt(Math.Pow(b[0] - a[0], 2) + Math.Pow(b[1] - a[1], 2) + Math.Pow(b[2] - a[2], 2));
}
