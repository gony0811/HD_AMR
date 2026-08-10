using HD_AMR.Contracts.Lidar;
using HD_AMR.LidarService.Device;

namespace HD_AMR.LidarService.Detection;

/// <summary>
/// 두 경사면의 교선으로 능선(코러게이션 마루)을 찾는다.
///
/// <b>왜 교선 방식인가.</b> 마루 픽셀을 직접 고르는 방식(행마다 가장 가까운 점을 찾아 잇기)은
/// 두 가지로 취약하다. 첫째, 행당 한 점만 쓰므로 단일 픽셀 노이즈(σ 8~12mm)를 그대로 받는다.
/// 둘째, <b>마루 자체가 무효 픽셀이면 아무것도 못 찾는다</b> — 금속 코러게이션은 마루에서
/// 정반사로 포화되기 쉬워 이 상황이 실제로 예상된다.
///
/// 교선 방식은 양옆 경사면의 수천 점으로 각 평면을 정하고 그 교선을 계산하므로, 마루에
/// 유효 픽셀이 하나도 없어도 능선을 낼 수 있다.
///
/// <b>어느 마루를 잡을지는 이 알고리즘이 정하지 않는다.</b> 시야에 마루가 여러 개면 어떤 두
/// 평면이 지배적인지는 장면에 따라 달라진다. "측정 대상 최상단"을 고르는 것은 <b>센서 조준과
/// ROI 설정</b>의 몫이고, 그래서 ROI 가 설정 항목으로 노출되어 있다.
/// </summary>
internal sealed class RidgeDetector : IRidgeDetector
{
    private readonly RidgeDetectorOptions _options;
    private readonly ILogger<RidgeDetector> _log;

    public RidgeDetector(RidgeDetectorOptions options, ILogger<RidgeDetector> log)
    {
        _options = options;
        _log = log;
    }

