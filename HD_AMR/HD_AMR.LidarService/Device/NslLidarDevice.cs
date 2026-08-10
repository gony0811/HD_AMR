using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HD_AMR.Contracts.Lidar;

namespace HD_AMR.LidarService.Device;

/// <summary>
/// <see cref="ILidarDevice"/> 의 실제 구현. nanolib 을 P/Invoke 로 호출한다.
/// 젯슨(linux-arm64)에서만 동작하며, Windows 개발 시에는 덤프 재생 구현으로 대체한다.
///
/// ⚠ 이 클래스의 모든 메서드는 단일 스레드에서만 호출해야 한다. <see cref="ILidarDevice"/>
///   주석 참고.
/// </summary>
internal sealed class NslLidarDevice : ILidarDevice
{
    private readonly NslDeviceOptions _options;
    private readonly ILogger<NslLidarDevice> _log;

    /// <summary>
    /// <c>NslPCD</c> 용 언매니지드 버퍼. 약 15MB 라서 한 번만 할당하고 계속 재사용한다.
    /// 구조체로 마샬링하면 호출마다 15MB 를 복사하게 되므로 절대 그렇게 하지 않는다.
    /// </summary>
    private IntPtr _pcd = IntPtr.Zero;

    private int _handle = -1;
    private bool _streaming;
    private NslNative.NslConfig _config;

    /// <summary>프레임 헤더가 보고한 실제 동작 모드. RGB 모드 폴백 감지에 쓴다.</summary>
    private NslNative.OperationMode _effectiveMode;

    /// <summary>
    /// <c>waitTimeMs</c> 하한. 실측상 지정 시간이 0.1ms 오차로 정확히 지켜지는데,
    /// 프레임 주기가 64ms(15fps)라 이보다 짧으면 데이터가 오기 전에 타임아웃된다.
    /// </summary>
    private const int MinWaitMs = 100;

    /// <summary>
    /// 설정 변경 후 버릴 프레임 수. 실측상 integrationTime/minAmplitude 는 3프레임,
    /// ROI 는 1프레임 지연 후 반영된다. 여유를 둬 5프레임을 버린다.
    /// </summary>
    private const int SettleFrames = 5;

    public NslLidarDevice(NslDeviceOptions options, ILogger<NslLidarDevice> log)
    {
        _options = options;
        _log = log;
    }

    public bool IsOpen => _handle >= 0;

    public LidarDeviceInfo? Info { get; private set; }

