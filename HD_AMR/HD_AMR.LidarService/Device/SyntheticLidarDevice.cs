using HD_AMR.Contracts.Lidar;

namespace HD_AMR.LidarService.Device;

/// <summary>
/// 정답을 아는 쐐기(wedge) 형상을 생성하는 <see cref="ILidarDevice"/> 구현.
///
/// <b>존재 이유.</b> 실측 덤프(scene_04/06)에는 검증 가능한 2면 능선이 들어 있지 않다 —
/// 상자의 한쪽 면만 찍혔고 모서리는 프레임 밖이다. 그래서 검출기가 <i>정말로 옳은 능선을
/// 찾는지</i>를 실측 데이터로는 확인할 수 없다. 여기서는 능선의 위치와 방향을 미리 정해
/// 놓고 그 표면을 렌더링하므로, 검출 결과를 정답과 직접 대조할 수 있다.
///
/// 노이즈는 실측치를 따른다(단일 프레임 σ 8~12mm). 따라서 "이 검출기가 실제 센서 노이즈
/// 수준에서 몇 mm 오차로 능선을 찾는가"를 정량적으로 답할 수 있다.
///
/// ⚠ 합성 데이터는 실물의 대체재가 아니다. 금속 코러게이션의 정반사 포화, 골에서의 신호
///   부족, 다중 반사 같은 실제 실패 모드는 재현하지 않는다. 알고리즘의 <b>기하 계산이
///   맞는지</b>만 검증한다.
/// </summary>
internal sealed class SyntheticLidarDevice : ILidarDevice
{
    private readonly SyntheticDeviceOptions _options;
    private readonly ILogger<SyntheticLidarDevice> _log;
    private readonly Random _rng;

    public SyntheticLidarDevice(SyntheticDeviceOptions options, ILogger<SyntheticLidarDevice> log)
    {
        _options = options;
        _log = log;
        _rng = new Random(options.NoiseSeed);
    }

    public bool IsOpen { get; private set; }

    public LidarDeviceInfo? Info { get; private set; }

    /// <summary>
    /// 정답 능선. 검증 시 검출 결과와 대조한다.
    ///
    /// 쐐기는 두 면이 만나는 모서리가, 비드는 반원 정점이 능선이다. 비드의 정점은 평판보다
    /// 반경만큼 센서 쪽(−Z)에 있다.
    /// </summary>
    public (Vec3 Point, Vec3 Direction) GroundTruth => _options.Shape == SyntheticShape.Bead
        ? (new Vec3(0, 0, _options.RidgeDistanceMm - _options.BeadRadiusMm), new Vec3(0, 1, 0))
        : (new Vec3(0, 0, _options.RidgeDistanceMm), new Vec3(0, 1, 0));

    public void Open()
    {
        IsOpen = true;
        Info = new LidarDeviceInfo
        {
            Model = "synthetic-wedge",
            LidarType = NslNative.LidarTypeOption.TypeA,
            LensType = LidarLensType.StandardField,
            FirmwareRelease = 0,
            ChipId = 0,
            SdkVersion = "synthetic",
            Width = NslNative.TypeAWidth,
            Height = NslNative.TypeAHeight,
        };

        if (_options.Shape == SyntheticShape.Bead)
        {
            _log.LogInformation(
                "합성 비드 생성: 평판 거리 {Dist}mm, 반경 {Radius}mm, 피치 {Pitch}mm, " +
                "정반사 <{Spec}°, 스침 >{Graze}°, 노이즈 σ {Sigma}mm",
                _options.RidgeDistanceMm, _options.BeadRadiusMm, _options.BeadPitchMm,
                _options.SpecularAngleDeg, _options.GrazingAngleDeg, _options.NoiseSigmaMm);
        }
        else
        {
            _log.LogInformation(
                "합성 쐐기 생성: 능선 거리 {Dist}mm, 반각 {Half}°, 노이즈 σ {Sigma}mm",
                _options.RidgeDistanceMm, _options.HalfAngleDeg, _options.NoiseSigmaMm);
        }
    }

    public void StartStreaming() { }
    public void StopStreaming() { }

