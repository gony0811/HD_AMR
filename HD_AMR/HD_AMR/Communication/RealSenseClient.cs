using System.Runtime.InteropServices;
using HD_AMR.Models;
using Intel.RealSense;
using Microsoft.Extensions.Logging;
using RsStream = Intel.RealSense.Stream;

namespace HD_AMR.Communication;

/// <summary>
/// Intel RealSense D435/D435i 카메라(USB 3.0 RGB-D)의 얇은 래퍼. 공식 librealsense C# 래퍼
/// (<c>Intel.RealSense</c>, 네이티브: <c>realsense2.dll</c>)를 사용한다. 컬러+깊이+IR 프레임을 받아
/// 불변 <see cref="CameraFrame"/> 으로 스냅샷한 뒤 <c>Interlocked.Exchange</c> 로 교체 → 소비자
/// 측에서 락 없이 안전하게 읽을 수 있다. 연결/스트림 시작/정지는 <see cref="SemaphoreSlim"/> 로
/// 직렬화(<see cref="CobotModbusTcpClient"/> 와 동일 패턴).
///
/// 네이티브 라이브러리가 없는 환경에서는 <see cref="ConnectAsync"/> 가
/// <see cref="DllNotFoundException"/> 으로 실패하지만 상위 5초 재연결 루프가 흡수하므로 앱은 죽지
/// 않는다.
/// </summary>
public class RealSenseClient : IDisposable
{
    private readonly RealSenseSettings _settings;
    private readonly ILogger<RealSenseClient> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Context? _context;
    private Pipeline? _pipeline;
    private Config? _config;
    // 스트림 시작 후에만 유효. 내부 파라미터(intrinsics/extrinsics)의 출처.
    private PipelineProfile? _profile;

    private CameraFrame? _latestColor;
    private CameraFrame? _latestDepth;
    private CameraFrame? _latestIr;
    private DateTime _lastFrameAt;

    // Z16 원시값 → mm 변환 계수. StartStream 시 깊이 센서의 DepthUnits(기본 0.001m = 1mm/unit)를
    // 읽어 갱신. 1.0 이면 무변환 fast path.
    private float _depthMmPerUnit = 1.0f;

    // IR 스트림이 실제로 활성화됐는지. IR 은 best-effort 라 실패 시 false 로 두고 컬러/깊이만 운용.
    private volatile bool _irActive;

    // USB 연결 협상 속도("USB3.2"/"USB2.1"/…). D435 는 USB 3.0 필요 — USB2.x 면 고해상도 모드가
    // 아예 없어 프레임이 전혀 안 올 수 있어 진단용으로 노출.
    private string? _connectionType;

    private volatile bool _connected;
    private volatile bool _streaming;

    // Depth↔Color 정합 파라미터(공장 캘리브레이션). 한 번 성공하면 캐시. 재연결 시 초기화.
    private readonly object _camParamLock = new();
    private CameraD2CParams? _camParam;

    // 진단용: 첫 color/depth/ir 프레임 수신 시 1회만 로그. 스트림 재시작마다 초기화.
    private bool _loggedFirstColor;
    private bool _loggedFirstDepth;
    private bool _loggedFirstIr;

    public RealSenseClient(RealSenseSettings settings, ILogger<RealSenseClient> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public bool IsConnected => _connected;
    public bool IsStreaming => _streaming;
    public CameraFrame? LatestColor => _latestColor;
    public CameraFrame? LatestDepth => _latestDepth;
    public CameraFrame? LatestIr => _latestIr;
    /// <summary>IR 스트림이 실제로 활성화되어 프레임을 받는 중인지.</summary>
    public bool IsIrActive => _irActive;
    public DateTime LastFrameAt => _lastFrameAt;
    public string? ConnectionType => _connectionType;
    public RealSenseSettings Settings => _settings;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_connected) return;

