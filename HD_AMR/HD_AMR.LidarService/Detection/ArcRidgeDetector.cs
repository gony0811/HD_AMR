using HD_AMR.Contracts.Lidar;
using HD_AMR.LidarService.Device;

namespace HD_AMR.LidarService.Detection;

/// <summary>
/// 평판 위에 성형된 <b>반원 코러게이션</b>의 피크선을 찾는다. 실제 측정 대상의 형상이다 —
/// 평평한 스테인리스 판에 반경 35mm 반원 돌기가 370mm 간격으로 성형되어 있다.
///
/// <b>왜 두 평면 교선 방식을 쓰지 않는가.</b> 그 방식은 마루가 두 경사면이 만나는 모서리라는
/// 전제 위에 서 있다. 이 형상에는 그런 모서리가 없다 — 코러게이션 양옆은 <b>같은 평판</b>이라
/// 교차하는 두 평면 자체가 존재하지 않는다. 실물에 돌렸을 때 판 안에서 두 번째 평면을 찾지
/// 못하고 배경 벽을 집어와 오검출을 냈다.
///
/// <b>정점 픽셀을 직접 고르지 않는다.</b> 광택 금속의 정점은 정반사로 포화되어 유효 픽셀이
/// 사라지는 일이 실제로 관측됐다. 게다가 반경 35mm 원의 정점 부근은 거의 평평해서 옆으로
/// 10mm 가도 높이가 1.4mm 밖에 안 떨어진다 — 최고점을 고르면 가로 위치가 노이즈로 ±30mm 씩
/// 튄다. 기울기가 가파른 옆면 수백 점으로 원을 맞추면 중심이 잘 정해지고 정점은 해석적으로
/// 나온다.
///
/// <b>코러게이션을 전부 찾는다.</b> 시야에 여러 개가 들어오는데 크기가 비슷해서, 하나만
/// 고르면 프레임 노이즈에 따라 대상이 바뀐다(실측에서 10회 측정이 세 덩어리로 갈렸다).
/// 전부 찾아 후보로 내보내고, 선택은 명시적 규칙으로 한다.
/// </summary>
internal sealed class ArcRidgeDetector : IRidgeDetector
{
    /// <summary>방향 훑기 각도 눈금 수. 180°를 이만큼 나눈다(5° 간격).</summary>
    private const int AxisSteps = 36;

    /// <summary>방향 훑기에 쓸 최대 표본 점 수. 점수 비교만 하므로 전수가 필요 없다.</summary>
    private const int AxisSampleMax = 3000;

    /// <summary>진단 히스토그램의 최대 칸 수. 응답 크기 상한이다.</summary>
    private const int MaxHistogramBins = 300;

    private readonly RidgeDetectorOptions _options;
    private readonly ILogger<ArcRidgeDetector> _log;

    public ArcRidgeDetector(RidgeDetectorOptions options, ILogger<ArcRidgeDetector> log)
    {
        _options = options;
        _log = log;
    }