    public void Open()
    {
        if (IsOpen) return;

        _pcd = Marshal.AllocHGlobal(NslPcdLayout.SizeBytes);
        unsafe { new Span<byte>((void*)_pcd, NslPcdLayout.SizeBytes).Clear(); }

        _config = default;

        // lidarAngle 과 lensType 은 nsl_open 의 '입력' 파라미터다(헤더 주석 기준).
        // 특히 lensType 은 장비에서 읽어오는 값이 아니라 우리가 알려주는 값이므로,
        // 실물과 다르면 distance3D 재투영이 통째로 틀어진다. 설정에서 명시적으로 받는다.
        _config.LensType = _options.LensType;

        // ⚠ 항상 0. nanolib 이 distance3D 에 회전을 미리 걸면, HD_AMR 의 hand-eye
        //   변환과 이중 적용되어 조용히 틀어진다. 좌표 변환은 전적으로 HD_AMR 담당.
        _config.LidarAngle = 0;

        var debug = _options.EnableSdkDebug ? NslNative.FunctionOption.On : NslNative.FunctionOption.Off;

        _log.LogInformation("nsl_open 시도: target={Target}, lens={Lens}", _options.Target, _options.LensType);

        var handle = NslNative.nsl_open(_options.Target, ref _config, debug);
        if (handle < 0)
        {
            Cleanup();
            var code = Enum.IsDefined(typeof(NslNative.NslError), handle)
                ? (NslNative.NslError)handle
                : (NslNative.NslError?)null;

            // -4 는 같은 프로세스에서 이미 연 상태를 뜻한다(장치 수준 배타 잠금이 아니다).
            // 여기까지 왔다는 건 우리 상태 추적이 어긋났다는 뜻이라 원인을 구분해 알린다.
            if (code == NslNative.NslError.IpDuplicated)
                throw new LidarDeviceException(
                    "이미 열려 있는 핸들이 있다. 이전 핸들이 정리되지 않았다.", code);

            throw new LidarDeviceException(
                $"센서를 열지 못했다. target={_options.Target}. " +
                "LAN 케이블/링크 상태와 IP 를 확인할 것. 반환값만으로는 '케이블 단선'과 " +
                "'잘못된 IP'를 구분할 수 없다(둘 다 -2). USB 와 LAN 을 동시에 연결해도 실패한다.",
                code);
        }

        _handle = handle;
        _effectiveMode = _options.Mode;

        // 열린 직후 장비가 채워준 값을 다시 읽어 실제 상태를 확보한다.
        var err = NslNative.nsl_getCurrentConfig(_handle, ref _config);
        if (err != NslNative.NslError.Success)
            _log.LogWarning("nsl_getCurrentConfig 실패: {Error}. nsl_open 이 채운 값으로 진행한다.", err);

        var version = NslNative.nsl_getSdkVersion(_handle);
        var (w, h) = _config.LidarType == NslNative.LidarTypeOption.TypeB
            ? (NslNative.TypeBWidth, NslNative.TypeBHeight)
            : (NslNative.TypeAWidth, NslNative.TypeAHeight);

        Info = new LidarDeviceInfo
        {
            Model = _options.Model,
            LidarType = _config.LidarType,
            LensType = ToContract(_config.LensType),
            FirmwareRelease = _config.FirmwareRelease,
            ChipId = _config.ChipId,
            SdkVersion = $"{version >> 16}.{version & 0xFFFF}",
            Width = w,
            Height = h,
        };

        _log.LogInformation(
            "센서 연결됨: handle={Handle}, type={Type}, lens={Lens}, fw={Fw}, chip={Chip}, sdk={Sdk}",
            _handle, Info.LidarType, Info.LensType, Info.FirmwareRelease, Info.ChipId, Info.SdkVersion);

        // lidarAngle 이 0 인지 되읽어 검증한다. 이 값은 open 시점의 write 파라미터라
        // 실수로 다른 값이 들어가면 distance3D 에 X축 중심 -angle 회전이 걸리고,
        // HD_AMR 의 hand-eye 변환과 이중 적용되어 조용히 틀어진다.
        if (NslNative.nsl_getLidarAngle(_handle, out var angle) == NslNative.NslError.Success && angle != 0)
        {
            throw new LidarDeviceException(
                $"lidarAngle 이 0 이 아니다({angle}). distance3D 에 회전이 걸려 " +
                "hand-eye 캘리브레이션과 이중 적용된다.");
        }

        // 설정으로 받은 초기 파라미터를 적용한다.
        if (_options.InitialConfig is { } initial)
            ApplyConfig(initial);
    }

    public void StartStreaming()
    {
        EnsureOpen();
        if (_streaming) return;

        var err = NslNative.nsl_streamingOn(_handle, _options.Mode);
        if (err != NslNative.NslError.Success)
            throw new LidarDeviceException($"스트리밍을 켜지 못했다. mode={_options.Mode}", err);

        _streaming = true;
        _log.LogInformation("스트리밍 시작: mode={Mode}", _options.Mode);
    }

    public void StopStreaming()
    {
        if (!IsOpen || !_streaming) return;

        var err = NslNative.nsl_streamingOff(_handle);
        if (err != NslNative.NslError.Success)
            _log.LogWarning("nsl_streamingOff 실패: {Error}", err);

        _streaming = false;
    }

