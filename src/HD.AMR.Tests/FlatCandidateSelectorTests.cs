using HD.AMR.App.Models;
using HD.AMR.App.Service;

namespace HD.AMR.Tests;

public class DepthPlaneFitTests
{
    // 기울었지만 평평한 면 — σ 는 기울기만큼 크지만 평면 잔차는 0 이어야 한다(σ 와의 핵심 차이).
    [Fact]
    public void TiltedPlane_HasZeroResidual()
    {
        var fit = new DepthPlaneFit(20, 20);
        for (var y = 0; y < 40; y++)
        for (var x = 0; x < 40; x++)
            fit.Add(x, y, 400 + 0.5 * x - 0.3 * y);

        Assert.Equal(0, fit.ResidualRms(), 6);
    }

    // 평면 위 단차(비드) — 기울기를 빼도 잔차가 남는다: 높이 h, 점유율 p 이면 RMS ≈ h·√(p(1−p)) 근처.
    [Fact]
    public void StepOnPlane_LeavesResidual()
    {
        var fit = new DepthPlaneFit(20, 20);
        for (var y = 0; y < 40; y++)
        for (var x = 0; x < 40; x++)
            fit.Add(x, y, 400 + 0.2 * x + (x is >= 16 and < 24 ? -2.0 : 0.0));   // 2 mm 단차, 20% 점유

        var r = fit.ResidualRms();
        Assert.InRange(r, 0.5, 2.0 * Math.Sqrt(0.2 * 0.8));
    }

    // 한 줄(평면 미결정) — 평균만 뺀 표준편차로 대신한다.
    [Fact]
    public void CollinearPoints_FallBackToStdDev()
    {
        var fit = new DepthPlaneFit(0, 0);
        foreach (var d in new[] { 1.0, 3.0, 1.0, 3.0 }) fit.Add(5, 5, d);
        Assert.Equal(1.0, fit.ResidualRms(), 6);
    }
}

public class FlatCandidateSelectorTests
{
    // 3×3 그리드, ROI = 전체 프레임(중심 0.5,0.5). Δu 0.1 = 10 mm 로 환산.
    private static (double, double) ToMm(double du, double dv, double z) => (du * 100, dv * 100);

    private static CameraService.DepthGridAnalysis Grid(params (double Sigma, double Residual, bool Valid)[] cells)
    {
        var list = new List<CameraService.DepthGridCell>();
        for (var i = 0; i < cells.Length; i++)
        {
            int gx = i % 3, gy = i / 3;
            var (s, r, v) = cells[i];
            list.Add(new CameraService.DepthGridCell(gx, gy, gx / 3.0, gy / 3.0, (gx + 1) / 3.0, (gy + 1) / 3.0,
                s, 400, v ? 1 : 0, v, r));
        }
        int best = list.Select((c, i) => (c, i)).Where(t => t.c.Valid).MinBy(t => t.c.SigmaMm).i;
        var frame = new CameraFrame(Array.Empty<byte>(), 3, 3, "Z16", DateTime.Now);
        var b = list[best];
        return new CameraService.DepthGridAnalysis(frame, 0, 0, 1, 1, 3, list, best, -1,
            new CameraService.DepthFlatnessResult((b.U0 + b.U1) / 2, (b.V0 + b.V1) / 2, b.SigmaMm, b.MeanMm));
    }

    private static readonly (double, double, bool) Flat = (0.6, 0.5, true);

    [Fact]
    public void ExcludesHighResidual_AndSigmaAboveBest()
    {
        var a = Grid(
            (0.5, 2.0, true),   // 0: σ 최소지만 잔차 초과(비드) → 제외
            Flat,               // 1
            (2.0, 0.5, true),   // 2: σ > 최소 0.5 + 1.0 → 제외
            Flat, Flat, Flat, Flat, Flat,
            (0, 0, false));     // 8: 무효

        var r = FlatCandidateSelector.Rank(a, 0.5, 0.5, new FlatCandidateOptions(1.5, 1.0, 0), ToMm);

        Assert.DoesNotContain(r.Candidates, c => c.CellIndex is 0 or 2 or 8);
        Assert.Equal(6, r.Candidates.Count);
        Assert.Contains(r.Excluded, e => e.CellIndex == 0 && e.Reason.Contains("잔차"));
        Assert.Contains(r.Excluded, e => e.CellIndex == 2 && e.Reason.Contains("σ"));
    }

    // 거리 패널티 — σ 가 약간 작은 모서리 셀보다 중앙 셀이 우선, 패널티 0 이면 σ 순.
    [Fact]
    public void DistancePenalty_PrefersCenterCell()
    {
        var a = Grid((0.50, 0.4, true), Flat, Flat, Flat, (0.60, 0.4, true), Flat, Flat, Flat, Flat);

        var withPenalty = FlatCandidateSelector.Rank(a, 0.5, 0.5, new FlatCandidateOptions(1.5, 1.0, 0.01), ToMm);
        var sigmaOnly = FlatCandidateSelector.Rank(a, 0.5, 0.5, new FlatCandidateOptions(1.5, 1.0, 0), ToMm);

        Assert.Equal(4, withPenalty.Candidates[0].CellIndex);   // 중앙 (2,2): 이동 0
        Assert.Equal(0, sigmaOnly.Candidates[0].CellIndex);     // 모서리 (1,1): σ 최소
        Assert.Equal(-33.3, sigmaOnly.Candidates[0].DxMm, 1);   // 셀 중심 u=1/6 → Δu −1/3
    }

    [Fact]
    public void WithSelected_MovesBestToChosenCell()
    {
        var a = Grid((0.50, 0.4, true), Flat, Flat, Flat, (0.60, 0.4, true), Flat, Flat, Flat, Flat);

        var s = FlatCandidateSelector.WithSelected(a, 4, 0);

        Assert.Equal(4, s.BestIndex);
        Assert.Equal(0, s.SecondIndex);
        Assert.Equal(0.5, s.Best.U, 9);
        Assert.Equal(0.60, s.Best.SigmaMm, 9);
    }
}