            _context = new Context();
            using (var devices = _context.QueryDevices(include_platform_camera: false))
            {
                if (devices.Count == 0)
                    throw new InvalidOperationException("연결된 RealSense 카메라를 찾을 수 없습니다.");

                // 시리얼 매칭 검증 + USB 협상 속도/장치 정보 조기 진단. Config.EnableDevice 는
                // resolve 시점에야 실패하므로 여기서 미리 확인해 명확한 예외를 던진다.
                InspectDevices(devices);
            }

            _pipeline = new Pipeline(_context);

            // 스트림 구성 3단계 fallback:
            // 1) 설정된 정확한 모드 + IR → 2) 정확한 모드, IR 제외 → 3) 포맷만 고정하고 해상도/FPS
            // 는 SDK 기본(USB2 등으로 요청 모드가 없는 경우의 마지막 수단).
            _config = BuildConfig(includeIr: _settings.EnableIr);
            if (_settings.EnableIr && _config.CanResolve(_pipeline))
            {
                _irActive = true;
            }
            else if (!_settings.EnableIr && _config.CanResolve(_pipeline))
            {
                _irActive = false;
            }
            else
            {
                _config.Dispose();
                _config = BuildConfig(includeIr: false);
                _irActive = false;
                if (_settings.EnableIr && _config.CanResolve(_pipeline))
                {
                    _logger.LogWarning(
                        "{Name} IR 스트림 활성화 실패(모드 조합 미지원) — IR 없이 컬러/깊이만 진행합니다.",
                        _settings.Name);
                }
                else if (!_config.CanResolve(_pipeline))
                {
                    _config.Dispose();
                    _config = BuildFallbackConfig();
                    _logger.LogWarning(
                        "{Name} 요청 모드(color {CW}x{CH}@{CF}, depth {DW}x{DH}@{DF})를 카메라가 지원하지 " +
                        "않아 SDK 기본 모드로 대체합니다(USB2 연결 추정).",
                        _settings.Name,
                        _settings.ColorWidth, _settings.ColorHeight, _settings.ColorFps,
                        _settings.DepthWidth, _settings.DepthHeight, _settings.DepthFps);
                }
            }