    public RidgeDetectionResult Detect(AveragedCapture capture)
    {
        // 결정적 동작을 위해 고정 시드를 쓴다. RANSAC 이 실행마다 다른 답을 내면
        // 같은 입력에 대한 회귀 테스트가 불가능하고, 현장 재현도 어려워진다.
        var rng = new Random(_options.RandomSeed);

        var candidates = CollectValid(capture);
        if (candidates.Length < _options.MinPlaneInliers * 2)
        {
            return RidgeDetectionResult.Fail(MeasureFailure.InsufficientValidPixels,
                $"유효 점이 {candidates.Length}개로 두 평면을 피팅하기에 부족하다 " +
                $"(평면당 최소 {_options.MinPlaneInliers}개 필요).")
                with { CandidateCount = candidates.Length };
        }

        var x = capture.X.AsSpan();
        var y = capture.Y.AsSpan();
        var z = capture.Z.AsSpan();

        var first = PlaneFit.Ransac(x, y, z, candidates,
            _options.InlierThresholdMm, _options.RansacIterations, _options.MinPlaneInliers, rng,
            _options.RansacScoreSampleMax);

        if (first is null)
        {
            return RidgeDetectionResult.Fail(MeasureFailure.RidgeFitFailed,
                "첫 번째 평면을 찾지 못했다. 대상이 시야에 없거나 표면이 평면이 아닐 수 있다.")
                with { CandidateCount = candidates.Length };
        }

        // 첫 평면의 인라이어를 제외한 나머지에서 두 번째 평면을 찾는다.
        var firstSet = new HashSet<int>(first.Inliers);
        var remaining = candidates.Where(i => !firstSet.Contains(i)).ToArray();

        var second = PlaneFit.Ransac(x, y, z, remaining,
            _options.InlierThresholdMm, _options.RansacIterations, _options.MinPlaneInliers, rng,
            _options.RansacScoreSampleMax);

        if (second is null)
        {
            return RidgeDetectionResult.Fail(MeasureFailure.RidgeFitFailed,
                $"두 번째 평면을 찾지 못했다(첫 평면 인라이어 {first.Inliers.Length}개, 잔여 점 {remaining.Length}개). " +
                "경사면 한쪽만 보이거나 대상이 단일 평면일 수 있다.")
                with { CandidateCount = candidates.Length, PlaneAInliers = first.Inliers };
        }

        // 실패 경로에서도 두 평면의 인라이어를 함께 돌려준다. 화면에 칠해 보면 "왜 실패했나"가
        // 대개 바로 보인다 — 예컨대 두 평면이 같은 벽을 나눠 가진 상황은 숫자로는 정상처럼
        // 보이지만 그림으로는 즉시 드러난다.
        var diagnostics = new RidgeDetectionResult
        {
            Success = false,
            CandidateCount = candidates.Length,
            PlaneAInliers = first.Inliers,
            PlaneBInliers = second.Inliers,
            PlaneAngleDeg = first.Plane.AngleDeg(second.Plane),
        };

        // 두 평면이 거의 평행하면 교선이 병적으로 부정확해진다. 법선 방향의 작은 오차가
        // 교선 위치를 크게 흔들기 때문에, 각도가 충분히 벌어졌을 때만 신뢰한다.
        var angle = diagnostics.PlaneAngleDeg;
        if (angle < _options.MinPlaneAngleDeg)
        {
            return diagnostics with
            {
                Failure = MeasureFailure.RidgeFitFailed,
                FailureDetail =
                    $"두 평면의 사잇각이 {angle:F1}°로 너무 작다(최소 {_options.MinPlaneAngleDeg}°). " +
                    "같은 면을 두 번 잡았거나 능선이 시야에 없다.",
            };
        }

        if (!TryIntersect(first.Plane, second.Plane, out var px, out var py, out var pz,
                out var dx, out var dy, out var dz))
        {
            return diagnostics with
            {
                Failure = MeasureFailure.RidgeFitFailed,
                FailureDetail = "두 평면의 교선을 계산하지 못했다.",
            };
        }

        // 능선의 유효 구간은 두 평면 인라이어 전체를 교선 방향으로 투영해 정한다.
        // 마루에 유효 픽셀이 없어도(정반사 포화) 경사면이 뻗은 만큼이 곧 능선의 범위다.
        var (tMin, tMax) = Extent(x, y, z, first.Inliers, second.Inliers, px, py, pz, dx, dy, dz);
        var lengthMm = tMax - tMin;

        if (lengthMm < _options.MinRidgeLengthMm)
        {
            return diagnostics with
            {
                Failure = MeasureFailure.RidgeFitFailed,
                FailureDetail =
                    $"능선 길이가 {lengthMm:F0}mm 로 최소 요구치 {_options.MinRidgeLengthMm}mm 에 못 미친다.",
            };
        }

        // 방향 부호를 결정적으로 고정한다. 직선의 방향은 본질적으로 ± 모호성이 있어,
        // 프레임마다 뒤집히면 소비 측 제어가 반대로 갈 수 있다. 절댓값이 가장 큰 성분이
        // 양수가 되도록 통일하고, Start→End 가 그 방향을 따르게 맞춘다.
        if (LargestComponentIsNegative(dx, dy, dz))
        {
            dx = -dx; dy = -dy; dz = -dz;
            (tMin, tMax) = (-tMax, -tMin);
        }

        var start = new Vec3(px + dx * tMin, py + dy * tMin, pz + dz * tMin);
        var end = new Vec3(px + dx * tMax, py + dy * tMax, pz + dz * tMax);
        var mid = new Vec3((start.X + end.X) / 2, (start.Y + end.Y) / 2, (start.Z + end.Z) / 2);

        // 능선의 품질은 두 평면이 데이터를 얼마나 잘 설명하는지로 본다. 점들이 능선 위에
        // 있는 게 아니라 평면 위에 있으므로, 평면 잔차가 곧 교선의 불확실성으로 이어진다.
        var inlierCount = first.Inliers.Length + second.Inliers.Length;
        var rms = Math.Sqrt((first.RmsMm * first.RmsMm + second.RmsMm * second.RmsMm) / 2);

        if (rms > _options.MaxRmsMm)
        {
            return diagnostics with
            {
                Failure = MeasureFailure.RmsExceeded,
                FailureDetail =
                    $"평면 피팅 잔차 RMS 가 {rms:F1}mm 로 허용치 {_options.MaxRmsMm}mm 를 넘었다. " +
                    "표면이 평면이 아니거나 노이즈가 과다하다.",
            };
        }

        var confidence = Confidence(inlierCount, candidates.Length, rms, angle);

        _log.LogDebug(
            "능선 검출: 평면각 {Angle:F1}°, 인라이어 {Inliers}/{Total}, RMS {Rms:F2}mm, 길이 {Length:F0}mm",
            angle, inlierCount, candidates.Length, rms, lengthMm);

        return diagnostics with
        {
            Success = true,
            Confidence = confidence,
            Ridge = new RidgeLine
            {
                Point = mid,
                Direction = new Vec3(dx, dy, dz),
                Start = start,
                End = end,
                LengthMm = lengthMm,
                InlierCount = inlierCount,
                RmsMm = rms,
            },
            Inliers = _options.IncludeInliersInResult ? Sample(x, y, z, first.Inliers, second.Inliers) : null,
        };
    }