    /// <summary>
    /// 한 프레임 수신. 타임아웃/일시적 손실은 null 로 표현하고 예외를 던지지 않는다 —
    /// 정지 측정에서 몇 프레임 놓치는 것은 정상 범위이고, 호출 측이 재시도로 흡수한다.
    /// </summary>
    public LidarCapture? Capture(int timeoutMs)
    {
        EnsureOpen();
        if (!_streaming) StartStreaming();

        // waitTimeMs 는 정확히 지켜지지만, 15fps(프레임 주기 64ms)에서 100ms 미만을 주면
        // 데이터가 준비되기 전에 타임아웃되어 항상 실패한다(10ms 는 100% 실패 확인).
        var wait = Math.Max(timeoutMs, MinWaitMs);

        var err = NslNative.nsl_getPointCloudData(_handle, _pcd, wait);
        if (err != NslNative.NslError.Success)
        {
            _log.LogDebug("프레임 수신 실패: {Error}", err);
            return null;
        }

        // RGB 모드(5~8)는 이 장비에 RGB 센서가 없어 반환값 0(성공)을 주면서도 조용히
        // 비RGB 등가 모드로 폴백한다. 요청 모드를 그대로 믿으면 없는 데이터를 기다리게 되므로
        // 프레임 헤더가 보고하는 실제 모드를 대조한다.
        var actual = ReadFrameMode(_pcd);
        if (actual != _effectiveMode)
        {
            _log.LogWarning(
                "요청 모드 {Requested} 가 실제 {Actual} 로 동작 중이다. 이 장비가 지원하지 않는 모드일 수 있다.",
                _options.Mode, actual);
            _effectiveMode = actual;
        }

        return Extract(_pcd, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// 언매니지드 <c>NslPCD</c> 버퍼에서 유효 영역만 매니지드 배열로 뽑아낸다.
    ///
    /// 원본 배열은 장비 종류와 무관하게 항상 TYPE_B(800x600)로 정적 할당되어 있으므로,
    /// 행 stride 는 언제나 800 이다. 실제 데이터는 (roiXMin, roiYMin) 에서 시작하는
    /// width x height 영역에만 들어 있다. 이 stride 를 무시하고 연속 복사하면 엉뚱한
    /// 픽셀을 읽는다 — 공식 샘플이 320 고정으로 순회하는 것도 같은 이유다.
    /// </summary>
    private static unsafe LidarCapture Extract(IntPtr pcd, DateTimeOffset capturedAt)
    {
        var basePtr = (byte*)pcd;

        int width = Unsafe.Read<int>(basePtr + NslPcdLayout.Width);
        int height = Unsafe.Read<int>(basePtr + NslPcdLayout.Height);
        int xMin = Unsafe.Read<int>(basePtr + NslPcdLayout.RoiXMin);
        int yMin = Unsafe.Read<int>(basePtr + NslPcdLayout.RoiYMin);
        double temperature = Unsafe.Read<double>(basePtr + NslPcdLayout.Temperature);

        if (width <= 0 || height <= 0 ||
            xMin < 0 || yMin < 0 ||
            xMin + width > NslPcdLayout.MaxWidth ||
            yMin + height > NslPcdLayout.MaxHeight)
        {
            throw new LidarDeviceException(
                $"프레임 기하 정보가 비정상이다: {width}x{height} @ ({xMin},{yMin}). " +
                "NslPCD 레이아웃 상수가 실제와 어긋났을 수 있다(NslPcdLayout.Verify 참고).");
        }

        int n = width * height;
        var distance = new int[n];
        var amplitude = new int[n];
        var x = new double[n];
        var y = new double[n];
        var z = new double[n];

        var srcAmp = (int*)(basePtr + NslPcdLayout.Amplitude);
        var srcDist = (int*)(basePtr + NslPcdLayout.Distance2D);
        var srcXyz = (double*)(basePtr + NslPcdLayout.Distance3D);

        const int planeStride = NslPcdLayout.PixelCount;
        int unfilled = 0;

        for (int row = 0; row < height; row++)
        {
            int srcRow = (row + yMin) * NslPcdLayout.MaxWidth + xMin;
            int dstRow = row * width;

            for (int col = 0; col < width; col++)
            {
                int s = srcRow + col;
                int d = dstRow + col;

                int dist = srcDist[s];
                if (dist == 0) unfilled++;

                distance[d] = dist;
                amplitude[d] = srcAmp[s];
                x[d] = srcXyz[NslNative.OutX * planeStride + s];
                y[d] = srcXyz[NslNative.OutY * planeStride + s];
                z[d] = srcXyz[NslNative.OutZ * planeStride + s];
            }
        }

        return new LidarCapture
        {
            Width = width,
            Height = height,
            CapturedAt = capturedAt,
            TemperatureC = temperature,
            Distance2D = distance,
            Amplitude = amplitude,
            X = x,
            Y = y,
            Z = z,
            IsComplete = unfilled == 0,
        };
    }

    /// <summary>프레임 헤더가 보고하는 실제 동작 모드.</summary>
    private static unsafe NslNative.OperationMode ReadFrameMode(IntPtr pcd) =>
        Unsafe.Read<NslNative.OperationMode>((byte*)pcd + NslPcdLayout.OperationMode);

    public LidarConfig ReadConfig()
    {
        EnsureOpen();

        var err = NslNative.nsl_getCurrentConfig(_handle, ref _config);
        if (err != NslNative.NslError.Success)
            throw new LidarDeviceException("설정을 읽지 못했다.", err);

        return new LidarConfig
        {
            IntegrationTime3D = _config.IntegrationTime3D,
            IntegrationTimeGrayscale = _config.IntegrationTimeGrayScale,
            MinAmplitude = _config.MinAmplitude,
            Hdr = (LidarHdrMode)_config.HdrOpt,
            Modulation = (LidarModulation)_config.ModFrequencyOpt,
            Roi = new RoiRect
            {
                XMin = _config.RoiXMin,
                YMin = _config.RoiYMin,
                XMax = _config.RoiXMax,
                YMax = _config.RoiYMax,
            },
            MedianFilter = _config.MedianOpt == NslNative.FunctionOption.On,
            AverageFilter = _config.GaussOpt == NslNative.FunctionOption.On,
            TemporalFactor = _config.TemporalFactorValue,
            TemporalThreshold = _config.TemporalThresholdValue,
            EdgeThreshold = _config.EdgeThresholdValue,
            InterferenceLimit = _config.InterferenceDetectionLimitValue,
            InterferenceUseLastValue = _config.InterferenceDetectionLastValueOpt == NslNative.FunctionOption.On,
            GrayscaleIllumination = _config.GrayscaleIlluminationOpt == NslNative.FunctionOption.On,
        };
    }

    public void ApplyConfig(LidarConfigPatch patch)
    {
        EnsureOpen();

        var current = ReadConfig();

        if (patch.IntegrationTime3D is not null || patch.IntegrationTimeGrayscale is not null)
        {
            Check(NslNative.nsl_setIntegrationTime(
                _handle,
                patch.IntegrationTime3D ?? current.IntegrationTime3D,
                _config.IntegrationTime3DHdr1,
                _config.IntegrationTime3DHdr2,
                patch.IntegrationTimeGrayscale ?? current.IntegrationTimeGrayscale), nameof(patch.IntegrationTime3D));
        }

        if (patch.MinAmplitude is { } minAmp)
            Check(NslNative.nsl_setMinAmplitude(_handle, minAmp), nameof(patch.MinAmplitude));

        if (patch.Hdr is { } hdr)
            Check(NslNative.nsl_setHdrMode(_handle, (NslNative.HdrOption)hdr), nameof(patch.Hdr));

        if (patch.Modulation is { } mod)
        {
            Check(NslNative.nsl_setModulation(
                _handle, (NslNative.ModulationOption)mod, _config.ModChannelOpt,
                _config.ModEnabledAutoChannelOpt), nameof(patch.Modulation));
        }

        if (patch.Roi is { } roi)
            Check(NslNative.nsl_setRoi(_handle, roi.XMin, roi.YMin, roi.XMax, roi.YMax), nameof(patch.Roi));

        // 필터 계열은 nanolib 이 개별 setter 를 주지 않고 한 번에 받는다.
        if (patch.MedianFilter is not null || patch.AverageFilter is not null ||
            patch.TemporalFactor is not null || patch.TemporalThreshold is not null ||
            patch.EdgeThreshold is not null || patch.InterferenceLimit is not null ||
            patch.InterferenceUseLastValue is not null)
        {
            Check(NslNative.nsl_setFilter(
                _handle,
                ToOption(patch.MedianFilter ?? current.MedianFilter),
                ToOption(patch.AverageFilter ?? current.AverageFilter),
                patch.TemporalFactor ?? current.TemporalFactor,
                patch.TemporalThreshold ?? current.TemporalThreshold,
                patch.EdgeThreshold ?? current.EdgeThreshold,
                patch.InterferenceLimit ?? current.InterferenceLimit,
                ToOption(patch.InterferenceUseLastValue ?? current.InterferenceUseLastValue)), "Filter");
        }

        if (patch.GrayscaleIllumination is { } illum)
            Check(NslNative.nsl_setGrayscaleillumination(_handle, ToOption(illum)), nameof(patch.GrayscaleIllumination));

        // 설정은 즉시 반영되지 않는다 — 실측상 integrationTime/minAmplitude 는 3프레임,
        // ROI 는 1프레임 뒤부터 적용된다. 버리지 않으면 옛 설정으로 찍힌 프레임이 평균에
        // 섞여 측정이 조용히 틀어진다. 스트리밍을 끌 필요는 없다(끄는 게 더 빠르지도 않다).
        if (_streaming) DiscardFrames(SettleFrames);

        void Check(NslNative.NslError err, string what)
        {
            if (err != NslNative.NslError.Success)
                throw new LidarDeviceException($"설정 변경 실패: {what}", err);
        }
    }

    /// <summary>설정 반영 지연을 흡수하기 위해 프레임을 읽고 버린다.</summary>
    private void DiscardFrames(int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (NslNative.nsl_getPointCloudData(_handle, _pcd, MinWaitMs) != NslNative.NslError.Success)
                break;
        }
    }

    public void PersistConfig()
    {
        EnsureOpen();

        _log.LogWarning("장비 EEPROM 에 설정을 영구 기록한다 (nsl_saveConfiguration).");

        var err = NslNative.nsl_saveConfiguration(_handle);
        if (err != NslNative.NslError.Success)
            throw new LidarDeviceException("설정 영구 저장 실패.", err);
    }

    public LidarStatus ReadStatus()
    {
        var linkUp = NetworkLink.IsUp(_options.InterfaceName);

        if (!IsOpen)
        {
            return new LidarStatus
            {
                Connected = false,
                LinkUp = linkUp,
                Model = _options.Model,
                LastError = linkUp == false
                    ? "이더넷 링크가 내려가 있다. 케이블을 확인할 것."
                    : "센서가 열려 있지 않다.",
            };
        }

        var err = NslNative.nsl_getBitInfo(_handle, out var bit);
        if (err != NslNative.NslError.Success)
        {
            return new LidarStatus
            {
                Connected = false,
                LinkUp = linkUp,
                Model = Info?.Model,
                LastError = $"nsl_getBitInfo 실패: {err}",
            };
        }

        return new LidarStatus
        {
            Connected = true,
            LinkUp = linkUp,
            Model = Info?.Model,
            FirmwareRelease = Info?.FirmwareRelease ?? 0,
            SdkVersion = Info?.SdkVersion,
            ChipId = Info?.ChipId ?? 0,
            LensType = Info?.LensType ?? LidarLensType.StandardField,
            SensorTemperatureC = bit.Temperature,
            VoltageV = bit.Voltage,
            CurrentMa = bit.Current,
        };
    }

    public void Close()
    {
        if (!IsOpen) return;

        StopStreaming();

        var err = NslNative.nsl_closeHandle(_handle);
        if (err != NslNative.NslError.Success)
            _log.LogWarning("nsl_closeHandle 실패: {Error}", err);

        _handle = -1;
        Info = null;
        _log.LogInformation("센서 연결 해제됨.");
    }

    public void Dispose()
    {
        try { Close(); }
        catch (Exception ex) { _log.LogWarning(ex, "Close 중 오류. 정리는 계속한다."); }
        Cleanup();
    }

    private void Cleanup()
    {
        if (_pcd != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_pcd);
            _pcd = IntPtr.Zero;
        }
    }

