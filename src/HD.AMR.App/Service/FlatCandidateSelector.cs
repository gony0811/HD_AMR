using System.Globalization;

namespace HD.AMR.App.Service;

/// <summary>
/// 평탄 후보 셀 선택 기준 — 범용 key/value(<see cref="ParameterService"/>)에 보관, Parameters 페이지에서 조정.
///
/// 근거(σ = 셀 깊이 단순 표준편차, 잔차 = 평면 맞춤 잔차 RMS, D435 400 mm 노이즈 ≈ 0.5–1 mm):
///  · <see cref="MaxResidualMm"/> — 절대 상한. 잔차는 기울기가 빠진 노이즈+단차라 노이즈 약 2배를 넘으면
///    비드·단차가 걸친 셀로 본다. 기본 1.5 mm 는 계산 출발점 — ④ 로그(선택 셀 잔차 ↔ Phase C 성패)로 보정한다.
///  · <see cref="MaxSigmaAboveBestMm"/> — 상대 상한(σ ≤ σ_best + Δ). 비율(×1.5)은 바닥이 노이즈(≈0.6 mm)라
///    0.9 mm 로 너무 빡빡해 노이즈만으로 탈락하므로 차이로 둔다.
///  · <see cref="DistancePenaltyPerMm"/> — 점수 = σ + λ·(ROI 중심까지 거리 mm). 비슷하게 평평하면 가까운 셀을
///    골라 횡이동을 줄인다(0.01 → 50 mm 멀면 σ 0.5 mm 불리).
/// ≤ 0 이면 해당 기준 비활성.
/// </summary>
public sealed record FlatCandidateOptions(
    double MaxResidualMm = 1.5,
    double MaxSigmaAboveBestMm = 1.0,
    double DistancePenaltyPerMm = 0.01)
{
    public const string MaxResidualKey = "Sequence.FlatSurface.Candidate.MaxResidualMm";
    public const string MaxSigmaAboveBestKey = "Sequence.FlatSurface.Candidate.MaxSigmaAboveBestMm";
    public const string DistancePenaltyKey = "Sequence.FlatSurface.Candidate.DistancePenaltyPerMm";

    public static async Task<FlatCandidateOptions> LoadAsync(ParameterService param)
    {
        var d = new FlatCandidateOptions();
        return new FlatCandidateOptions(
            await param.GetDoubleAsync(MaxResidualKey) ?? d.MaxResidualMm,
            await param.GetDoubleAsync(MaxSigmaAboveBestKey) ?? d.MaxSigmaAboveBestMm,
            await param.GetDoubleAsync(DistancePenaltyKey) ?? d.DistancePenaltyPerMm);
    }

    /// <summary>Parameters 페이지 노출용 시드(이름, 값, 설명).</summary>
    public static IEnumerable<(string Name, string Value, string Desc)> SeedDefaults()
    {
        var d = new FlatCandidateOptions();
        yield return (MaxResidualKey, d.MaxResidualMm.ToString(CultureInfo.InvariantCulture),
            "④ 평탄 후보 셀 평면 잔차 상한(mm) — 넘으면 비드·단차가 걸친 셀로 보고 제외. 0 이하 = 비활성.");
        yield return (MaxSigmaAboveBestKey, d.MaxSigmaAboveBestMm.ToString(CultureInfo.InvariantCulture),
            "④ 평탄 후보 σ 상한 = 최소 σ + 이 값(mm). 0 이하 = 비활성.");
        yield return (DistancePenaltyKey, d.DistancePenaltyPerMm.ToString(CultureInfo.InvariantCulture),
            "④ 평탄 후보 점수 = σ + 이 값 × ROI 중심 거리(mm). 클수록 가까운 셀 우선. 0 = σ 순.");
    }
}