    /// <summary>
    /// 평균 결과에서 유효 점의 인덱스만 모은다.
    ///
    /// ⚠ 무효 픽셀은 X/Y/Z 에 0 이 아니라 무효 코드값(예: 64002.0)이 그대로 들어 있다.
    ///   거르지 않으면 64미터 지점에 유령 점이 생겨 평면 피팅이 통째로 망가진다.
    ///   <see cref="CaptureAverager"/> 가 무효 픽셀을 NaN 으로 만들어 두므로 NaN 검사로 충분하다.
    /// </summary>
    private int[] CollectValid(AveragedCapture capture)
    {
        var result = new List<int>(capture.Width * capture.Height / 2);
        var hasMax = _options.MaxDistanceMm > 0;

        // 픽셀을 쓰려면 프레임 중 이 비율 이상에서 유효해야 한다. 절대 개수로 두면
        // 1~2 프레임만 요청했을 때 모든 픽셀이 걸러져 검출이 통째로 실패한다.
        var minSamples = Math.Max(1, (int)Math.Ceiling(capture.FramesUsed * _options.MinSampleFraction));

        for (int i = 0; i < capture.SampleCount.Length; i++)
        {
            // 유효 샘플이 너무 적은 픽셀은 신뢰할 수 없다 — 대부분의 프레임에서 무효였다는 뜻이다.
            if (capture.SampleCount[i] < minSamples) continue;

            var d = capture.Distance[i];
            if (double.IsNaN(d)) continue;

            // 깊이 구간 게이팅. 이게 없으면 RANSAC 이 배경을 잡는다 — 실측 장면에서
            // 대상(박스)이 12,000점인데 배경 벽이 50,000점이라 다수결에서 진다.
            // 코봇은 알려진 스탠드오프에서 측정하므로 작업 거리는 사전에 정할 수 있다.
            if (d < _options.MinDistanceMm) continue;
            if (hasMax && d > _options.MaxDistanceMm) continue;

            result.Add(i);
        }

        return result.ToArray();
    }

