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

    /// <summary>정답 능선. 검증 시 검출 결과와 대조한다.</summary>
    public (Vec3 Point, Vec3 Direction) GroundTruth =>
        (new Vec3(0, 0, _options.RidgeDistanceMm), new Vec3(0, 1, 0));

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

        _log.LogInformation(
            "합성 쐐기 생성: 능선 거리 {Dist}mm, 반각 {Half}°, 노이즈 σ {Sigma}mm",
            _options.RidgeDistanceMm, _options.HalfAngleDeg, _options.NoiseSigmaMm);
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

                // 볼록 쐐기가 센서를 향하므로, 두 면의 교점 중 가까운 쪽이 보이는 표면이다.
                var tA = RayPlane(sin, 0, -cos, -cos * z0, rx, ry, rz);
                var tB = RayPlane(sin, 0, cos, cos * z0, rx, ry, rz);

                var t = Nearest(tA, tB);
                if (t is null || t > _options.MaxRangeMm)
                {
                    dist[i] = NslNative.LowAmplitude;
                    ampl[i] = NslNative.LowAmplitude;
                    xs[i] = ys[i] = zs[i] = NslNative.LowAmplitude;
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

internal sealed class SyntheticDeviceOptions
{
    /// <summary>능선까지의 거리(mm). 코봇 작업 스탠드오프를 흉내낸다.</summary>
    public double RidgeDistanceMm { get; set; } = 800;

    /// <summary>각 면이 광축 수직면에서 기울어진 각(도). 45°면 두 면 사잇각이 90°.</summary>
    public double HalfAngleDeg { get; set; } = 45;

    /// <summary>단일 프레임 거리 노이즈 σ(mm). 실측 8~12mm.</summary>
    public double NoiseSigmaMm { get; set; } = 10;

    public double HorizontalFovDeg { get; set; } = 90;

    /// <summary>이보다 먼 표면은 무효 픽셀로 만든다.</summary>
    public double MaxRangeMm { get; set; } = 2000;

    public int NoiseSeed { get; set; } = 4242;
}