    public LidarCapture? Capture(int timeoutMs)
    {
        if (!IsOpen) throw new LidarDeviceException("합성 장치가 열려 있지 않다.");

        int w = NslNative.TypeAWidth, h = NslNative.TypeAHeight, n = w * h;

        // 90° 화각(LENS_SF) 가정의 단순 핀홀 모델. 실제 렌즈 왜곡은 재현하지 않는다 —
        // 검증 대상은 검출기의 기하 계산이지 렌즈 모델이 아니다.
        double cx = (w - 1) / 2.0, cy = (h - 1) / 2.0;
        double fx = (w / 2.0) / Math.Tan(_options.HorizontalFovDeg / 2 * Math.PI / 180.0);
        double fy = fx;

        var theta = _options.HalfAngleDeg * Math.PI / 180.0;
        var z0 = _options.RidgeDistanceMm;

        // 능선을 (0,0,z0), 방향 (0,1,0) 으로 두고 두 면을 세운다.
        // nA = (sinθ, 0, -cosθ), dA = -cosθ·z0  /  nB = (sinθ, 0, +cosθ), dB = +cosθ·z0
        double sin = Math.Sin(theta), cos = Math.Cos(theta);

        var dist = new int[n];
        var ampl = new int[n];
        var xs = new double[n];
        var ys = new double[n];
        var zs = new double[n];

        for (int row = 0; row < h; row++)
        {
            for (int col = 0; col < w; col++)
            {
                int i = row * w + col;

                // 축 규약: 열 증가 = +X, 행 증가 = +Y, 광축 전방 = +Z (실측 확인된 규약)
                double rx = (col - cx) / fx;
                double ry = (row - cy) / fy;
                double len = Math.Sqrt(rx * rx + ry * ry + 1);
                rx /= len; ry /= len;
                double rz = 1 / len;

                double? t;
                int invalidCode = NslNative.LowAmplitude;

                if (_options.Shape == SyntheticShape.Bead)
                {
                    t = TraceBead(rx, ry, rz, out invalidCode);
                }
                else
                {
                    // 볼록 쐐기가 센서를 향하므로, 두 면의 교점 중 가까운 쪽이 보이는 표면이다.
                    var tA = RayPlane(sin, 0, -cos, -cos * z0, rx, ry, rz);
                    var tB = RayPlane(sin, 0, cos, cos * z0, rx, ry, rz);
                    t = Nearest(tA, tB);
                }

                if (t is null || t > _options.MaxRangeMm)
                {
                    dist[i] = invalidCode;
                    ampl[i] = invalidCode;
                    xs[i] = ys[i] = zs[i] = invalidCode;
                    continue;
                }

                // 실측 노이즈 수준의 가우시안을 방사 거리에 더한다.
                var r = t.Value + Gaussian() * _options.NoiseSigmaMm;

                dist[i] = (int)Math.Round(r);
                ampl[i] = 700;                       // 실측 평균 진폭
                xs[i] = rx * r;
                ys[i] = ry * r;
                zs[i] = rz * r;
            }
        }

        return new LidarCapture
        {
            Width = w,
            Height = h,
            CapturedAt = DateTimeOffset.UtcNow,
            TemperatureC = 40,
            Distance2D = dist,
            Amplitude = ampl,
            X = xs,
            Y = ys,
            Z = zs,
            IsComplete = true,
        };
    }

    /// <summary>
    /// 평판 + 반원 비드에 광선을 쏜다. 실제 측정 대상의 형상이다 — 평평한 스테인리스 판에
    /// 반경 <see cref="SyntheticDeviceOptions.BeadRadiusMm"/> 반원 리브가 성형되어 있다.
    ///
    /// <b>정반사 포화와 스침각 신호부족을 함께 재현한다.</b> 광택 금속에서 실제로 관측된
    /// 현상이고, 이 검출기의 존재 이유가 "정점이 포화로 사라져도 위치를 낸다"이기 때문에
    /// 그 조건을 재현하지 않으면 검증이 무의미하다.
    /// </summary>
    /// <param name="invalidCode">표면을 맞췄지만 유효하지 않을 때의 무효 코드.</param>
    private double? TraceBead(double rx, double ry, double rz, out int invalidCode)
    {
        invalidCode = NslNative.LowAmplitude;

        var z0 = _options.RidgeDistanceMm;
        var r = _options.BeadRadiusMm;

        double? hit = null;
        double nx = 0, ny = 0, nz = -1;   // 평판 법선(센서 쪽)

        // 반원 비드는 축이 Y 와 나란한 반원기둥이다. 피치가 있으면 X 방향으로 반복된다.
        var repeats = _options.BeadPitchMm > 0 ? 1 : 0;
        for (int k = -repeats; k <= repeats; k++)
        {
            var cx = k * _options.BeadPitchMm;

            // (x-cx)² + (z-z0)² = r², z < z0
            var ox = -cx;
            var a = rx * rx + rz * rz;
            var b = 2 * (rx * ox - rz * z0);
            var c = ox * ox + z0 * z0 - r * r;

            var disc = b * b - 4 * a * c;
            if (disc < 0 || a < 1e-12) continue;

            var t = (-b - Math.Sqrt(disc)) / (2 * a);
            if (t <= 0 || t * rz >= z0) continue;

            if (hit is null || t < hit)
            {
                hit = t;
                var px = t * rx - cx;
                var pz = t * rz - z0;
                var len = Math.Sqrt(px * px + pz * pz);
                nx = px / len; ny = 0; nz = pz / len;
            }
        }

        // 비드를 빗나간 광선은 평판에 닿는다.
        if (hit is null)
        {
            if (rz <= 1e-9) return null;
            hit = z0 / rz;
            nx = 0; ny = 0; nz = -1;
        }

        // 입사각(표면 법선과 시선 사이). 0°면 정면 반사다.
        var cosIncidence = Math.Clamp(-(rx * nx + ry * ny + rz * nz), -1, 1);
        var incidenceDeg = Math.Acos(cosIncidence) * 180.0 / Math.PI;

        if (incidenceDeg < _options.SpecularAngleDeg)
        {
            // 정면에 가까우면 광택면이 되쏘아 ADC 가 넘친다. 실측에서 비드 정점 부근에
            // 나타난 현상이다.
            invalidCode = NslNative.AdcOverflow;
            return null;
        }

        if (incidenceDeg > _options.GrazingAngleDeg)
        {
            // 스치는 각도에서는 되돌아오는 빛이 부족하다. 비드 뿌리 쪽이 여기 해당한다.
            invalidCode = NslNative.LowAmplitude;
            return null;
        }

        return hit;
    }