    public RidgeDetectionResult Detect(AveragedCapture capture)
    {
        // 고정 시드. RANSAC 이 실행마다 다른 답을 내면 회귀 테스트도 현장 재현도 불가능하다.
        var rng = new Random(_options.RandomSeed);

        var x = capture.X.AsSpan();
        var y = capture.Y.AsSpan();
        var z = capture.Z.AsSpan();

        var candidates = CollectValid(capture);
        if (candidates.Length < _options.MinPlaneInliers)
        {
            return RidgeDetectionResult.Fail(MeasureFailure.InsufficientValidPixels,
                $"깊이 구간을 통과한 점이 {candidates.Length}개로 평판을 찾기에 부족하다 " +
                $"(최소 {_options.MinPlaneInliers}개).")
                with { CandidateCount = candidates.Length };
        }

        // ── 1. 평판 평면 ────────────────────────────────────────────────────
        // 코러게이션은 판 면적의 일부일 뿐이라 평판이 언제나 지배적이다. 인라이어 임계를
        // 좁게 두는 이유는, 넓으면 코러게이션 뿌리까지 평판으로 빨아들여 높이 계산이
        // 무뎌지기 때문이다.
        var sheet = PlaneFit.Ransac(x, y, z, candidates,
            _options.PlaneInlierThresholdMm, _options.RansacIterations, _options.MinPlaneInliers, rng,
            _options.RansacScoreSampleMax);

        if (sheet is null)
        {
            return RidgeDetectionResult.Fail(MeasureFailure.RidgeFitFailed,
                "평판 평면을 찾지 못했다. 대상이 시야에 없거나 깊이 구간이 잘못 잡혔을 수 있다.")
                with { CandidateCount = candidates.Length };
        }

        // 법선 부호를 고정한다. D > 0 이 되도록 맞추면(평면이 센서 앞에 있으므로 항상 가능)
        // 높이 h = D - n·p 가 "센서 쪽으로 튀어나온 양"이 된다.
        var plane = sheet.Plane;
        if (plane.D < 0) plane = new Plane(-plane.Nx, -plane.Ny, -plane.Nz, -plane.D);

        // ── 2. 코러게이션 점군 ──────────────────────────────────────────────
        var raised = new List<int>(candidates.Length / 8);
        double maxHeight = 0;

        foreach (var i in candidates)
        {
            var height = plane.D - (plane.Nx * x[i] + plane.Ny * y[i] + plane.Nz * z[i]);
            if (height > maxHeight) maxHeight = height;
            if (height < _options.CorrugationMinHeightMm || height > _options.CorrugationMaxHeightMm) continue;

            raised.Add(i);
        }

        var baseline = new RidgeDetectionResult
        {
            Success = false,
            CandidateCount = candidates.Length,
            PlaneAInliers = sheet.Inliers,
            PlaneBInliers = raised.ToArray(),
            Arc = new ArcDiagnostics
            {
                PlaneRmsMm = Math.Round(sheet.RmsMm, 2),
                CorrugationPointCount = raised.Count,
                MaxHeightMm = Math.Round(maxHeight, 1),
            },
        };

        if (raised.Count < _options.MinCorrugationPoints)
        {
            return baseline with
            {
                Failure = MeasureFailure.InsufficientValidPixels,
                FailureDetail =
                    $"코러게이션 점이 {raised.Count}개로 부족하다(최소 {_options.MinCorrugationPoints}개). " +
                    $"관측된 최대 높이는 {maxHeight:F1}mm 다 — 대상이 시야에 없거나, " +
                    $"높이 구간 {_options.CorrugationMinHeightMm}~{_options.CorrugationMaxHeightMm}mm 가 실물과 맞지 않는다.",
            };
        }

        // ── 3. 평면 내 좌표계 ───────────────────────────────────────────────
        var (e1, e2) = InPlaneBasis(plane);
        var origin = new[] { plane.Nx * plane.D, plane.Ny * plane.D, plane.Nz * plane.D };

        var n = capture.X.Length;
        var a = new double[n];   // 평면 내 좌표 1
        var b = new double[n];   // 평면 내 좌표 2
        var h = new double[n];   // 평면 위 높이

        foreach (var i in raised)
        {
            var dx = x[i] - origin[0];
            var dy = y[i] - origin[1];
            var dz = z[i] - origin[2];

            h[i] = -(plane.Nx * dx + plane.Ny * dy + plane.Nz * dz);
            a[i] = e1[0] * dx + e1[1] * dy + e1[2] * dz;
            b[i] = e2[0] * dx + e2[1] * dy + e2[2] * dz;
        }

        // ── 4. 방향을 한 번만 정하고, 가로 위치로 코러게이션을 가른다 ────────
        //
        // 예전에는 "띠 하나를 RANSAC 으로 찾고 그 점들을 뺀 뒤 다시 찾기"를 반복했는데,
        // 실측에서 검출 재현율이 무너졌다 — 광축에 가장 가까운 코러게이션이 10회 중 4회만
        // 후보에 올라왔다. 매 반복이 독립적인 RANSAC 추첨이라, 앞 단계가 조금만 어긋나도
        // 뒤가 연쇄로 무너지는 구조였다.
        //
        // 코러게이션은 모두 <b>평행</b>하다는 사실을 쓰면 추첨을 한 번으로 줄일 수 있다.
        // 방향을 한 번 정하고 나면 각 점의 가로 위치가 정해지고, 코러게이션은 그 축에서
        // 피치(370mm)만큼 떨어진 무리로 나타난다. 무리를 가르는 것은 추첨이 아니라 정렬이라
        // 결정적이고, 약한 코러게이션도 빠지지 않는다.
        var raisedArray = raised.ToArray();
        var (dirA, dirB, axisScore) = FindAxis(a, b, raisedArray);

        var vA = -dirB;
        var vB = dirA;

        var w = new double[n];
        foreach (var i in raisedArray) w[i] = a[i] * vA + b[i] * vB;

        var clusters = Cluster(w, raisedArray);

        // 방향을 세밀하게 다시 잡는다. 각도 훑기는 5° 간격이라, 350mm 길이 코러게이션에서
        // 가로 위치가 최대 30mm 번진다. 가장 큰 무리의 주성분으로 맞추면 그 번짐이 사라진다.
        if (clusters.Count > 0)
        {
            var largest = clusters.MaxBy(c => c.Count)!;
            (dirA, dirB) = PrincipalDirection(a, b, largest);

            vA = -dirB;
            vB = dirA;
            foreach (var i in raisedArray) w[i] = a[i] * vA + b[i] * vB;

            clusters = Cluster(w, raisedArray);
        }

        // 무리의 점은 가로 위치 순서로 담기지 않으므로(밀도 구간에 배정하는 방식이라 원래
        // 인덱스 순서다) 양 끝을 직접 찾아야 한다.
        var summaries = clusters
            .Select(c =>
            {
                double lo = double.MaxValue, hi = double.MinValue;
                foreach (var i in c)
                {
                    if (w[i] < lo) lo = w[i];
                    if (w[i] > hi) hi = w[i];
                }
                return new ClusterSummary(Math.Round((lo + hi) / 2, 1), Math.Round(hi - lo, 1), c.Count);
            })
            .OrderBy(c => c.CenterMm)
            .ToArray();

        baseline = baseline with
        {
            Arc = baseline.Arc! with
            {
                ClusterCount = clusters.Count,
                Clusters = summaries,
                AxisScore = axisScore,
                Histogram = BuildHistogram(w, raisedArray),
            },
        };

        var found = new List<RidgeCandidate>();
        string? firstFailure = null;
        var allInliers = new List<int>();

        foreach (var cluster in clusters.OrderByDescending(c => c.Count).Take(_options.MaxCandidates))
        {
            if (cluster.Count < _options.MinCorrugationPoints)
            {
                firstFailure ??=
                    $"가장 큰 무리가 {cluster.Count}점으로 최소 요구치 {_options.MinCorrugationPoints}점에 못 미친다.";
                continue;
            }

            var candidate = BuildCandidate(
                plane, origin, e1, e2, a, b, h, dirA, dirB, cluster.ToArray(), sheet, rng, out var reason);

            if (candidate is not null)
            {
                found.Add(candidate);
                allInliers.AddRange(candidate.Inliers);
            }
            else
            {
                firstFailure ??= reason;
            }
        }

        if (found.Count == 0)
        {
            return baseline with
            {
                Failure = MeasureFailure.RidgeFitFailed,
                FailureDetail = firstFailure ?? "코러게이션을 찾지 못했다.",
            };
        }

        // ── 5. 기본 선택: 광축에 가장 가까운 것 ─────────────────────────────
        // 조준한 것을 잰다는 뜻이라 운영자의 직관과 맞고, 크기 비교와 달리 프레임 노이즈에
        // 흔들리지 않는다. 호출 측이 목표 위치를 주면 LidarSession 이 다시 고른다.
        found.Sort((p, q) => AxisDistance(p).CompareTo(AxisDistance(q)));
        var chosen = found[0];

        _log.LogDebug(
            "코러게이션 {Count}개 검출, 광축 최근접 선택: 반경 {Radius:F1}mm, 원 RMS {Rms:F2}mm, 길이 {Length:F0}mm",
            found.Count, chosen.Arc!.RadiusMm, chosen.Arc.CircleRmsMm, chosen.Ridge.LengthMm);

        return baseline with
        {
            Success = true,
            Confidence = chosen.Confidence,
            Ridge = chosen.Ridge,
            Arc = chosen.Arc with
            {
                PlaneRmsMm = baseline.Arc!.PlaneRmsMm,
                CorrugationPointCount = raised.Count,
                MaxHeightMm = baseline.Arc.MaxHeightMm,
                ClusterCount = baseline.Arc.ClusterCount,
                Clusters = baseline.Arc.Clusters,
                AxisScore = baseline.Arc.AxisScore,
                Histogram = baseline.Arc.Histogram,
            },
            Candidates = found,
            // 오버레이에는 후보 전부를 칠한다. 하나만 칠하면 "고를 수 있는 게 여럿"이라는
            // 사실 자체가 화면에서 사라지는데, 그게 이 대상의 핵심 난점이다.
            PlaneBInliers = allInliers.ToArray(),
            Inliers = _options.IncludeInliersInResult ? Sample(x, y, z, chosen.Inliers) : null,
        };

        static double AxisDistance(RidgeCandidate c) =>
            c.Ridge.Point.X * c.Ridge.Point.X + c.Ridge.Point.Y * c.Ridge.Point.Y;
    }

