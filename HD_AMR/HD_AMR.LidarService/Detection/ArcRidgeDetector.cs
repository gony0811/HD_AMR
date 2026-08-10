using HD_AMR.Contracts.Lidar;
using HD_AMR.LidarService.Device;

namespace HD_AMR.LidarService.Detection;

/// <summary>
/// 평판 위에 솟은 <b>반원 비드</b>의 정점선을 찾는다. 실제 측정 대상의 형상이다 —
/// 평평한 스테인리스 판에 반경 35mm 반원 리브가 370mm 간격으로 성형되어 있다.
///
/// <b>왜 두 평면 교선 방식을 쓰지 않는가.</b> 그 방식은 마루가 두 경사면이 만나는 모서리라는
/// 전제 위에 서 있다. 이 형상에는 그런 모서리가 없다 — 비드 양옆은 <b>같은 평판</b>이라
/// 교차하는 두 평면 자체가 존재하지 않는다. 실제로 그 검출기를 실물에 돌렸을 때 판 안에서
/// 두 번째 평면을 찾지 못하고 배경 벽을 집어와 오검출을 냈다.
///
/// <b>대신 철학은 그대로 가져간다.</b> 정점 픽셀을 직접 고르지 않고 <b>옆면으로부터 정점을
/// 계산</b>한다. 광택 금속의 정점은 정반사로 포화되어 유효 픽셀이 사라지는 일이 실제로
/// 관측됐고(ADC 오버플로), 그때도 위치가 나와야 하기 때문이다.
///
/// 절차:
/// <list type="number">
///   <item>깊이 게이팅 후 <b>평판 평면</b>을 RANSAC 으로 찾는다(지배적 평면이라 쉽다)</item>
///   <item>평면으로부터의 높이를 계산해 <b>비드 점군</b>을 뽑는다</item>
///   <item>평면 안에서 직선 RANSAC 으로 <b>비드 하나</b>만 고른다(시야에 여러 개가 들어온다)</item>
///   <item>그 비드의 단면에 <b>원</b>을 맞춘다</item>
///   <item>정점 = 원 중심 + 반경. 정점들을 잇는 직선이 능선이다</item>
/// </list>
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
        // 비드는 판 면적의 일부일 뿐이라 평판이 언제나 지배적이다. 인라이어 임계를 좁게
        // 두는 이유는, 넓으면 비드 뿌리까지 평판으로 빨아들여 높이 계산이 무뎌지기 때문이다.
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

        // ── 2. 비드 점군 ────────────────────────────────────────────────────
        var beadPoints = new List<int>(candidates.Length / 8);
        double maxHeight = 0;

        foreach (var i in candidates)
        {
            var height = plane.D - (plane.Nx * x[i] + plane.Ny * y[i] + plane.Nz * z[i]);
            if (height > maxHeight) maxHeight = height;
            if (height < _options.BeadMinHeightMm || height > _options.BeadMaxHeightMm) continue;

            beadPoints.Add(i);
        }

        var diagnostics = new RidgeDetectionResult
        {
            Success = false,
            CandidateCount = candidates.Length,
            PlaneAInliers = sheet.Inliers,
            PlaneBInliers = beadPoints.ToArray(),
            Arc = new ArcDiagnostics
            {
                PlaneRmsMm = Math.Round(sheet.RmsMm, 2),
                BeadPointCount = beadPoints.Count,
                MaxHeightMm = Math.Round(maxHeight, 1),
            },
        };

        if (beadPoints.Count < _options.MinBeadPoints)
        {
            return diagnostics with
            {
                Failure = MeasureFailure.InsufficientValidPixels,
                FailureDetail =
                    $"비드 점이 {beadPoints.Count}개로 부족하다(최소 {_options.MinBeadPoints}개). " +
                    $"관측된 최대 높이는 {maxHeight:F1}mm 다 — 비드가 시야에 없거나, " +
                    $"높이 구간 {_options.BeadMinHeightMm}~{_options.BeadMaxHeightMm}mm 가 실물과 맞지 않는다.",
            };
        }

        // ── 3. 평면 내 좌표계와 비드 하나 고르기 ─────────────────────────────
        var (e1, e2) = InPlaneBasis(plane);
        var origin = new[] { plane.Nx * plane.D, plane.Ny * plane.D, plane.Nz * plane.D };

        var beadArray = beadPoints.ToArray();
        var a = new double[capture.X.Length];   // 평면 내 좌표 1
        var b = new double[capture.X.Length];   // 평면 내 좌표 2
        var h = new double[capture.X.Length];   // 평면 위 높이

        foreach (var i in beadArray)
        {
            var dx = x[i] - origin[0];
            var dy = y[i] - origin[1];
            var dz = z[i] - origin[2];

            h[i] = -(plane.Nx * dx + plane.Ny * dy + plane.Nz * dz);
            a[i] = e1[0] * dx + e1[1] * dy + e1[2] * dz;
            b[i] = e2[0] * dx + e2[1] * dy + e2[2] * dz;
        }

        // 시야에 비드가 여러 개 들어온다(피치 370mm). 전체를 한꺼번에 다루면 단면 원 피팅이
        // 여러 개의 호를 하나로 뭉개서 무의미한 답을 낸다. 평면 안에서 직선 RANSAC 으로
        // 띠 하나를 고르면 그게 곧 비드 하나이고, 그 직선의 방향이 능선 방향이다.
        var strip = LineRansac(a, b, beadArray, _options.BeadWidthMm / 2 + _options.BeadBandMarginMm,
            _options.RansacIterations, _options.MinBeadPoints, rng);

        if (strip is null)
        {
            return diagnostics with
            {
                Failure = MeasureFailure.RidgeFitFailed,
                FailureDetail =
                    $"비드 점 {beadPoints.Count}개에서 띠 형태를 찾지 못했다. " +
                    "높이 구간이 넓어 잡음을 포함했거나, 비드가 부분적으로만 보일 수 있다.",
            };
        }

        var (dirA, dirB, stripInliers) = strip.Value;
        diagnostics = diagnostics with { PlaneBInliers = stripInliers };

        // 능선 방향(3D). 평면 안의 직선 방향을 그대로 들어올린 것이다.
        var ux = e1[0] * dirA + e2[0] * dirB;
        var uy = e1[1] * dirA + e2[1] * dirB;
        var uz = e1[2] * dirA + e2[2] * dirB;

        // 단면 좌표: 능선 방향 s, 그에 수직인 가로 방향 w, 높이 h.
        var vA = -dirB;
        var vB = dirA;

        var s = new double[capture.X.Length];
        var w = new double[capture.X.Length];
        foreach (var i in stripInliers)
        {
            s[i] = a[i] * dirA + b[i] * dirB;
            w[i] = a[i] * vA + b[i] * vB;
        }

        // ── 4. 단면 원 피팅 ─────────────────────────────────────────────────
        var circle = CircleFit.Ransac(w, h, stripInliers,
            _options.ArcInlierThresholdMm, _options.RansacIterations, _options.MinBeadPoints, rng);

        if (circle is null)
        {
            return diagnostics with
            {
                Failure = MeasureFailure.RidgeFitFailed,
                FailureDetail = "비드 단면에 원을 맞추지 못했다. 형상이 원호가 아닐 수 있다.",
            };
        }

        diagnostics = diagnostics with
        {
            PlaneBInliers = circle.Inliers,
            Arc = diagnostics.Arc! with
            {
                RadiusMm = Math.Round(circle.Circle.Radius, 2),
                CenterHeightMm = Math.Round(circle.Circle.CenterY, 2),
                CircleRmsMm = Math.Round(circle.RmsMm, 2),
            },
        };

        // 반경이 실물과 맞는지가 가장 강력한 검증이다. 배경이나 엉뚱한 곡면을 잡으면 반경이
        // 전혀 다른 값으로 나오는데, 다른 지표(인라이어 수·잔차)는 그때도 멀쩡해 보인다.
        if (_options.ExpectedRadiusMm > 0)
        {
            var lower = _options.ExpectedRadiusMm * (1 - _options.RadiusTolerance);
            var upper = _options.ExpectedRadiusMm * (1 + _options.RadiusTolerance);

            if (circle.Circle.Radius < lower || circle.Circle.Radius > upper)
            {
                return diagnostics with
                {
                    Failure = MeasureFailure.RidgeFitFailed,
                    FailureDetail =
                        $"추정 반경 {circle.Circle.Radius:F1}mm 가 실물 {_options.ExpectedRadiusMm}mm 의 " +
                        $"허용 범위({lower:F0}~{upper:F0}mm)를 벗어났다. 비드가 아닌 곡면을 잡았을 수 있다.",
                };
            }
        }

        if (circle.RmsMm > _options.MaxRmsMm)
        {
            return diagnostics with
            {
                Failure = MeasureFailure.RmsExceeded,
                FailureDetail =
                    $"원 피팅 잔차 RMS 가 {circle.RmsMm:F1}mm 로 허용치 {_options.MaxRmsMm}mm 를 넘었다.",
            };
        }

        // ── 5. 정점선 ───────────────────────────────────────────────────────
        // 정점은 원 중심에서 판 바깥쪽(센서 쪽)으로 반경만큼 간 점이다. 정점 자체에 유효
        // 픽셀이 없어도 여기서 나온다 — 이 검출기를 만든 이유가 그것이다.
        var apexW = circle.Circle.CenterX;
        var apexH = circle.Circle.CenterY + circle.Circle.Radius;

        var (sMin, sMax) = Extent(s, circle.Inliers);
        var lengthMm = sMax - sMin;

        if (lengthMm < _options.MinRidgeLengthMm)
        {
            return diagnostics with
            {
                Failure = MeasureFailure.RidgeFitFailed,
                FailureDetail =
                    $"능선 길이가 {lengthMm:F0}mm 로 최소 요구치 {_options.MinRidgeLengthMm}mm 에 못 미친다.",
            };
        }

        // 방향 부호를 결정적으로 고정한다. 직선의 방향은 ± 모호성이 있어 프레임마다 뒤집히면
        // 소비 측 제어가 반대로 간다.
        if (LargestComponentIsNegative(ux, uy, uz))
        {
            ux = -ux; uy = -uy; uz = -uz;
            (sMin, sMax) = (-sMax, -sMin);
        }

        var start = ApexAt(sMin);
        var end = ApexAt(sMax);
        var mid = new Vec3((start.X + end.X) / 2, (start.Y + end.Y) / 2, (start.Z + end.Z) / 2);

        var confidence = Confidence(circle, sheet, beadPoints.Count);

        _log.LogDebug(
            "비드 능선 검출: 반경 {Radius:F1}mm, 중심높이 {Center:F1}mm, 원 RMS {Rms:F2}mm, 길이 {Length:F0}mm",
            circle.Circle.Radius, circle.Circle.CenterY, circle.RmsMm, lengthMm);

        return diagnostics with
        {
            Success = true,
            Failure = null,
            FailureDetail = null,
            Confidence = confidence,
            Ridge = new RidgeLine
            {
                Point = mid,
                Direction = new Vec3(ux, uy, uz),
                Start = start,
                End = end,
                LengthMm = lengthMm,
                InlierCount = circle.Inliers.Length,
                RmsMm = circle.RmsMm,
            },
            Inliers = _options.IncludeInliersInResult ? Sample(x, y, z, circle.Inliers) : null,
        };

        // 능선 위의 점: 평면 원점에서 s 만큼 능선 방향, apexW 만큼 가로 방향으로 간 뒤
        // apexH 만큼 판 바깥(센서 쪽)으로 들어올린다.
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
    /// 띠를 찾는다. 비드가 여러 개일 때 하나만 골라내는 수단이다.
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

            // 두 점이 너무 가까우면 방향이 노이즈로 정해진다. 비드 길이에 비해 의미 있는
            // 간격만 채택한다.
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

        // 인라이어 전체로 방향을 다시 정한다(2차원 주성분). 두 점으로 정한 방향은 351mm
        // 길이의 비드에서도 각도 오차가 커서, 그대로 두면 단면 투영이 비스듬해진다.
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

    /// <summary>평면에 수직인 법선으로부터 평면 내 정규직교 기저를 만든다.</summary>
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

    /// <summary>능선 방향 구간. 양 끝 2%를 잘라 이상점 하나가 능선을 늘리는 것을 막는다.</summary>
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
    /// 두 평면 방식과 달리 <b>사잇각 항이 없다.</b> 대신 반경 일치도가 그 자리를 대신한다 —
    /// 이 형상에서 "엉뚱한 것을 잡았는가"를 가르는 가장 강한 신호이기 때문이다.
    /// </summary>
    private double Confidence(CircleFitResult circle, PlaneFitResult sheet, int beadPoints)
    {
        var fit = Math.Clamp(1.0 - circle.RmsMm / _options.MaxRmsMm, 0, 1);
        var coverage = Math.Clamp((double)circle.Inliers.Length / Math.Max(beadPoints, 1), 0, 1);
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