    /// <summary>두 평면의 교선. 방향은 법선의 외적, 점은 두 평면 방정식의 최소노름 해.</summary>
    private static bool TryIntersect(
        in Plane a, in Plane b,
        out double px, out double py, out double pz,
        out double dx, out double dy, out double dz)
    {
        dx = a.Ny * b.Nz - a.Nz * b.Ny;
        dy = a.Nz * b.Nx - a.Nx * b.Nz;
        dz = a.Nx * b.Ny - a.Ny * b.Nx;

        var lenSq = dx * dx + dy * dy + dz * dz;
        if (lenSq < 1e-12)
        {
            px = py = pz = 0;
            return false;
        }

        // p0 = (Da·(nb×d) + Db·(d×na)) / |d|²
        double c1x = b.Ny * dz - b.Nz * dy;
        double c1y = b.Nz * dx - b.Nx * dz;
        double c1z = b.Nx * dy - b.Ny * dx;

        double c2x = dy * a.Nz - dz * a.Ny;
        double c2y = dz * a.Nx - dx * a.Nz;
        double c2z = dx * a.Ny - dy * a.Nx;

        px = (a.D * c1x + b.D * c2x) / lenSq;
        py = (a.D * c1y + b.D * c2y) / lenSq;
        pz = (a.D * c1z + b.D * c2z) / lenSq;

        var len = Math.Sqrt(lenSq);
        dx /= len; dy /= len; dz /= len;
        return true;
    }

    /// <summary>
    /// 인라이어를 교선 방향으로 투영해 구간을 정한다. 양 끝 2%를 잘라내 이상점 하나가
    /// 능선을 몇 미터씩 늘려버리는 것을 막는다.
    /// </summary>
    private static (double Min, double Max) Extent(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z,
        int[] a, int[] b,
        double px, double py, double pz, double dx, double dy, double dz)
    {
        var t = new double[a.Length + b.Length];
        var k = 0;

        foreach (var i in a) t[k++] = (x[i] - px) * dx + (y[i] - py) * dy + (z[i] - pz) * dz;
        foreach (var i in b) t[k++] = (x[i] - px) * dx + (y[i] - py) * dy + (z[i] - pz) * dz;

        Array.Sort(t);
        var lo = (int)(t.Length * 0.02);
        var hi = (int)(t.Length * 0.98);
        return (t[lo], t[Math.Min(hi, t.Length - 1)]);
    }

    private static bool LargestComponentIsNegative(double dx, double dy, double dz)
    {
        var ax = Math.Abs(dx);
        var ay = Math.Abs(dy);
        var az = Math.Abs(dz);

        if (ax >= ay && ax >= az) return dx < 0;
        if (ay >= az) return dy < 0;
        return dz < 0;
    }

    /// <summary>인라이어 비율·잔차·평면각을 0~1 신뢰도로 합친다.</summary>
    private double Confidence(int inliers, int total, double rms, double angleDeg)
    {
        var coverage = Math.Clamp((double)inliers / Math.Max(total, 1), 0, 1);
        var fit = Math.Clamp(1.0 - rms / _options.MaxRmsMm, 0, 1);

        // 사잇각이 클수록 교선이 잘 결정된다. 90°에서 최선, 임계각에서 0.
        var geometry = Math.Clamp(
            (angleDeg - _options.MinPlaneAngleDeg) / (90.0 - _options.MinPlaneAngleDeg), 0, 1);

        return Math.Round(coverage * 0.3 + fit * 0.4 + geometry * 0.3, 3);
    }

    /// <summary>진단용 인라이어 점군. 응답이 비대해지지 않도록 균등 간격으로 솎아낸다.</summary>
    private IReadOnlyList<Vec3> Sample(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, int[] a, int[] b)
    {
        var all = a.Concat(b).ToArray();
        var step = Math.Max(1, all.Length / _options.MaxInlierSamples);
        var result = new List<Vec3>(Math.Min(all.Length, _options.MaxInlierSamples));

        for (int i = 0; i < all.Length; i += step)
        {
            var p = all[i];
            result.Add(new Vec3(x[p], y[p], z[p]));
        }

        return result;
    }
}