    /// <summary>띠 하나에서 원을 맞추고 피크선을 만든다. 검증에 걸리면 null 과 사유를 낸다.</summary>
    private RidgeCandidate? BuildCandidate(
        Plane plane, double[] origin, double[] e1, double[] e2,
        double[] a, double[] b, double[] h,
        double dirA, double dirB, int[] stripInliers,
        PlaneFitResult sheet, Random rng, out string? reason)
    {
        reason = null;

        // 단면 좌표: 피크선 방향 s, 그에 수직인 가로 방향 lateral, 높이 h.
        var vA = -dirB;
        var vB = dirA;

        var s = new double[a.Length];
        var lateral = new double[a.Length];
        foreach (var i in stripInliers)
        {
            s[i] = a[i] * dirA + b[i] * dirB;
            lateral[i] = a[i] * vA + b[i] * vB;
        }

        // 원 인라이어 하한을 무리 자체의 하한과 분리한다. 무리에 든 점이 전부 원 위에 있는
        // 것은 아니라서(뿌리 부근은 곡률이 다르고 노이즈도 크다), 같은 값을 쓰면 무리는
        // 통과했는데 원에서 탈락하는 일이 생긴다.
        var circle = CircleFit.Ransac(lateral, h, stripInliers,
            _options.ArcInlierThresholdMm, _options.RansacIterations, _options.MinArcPoints, rng);

        if (circle is null)
        {
            reason = "코러게이션 단면에 원을 맞추지 못했다. 형상이 원호가 아닐 수 있다.";
            return null;
        }

        // 반경이 실물과 맞는지가 가장 강력한 검증이다. 배경이나 엉뚱한 곡면을 잡으면 반경이
        // 전혀 다른 값으로 나오는데, 다른 지표(인라이어 수·잔차)는 그때도 멀쩡해 보인다.
        if (_options.ExpectedRadiusMm > 0)
        {
            var lower = _options.ExpectedRadiusMm * (1 - _options.RadiusTolerance);
            var upper = _options.ExpectedRadiusMm * (1 + _options.RadiusTolerance);

            if (circle.Circle.Radius < lower || circle.Circle.Radius > upper)
            {
                reason =
                    $"추정 반경 {circle.Circle.Radius:F1}mm 가 실물 {_options.ExpectedRadiusMm}mm 의 " +
                    $"허용 범위({lower:F0}~{upper:F0}mm)를 벗어났다. 코러게이션이 아닌 곡면을 잡았을 수 있다.";
                return null;
            }
        }

        if (circle.RmsMm > _options.MaxRmsMm)
        {
            reason = $"원 피팅 잔차 RMS 가 {circle.RmsMm:F1}mm 로 허용치 {_options.MaxRmsMm}mm 를 넘었다.";
            return null;
        }

        // 정점은 원 중심에서 판 바깥쪽(센서 쪽)으로 반경만큼 간 점이다. 정점 자체에 유효
        // 픽셀이 없어도 여기서 나온다 — 이 검출기를 만든 이유가 그것이다.
        var apexW = circle.Circle.CenterX;
        var apexH = circle.Circle.CenterY + circle.Circle.Radius;

        var (sMin, sMax) = Extent(s, circle.Inliers);
        var lengthMm = sMax - sMin;

        if (lengthMm < _options.MinRidgeLengthMm)
        {
            reason = $"피크선 길이가 {lengthMm:F0}mm 로 최소 요구치 {_options.MinRidgeLengthMm}mm 에 못 미친다.";
            return null;
        }

        // 길이 상한은 실물 치수를 아는 대상에서 가장 값싼 오검출 차단이다. 배경을 잡으면
        // 길이가 실물을 크게 넘는데(실측에서 대상 최대 치수 1000mm 인데 1557mm 가 나왔다),
        // 인라이어 수·잔차·반경은 그때도 정상으로 보인다.
        if (_options.MaxRidgeLengthMm > 0 && lengthMm > _options.MaxRidgeLengthMm)
        {
            reason =
                $"피크선 길이가 {lengthMm:F0}mm 로 상한 {_options.MaxRidgeLengthMm}mm 를 넘었다. " +
                "대상 밖까지 이어 붙였을 수 있다.";
            return null;
        }

        var ux = e1[0] * dirA + e2[0] * dirB;
        var uy = e1[1] * dirA + e2[1] * dirB;
        var uz = e1[2] * dirA + e2[2] * dirB;

        // 방향 부호를 결정적으로 고정한다. 직선의 방향은 ± 모호성이 있어 프레임마다 뒤집히면
        // 소비 측 제어가 반대로 간다.
        if (LargestComponentIsNegative(ux, uy, uz))
        {
            ux = -ux; uy = -uy; uz = -uz;
            (sMin, sMax) = (-sMax, -sMin);
        }

        var start = ApexAt(sMin);
        var end = ApexAt(sMax);

        var ridge = new RidgeLine
        {
            Point = new Vec3((start.X + end.X) / 2, (start.Y + end.Y) / 2, (start.Z + end.Z) / 2),
            Direction = new Vec3(ux, uy, uz),
            Start = start,
            End = end,
            LengthMm = lengthMm,
            InlierCount = circle.Inliers.Length,
            RmsMm = circle.RmsMm,
        };

        var arc = new ArcDiagnostics
        {
            RadiusMm = Math.Round(circle.Circle.Radius, 2),
            CenterHeightMm = Math.Round(circle.Circle.CenterY, 2),
            CircleRmsMm = Math.Round(circle.RmsMm, 2),
            PlaneRmsMm = Math.Round(sheet.RmsMm, 2),
            CorrugationPointCount = stripInliers.Length,
            MaxHeightMm = Math.Round(apexH, 1),
        };

        return new RidgeCandidate(ridge, Confidence(circle, sheet, stripInliers.Length), circle.Inliers, arc);

        Vec3 ApexAt(double along)
        {
            var pa = along * dirA + apexW * vA;
            var pb = along * dirB + apexW * vB;

            return new Vec3(
                origin[0] + e1[0] * pa + e2[0] * pb - plane.Nx * apexH,
                origin[1] + e1[1] * pa + e2[1] * pb - plane.Ny * apexH,
                origin[2] + e1[2] * pa + e2[2] * pb - plane.Nz * apexH);
        }
    }

