using HD_AMR.Models;

namespace HD_AMR.Communication;

/// <summary>
/// 레이저 보조 터치 방식 토치 팁 TCP 캘리브레이션 — 접촉 기록 수집 + 최소자승 산출.
///
/// 원리: 오퍼레이터가 팁을 평판에 여러 자세로 물리 접촉시키면, 매 접촉마다 레이저가 평면을
/// 툴-1 좌표로 실측한다. "팁(플랜지 좌표 p)은 평면 위에 있다"는 제약 n_k·p = c_k 가 자세당
/// 1개씩 생기고, 법선이 다양한(기울기 펼침 ≥15° 권장) 기록 ≥3개면 p 를 최소자승으로 푼다.
/// 제약이 플랜지 국소(base FK 불필요)라 로봇 절대정밀도와 무관하다.
///
/// 언미러링: 시스템 pose 는 "빔=+Z 미러 규약"(TiltReadingSignForUp=true, 실제 빔은 −Z)으로
/// 동작하므로 실기하 계산은 z 미러(M=diag(1,1,−1))를 되돌린다. 보정 pose(Rx,Ry)에서
/// 모델 법선 n_m ∝ (tan Ry, tan Rx, 1) 을 재구성한 뒤:
///   실제 평면점(툴1) q_t = (X, Y, −Z),  실제 법선 ñ_t = (−n_mx, −n_my, +n_mz)  [ñ_z&gt;0 = 표면→센서]
/// 이 부호는 실기 검증된 보정 gain(지령 +Rx→측정 Rx −1, 지령 +Ry→ +1)과 일치함을 유도로 확인했다.
/// 이후 tool-1 정의(<see cref="PoseMath.FromPose"/>, flange→TCP, ZYX 도)로 플랜지 변환:
///   n_f = R·ñ_t,  c = n_f·(R·q_t + t).
///
/// ⚠ 관측 불가 DOF: 위 q_t 는 출사면이 툴-1 Z=0 에 있다고 가정하지만 실제 출사면 높이 h 는
/// 미지다. 진짜 제약은 n·p = c + h·ñ_z 인데, 빔이 툴 Z와 평행이라 팁 z(계수 n_z = R·ñ 의 z…
/// 툴프레임에서 보면 ñ_z)와 h(계수 ñ_z)가 모든 식에서 정확히 비례 → <b>팁 절대 Z 는 어떤 터치
/// 조합으로도 분리 불가</b> — 단, 둘 중 하나를 알면 나머지는 풀린다. 그래서 두 모드를 제공한다:
///   모드 1(기준 Z 고정): 팁 Z 를 외부 기준(펜던트/도면)으로 고정 → (px, py, h) 산출.
///   모드 2(h 고정): 출사면 높이를 고정 → (px, py, pz) 전부 산출. h 는 모드 1에서 신뢰하는
///   기준 Z 로 1회 앵커링한 추정치를 재사용하는 것을 권장 — 센서 브래킷은 토치 교체와 무관하게
///   불변이므로, 이후 토치/노즐 교체 시 펜던트 없이 터치만으로 새 팁 XYZ 재캘리브레이션이 가능하다.
///
/// 결과는 저장하지 않는다(세션 상태) — 컨트롤러 공구 좌표 쓰기는 호출측의 명시적 조작으로만.
/// </summary>
public sealed class TcpTouchCalibrator
{
    private const double Deg2Rad = Math.PI / 180.0;
    private const double Rad2Deg = 180.0 / Math.PI;

    /// <summary>이 잔차(mm) 초과 터치는 아웃라이어로 경고.</summary>
    private const double OutlierResidualMm = 1.0;

    /// <summary>유효 펼침각이 이 값(도) 미만이면 다양성 부족 경고.</summary>
    private const double SpreadWarnDeg = 10.0;

    /// <summary>유효 펼침각이 이 값(도) 미만이면 산출 실패(해가 불안정).</summary>
    private const double SpreadFailDeg = 2.0;