internal sealed class RidgeDetectorOptions
{
    /// <summary>
    /// 평면 인라이어 판정 거리(mm).
    ///
    /// 10프레임 평균 후 단일 픽셀 σ 가 4~7mm 이므로 3σ 에 해당하는 20mm 를 기본으로 둔다.
    /// 너무 좁히면 정상 노이즈를 아웃라이어로 버려 평면을 못 찾고, 너무 넓히면 두 경사면을
    /// 한 평면으로 뭉뚱그린다.
    /// </summary>
    public double InlierThresholdMm { get; set; } = 20.0;

    /// <summary>
    /// 측정 대상이 있을 것으로 기대하는 거리 구간의 하한(mm).
    ///
    /// 이 게이팅이 왜 필요한가: RANSAC 은 다수결이라 시야에 배경이 크게 잡히면 대상이 아니라
    /// 배경을 평면으로 뽑는다. 실측 장면(scene_04)에서 대상 박스는 12,000점인데 배경 벽이
    /// 50,000점이라 게이팅 없이는 배경 평면 두 개가 선택됐다.
    ///
    /// 코봇은 알려진 스탠드오프 거리에서 측정하므로 작업 거리를 사전에 정할 수 있다.
    /// 실제 설치 후 이 구간을 대상 거리 ± 여유로 맞춰야 한다.
    /// </summary>
    public double MinDistanceMm { get; set; } = 0;

    /// <summary>측정 대상 거리 구간의 상한(mm). 0 이면 제한 없음.</summary>
    public double MaxDistanceMm { get; set; } = 0;

    public int RansacIterations { get; set; } = 500;

    /// <summary>
    /// RANSAC 가설 채점에 쓸 최대 표본 점 수. 0 이면 후보 전체로 채점한다.
    ///
    /// 전체 채점은 320x240 전면 기준 검출 한 번에 1.3초가 걸린다(개발 PC 실측). 젯슨에서는
    /// 더 느려지므로, 측정 응답 시간과 모니터링 화면 반응성이 함께 무너진다.
    ///
    /// 채점의 목적은 가설 간 <b>순위</b>를 가리는 것뿐이라 표본으로 충분하다. 최종 인라이어
    /// 수집과 최소제곱 재피팅은 후보 전체로 하므로 결과 정밀도는 영향받지 않는다.
    /// </summary>
    public int RansacScoreSampleMax { get; set; } = 4000;

    /// <summary>평면 하나로 인정할 최소 인라이어 수.</summary>
    public int MinPlaneInliers { get; set; } = 1000;

    /// <summary>
    /// 두 평면 사잇각 하한(도). 이보다 작으면 교선이 병적으로 부정확해진다 —
    /// 법선의 작은 오차가 교선 위치를 크게 흔들기 때문이다.
    /// </summary>
    public double MinPlaneAngleDeg { get; set; } = 15.0;

    /// <summary>평면 피팅 잔차 RMS 허용치(mm). 넘으면 검출 실패로 처리한다.</summary>
    public double MaxRmsMm { get; set; } = 15.0;

    /// <summary>능선으로 인정할 최소 길이(mm).</summary>
    public double MinRidgeLengthMm { get; set; } = 100.0;

    /// <summary>
    /// 픽셀을 쓰기 위해 필요한 유효 샘플 비율(0~1). 대부분의 프레임에서 무효였던 픽셀은
    /// 평균값 자체를 신뢰할 수 없다.
    ///
    /// 절대 개수가 아니라 비율인 이유: 프레임 수는 요청마다 달라진다. 절대 개수로 두면
    /// 1~2 프레임 요청 시 모든 픽셀이 걸러져 "유효 점 0개"로 실패한다.
    /// </summary>
    public double MinSampleFraction { get; set; } = 0.5;

    /// <summary>RANSAC 난수 시드. 고정해 두어야 같은 입력에 같은 결과가 나온다.</summary>
    public int RandomSeed { get; set; } = 12345;

    public bool IncludeInliersInResult { get; set; } = true;

    public int MaxInlierSamples { get; set; } = 500;