    /// <summary>깊이 게이팅을 통과한 유효 픽셀 인덱스.</summary>
    private int[] CollectValid(AveragedCapture capture)
    {
        var result = new List<int>(capture.Width * capture.Height / 2);
        var hasMax = _options.MaxDistanceMm > 0;
        var minSamples = Math.Max(1, (int)Math.Ceiling(capture.FramesUsed * _options.MinSampleFraction));

        for (int i = 0; i < capture.SampleCount.Length; i++)
        {
            if (capture.SampleCount[i] < minSamples) continue;

            var d = capture.Distance[i];
            if (double.IsNaN(d)) continue;
            if (d < _options.MinDistanceMm) continue;
            if (hasMax && d > _options.MaxDistanceMm) continue;

            result.Add(i);
        }

        return result.ToArray();
    }

    /// <summary>
    /// 코러게이션이 뻗은 방향을 찾는다.
    ///
    /// <b>RANSAC 으로 띠를 찾아 그 주성분을 쓰는 방식은 쓰지 않는다.</b> 실측과 합성 양쪽에서
    /// 방향이 90° 뒤집히는 일이 재현됐다 — 추첨이 코러게이션을 <i>가로지르는</i> 선을 고르면
    /// 가로축이 코러게이션을 <i>따라가게</i> 되고, 그러면 모든 코러게이션이 하나의 무리로
    /// 뭉쳐 원 적합이 통째로 무너진다.
    ///
    /// 대신 <b>밀도 집중도를 직접 최적화한다.</b> 각도를 훑으며, 코러게이션 폭짜리 창 하나에
    /// 담기는 점의 비율이 <b>균등 분포일 때보다 몇 배인지</b>를 점수로 쓴다. 맞는 방향에서는
    /// 코러게이션이 그 창에 통째로 들어가 몇 배가 나오고, 틀린 방향에서는 점이 코러게이션
    /// 길이 전체로 퍼져 1배에 가까워진다.
    ///
    /// <b>"무리 폭 최소" 같은 기준은 쓸 수 없다.</b> 틀린 방향에서 점이 고르게 퍼지면 임계
    /// 근처에서 잘게 쪼개져 오히려 좁은 조각이 여럿 나오고, 그게 맞는 방향의 정상 무리보다
    /// 좋은 점수를 받는다. 실제로 이 함정에 걸려 방향이 90° 뒤집힌 적이 있다.
    ///
    /// 폭 대신 <b>비율</b>을 쓰는 이유는 이 대상의 점군이 가로로 더 길기 때문이다 — 코러게이션
    /// 길이는 350mm 인데 피치 370mm 로 여러 개가 늘어서면 가로 폭이 1000mm 에 이른다. 퍼짐의
    /// 크기로 방향을 고르면 정확히 반대를 고른다(주성분 분석도 같은 이유로 못 쓴다).
    /// </summary>
    private (double DirA, double DirB, double Score) FindAxis(double[] a, double[] b, int[] points)
    {
        // 각도 훑기는 점수 비교만 하므로 표본으로 충분하다. 전체를 매 각도마다 정렬하면
        // 미리보기 주기를 넘긴다.
        var sample = points;
        if (points.Length > AxisSampleMax)
        {
            var stride = points.Length / AxisSampleMax;
            sample = new int[AxisSampleMax];
            for (int k = 0; k < AxisSampleMax; k++) sample[k] = points[k * stride];
        }

        var window = _options.CorrugationWidthMm + 2 * _options.CorrugationBandMarginMm;
        var buffer = new double[sample.Length];
        var best = 0.0;
        double bestA = 1, bestB = 0;

        for (int step = 0; step < AxisSteps; step++)
        {
            // 직선의 방향은 180° 주기이므로 절반만 훑으면 된다.
            var theta = step * Math.PI / AxisSteps;
            var dirA = Math.Cos(theta);
            var dirB = Math.Sin(theta);

            for (int k = 0; k < sample.Length; k++)
                buffer[k] = a[sample[k]] * -dirB + b[sample[k]] * dirA;

            Array.Sort(buffer);

            var span = buffer[^1] - buffer[0];
            if (span <= window) continue;   // 전부 창 하나에 들어가면 방향을 가릴 수 없다

            var most = 0;
            var lo = 0;
            for (int hi = 0; hi < buffer.Length; hi++)
            {
                while (buffer[hi] - buffer[lo] > window) lo++;
                most = Math.Max(most, hi - lo + 1);
            }

            // 균등 분포 대비 밀도 배수. 1 이면 방향을 못 가린 것이다.
            var score = (double)most / buffer.Length / (window / span);

            if (score <= best) continue;
            best = score;
            bestA = dirA;
            bestB = dirB;
        }

        return (bestA, bestB, Math.Round(best, 2));
    }