    private readonly List<TcpTouchRecord> _touches = new();

    /// <summary>수집된 접촉 기록(추가 순).</summary>
    public IReadOnlyList<TcpTouchRecord> Touches => _touches;

    /// <summary>
    /// 평균 낸 보정 pose(모델 규약)와 기록 시점 tool-1 정의로 접촉 기록 1건 생성 —
    /// 언미러링 + 플랜지 변환(클래스 주석 수식).
    /// </summary>
    public static TcpTouchRecord BuildRecord(
        double xMean, double yMean, double zMean, double rxMeanDeg, double ryMeanDeg,
        double[] toolCoord1, DateTime atUtc)
    {
        // 모델 법선 재구성 — LaserDisplacementSensorService.GetPlanePose 의 역과 동일식.
        double nmx = Math.Tan(ryMeanDeg * Deg2Rad);
        double nmy = Math.Tan(rxMeanDeg * Deg2Rad);
        double nmz = 1.0;
        double m = Math.Sqrt(nmx * nmx + nmy * nmy + nmz * nmz);
        nmx /= m; nmy /= m; nmz /= m;

        // 언미러링(툴1 실좌표): 점은 z 만 미러, 법선은 ñ_z>0(표면→센서)로 부호 통일.
        var qT = new[] { xMean, yMean, -zMean };
        var nT = new[] { -nmx, -nmy, nmz };

        // 플랜지 변환: T = flange→tool1. 법선은 회전만, 점은 회전+병진.
        var t14 = PoseMath.FromPose(toolCoord1);
        var nF = RotateOnly(t14, nT);
        var qF = new[]
        {
            t14[0, 0] * qT[0] + t14[0, 1] * qT[1] + t14[0, 2] * qT[2] + t14[0, 3],
            t14[1, 0] * qT[0] + t14[1, 1] * qT[1] + t14[1, 2] * qT[2] + t14[1, 3],
            t14[2, 0] * qT[0] + t14[2, 1] * qT[1] + t14[2, 2] * qT[2] + t14[2, 3],
        };
        double c = nF[0] * qF[0] + nF[1] * qF[1] + nF[2] * qF[2];

        return new TcpTouchRecord(
            nF, c, nmz, rxMeanDeg, ryMeanDeg, zMean,
            (double[])toolCoord1.Clone(), atUtc);
    }

    public void Add(TcpTouchRecord r) => _touches.Add(r);

    public void RemoveAt(int index)
    {
        if ((uint)index < (uint)_touches.Count) _touches.RemoveAt(index);
    }

    public void Clear() => _touches.Clear();

    /// <summary>
    /// 모드 1 — 기준 Z 고정: 팁 X/Y + 출사면 높이 h 를 산출한다. 팁 Z 는
    /// <paramref name="fixedTipZ"/>(외부 기준값, 플랜지 mm)로 고정.
    /// 미지수 u=(px, py, h), 행 (n_kx, n_ky, −ñ_kz), rhs = c_k − n_kz·Z_ref.
    /// </summary>
    public TcpTouchCalibrationResult Solve(double fixedTipZ, double[]? currentTool1 = null)
        => SolveInternal(fixTipZ: true, fixedTipZ, currentTool1);

    /// <summary>
    /// 모드 2 — 출사면 h 고정: 팁 X/Y/Z 전부를 산출한다. h 는 모드 1에서 경험적으로
    /// 앵커링한 추정치(권장) 또는 외부 값. h 오차는 팁 Z 에 ≈1:1 전파된다.
    /// 미지수 u=(px, py, pz), 행 n_k, rhs = c_k + h_fix·ñ_kz.
    /// </summary>
    public TcpTouchCalibrationResult SolveWithFixedHeadPlane(double fixedHeadPlaneZ, double[]? currentTool1 = null)
        => SolveInternal(fixTipZ: false, fixedHeadPlaneZ, currentTool1);