    private static double? RayPlane(double nx, double ny, double nz, double d,
        double rx, double ry, double rz)
    {
        var denom = nx * rx + ny * ry + nz * rz;
        if (Math.Abs(denom) < 1e-9) return null;
        var t = d / denom;
        return t > 0 ? t : null;
    }

    private static double? Nearest(double? a, double? b) =>
        (a, b) switch
        {
            (null, null) => null,
            (null, _) => b,
            (_, null) => a,
            _ => Math.Min(a.Value, b.Value),
        };

    /// <summary>Box-Muller 표준정규.</summary>
    private double Gaussian()
    {
        var u1 = 1.0 - _rng.NextDouble();
        var u2 = _rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    public LidarConfig ReadConfig() => new();
    public void ApplyConfig(LidarConfigPatch patch) { }
    public void PersistConfig() { }

    public LidarStatus ReadStatus() => new()
    {
        Connected = IsOpen,
        Model = Info?.Model,
        SdkVersion = "synthetic",
        LensType = LidarLensType.StandardField,
        MeasuredFps = 15,
        LastFrameAt = DateTimeOffset.UtcNow,
    };

    public void Close() => IsOpen = false;
    public void Dispose() => Close();
}

/// <summary>합성할 형상.</summary>
internal enum SyntheticShape
{
    /// <summary>볼록 쐐기. 두 평면 교선 검출기 검증용.</summary>
    Wedge = 0,

    /// <summary>평판 + 반원 비드. 실제 측정 대상의 형상이다.</summary>
    Bead = 1,
}

internal sealed class SyntheticDeviceOptions
{
    /// <summary>생성할 형상.</summary>
    public SyntheticShape Shape { get; set; } = SyntheticShape.Bead;

    /// <summary>
    /// 기준면까지의 거리(mm). 쐐기면 능선까지, 비드면 <b>평판까지</b>의 거리다
    /// (비드 정점은 여기서 반경만큼 더 가깝다).
    /// </summary>
    public double RidgeDistanceMm { get; set; } = 800;

    /// <summary>비드 반경(mm). 실물 35mm(폭 70mm, 돌출 35mm 의 반원).</summary>
    public double BeadRadiusMm { get; set; } = 35;

    /// <summary>
    /// 비드 간격(mm). 0 이면 비드 하나만, 양수면 좌우로 하나씩 더 놓는다.
    /// 여러 비드 중 하나만 골라내는 동작을 검증하기 위한 것이다. 실물 피치는 370mm.
    /// </summary>
    public double BeadPitchMm { get; set; } = 0;

    /// <summary>
    /// 이 각도보다 정면에 가까우면 정반사로 포화시킨다(도).
    ///
    /// 광택 금속 비드의 정점 부근에서 실제로 관측된 현상이라 재현한다. 이 값이 0 이면
    /// 정점에 데이터가 남아, 검출기가 <b>정점 없이도 동작하는지</b>를 검증할 수 없다.
    /// </summary>
    public double SpecularAngleDeg { get; set; } = 8;

    /// <summary>이 각도보다 스치면 신호 부족으로 무효 처리한다(도). 비드 뿌리 쪽이 해당한다.</summary>
    public double GrazingAngleDeg { get; set; } = 75;

    /// <summary>각 면이 광축 수직면에서 기울어진 각(도). 45°면 두 면 사잇각이 90°. 쐐기 전용.</summary>
    public double HalfAngleDeg { get; set; } = 45;

    /// <summary>단일 프레임 거리 노이즈 σ(mm). 실측 8~12mm.</summary>
    public double NoiseSigmaMm { get; set; } = 10;

    public double HorizontalFovDeg { get; set; } = 90;

    /// <summary>이보다 먼 표면은 무효 픽셀로 만든다.</summary>
    public double MaxRangeMm { get; set; } = 2000;

    public int NoiseSeed { get; set; } = 4242;
}