    /// <summary>
    /// 가로 위치 분포를 히스토그램으로 만든다. 계산에는 쓰지 않고 진단으로만 내보낸다.
    ///
    /// 칸 폭은 코러게이션 폭의 1/7 로 잡는다 — 대상이 바뀌어도 코러게이션 하나가 일곱 칸쯤
    /// 차지하게 되어, 봉우리 모양과 골 깊이를 같은 눈으로 읽을 수 있다.
    /// </summary>
    private ArcHistogram BuildHistogram(double[] w, int[] points)
    {
        double min = double.MaxValue, max = double.MinValue;
        foreach (var i in points)
        {
            if (w[i] < min) min = w[i];
            if (w[i] > max) max = w[i];
        }

        var binWidth = Math.Max(2.0, _options.CorrugationWidthMm / 7);
        var span = Math.Max(binWidth, max - min);

        // 응답이 비대해지지 않도록 칸 수를 묶는다. 깊이 게이트가 넓게 열려 배경까지
        // 들어오면 가로 범위가 수 미터가 될 수 있다.
        var binCount = (int)Math.Ceiling(span / binWidth) + 1;
        if (binCount > MaxHistogramBins)
        {
            binWidth = span / MaxHistogramBins;
            binCount = MaxHistogramBins + 1;
        }

        var counts = new int[binCount];
        foreach (var i in points)
            counts[Math.Clamp((int)((w[i] - min) / binWidth), 0, binCount - 1)]++;

        return new ArcHistogram(Math.Round(binWidth, 2), Math.Round(min, 1), counts);
    }

