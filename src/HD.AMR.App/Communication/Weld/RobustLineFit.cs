namespace HD.AMR.App.Communication.Weld;

/// <summary>
/// 진행축 슬라이스별 비드 후보점들에서 직선(cross = A·s + B)을 RANSAC 으로 찾는다. 한 슬라이스에 후보가 여러 개
/// (비드 + 코로게이션 모서리 정반사 등) 있어도 <b>가장 많은 슬라이스를 일직선으로 지나는</b> 선을 고르므로,
/// 최장 run 하나만 보고 최소제곱하던 방식보다 반사·끊김에 강하다. 점수는 후보 수가 아니라 inlier 가 있는
/// <b>슬라이스 수</b>로 매겨 한 슬라이스의 잡음 여러 개가 표를 몰아주지 못하게 한다.
/// 시드 고정 난수라 같은 입력이면 같은 결과(재현성·테스트). 순수 함수 — OpenCV 불필요.
/// </summary>
public static class RobustLineFit
{
    /// <summary>슬라이스 s 의 후보 하나. Tag 는 호출자가 되찾을 인덱스(예: run 번호).</summary>
    public readonly record struct Candidate(int S, double Cross, int Tag = 0);

    /// <param name="A">기울기(cross/s).</param>
    /// <param name="B">절편.</param>
    /// <param name="Ok">선을 찾았는지.</param>
    /// <param name="InlierSlices">inlier 가 있는 슬라이스 수.</param>
    /// <param name="Chosen">슬라이스별 선택된 inlier 후보(선에 가장 가까운 것). 키=s.</param>
    public sealed record Result(double A, double B, bool Ok, int InlierSlices, IReadOnlyDictionary<int, Candidate> Chosen)
    {
        public static readonly Result Fail = new(0, 0, false, 0, new Dictionary<int, Candidate>());
    }

    public static Result Fit(IReadOnlyList<Candidate> cand, double inlierPx = 3.0, double maxAngleDeg = 30.0,
        int iterations = 400, int minInlierSlices = 3, int seed = 12345)
    {
        if (cand.Count < 2) return Result.Fail;
        double maxSlope = Math.Tan(Math.Clamp(maxAngleDeg, 0.1, 89.0) * Math.PI / 180.0);
        var rnd = new Random(seed);

        double bestA = 0, bestB = 0, bestResid = double.MaxValue;
        int bestScore = 0;
        bool found = false;
        for (int it = 0; it < iterations; it++)
        {
            var p = cand[rnd.Next(cand.Count)];
            var q = cand[rnd.Next(cand.Count)];
            if (p.S == q.S) continue;
            double a = (q.Cross - p.Cross) / (q.S - p.S);
            if (Math.Abs(a) > maxSlope) continue;
            double b = p.Cross - a * p.S;
            var (score, resid) = Score(cand, a, b, inlierPx);
            if (score > bestScore || (score == bestScore && resid < bestResid))
            {
                bestScore = score; bestResid = resid; bestA = a; bestB = b; found = true;
            }
        }
        if (!found || bestScore < minInlierSlices) return Result.Fail;

        // 정제: 슬라이스별 최근접 inlier 로 최소제곱 → 그 선으로 inlier 재선정(2회).
        double A = bestA, B = bestB;
        Dictionary<int, Candidate> chosen = PickInliers(cand, A, B, inlierPx);
        for (int r = 0; r < 2 && chosen.Count >= 2; r++)
        {
            var (a2, b2, ok) = LeastSquares(chosen.Values);
            if (!ok || Math.Abs(a2) > maxSlope) break;
            var next = PickInliers(cand, a2, b2, inlierPx);
            if (next.Count < chosen.Count) break;   // 정제가 오히려 나빠지면 멈춤
            A = a2; B = b2; chosen = next;
        }
        if (chosen.Count < minInlierSlices) return Result.Fail;
        return new Result(A, B, true, chosen.Count, chosen);
    }

    // 점수 = inlier 가 있는 서로 다른 슬라이스 수, 보조 = inlier 잔차 합.
    private static (int Score, double Resid) Score(IReadOnlyList<Candidate> cand, double a, double b, double tol)
    {
        var seen = new HashSet<int>();
        double resid = 0;
        foreach (var c in cand)
        {
            double r = Math.Abs(c.Cross - (a * c.S + b));
            if (r > tol) continue;
            resid += r;
            seen.Add(c.S);
        }
        return (seen.Count, resid);
    }

    private static Dictionary<int, Candidate> PickInliers(IReadOnlyList<Candidate> cand, double a, double b, double tol)
    {
        var best = new Dictionary<int, (Candidate C, double R)>();
        foreach (var c in cand)
        {
            double r = Math.Abs(c.Cross - (a * c.S + b));
            if (r > tol) continue;
            if (!best.TryGetValue(c.S, out var cur) || r < cur.R) best[c.S] = (c, r);
        }
        return best.ToDictionary(kv => kv.Key, kv => kv.Value.C);
    }

    public static (double A, double B, bool Ok) LeastSquares(IEnumerable<Candidate> pts)
    {
        double sx = 0, sy = 0, sxx = 0, sxy = 0; int m = 0;
        foreach (var p in pts)
        {
            sx += p.S; sy += p.Cross; sxx += (double)p.S * p.S; sxy += p.S * p.Cross; m++;
        }
        if (m < 2) return (0, 0, false);
        double denom = m * sxx - sx * sx;
        if (Math.Abs(denom) < 1e-9) return (0, 0, false);
        double a = (m * sxy - sx * sy) / denom;
        return (a, (sy - a * sx) / m, true);
    }
}