/// <summary>후보 셀 하나 — 점수 오름차순으로 도달성 판정을 시도한다.</summary>
/// <param name="CellIndex"><see cref="CameraService.DepthGridAnalysis.Cells"/> 인덱스.</param>
/// <param name="DxMm">셀 중심을 ROI 중심에 맞추는 이미지 X 이동량(mm).</param>
/// <param name="DyMm">이미지 Y 이동량(mm).</param>
/// <param name="DistanceMm">ROI 중심까지 거리(mm).</param>
/// <param name="Score">σ + λ·거리 — 작을수록 우선.</param>
public sealed record FlatCandidate(
    int CellIndex, CameraService.DepthGridCell Cell, double DxMm, double DyMm, double DistanceMm, double Score)
{
    public string Label => $"({Cell.Gx + 1},{Cell.Gy + 1})";
}

/// <summary>랭킹 결과 — 후보(점수순)와 제외 셀(사유).</summary>
public sealed record FlatCandidateRanking(
    IReadOnlyList<FlatCandidate> Candidates,
    IReadOnlyList<(int CellIndex, string Reason)> Excluded,
    double BestSigmaMm);

/// <summary>
/// 평탄 셀 후보 랭킹(순수 함수) — 시퀀스 ④와 카메라 페이지 '평탄 검증'이 같은 기준을 쓰도록 공유한다.
/// 도달성 판정은 코봇 RPC 가 필요해 <see cref="FlatSurfaceCenteringService.SelectReachableAsync"/> 가 한다.
/// </summary>
public static class FlatCandidateSelector
{
    /// <param name="pixelDeltaToMm">(Δu, Δv, z mm) → (dx, dy) mm — <see cref="CameraService.PixelDeltaToMm"/>.</param>
    public static FlatCandidateRanking Rank(
        CameraService.DepthGridAnalysis a, double centerU, double centerV, FlatCandidateOptions o,
        Func<double, double, double, (double Dx, double Dy)> pixelDeltaToMm, double fallbackDepthMm = 400.0)
    {
        var bestSigma = a.Cells.Where(c => c.Valid).Select(c => c.SigmaMm).DefaultIfEmpty(double.NaN).Min();
        var candidates = new List<FlatCandidate>();
        var excluded = new List<(int, string)>();

        for (var i = 0; i < a.Cells.Count; i++)
        {
            var c = a.Cells[i];
            if (!c.Valid) { excluded.Add((i, $"유효 픽셀 부족({c.ValidRatio:P0})")); continue; }
            if (o.MaxResidualMm > 0 && c.ResidualMm > o.MaxResidualMm)
            {
                excluded.Add((i, $"평면 잔차 {c.ResidualMm:0.00}mm > {o.MaxResidualMm:0.##}mm"));
                continue;
            }
            if (o.MaxSigmaAboveBestMm > 0 && c.SigmaMm > bestSigma + o.MaxSigmaAboveBestMm)
            {
                excluded.Add((i, $"σ {c.SigmaMm:0.00}mm > 최소 {bestSigma:0.00}+{o.MaxSigmaAboveBestMm:0.##}mm"));
                continue;
            }

            double z = c.MeanMm > 0 ? c.MeanMm : fallbackDepthMm;
            var (dx, dy) = pixelDeltaToMm((c.U0 + c.U1) / 2 - centerU, (c.V0 + c.V1) / 2 - centerV, z);
            double dist = Math.Sqrt(dx * dx + dy * dy);
            double score = c.SigmaMm + Math.Max(0, o.DistancePenaltyPerMm) * dist;
            candidates.Add(new FlatCandidate(i, c, dx, dy, dist, score));
        }

        candidates.Sort((p, q) => p.Score.CompareTo(q.Score));
        return new FlatCandidateRanking(candidates, excluded, bestSigma);
    }

    /// <summary>선택 셀을 분석 결과의 Best 로 바꿔 끼운다 — 화면(선택 셀 테두리·이동 벡터)과 '카메라 이동'이
    /// 같은 셀을 쓰게 한다. <paramref name="secondIndex"/> 는 다음 후보(없으면 -1).</summary>
    public static CameraService.DepthGridAnalysis WithSelected(
        CameraService.DepthGridAnalysis a, int index, int secondIndex)
    {
        var c = a.Cells[index];
        return a with
        {
            BestIndex = index,
            SecondIndex = secondIndex,
            Best = new CameraService.DepthFlatnessResult((c.U0 + c.U1) / 2, (c.V0 + c.V1) / 2, c.SigmaMm, c.MeanMm),
        };
    }
}