    /// <summary>점들의 2차원 주성분 방향. 각도 훑기의 눈금 오차를 없애는 마무리에 쓴다.</summary>
    private static (double DirA, double DirB) PrincipalDirection(double[] a, double[] b, List<int> points)
    {
        double ma = 0, mb = 0;
        foreach (var i in points) { ma += a[i]; mb += b[i]; }
        ma /= points.Count; mb /= points.Count;

        double caa = 0, cab = 0, cbb = 0;
        foreach (var i in points)
        {
            var da = a[i] - ma;
            var db = b[i] - mb;
            caa += da * da; cab += da * db; cbb += db * db;
        }

        var theta = 0.5 * Math.Atan2(2 * cab, caa - cbb);
        return Canonical(Math.Cos(theta), Math.Sin(theta));
    }

    /// <summary>
    /// 방향의 ± 부호를 고정한다.
    ///
    /// 직선의 방향은 본질적으로 두 가지로 표현되는데, 어느 쪽이 나오느냐에 따라 가로 위치가
    /// 통째로 부호를 바꾼다. 계산 결과는 같지만 <b>진단값이 회차마다 뒤집혀 비교가 안 된다</b> —
    /// 실측에서 히스토그램 시작 위치가 −389 와 −59 사이를 오갔는데, 축이 흔들린 게 아니라
    /// 부호가 뒤집힌 것이었다.
    /// </summary>
    private static (double DirA, double DirB) Canonical(double dirA, double dirB) =>
        dirB < 0 || (dirB == 0 && dirA < 0) ? (-dirA, -dirB) : (dirA, dirB);