    private TcpTouchCalibrationResult SolveInternal(bool fixTipZ, double fixedValue, double[]? currentTool1)
    {
        int n = _touches.Count;
        if (n < 3)
            return TcpTouchCalibrationResult.Fail($"터치 기록이 부족합니다({n}/3) — 서로 다른 기울기로 더 기록하세요.");

        var warnings = new List<string>();

        // 기록 간 tool-1 정의 변경 감지 — 플랜지 변환 기준이 흔들리면 제약이 뒤섞인다.
        var first = _touches[0].ToolCoord1;
        for (int k = 1; k < n; k++)
        {
            for (int i = 0; i < 6; i++)
            {
                if (Math.Abs(_touches[k].ToolCoord1[i] - first[i]) > 0.01)
                {
                    warnings.Add("기록 중 tool-1 정의가 변경되었습니다 — 전체 삭제 후 재기록을 권장합니다.");
                    k = n;   // 한 번만 경고.
                    break;
                }
            }
        }

        // 정규방정식 A u = b. 모드별 행/우변:
        //   기준 Z 고정: u=(px,py,h),  행 (nx, ny, −ñz), rhs = c − nz·Z_ref
        //   h 고정:      u=(px,py,pz), 행 (nx, ny, nz),  rhs = c + h_fix·ñz
        double[] Row(TcpTouchRecord t) => fixTipZ
            ? new[] { t.NormalFlange[0], t.NormalFlange[1], -t.NormalToolZ }
            : new[] { t.NormalFlange[0], t.NormalFlange[1], t.NormalFlange[2] };
        double Rhs(TcpTouchRecord t) => fixTipZ
            ? t.C - t.NormalFlange[2] * fixedValue
            : t.C + fixedValue * t.NormalToolZ;

        var a = new double[3, 3];
        var b = new double[3];
        foreach (var t in _touches)
        {
            var row = Row(t);
            double rhs = Rhs(t);
            for (int i = 0; i < 3; i++)
            {
                b[i] += rhs * row[i];
                for (int j = 0; j < 3; j++) a[i, j] += row[i] * row[j];
            }
        }

        double det =
            a[0, 0] * (a[1, 1] * a[2, 2] - a[1, 2] * a[2, 1]) -
            a[0, 1] * (a[1, 0] * a[2, 2] - a[1, 2] * a[2, 0]) +
            a[0, 2] * (a[1, 0] * a[2, 1] - a[1, 1] * a[2, 0]);

        double lambdaMin = MinEigenvalueSym3(a);
        double spreadDeg = Math.Asin(Math.Clamp(Math.Sqrt(Math.Max(0.0, lambdaMin) / n), 0.0, 1.0)) * Rad2Deg;

        double sinFail = Math.Sin(SpreadFailDeg * Deg2Rad);
        if (Math.Abs(det) < 1e-9 || lambdaMin / n < sinFail * sinFail)
            return TcpTouchCalibrationResult.Fail(
                $"자세 다양성이 부족해 해가 불안정합니다(펼침 {spreadDeg:0.#}°) — 기울기를 ±15° 이상, 양쪽 부호로 벌려 재기록하세요.");

        // Cramer 해 u = (u0, u1, u2).
        double u0 = (b[0] * (a[1, 1] * a[2, 2] - a[1, 2] * a[2, 1]) -
                     a[0, 1] * (b[1] * a[2, 2] - a[1, 2] * b[2]) +
                     a[0, 2] * (b[1] * a[2, 1] - a[1, 1] * b[2])) / det;
        double u1 = (a[0, 0] * (b[1] * a[2, 2] - a[1, 2] * b[2]) -
                     b[0] * (a[1, 0] * a[2, 2] - a[1, 2] * a[2, 0]) +
                     a[0, 2] * (a[1, 0] * b[2] - b[1] * a[2, 0])) / det;
        double u2 = (a[0, 0] * (a[1, 1] * b[2] - b[1] * a[2, 1]) -
                     a[0, 1] * (a[1, 0] * b[2] - b[1] * a[2, 0]) +
                     b[0] * (a[1, 0] * a[2, 1] - a[1, 1] * a[2, 0])) / det;

        double px = u0, py = u1;
        double tipZ = fixTipZ ? fixedValue : u2;
        double h = fixTipZ ? u2 : fixedValue;

        // 잔차 = 해에서 각 (h 반영된) 평면까지 부호 거리 = row·u − rhs.
        var residuals = new double[n];
        double sumSq = 0, maxAbs = 0;
        for (int k = 0; k < n; k++)
        {
            var t = _touches[k];
            var row = Row(t);
            residuals[k] = row[0] * u0 + row[1] * u1 + row[2] * u2 - Rhs(t);
            sumSq += residuals[k] * residuals[k];
            maxAbs = Math.Max(maxAbs, Math.Abs(residuals[k]));
            if (Math.Abs(residuals[k]) > OutlierResidualMm)
                warnings.Add($"터치 #{k + 1} 잔차 {residuals[k]:+0.00;-0.00}mm > {OutlierResidualMm:0.0}mm — 접촉 상태 재확인/삭제를 권장합니다.");
        }
        double rms = Math.Sqrt(sumSq / n);

        if (spreadDeg < SpreadWarnDeg)
            warnings.Add($"법선 다양성 부족(펼침 {spreadDeg:0.#}° < {SpreadWarnDeg:0.#}°) — 더 기울인 자세로 추가 터치를 권장합니다(≥15°, 양쪽 부호).");

        double[]? delta = currentTool1 is { Length: >= 3 }
            ? new[] { px - currentTool1[0], py - currentTool1[1], tipZ - currentTool1[2] }
            : null;

        return new TcpTouchCalibrationResult(
            true, null, px, py, tipZ, h, residuals, rms, maxAbs, lambdaMin, spreadDeg, delta, warnings);
    }