    private void EnsureOpen()
    {
        if (!IsOpen) throw new LidarDeviceException("센서가 열려 있지 않다. Open() 을 먼저 호출할 것.");
    }

    private static NslNative.FunctionOption ToOption(bool on) =>
        on ? NslNative.FunctionOption.On : NslNative.FunctionOption.Off;

    private static LidarLensType ToContract(NslNative.LensType lens) => lens switch
    {
        NslNative.LensType.NarrowField => LidarLensType.NarrowField,
        NslNative.LensType.WideField => LidarLensType.WideField,
        _ => LidarLensType.StandardField,
    };
}

/// <summary>센서 연결 설정. appsettings.json 의 <c>Lidar:Device</c> 에서 바인딩한다.</summary>
internal sealed class NslDeviceOptions
{
    /// <summary>
    /// 연결 대상. LAN 구성이면 IP 문자열(예: "192.168.0.220"), USB 구성이면 무시된다.
    /// nanolib 이 network → CDC USB → vendor USB 순으로 자동 폴백한다.
    /// </summary>
    public string Target { get; set; } = "192.168.0.220";

    public string Model { get; set; } = "NSL-1110AV";

    /// <summary>
    /// 센서가 붙어 있는 이더넷 인터페이스명(예: "enP8p1s0"). 링크 상태를 직접 확인해
    /// 단선 상태에서 3초씩 블로킹되는 헛된 재연결 시도를 피하는 데 쓴다.
    /// 비워두면 링크 확인 없이 항상 연결을 시도한다.
    /// </summary>
    public string? InterfaceName { get; set; }