    /// <summary>
    /// 가로 위치로 코러게이션을 가른다. <b>간격이 아니라 밀도</b>로 나눈다.
    ///
    /// <b>왜 간격으로 나누면 안 되는가.</b> 평판은 완벽히 평평하지 않고 측정 노이즈도 있어서,
    /// 높이 하한을 조금만 낮게 잡아도 평판 전역에 산발적인 점이 남는다. 실측 규모로는 수천
    /// 개다. 이 점들이 코러게이션 사이의 빈 구간을 촘촘히 메우기 때문에, 정렬해서 간격을 봐도
    /// <b>끊기는 곳이 없다</b> — 실제로 이 방식은 어느 방향에서도 무리를 하나로만 냈다.
    ///
    /// 밀도로 보면 구분이 압도적이다. 코러게이션이 있는 구간은 산발 점의 수십 배가 모여 있다.
    /// 평균 이상인 구간만 남기면 산발 점은 자연히 떨어져 나간다.
    ///
    /// 마지막에 가까운 구간을 병합하는 이유는 <b>정점 포화</b> 때문이다. 광택 금속의 정점이
    /// 정반사로 날아가면 코러게이션 한가운데에 빈 칸이 생기는데, 병합하지 않으면 코러게이션
    /// 하나가 둘로 쪼개진다.
    /// </summary>
    private List<List<int>> Cluster(double[] w, int[] points)
    {
        // 코러게이션 하나가 담길 창의 폭. 실물 폭에 장착 기울기와 노이즈 여유를 더한 값이다.
        var window = _options.CorrugationWidthMm + 2 * _options.CorrugationBandMarginMm;

        // 다음 창을 찾을 때 이미 뗀 창에서 최소 이만큼 떨어져야 한다.
        //
        // <b>이게 없으면 같은 코러게이션을 두 번 센다.</b> 봉우리 폭이 창보다 넓으면 창을
        // 떼어낸 뒤 어깨가 남고, 그 어깨가 바로 옆 창으로 채택된다. 실측에서 무리 간격 11개
        // 중 10개가 창 폭(100mm)과 정확히 일치했다 — 피치 370mm 인 실물에서 나올 수 없는 값이다.
        //
        // 물리적으로 두 코러게이션은 피치만큼 떨어져 있으므로, 그 절반보다 가까운 두 봉우리는
        // 같은 것의 다른 부분이다.
        var separation = _options.CorrugationPitchMm > 0
            ? Math.Max(window, _options.CorrugationPitchMm / 2)
            : window;

        var pool = points.OrderBy(i => w[i]).ToList();
        var clusters = new List<List<int>>();
        var tallest = 0;

        while (clusters.Count < _options.MaxCandidates && pool.Count >= _options.MinCorrugationPoints)
        {
            // 두 포인터로 폭 window 인 창을 훑어 가장 많이 담기는 위치를 찾는다.
            int bestLo = 0, bestHi = -1, bestCount = 0, lo = 0;

            for (int hi = 0; hi < pool.Count; hi++)
            {
                while (w[pool[hi]] - w[pool[lo]] > window) lo++;

                var count = hi - lo + 1;
                if (count <= bestCount) continue;
                bestCount = count; bestLo = lo; bestHi = hi;
            }

            if (bestCount < _options.MinCorrugationPoints) break;

            // 가장 큰 봉우리 대비 너무 낮으면 코러게이션이 아니라 잔여 산발 점이다.
            if (tallest == 0) tallest = bestCount;
            else if (bestCount < tallest * _options.PeakRelativeThreshold) break;

            var taken = pool.GetRange(bestLo, bestHi - bestLo + 1);
            clusters.Add(taken);

            // 창 안의 점만 빼면 어깨가 남아 바로 옆이 다음 봉우리가 된다. 중심에서
            // separation 안쪽을 통째로 비워야 다음 후보가 진짜 다른 코러게이션이 된다.
            var center = (w[taken[0]] + w[taken[^1]]) / 2;
            pool.RemoveAll(i => Math.Abs(w[i] - center) < separation);
        }

        return clusters;
    }

