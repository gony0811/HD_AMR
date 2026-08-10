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

        // ── 4. 코러게이션을 하나씩 벗겨낸다 ─────────────────────────────────
        // 띠 하나를 찾아 원을 맞추고, 그 점들을 빼고 다시 찾는다. 실패한 띠도 반드시 빼야
        // 한다 — 안 그러면 같은 띠를 무한히 다시 찾는다.
        var remaining = raised.ToArray();
        var found = new List<RidgeCandidate>();
        string? firstFailure = null;

        var band = _options.CorrugationWidthMm / 2 + _options.CorrugationBandMarginMm;
        var allInliers = new List<int>();

        for (int k = 0; k < _options.MaxCandidates && remaining.Length >= _options.MinCorrugationPoints; k++)
        {
            var strip = LineRansac(a, b, remaining, band,
                _options.RansacIterations, _options.MinCorrugationPoints, rng);

            if (strip is null)
            {
                firstFailure ??=
                    $"코러게이션 점 {remaining.Length}개에서 띠 형태를 찾지 못했다. " +
                    "높이 구간이 넓어 잡음을 포함했거나, 코러게이션이 부분적으로만 보일 수 있다.";
                break;
            }

            var (dirA, dirB, stripInliers) = strip.Value;

            var candidate = BuildCandidate(
                plane, origin, e1, e2, a, b, h, dirA, dirB, stripInliers, sheet, rng, out var reason);

            if (candidate is not null)
            {
                found.Add(candidate);
                allInliers.AddRange(candidate.Inliers);
            }
            else
            {
                firstFailure ??= reason;
            }

            var used = new HashSet<int>(stripInliers);
            remaining = remaining.Where(i => !used.Contains(i)).ToArray();
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

        // 단면 좌표: 피크선 방향 s, 그에 수직인 가로 방향 w, 높이 h.
        var vA = -dirB;
        var vB = dirA;

        var s = new double[a.Length];
        var w = new double[a.Length];
        foreach (var i in stripInliers)
        {
            s[i] = a[i] * dirA + b[i] * dirB;
            w[i] = a[i] * vA + b[i] * vB;
        }

        var circle = CircleFit.Ransac(w, h, stripInliers,
            _options.ArcInlierThresholdMm, _options.RansacIterations, _options.MinCorrugationPoints, rng);

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
    /// 평면 안의 2차원 직선 RANSAC. 폭 <paramref name="bandMm"/> 안에 가장 많은 점을 담는
    /// 띠를 찾는다. 코러게이션이 여러 개일 때 하나씩 벗겨내는 수단이다.
    /// </summary>
    private static (double DirA, double DirB, int[] Inliers)? LineRansac(
        double[] a, double[] b, int[] candidates, double bandMm, int iterations, int minInliers, Random rng)
    {
        if (candidates.Length < Math.Max(2, minInliers)) return null;

        var bestCount = 0;
        double bestDirA = 0, bestDirB = 0, bestOriginA = 0, bestOriginB = 0;

        for (int iter = 0; iter < iterations; iter++)
        {
            var i0 = candidates[rng.Next(candidates.Length)];
            var i1 = candidates[rng.Next(candidates.Length)];

            var da = a[i1] - a[i0];
            var db = b[i1] - b[i0];
            var len = Math.Sqrt(da * da + db * db);

            // 두 점이 너무 가까우면 방향이 노이즈로 정해진다.
            if (len < bandMm) continue;

            da /= len; db /= len;

            var count = 0;
            foreach (var i in candidates)
            {
                if (Math.Abs((a[i] - a[i0]) * -db + (b[i] - b[i0]) * da) <= bandMm) count++;
            }

            if (count > bestCount)
            {
                bestCount = count;
                bestDirA = da; bestDirB = db;
                bestOriginA = a[i0]; bestOriginB = b[i0];
            }
        }

        if (bestCount < minInliers) return null;

        var inliers = new List<int>(bestCount);
        foreach (var i in candidates)
        {
            if (Math.Abs((a[i] - bestOriginA) * -bestDirB + (b[i] - bestOriginB) * bestDirA) <= bandMm)
                inliers.Add(i);
        }

        // 인라이어 전체로 방향을 다시 정한다(2차원 주성분). 두 점으로 정한 방향은 각도 오차가
        // 커서, 그대로 두면 단면 투영이 비스듬해진다.
        double ma = 0, mb = 0;
        foreach (var i in inliers) { ma += a[i]; mb += b[i]; }
        ma /= inliers.Count; mb /= inliers.Count;

        double caa = 0, cab = 0, cbb = 0;
        foreach (var i in inliers)
        {
            var da2 = a[i] - ma;
            var db2 = b[i] - mb;
            caa += da2 * da2; cab += da2 * db2; cbb += db2 * db2;
        }

        var theta = 0.5 * Math.Atan2(2 * cab, caa - cbb);
        return (Math.Cos(theta), Math.Sin(theta), inliers.ToArray());
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