    // ── 반원 코러게이션 검출(ArcRidgeDetector) 전용 ──────────────────────────
    // 실제 측정 대상은 평판에 성형된 반경 35mm 반원 돌기다. 두 평면 교선 방식은 이 형상에
    // 맞지 않아(코러게이션 양옆이 같은 평면이라 교선이 존재하지 않는다) 별도 검출기를 쓴다.
    //
    // ⚠ 이름을 "비드"로 쓰지 않는다. 이 시스템의 최종 목적이 용접이고, 용접 비드(수 mm)
    //   검출이 들어오면 코러게이션 폭(70mm)과 이름이 정면 충돌한다. 자릿수가 달라서
    //   잘못 읽으면 조용히 틀린 결과를 낸다.
    //
    // 옵션 클래스를 나누지 않은 것은 깊이 게이트·시드처럼 공통 항목이 많고, 설정 API 와
    // 화면이 둘로 갈라지면 현장 튜닝이 번거로워지기 때문이다.

    /// <summary>
    /// 평판 평면의 인라이어 판정 거리(mm).
    ///
    /// 교선 방식의 <see cref="InlierThresholdMm"/> 보다 좁게 잡는다. 넓으면 코러게이션
    /// 뿌리까지 평판으로 빨아들여 높이 계산이 무뎌지고, 점군의 아래쪽이 잘려나간다.
    /// </summary>
    public double PlaneInlierThresholdMm { get; set; } = 8.0;

    /// <summary>코러게이션으로 인정할 평판 위 높이 하한(mm). 판 자체의 굴곡과 노이즈를 넘어야 한다.</summary>
    public double CorrugationMinHeightMm { get; set; } = 5.0;

    /// <summary>
    /// 코러게이션 높이 상한(mm). 실물 돌출 35mm.
    ///
    /// 이 상한이 <b>받침 구조물과 배경을 걸러내는 역할</b>을 겸한다. 실측에서 판 뒤에 세워둔
    /// 박스 때문에 평판 위 85~277mm 지점에 점이 잡혔는데, 상한이 없으면 그게 전부 후보가 된다.
    /// </summary>
    public double CorrugationMaxHeightMm { get; set; } = 60.0;

    /// <summary>코러게이션 폭(mm). 평면 내 직선 RANSAC 의 띠 폭을 정한다. 실물 70mm.</summary>
    public double CorrugationWidthMm { get; set; } = 70.0;

    /// <summary>코러게이션 폭에 더하는 띠 여유(mm). 장착 기울기와 노이즈를 흡수한다.</summary>
    public double CorrugationBandMarginMm { get; set; } = 15.0;

    /// <summary>코러게이션 하나로 인정할 최소 점 수.</summary>
    public int MinCorrugationPoints { get; set; } = 300;

    /// <summary>
    /// 찾을 코러게이션 후보의 최대 개수.
    ///
    /// 실물 판(1000mm)에 피치 370mm 면 2~3개가 보인다. 여유를 두되 무한정 훑지는 않는다 —
    /// 후보를 하나 찾을 때마다 RANSAC 을 한 번 더 돌리므로 검출 시간이 비례해 늘어난다.
    /// </summary>
    public int MaxCandidates { get; set; } = 5;

    /// <summary>단면 원 피팅의 인라이어 판정 거리(mm).</summary>
    public double ArcInlierThresholdMm { get; set; } = 8.0;

    /// <summary>
    /// 실물 코러게이션 반경(mm). 0 이면 검증하지 않는다.
    ///
    /// 이 검사가 이 검출기의 핵심 안전장치다. 배경이나 엉뚱한 곡면을 잡으면 반경이 전혀
    /// 다르게 나오는데, 인라이어 수와 잔차는 그때도 정상으로 보인다.
    /// </summary>
    public double ExpectedRadiusMm { get; set; } = 35.0;

    /// <summary>반경 허용 오차 비율(0~1). 0.4 면 실물 35mm 에 대해 21~49mm 를 통과시킨다.</summary>
    public double RadiusTolerance { get; set; } = 0.4;
}