    /// <summary>평면 법선으로부터 평면 내 정규직교 기저를 만든다.</summary>
    private static (double[] E1, double[] E2) InPlaneBasis(in Plane plane)
    {
        // 법선과 가장 덜 나란한 좌표축을 골라 외적한다. 나란한 축을 쓰면 외적이 0에 가까워져
        // 기저가 수치적으로 무너진다.
        double[] seed = Math.Abs(plane.Nx) < Math.Abs(plane.Ny)
            ? (Math.Abs(plane.Nx) < Math.Abs(plane.Nz) ? [1, 0, 0] : [0, 0, 1])
            : (Math.Abs(plane.Ny) < Math.Abs(plane.Nz) ? [0, 1, 0] : [0, 0, 1]);

        var e1 = new[]
        {
            plane.Ny * seed[2] - plane.Nz * seed[1],
            plane.Nz * seed[0] - plane.Nx * seed[2],
            plane.Nx * seed[1] - plane.Ny * seed[0],
        };

        var len = Math.Sqrt(e1[0] * e1[0] + e1[1] * e1[1] + e1[2] * e1[2]);
        e1[0] /= len; e1[1] /= len; e1[2] /= len;

        var e2 = new[]
        {
            plane.Ny * e1[2] - plane.Nz * e1[1],
            plane.Nz * e1[0] - plane.Nx * e1[2],
            plane.Nx * e1[1] - plane.Ny * e1[0],
        };

        return (e1, e2);
    }

    /// <summary>피크선 방향 구간. 양 끝 2%를 잘라 이상점 하나가 길이를 늘리는 것을 막는다.</summary>
    private static (double Min, double Max) Extent(double[] s, int[] indices)
    {
        var t = new double[indices.Length];
        for (int k = 0; k < indices.Length; k++) t[k] = s[indices[k]];

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

    /// <summary>
    /// 원 피팅 잔차·평판 잔차·반경 일치도를 0~1 신뢰도로 합친다.
    ///
    /// 반경 일치도의 비중이 가장 크다 — 이 형상에서 "엉뚱한 것을 잡았는가"를 가르는 가장
    /// 강한 신호이기 때문이다.
    /// </summary>
    private double Confidence(CircleFitResult circle, PlaneFitResult sheet, int stripPoints)
    {
        var fit = Math.Clamp(1.0 - circle.RmsMm / _options.MaxRmsMm, 0, 1);
        var coverage = Math.Clamp((double)circle.Inliers.Length / Math.Max(stripPoints, 1), 0, 1);
        var planeQuality = Math.Clamp(1.0 - sheet.RmsMm / _options.PlaneInlierThresholdMm, 0, 1);

        var radius = 1.0;
        if (_options.ExpectedRadiusMm > 0)
        {
            var error = Math.Abs(circle.Circle.Radius - _options.ExpectedRadiusMm) / _options.ExpectedRadiusMm;
            radius = Math.Clamp(1.0 - error / Math.Max(_options.RadiusTolerance, 1e-6), 0, 1);
        }

        return Math.Round(fit * 0.3 + coverage * 0.2 + planeQuality * 0.15 + radius * 0.35, 3);
    }

    /// <summary>진단용 인라이어 점군. 응답이 비대해지지 않도록 균등 간격으로 솎아낸다.</summary>
    private IReadOnlyList<Vec3> Sample(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, int[] indices)
    {
        var step = Math.Max(1, indices.Length / _options.MaxInlierSamples);
        var result = new List<Vec3>(Math.Min(indices.Length, _options.MaxInlierSamples));

        for (int i = 0; i < indices.Length; i += step)
        {
            var p = indices[i];
            result.Add(new Vec3(x[p], y[p], z[p]));
        }

        return result;
    }
}
