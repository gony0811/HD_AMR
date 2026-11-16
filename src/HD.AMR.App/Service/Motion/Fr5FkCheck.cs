using System.Text.Json;
using HD.AMR.App.Communication;

namespace HD.AMR.App.Service.Motion;

/// <summary>실물 대조 표본 하나 — 관절각[도]과 그때 컨트롤러가 준 BASE 기준 플랜지 pose [x,y,z,rx,ry,rz](mm·도, R = Rz·Ry·Rx).</summary>
public sealed record FkSample(double[] JointsDeg, double[] FlangePose);

/// <summary>관절 규약 — 모델 관절각 = 부호 × 컨트롤러 관절각 + 0점 차이[도].</summary>
public sealed record JointConvention(int[] Sign, double[] OffsetDeg)
{
    public static JointConvention Identity => new(new[] { 1, 1, 1, 1, 1, 1 }, new double[6]);
    public bool IsIdentity => Sign.All(s => s == 1) && OffsetDeg.All(o => Math.Abs(o) < 1e-9);
    public double[] Apply(double[] controllerDeg) => controllerDeg.Select((q, i) => Sign[i] * q + OffsetDeg[i]).ToArray();

    public override string ToString() => IsIdentity
        ? "컨트롤러와 같음"
        : string.Join(", ", Enumerable.Range(0, 6).Where(i => Sign[i] != 1 || Math.Abs(OffsetDeg[i]) > 1e-9)
            .Select(i => $"J{i + 1} = {(Sign[i] < 0 ? "−" : "")}q{(OffsetDeg[i] == 0 ? "" : $" {OffsetDeg[i]:+0;-0}°")}"));
}

/// <summary>대조 결과 — 위치 오차 RMS·최대[mm], 자세 오차 최대[도].</summary>
public sealed record FkResidual(double PosRmsMm, double PosMaxMm, double RotMaxDeg)
{
    /// <summary>모델이 실물과 맞는다고 볼 기준 — 위치 2 mm·자세 0.5° 이내(컨트롤러 DH 보정값·표시 반올림 포함).</summary>
    public bool Matches => PosMaxMm <= 2.0 && RotMaxDeg <= 0.5;
    public override string ToString() => $"위치 RMS {PosRmsMm:0.00} mm · 최대 {PosMaxMm:0.00} mm · 자세 최대 {RotMaxDeg:0.00}°";
}