            _connected = true;
            _logger.LogInformation("{Name} 연결 완료 (color={Color}, depth={Depth}, ir={Ir})",
                _settings.Name, _settings.EnableColor, _settings.EnableDepth, _irActive);
        }
        catch
        {
            DisposeNativeUnlocked();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 감지된 디바이스를 로그로 남기고, DeviceSerial 지정 시 존재 여부를 검증하며, USB 협상 속도를
    /// <see cref="_connectionType"/> 에 저장한다. D435(USB 3.0 카메라)가 USB 2.x 로 잡히면 848x480
    /// 등 고해상도 모드가 노출되지 않아 요청 모드 resolve 가 실패한다 — 코드가 아닌 케이블/포트 문제.
    /// </summary>
    private void InspectDevices(DeviceList devices)
    {
        bool serialFound = string.IsNullOrWhiteSpace(_settings.DeviceSerial);
        foreach (var dev in devices)
        {
            using (dev)
            {
                var info = dev.Info;
                string? name = info.Supports(CameraInfo.Name) ? info[CameraInfo.Name] : null;
                string? serial = info.Supports(CameraInfo.SerialNumber) ? info[CameraInfo.SerialNumber] : null;
                _logger.LogInformation("{Name} 감지된 디바이스: {Dev} (S/N {Serial})",
                    _settings.Name, name ?? "(unknown)", serial ?? "(unknown)");

                bool isTarget = string.IsNullOrWhiteSpace(_settings.DeviceSerial) || serial == _settings.DeviceSerial;
                if (!isTarget) continue;
                serialFound = true;

                if (info.Supports(CameraInfo.UsbTypeDescriptor))
                {
                    var usb = info[CameraInfo.UsbTypeDescriptor];   // "3.2" / "2.1" 등
                    _connectionType = string.IsNullOrEmpty(usb) ? null : "USB" + usb;
                }
                if (_connectionType is not null &&
                    _connectionType.StartsWith("USB2", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "{Name} USB 2.x 연결 감지({Conn}) — D435 는 USB 3.0 필요. 요청 해상도 모드가 " +
                        "노출되지 않을 수 있으니 USB 3.0 포트 + SuperSpeed 케이블 직결(허브 제거)을 권장합니다.",
                        _settings.Name, _connectionType);
                }
            }
        }
        if (!serialFound)
            throw new InvalidOperationException($"시리얼 {_settings.DeviceSerial} 인 카메라를 찾지 못했습니다.");
    }

    private Config BuildConfig(bool includeIr)
    {
        var cfg = new Config();
        if (!string.IsNullOrWhiteSpace(_settings.DeviceSerial))
            cfg.EnableDevice(_settings.DeviceSerial);
        if (_settings.EnableColor)
            cfg.EnableStream(RsStream.Color, _settings.ColorWidth, _settings.ColorHeight,
                Format.Rgb8, _settings.ColorFps);
        if (_settings.EnableDepth)
            cfg.EnableStream(RsStream.Depth, _settings.DepthWidth, _settings.DepthHeight,
                Format.Z16, _settings.DepthFps);
        if (includeIr)
            // index 1 = 좌측 이미저(깊이 좌표계와 동일 시점). D435 IR 은 Y8 그레이스케일.
            cfg.EnableStream(RsStream.Infrared, 1, _settings.IrWidth, _settings.IrHeight,
                Format.Y8, _settings.IrFps);
        return cfg;
    }

    /// <summary>해상도/FPS 는 SDK 기본에 맡기고 포맷만 고정한다(다운스트림 디코더 호환 유지).</summary>
    private Config BuildFallbackConfig()
    {
        var cfg = new Config();
        if (!string.IsNullOrWhiteSpace(_settings.DeviceSerial))
            cfg.EnableDevice(_settings.DeviceSerial);
        if (_settings.EnableColor)
            cfg.EnableStream(RsStream.Color, Format.Rgb8, 0);
        if (_settings.EnableDepth)
            cfg.EnableStream(RsStream.Depth, Format.Z16, 0);
        return cfg;
    }

    /// <summary>
    /// Depth↔Color 정합용 공장 캘리브레이션 파라미터를 반환한다(스트리밍 중에만 유효). 한 번
    /// 성공하면 캐시한다. 네이티브 부재/미스트리밍/유효하지 않으면 null(다음에 재시도).
    /// </summary>
    public CameraD2CParams? TryGetCameraParam()
    {
        if (_camParam is not null) return _camParam;
        lock (_camParamLock)
        {
            if (_camParam is not null) return _camParam;
            if (_profile is null || !_streaming) return null;
            try
            {
                using var dp = _profile.GetStream<VideoStreamProfile>(RsStream.Depth, -1);
                using var cp = _profile.GetStream<VideoStreamProfile>(RsStream.Color, -1);
                if (dp is null || cp is null) return null;

                var di = dp.GetIntrinsics();
                var ci = cp.GetIntrinsics();
                var ex = dp.GetExtrinsicsTo(cp);   // Depth → Color

                // rs2_extrinsics 의 rotation 은 column-major — CameraD2CParams.Rot 는 row-major 계약
                // (DepthColorMapper 가 r[0..2] 를 첫 행으로 사용)이므로 전치한다.
                var rot = new double[9];
                for (int r = 0; r < 3; r++)
                    for (int c = 0; c < 3; c++)
                        rot[r * 3 + c] = ex.rotation[c * 3 + r];

                // translation 은 meter — 계약은 mm.
                var trans = new double[]
                {
                    ex.translation[0] * 1000.0,
                    ex.translation[1] * 1000.0,
                    ex.translation[2] * 1000.0,
                };

                var p = new CameraD2CParams(
                    di.fx, di.fy, di.ppx, di.ppy, di.width, di.height,
                    ci.fx, ci.fy, ci.ppx, ci.ppy, ci.width, ci.height,
                    rot, trans);
                if (!p.IsValid) return null;   // 첫 프레임 전 등 0 값이면 캐시하지 않고 재시도
                _camParam = p;
                // sanity 기대값(D435): depth fx≈420~430@848x480, color fx≈910~920@1280x720,
                // baseline |t0|≈15mm. 크게 어긋나면 단위/전치 회귀 의심.
                _logger.LogInformation(
                    "{Name} Depth↔Color 캘리브레이션 획득: depth fx={Dfx:0.0} color fx={Cfx:0.0} baseline≈{B:0.0}mm",
                    _settings.Name, p.DepthFx, p.ColorFx, Math.Abs(trans[0]));
                return _camParam;
            }
            catch (DllNotFoundException) { return null; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{Name} 카메라 파라미터 조회 실패", _settings.Name);
                return null;
            }
        }
    }

    public async Task StartStreamAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_connected) throw new InvalidOperationException("연결되지 않았습니다.");
            if (_streaming) return;

            _profile = _pipeline!.Start(_config);
            ApplyDepthSensorSettings();

            _streaming = true;
            _loggedFirstColor = false;
            _loggedFirstDepth = false;
            _loggedFirstIr = false;
            _logger.LogInformation("{Name} 스트림 시작", _settings.Name);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 깊이 센서에서 DepthUnits(Z16 → m 스케일)를 읽어 mm 변환 계수를 갱신하고, 이미터(도트
    /// 프로젝터)를 설정대로 켜거나 끈다. 옵션 미지원 펌웨어에서도 본 흐름엔 영향이 없다.
    /// </summary>
    private void ApplyDepthSensorSettings()
    {
        try
        {
            using var dev = _profile!.Device;
            foreach (var sensor in dev.Sensors)
            {
                using (sensor)
                {
                    if (!sensor.Options.Supports(Option.DepthUnits)) continue;

                    var metersPerUnit = sensor.DepthScale;   // 기본 0.001 = 1mm/unit
                    _depthMmPerUnit = metersPerUnit * 1000f;
                    if (Math.Abs(_depthMmPerUnit - 1f) > 0.0001f)
                        _logger.LogInformation("{Name} 깊이 스케일 {Scale}m/unit — mm 변환 적용",
                            _settings.Name, metersPerUnit);

                    // Visual Preset 은 EmitterEnabled/LaserPower 등 다수 옵션을 덮어쓰므로 가장 먼저 적용.
                    if (!string.IsNullOrWhiteSpace(_settings.VisualPreset) &&
                        sensor.Options.Supports(Option.VisualPreset))
                    {
                        if (Enum.TryParse<Rs400VisualPreset>(_settings.VisualPreset, true, out var preset))
                        {
                            try
                            {
                                sensor.Options[Option.VisualPreset].Value = (float)preset;
                                _logger.LogInformation("{Name} Visual Preset = {Preset}", _settings.Name, preset);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "{Name} Visual Preset({Preset}) 적용 실패 — 계속 진행",
                                    _settings.Name, preset);
                            }
                        }
                        else
                        {
                            _logger.LogWarning("{Name} 알 수 없는 VisualPreset '{Value}' — 무시",
                                _settings.Name, _settings.VisualPreset);
                        }
                    }

                    if (sensor.Options.Supports(Option.EmitterEnabled))
                    {
                        sensor.Options[Option.EmitterEnabled].Value = _settings.EnableEmitter ? 1f : 0f;
                        _logger.LogInformation("{Name} IR 이미터 = {On}", _settings.Name, _settings.EnableEmitter);
                    }

                    if (_settings.LaserPower >= 0f && sensor.Options.Supports(Option.LaserPower))
                    {
                        try
                        {
                            var lp = sensor.Options[Option.LaserPower];
                            var v = Math.Clamp(_settings.LaserPower, lp.Min, lp.Max);   // D435: 0~360
                            lp.Value = v;
                            _logger.LogInformation("{Name} 레이저 파워 = {Power}", _settings.Name, v);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "{Name} LaserPower 적용 실패 — 계속 진행", _settings.Name);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Name} 깊이 센서 옵션 적용 실패 — 기본값으로 진행", _settings.Name);
        }
    }

    public async Task StopStreamAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_streaming) return;
            try { _pipeline?.Stop(); }
            catch (Exception ex) { _logger.LogWarning(ex, "{Name} pipeline stop 경고", _settings.Name); }
            _streaming = false;
            SafeDispose(ref _profile);
            // 정지 시점에 잔존하던 프레임 스냅샷을 비워 UI 가 즉시 "정지" 상태로 보이도록.
            Interlocked.Exchange(ref _latestColor, null);
            Interlocked.Exchange(ref _latestDepth, null);
            Interlocked.Exchange(ref _latestIr, null);
            _logger.LogInformation("{Name} 스트림 정지", _settings.Name);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 취소될 때까지 <c>TryWaitForFrames</c> 로 프레임을 받아 스냅샷을 갱신한다. 연결/스트림
    /// 시작은 호출자가 먼저 보장해야 한다. 예외 발생 시 한 번만 로그를 남기고 빠져나와 상위 재연결
    /// 루프에 위임.
    /// </summary>
    public Task RunAsync(CancellationToken ct)
    {
        return Task.Run(() =>
        {
            // 깊이 후처리(홀 복원) 체인. 세션(RunAsync 1회)마다 새로 만들어 TemporalFilter
            // 히스토리를 리셋하고, 이 스레드에서만 접근한다. 생성 실패는 치명적이지 않게 —
            // 원시 깊이로 진행한다.
            DepthFilterChain? depthFilters = null;
            try
            {
                depthFilters = DepthFilterChain.TryCreate(_settings.DepthFilters);
                if (depthFilters is not null)
                    _logger.LogInformation(
                        "{Name} 깊이 후처리 체인 활성 (threshold={Th}, spatial={Sp}, temporal={Tp}, holeFill={Hf})",
                        _settings.Name, _settings.DepthFilters.UseThreshold,
                        _settings.DepthFilters.UseSpatial,
                        _settings.DepthFilters.UseTemporal, _settings.DepthFilters.UseHoleFilling);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{Name} 깊이 필터 체인 생성 실패 — 원시 깊이로 진행", _settings.Name);
            }

            try
            {
                int consecutiveTimeouts = 0;
                // 워치독: 마지막으로 프레임을 성공 수신한 시점. 이 시점 이후 일정 시간 프레임이 끊기면
                // (USB 재열거 등으로 TryWaitForFrames 가 예외 없이 타임아웃만 반복) 강제 재연결한다.
                var lastOk = DateTime.UtcNow;
                bool receivedAny = false;
                while (!ct.IsCancellationRequested && _streaming)
                {
                    try
                    {
                        if (!_pipeline!.TryWaitForFrames(out var frames, (uint)_settings.FrameWaitTimeoutMs))
                        {
                            // 타임아웃 — 프레임이 한동안 안 오면 주기적으로 한 번씩 경고(매 30회 ≈ 60초).
                            if (++consecutiveTimeouts % 30 == 0)
                                _logger.LogWarning("{Name} TryWaitForFrames {Count}회 연속 타임아웃 — 프레임 미수신",
                                    _settings.Name, consecutiveTimeouts);

                            // 프레임 기아 감지 → 강제 teardown 후 종료. 첫 프레임 수신 전(콜드스타트)에는
                            // 카메라 초기화에 시간이 걸리므로 더 넉넉히 기다린다.
                            var idleMs = (DateTime.UtcNow - lastOk).TotalMilliseconds;
                            var limitMs = receivedAny
                                ? _settings.FrameStarvationReconnectMs
                                : Math.Max(_settings.FrameStarvationReconnectMs, 8000);
                            if (idleMs >= limitMs)
                            {
                                _logger.LogWarning(
                                    "{Name} {Idle:F0}ms 동안 프레임 끊김 — 강제 재연결(USB 재열거 추정)",
                                    _settings.Name, idleMs);
                                Disconnect();   // _connected/_streaming=false + 프레임 스냅샷 비움 → 상위 루프가 재연결
                                return;
                            }
                            continue;
                        }
                        consecutiveTimeouts = 0;
                        lastOk = DateTime.UtcNow;
                        receivedAny = true;

                        // 래퍼 Frame/FrameSet 은 매 반복 즉시 dispose — librealsense 내부 프레임 풀(16개)
                        // 고갈 시 스트림이 조용히 멈춘다.
                        using (frames)
                        {
                            if (_settings.EnableColor) TrySwapColor(frames);
                            if (_settings.EnableDepth) TrySwapDepth(frames, ref depthFilters);
                            if (_irActive) TrySwapIr(frames);
                        }

                        // 첫 color/depth/ir 프레임을 1회씩 로그 — 어느 스트림이 실제로 들어오는지,
                        // 해상도/사이즈가 정상인지 즉시 확인용.
                        if (!_loggedFirstColor && _latestColor is { } c)
                        {
                            _loggedFirstColor = true;
                            _logger.LogInformation("{Name} 첫 color 프레임 수신: {W}x{H} fmt={Fmt} size={Size}",
                                _settings.Name, c.Width, c.Height, c.PixelFormat, c.Pixels.Length);
                        }
                        if (!_loggedFirstDepth && _latestDepth is { } d)
                        {
                            _loggedFirstDepth = true;
                            _logger.LogInformation("{Name} 첫 depth 프레임 수신: {W}x{H} size={Size}",
                                _settings.Name, d.Width, d.Height, d.Pixels.Length);
                        }
                        if (!_loggedFirstIr && _latestIr is { } ir)
                        {
                            _loggedFirstIr = true;
                            _logger.LogInformation("{Name} 첫 IR 프레임 수신: {W}x{H} fmt={Fmt} size={Size}",
                                _settings.Name, ir.Width, ir.Height, ir.PixelFormat, ir.Pixels.Length);
                        }

                        _lastFrameAt = DateTime.UtcNow;
                    }
                    catch (Exception ex)
                    {
                        // 장치 분리 등으로 TryWaitForFrames 가 예외를 던지면 종료 → 상위 재연결 루프에 위임.
                        _logger.LogWarning(ex, "{Name} 프레임 수신 오류 — 루프 종료", _settings.Name);
                        return;
                    }
                }
            }
            finally
            {
                // 재연결 시 RunAsync 가 다시 돌며 TryCreate 가 새로 실행 → temporal 히스토리 자동 리셋.
                depthFilters?.Dispose();
            }
        }, ct);
    }

    private void TrySwapColor(FrameSet frames)
    {
        using var frame = frames.ColorFrame;
        if (frame is null) return;
        var snap = ExtractTightRows(frame, bytesPerPixel: 3, "rgb24");
        if (snap is not null) Interlocked.Exchange(ref _latestColor, snap);
    }

    private void TrySwapDepth(FrameSet frames, ref DepthFilterChain? filters)
    {
        using var raw = frames.DepthFrame;
        if (raw is null) return;

        CameraFrame? snap;
        if (filters is not null)
        {
            VideoFrame? filtered = null;
            try
            {
                filtered = filters.Process(raw);
                snap = ExtractTightRows(filtered, bytesPerPixel: 2, "depth16");
            }
            catch (Exception ex)
            {
                // 런타임 필터 실패는 세션 나머지 동안 원시 깊이로 폴백(스트림은 유지).
                _logger.LogWarning(ex, "{Name} 깊이 필터 처리 실패 — 원시 깊이로 폴백", _settings.Name);
                filters.Dispose();
                filters = null;
                snap = ExtractTightRows(raw, bytesPerPixel: 2, "depth16");
            }
            finally
            {
                filtered?.Dispose();   // 최종 필터 프레임도 네이티브 풀에 반환
            }
        }
        else
        {
            snap = ExtractTightRows(raw, bytesPerPixel: 2, "depth16");
        }
        if (snap is null) return;

        // Z16 원시값 → mm. 기본 스케일(0.001m = 1mm/unit)이면 무변환.
        if (Math.Abs(_depthMmPerUnit - 1f) > 0.0001f)
        {
            var span = MemoryMarshal.Cast<byte, ushort>(snap.Pixels.AsSpan());
            for (int i = 0; i < span.Length; i++)
            {
                if (span[i] == 0) continue;   // 0 = 무효값 유지
                var mm = span[i] * _depthMmPerUnit;
                span[i] = mm >= 65535f ? (ushort)65535 : (ushort)(mm + 0.5f);
            }
        }
        Interlocked.Exchange(ref _latestDepth, snap);
    }

    private void TrySwapIr(FrameSet frames)
    {
        using var frame = frames.InfraredFrame;
        if (frame is null) return;
        var snap = ExtractTightRows(frame, bytesPerPixel: 1, "ir8");
        if (snap is not null) Interlocked.Exchange(ref _latestIr, snap);
    }

    /// <summary>
    /// VideoFrame 의 픽셀을 관리 배열로 복사해 <see cref="CameraFrame"/> 스냅샷을 만든다.
    /// stride 가 행 폭보다 크면(정렬 패딩) 행 단위로 재패킹해 소비자가 기대하는 밀집 레이아웃을 보장.
    /// </summary>
    private static CameraFrame? ExtractTightRows(VideoFrame frame, int bytesPerPixel, string pixelFormat)
    {
        int w = frame.Width, h = frame.Height, stride = frame.Stride;
        var data = frame.Data;
        if (w <= 0 || h <= 0 || data == IntPtr.Zero) return null;

        int rowBytes = w * bytesPerPixel;
        var managed = new byte[rowBytes * h];
        if (stride == rowBytes || stride <= 0)
        {
            Marshal.Copy(data, managed, 0, managed.Length);
        }
        else
        {
            for (int y = 0; y < h; y++)
                Marshal.Copy(data + y * stride, managed, y * rowBytes, rowBytes);
        }
        return new CameraFrame(managed, w, h, pixelFormat, DateTime.UtcNow);
    }

    public void Disconnect()
    {
        // _gate 이 이미 Dispose() 된 상황(중복 호출 등)에서는 Wait 가 ObjectDisposedException 을
        // 던진다. 그 경우 네이티브는 어차피 누군가 정리했거나 정리 중이므로 조용히 패스.
        try { _gate.Wait(); }
        catch (ObjectDisposedException) { return; }
        try { DisposeNativeUnlocked(); }
        finally { try { _gate.Release(); } catch { /* not held / disposed */ } }
    }

    private void DisposeNativeUnlocked()
    {
        if (_streaming && _pipeline is not null)
        {
            try { _pipeline.Stop(); }
            catch (Exception ex) { _logger.LogWarning(ex, "{Name} pipeline stop 경고", _settings.Name); }
        }
        _streaming = false;
        _connected = false;
        _irActive = false;

        SafeDispose(ref _profile);
        SafeDispose(ref _config);
        SafeDispose(ref _pipeline);
        SafeDispose(ref _context);

        Interlocked.Exchange(ref _latestColor, null);
        Interlocked.Exchange(ref _latestDepth, null);
        Interlocked.Exchange(ref _latestIr, null);
        _connectionType = null;
        _depthMmPerUnit = 1.0f;
        lock (_camParamLock) _camParam = null;   // 파이프라인 재생성 시 재획득
    }

    private static void SafeDispose<T>(ref T? disposable) where T : class, IDisposable
    {
        var d = disposable;
        disposable = null;
        if (d is null) return;
        try { d.Dispose(); } catch { /* ignore */ }
    }

    public void Dispose()
    {
        Disconnect();
        _gate.Dispose();
    }
}