    /// <summary>4x4 동차변환의 회전부만 벡터에 적용.</summary>
    private static double[] RotateOnly(double[,] t, double[] v) => new[]
    {
        t[0, 0] * v[0] + t[0, 1] * v[1] + t[0, 2] * v[2],
        t[1, 0] * v[0] + t[1, 1] * v[1] + t[1, 2] * v[2],
        t[2, 0] * v[0] + t[2, 1] * v[1] + t[2, 2] * v[2],
    };

    /// <summary>대칭 3×3 최소 고유값 — 삼각함수 폐형식(Smith 방법). PSD 입력 가정.</summary>
    private static double MinEigenvalueSym3(double[,] a)
    {
        double p1 = a[0, 1] * a[0, 1] + a[0, 2] * a[0, 2] + a[1, 2] * a[1, 2];
        double trace = a[0, 0] + a[1, 1] + a[2, 2];
        if (p1 < 1e-18)
            return Math.Min(a[0, 0], Math.Min(a[1, 1], a[2, 2]));   // 이미 대각.

        double q = trace / 3.0;
        double p2 = (a[0, 0] - q) * (a[0, 0] - q) + (a[1, 1] - q) * (a[1, 1] - q) +
                    (a[2, 2] - q) * (a[2, 2] - q) + 2.0 * p1;
        double p = Math.Sqrt(p2 / 6.0);
        if (p < 1e-18) return q;

        // B = (A − qI)/p 의 det/2 = cos(3φ).
        double b00 = (a[0, 0] - q) / p, b11 = (a[1, 1] - q) / p, b22 = (a[2, 2] - q) / p;
        double b01 = a[0, 1] / p, b02 = a[0, 2] / p, b12 = a[1, 2] / p;
        double detB =
            b00 * (b11 * b22 - b12 * b12) -
            b01 * (b01 * b22 - b12 * b02) +
            b02 * (b01 * b12 - b11 * b02);
        double r = Math.Clamp(detB / 2.0, -1.0, 1.0);
        double phi = Math.Acos(r) / 3.0;

        // λ_min = q + 2p·cos(φ + 2π/3).
        return q + 2.0 * p * Math.Cos(phi + 2.0 * Math.PI / 3.0);
    }
}