/// <summary>대조 종합 — d6 후보별 오차, 표본에서 거꾸로 구한 d6, (맞지 않으면) 관절 0점·부호 진단.</summary>
public sealed record FkVerdict(
    int Samples, double ConfiguredD6Mm, FkResidual Configured,
    IReadOnlyDictionary<double, FkResidual> Candidates, double EstimatedD6Mm, FkResidual AtEstimated,
    JointConvention Convention, double ConventionD6Mm, FkResidual AtConvention)
{
    /// <summary>표본이 가리키는 d6 후보(오차 기준 통과하는 것 중 가장 잘 맞는 값) — 없으면 null.</summary>
    public double? MatchingCandidateMm => Candidates.Where(kv => kv.Value.Matches).OrderBy(kv => kv.Value.PosRmsMm)
        .Select(kv => (double?)kv.Key).FirstOrDefault();

    public string Summary()
    {
        var lines = new List<string> { $"표본 {Samples}개 — 설정 d6 {ConfiguredD6Mm:0.#} mm: {Configured}{(Configured.Matches ? " → 일치" : " → 불일치")}" };
        foreach (var (d6, r) in Candidates.OrderBy(kv => kv.Key))
            lines.Add($"  d6 {d6:0.#} mm: {r}{(r.Matches ? " → 일치" : "")}");
        lines.Add($"  표본에서 구한 d6 = {EstimatedD6Mm:0.0} mm: {AtEstimated}");
        if (!AtEstimated.Matches)
            lines.Add($"  관절 0점·부호 진단(d6 {ConventionD6Mm:0.0} mm): {Convention} → {AtConvention}{(AtConvention.Matches ? " → 이 규약이면 일치" : " → 그래도 불일치(링크 치수·좌표계 확인 필요)")}");
        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// 실물 <c>GetForwardKin</c> 값과 우리 순기구학을 대조한다 — d6(106 / 160)와 관절 0점·부호를 한 번에 본다.
/// 표본 파일은 <c>tools/fr5_fk_capture.py</c> 가 만든다(컨트롤러에 조회만 한다 — 로봇은 움직이지 않는다).
/// HD_ACS 의 <c>Fr5FkCheck</c> 와 같은 식이다 — 바꾸면 함께 맞출 것.
/// </summary>
public static class Fr5FkCheck
{
    /// <summary>[x,y,z,rx,ry,rz](도) → 동차변환. R = Rz·Ry·Rx (FAIRINO 규약 — <see cref="PoseMath.FromPose"/>).</summary>
    public static double[,] FromPose(double[] p) => PoseMath.FromPose(p);

    /// <summary>
    /// 컨트롤러 <c>GetForwardKin</c> 결과(활성 작업물 좌표계 기준 활성 공구 TCP) → BASE 기준 플랜지 pose 행렬.
    /// T_base_flange = T_wobj · T_fk · T_tool⁻¹. 공구·작업물이 0 번(없음)이면 null 을 넘긴다.
    /// </summary>
    public static double[,] FlangeInBase(double[] fkPose, double[]? toolPose, double[]? wobjPose)
    {
        var t = FromPose(fkPose);
        if (wobjPose is { Length: 6 }) t = PoseMath.Multiply(FromPose(wobjPose), t);
        if (toolPose is { Length: 6 }) t = PoseMath.Multiply(t, PoseMath.Inverse(FromPose(toolPose)));
        return t;
    }

    /// <summary>표본에 대한 모델 오차(관절 규약을 주면 그 규약으로 바꿔 계산).</summary>
    public static FkResidual Residual(Fr5Kinematics kin, IReadOnlyList<FkSample> samples, JointConvention? convention = null)
    {
        if (samples.Count == 0) return new FkResidual(0, 0, 0);
        double sum = 0, max = 0, rot = 0;
        foreach (var s in samples)
        {
            var model = kin.ForwardFlange((convention ?? JointConvention.Identity).Apply(s.JointsDeg));
            var real = FromPose(s.FlangePose);
            var d = Math.Sqrt(Enumerable.Range(0, 3).Sum(i => Math.Pow(model[i, 3] - real[i, 3], 2)));
            sum += d * d;
            max = Math.Max(max, d);
            rot = Math.Max(rot, RotationAngleDeg(model, real));
        }
        return new FkResidual(Math.Sqrt(sum / samples.Count), max, rot);
    }

    /// <summary>
    /// 표본에서 d6 를 거꾸로 구한다 — d6 는 플랜지 z 축 방향으로만 위치를 옮기므로 (실물 위치 − d6 = 0 모델 위치)를
    /// 모델 플랜지 z 축에 투영한 값의 평균이다. 나머지 치수·관절 규약이 맞을 때만 뜻이 있다(오차는 <see cref="Residual"/> 로 확인).
    /// </summary>
    public static double EstimateD6(Fr5Kinematics kin, IReadOnlyList<FkSample> samples, JointConvention? convention = null)
    {
        if (samples.Count == 0) return kin.D6;
        var zero = kin.WithD6(0);
        return samples.Average(s =>
        {
            var m = zero.ForwardFlange((convention ?? JointConvention.Identity).Apply(s.JointsDeg));
            return Enumerable.Range(0, 3).Sum(i => (s.FlangePose[i] - m[i, 3]) * m[i, 2]);
        });
    }

    /// <summary>
    /// 관절 0점·부호 진단 — 관절마다 (부호 ±, 0점 차이 0·±90·180°) 8가지, 모두 8⁶ 조합을 본다.
    /// 플랜지 자세는 링크 길이·d6 와 무관하므로 먼저 자세가 맞는 조합만 남기고(J2·J3·J4 는 축이 평행해 자세만으로는 여럿이 남는다),
    /// 그중 d6 를 표본에서 다시 구했을 때 위치 오차가 가장 작은 조합을 고른다(같으면 컨트롤러 규약에서 덜 바꾼 쪽).
    /// 진단용이다 — 자세가 맞는 조합이 없거나 찾은 규약으로도 위치가 맞지 않으면 링크 치수나 좌표계가 다르다.
    /// </summary>
    public static (JointConvention Convention, double D6Mm, FkResidual Residual) FitConvention(Fr5Kinematics kin, IReadOnlyList<FkSample> samples)
    {
        var identity = JointConvention.Identity;
        (JointConvention C, double D6, FkResidual R) At(JointConvention c)
        {
            var d6 = Math.Clamp(EstimateD6(kin, samples, c), 0, 400);
            return (c, d6, Residual(kin.WithD6(d6), samples, c));
        }
        if (samples.Count == 0) return At(identity);

        var signs = new[] { 1, -1 };
        var offsets = new[] { 0.0, 90, -90, 180 };
        var real = samples.Select(s => FromPose(s.FlangePose)).ToArray();
        var best = At(identity);
        var bestChanges = 0;
        var found = best.R.RotMaxDeg <= 0.5;
        for (var code = 1; code < 1 << 18; code++)   // 관절마다 3비트(부호 1 + 0점 2)
        {
            var sign = new int[6];
            var offset = new double[6];
            var changes = 0;
            for (var j = 0; j < 6; j++)
            {
                var bits = (code >> (3 * j)) & 7;
                sign[j] = signs[bits & 1];
                offset[j] = offsets[bits >> 1];
                if (bits != 0) changes++;
            }
            var c = new JointConvention(sign, offset);
            var ok = true;
            for (var k = 0; k < samples.Count && ok; k++)
                ok = RotationAngleDeg(kin.ForwardFlange(c.Apply(samples[k].JointsDeg)), real[k]) <= 0.5;
            if (!ok) continue;
            var cand = At(c);
            if (!found || cand.R.PosRmsMm < best.R.PosRmsMm - 1e-6
                || (Math.Abs(cand.R.PosRmsMm - best.R.PosRmsMm) <= 1e-6 && changes < bestChanges))
            {
                best = cand;
                bestChanges = changes;
                found = true;
            }
        }
        return best;
    }

    /// <summary>대조 종합 — 설정 d6·두 후보(106·160)·표본에서 구한 d6·관절 규약 진단.</summary>
    public static FkVerdict Check(Fr5Kinematics kin, IReadOnlyList<FkSample> samples)
    {
        var candidates = new[] { Fr5Kinematics.D6StepMm, Fr5Kinematics.D6DatasheetMm }
            .ToDictionary(d => d, d => Residual(kin.WithD6(d), samples));
        var est = EstimateD6(kin, samples);
        var fit = FitConvention(kin, samples);
        return new FkVerdict(samples.Count, kin.D6, Residual(kin, samples), candidates, est, Residual(kin.WithD6(est), samples),
            fit.Convention, fit.D6Mm, fit.Residual);
    }

    /// <summary>
    /// 표본 파일(JSON) 읽기 — <c>{ tool: {pose}, wobj: {pose}, samples: [{ jointsDeg, fk }] }</c>.
    /// fk 는 컨트롤러가 준 그대로이고 여기서 BASE 기준 플랜지로 바꾼다. 표본이 없으면 빈 목록.
    /// </summary>
    public static IReadOnlyList<FkSample> LoadSamples(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        double[]? Pose(string name) =>
            root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Object
            && e.TryGetProperty("pose", out var p) && p.ValueKind == JsonValueKind.Array && p.GetArrayLength() == 6
                ? p.EnumerateArray().Select(v => v.GetDouble()).ToArray() : null;
        var tool = Pose("tool");
        var wobj = Pose("wobj");
        var list = new List<FkSample>();
        if (!root.TryGetProperty("samples", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var s in arr.EnumerateArray())
        {
            if (!s.TryGetProperty("jointsDeg", out var j) || !s.TryGetProperty("fk", out var f)) continue;
            var joints = j.EnumerateArray().Select(v => v.GetDouble()).ToArray();
            var fk = f.EnumerateArray().Select(v => v.GetDouble()).ToArray();
            if (joints.Length != 6 || fk.Length != 6) continue;
            list.Add(new FkSample(joints, ToPose(FlangeInBase(fk, tool, wobj))));
        }
        return list;
    }

    /// <summary>동차변환 → [x,y,z,rx,ry,rz](도) — <see cref="FromPose"/> 의 역.</summary>
    public static double[] ToPose(double[,] t) => PoseMath.ToPose(t);

    private static double RotationAngleDeg(double[,] a, double[,] b)
    {
        double trace = 0;
        for (var i = 0; i < 3; i++)
            for (var k = 0; k < 3; k++)
                trace += a[k, i] * b[k, i];   // tr(aᵀ·b)
        return Math.Acos(Math.Clamp((trace - 1) / 2, -1, 1)) * 180.0 / Math.PI;
    }
}