    /// <summary>
    /// ⚠ 장착된 실제 렌즈. nsl_open 의 <b>입력</b> 파라미터이므로 장비에서 읽어오는 값이
    /// 아니다. 실물과 다르면 distance3D 재투영이 통째로 틀어지는데 겉으로는 정상처럼
    /// 보인다. 진단 보고서 3-2 로 확인한 값을 반드시 넣을 것.
    /// </summary>
    public NslNative.LensType LensType { get; set; } = NslNative.LensType.StandardField;

    /// <summary>
    /// 스트리밍 모드. 거리 + 진폭(적외선)을 함께 받는 DistanceAmplitude 가 기본이다 —
    /// 능선 검출에 거리가, 신뢰도 판정과 모니터링에 진폭이 필요하다.
    /// </summary>
    public NslNative.OperationMode Mode { get; set; } = NslNative.OperationMode.DistanceAmplitude;

    /// <summary>nanolib 자체 디버그 로그 출력 여부. 브링업 중에는 켜두면 유용하다.</summary>
    public bool EnableSdkDebug { get; set; } = true;

    /// <summary>연결 직후 적용할 초기 파라미터. null 이면 장비의 현재 설정을 그대로 쓴다.</summary>
    public LidarConfigPatch? InitialConfig { get; set; }
}
