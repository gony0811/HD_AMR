using System.Runtime.InteropServices;
using HD_AMR.Models;
using Microsoft.Extensions.Logging;

namespace HD_AMR.Communication;

/// <summary>
/// Orbbec Gemini 2 카메라(USB 3.0 RGB-D)의 얇은 래퍼. 공식 OrbbecSDK 의 C ABI 를 P/Invoke 로 직접
/// 호출한다(네이티브 라이브러리: <c>libOrbbecSDK.{so,dylib,dll}</c>). 컬러+깊이 프레임을 받아
/// 불변 <see cref="CameraFrame"/> 으로 스냅샷한 뒤 <c>Interlocked.Exchange</c> 로 교체 → 소비자
/// 측에서 락 없이 안전하게 읽을 수 있다. 연결/스트림 시작/정지는 <see cref="SemaphoreSlim"/> 로
/// 직렬화(<see cref="CobotModbusTcpClient"/> 와 동일 패턴).
///
/// 네이티브 라이브러리가 없는 환경(예: macOS 개발 머신)에서는 <see cref="ConnectAsync"/> 가
/// <see cref="DllNotFoundException"/> 으로 실패하지만 상위 5초 재연결 루프가 흡수하므로 앱은 죽지
/// 않는다.
/// </summary>
public class OrbbecGeminiClient : IDisposable
{
    private readonly OrbbecGeminiSettings _settings;
    private readonly ILogger<OrbbecGeminiClient> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IntPtr _context;
    private IntPtr _device;
    private IntPtr _pipeline;
    private IntPtr _config;

    private CameraFrame? _latestColor;
    private CameraFrame? _latestDepth;
    private DateTime _lastFrameAt;

    // 실제 카메라에서 선택된 컬러 포맷(MJPG/RGB/YUYV 등). 프레임 추출 시 어떻게 해석할지 결정.
    private int _chosenColorFormat = OrbbecNative.OB_FORMAT_UNKNOWN;

    private volatile bool _connected;
    private volatile bool _streaming;

    public OrbbecGeminiClient(OrbbecGeminiSettings settings, ILogger<OrbbecGeminiClient> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public bool IsConnected => _connected;
    public bool IsStreaming => _streaming;
    public CameraFrame? LatestColor => _latestColor;
    public CameraFrame? LatestDepth => _latestDepth;
    public DateTime LastFrameAt => _lastFrameAt;
    public OrbbecGeminiSettings Settings => _settings;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_connected) return;

            // DeviceSerial 미지정이면 ob_create_pipeline() 한 번에 첫 디바이스를 잡는다 — 명시적
            // 디바이스 열거(query_device_list + get_device)는 macOS libuvc 에서 같은 핸들을 두 번
            // 열려고 시도하다 'already opened' 로 실패하는 경우가 있어 단순 경로를 우선.
            if (string.IsNullOrWhiteSpace(_settings.DeviceSerial))
            {
                _pipeline = OrbbecNative.CheckPtr(OrbbecNative.ob_create_pipeline(out var pipeErr), pipeErr, "create_pipeline");
            }
            else
            {
                _context = OrbbecNative.CheckPtr(OrbbecNative.ob_create_context(out var ctxErr), ctxErr, "create_context");
                var devList = OrbbecNative.CheckPtr(OrbbecNative.ob_query_device_list(_context, out var listErr), listErr, "query_device_list");
                try
                {
                    var count = OrbbecNative.ob_device_list_device_count(devList, out var cntErr);
                    OrbbecNative.ThrowIfError(cntErr, "device_list_device_count");
                    if (count == 0)
                        throw new InvalidOperationException("연결된 Orbbec 카메라를 찾을 수 없습니다.");

                    _device = PickDevice(devList, count);
                    _pipeline = OrbbecNative.CheckPtr(OrbbecNative.ob_create_pipeline_with_device(_device, out var pipeErr), pipeErr, "create_pipeline_with_device");
                }
                finally
                {
                    OrbbecNative.SafeDeleteDeviceList(devList);
                }
            }
            _config = OrbbecNative.CheckPtr(OrbbecNative.ob_create_config(out var cfgErr), cfgErr, "create_config");

            // 카메라가 실제로 노출하는 프로파일을 열거하고 그 중에서 매칭 — 하드코딩된 w/h/fps/format
            // 으로 enable_video_stream 을 호출하면 카메라/SDK 의 실제 프로파일 셋과 어긋날 때
            // 'No matched profile found' 로 실패하므로 (Linux arm64 + Gemini 2 조합에서 자주 발생).
            if (_settings.EnableColor)
            {
                _chosenColorFormat = EnableStream(
                    OrbbecNative.OB_STREAM_COLOR, "color",
                    preferredFormats: new[] {
                        OrbbecNative.OB_FORMAT_MJPG,   // 브라우저 패스스루용 1순위
                        OrbbecNative.OB_FORMAT_RGB,    // SDK 가 변환 지원하면
                        0,                              // YUYV — 아직 변환 미지원이나 일단 받아두면 디버그용
                    });
            }
            if (_settings.EnableDepth)
            {
                EnableStream(
                    OrbbecNative.OB_STREAM_DEPTH, "depth",
                    preferredFormats: new[] { OrbbecNative.OB_FORMAT_Y16 });
            }

            _connected = true;
            _logger.LogInformation("{Name} 연결 완료 (color={Color}, depth={Depth})",
                _settings.Name, _settings.EnableColor, _settings.EnableDepth);
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

    public async Task StartStreamAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_connected) throw new InvalidOperationException("연결되지 않았습니다.");
            if (_streaming) return;

            OrbbecNative.ob_pipeline_start_with_config(_pipeline, _config, out var err);
            OrbbecNative.ThrowIfError(err, "pipeline_start_with_config");
            _streaming = true;
            _logger.LogInformation("{Name} 스트림 시작", _settings.Name);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopStreamAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_streaming) return;
            OrbbecNative.ob_pipeline_stop(_pipeline, out var err);
            _streaming = false;
            // 정지 시점에 잔존하던 프레임 스냅샷을 비워 UI 가 즉시 "정지" 상태로 보이도록.
            Interlocked.Exchange(ref _latestColor, null);
            Interlocked.Exchange(ref _latestDepth, null);
            OrbbecNative.LogIfError(_logger, err, "pipeline_stop");
            _logger.LogInformation("{Name} 스트림 정지", _settings.Name);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 취소될 때까지 <c>wait_for_frameset</c> 으로 프레임을 받아 스냅샷을 갱신한다. 연결/스트림
    /// 시작은 호출자가 먼저 보장해야 한다. 예외 발생 시 한 번만 로그를 남기고 빠져나와 상위 재연결
    /// 루프에 위임.
    /// </summary>
    public Task RunAsync(CancellationToken ct)
    {
        return Task.Run(() =>
        {
            while (!ct.IsCancellationRequested && _streaming)
            {
                IntPtr set = IntPtr.Zero;
                try
                {
                    set = OrbbecNative.ob_pipeline_wait_for_frameset(_pipeline, (uint)_settings.FrameWaitTimeoutMs, out var err);
                    OrbbecNative.ThrowIfError(err, "wait_for_frameset");
                    if (set == IntPtr.Zero) continue; // 타임아웃 — 다음 틱.

                    if (_settings.EnableColor) TrySwapColor(set);
                    if (_settings.EnableDepth) TrySwapDepth(set);

                    _lastFrameAt = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "{Name} 프레임 수신 오류 — 루프 종료", _settings.Name);
                    return;
                }
                finally
                {
                    if (set != IntPtr.Zero)
                        OrbbecNative.SafeDeleteFrame(set);
                }
            }
        }, ct);
    }

    private void TrySwapColor(IntPtr frameset)
    {
        var frame = OrbbecNative.ob_frameset_color_frame(frameset, out var err);
        if (err != IntPtr.Zero || frame == IntPtr.Zero) { OrbbecNative.SafeDeleteError(err); return; }
        try
        {
            var snap = ExtractColor(frame, _chosenColorFormat);
            if (snap is not null) Interlocked.Exchange(ref _latestColor, snap);
        }
        finally { OrbbecNative.SafeDeleteFrame(frame); }
    }

    private void TrySwapDepth(IntPtr frameset)
    {
        var frame = OrbbecNative.ob_frameset_depth_frame(frameset, out var err);
        if (err != IntPtr.Zero || frame == IntPtr.Zero) { OrbbecNative.SafeDeleteError(err); return; }
        try
        {
            var snap = ExtractDepth16(frame);
            if (snap is not null) Interlocked.Exchange(ref _latestDepth, snap);
        }
        finally { OrbbecNative.SafeDeleteFrame(frame); }
    }

    private static CameraFrame? ExtractColor(IntPtr frame, int format)
    {
        var w = OrbbecNative.ob_video_frame_width(frame, out var werr); OrbbecNative.SafeDeleteError(werr);
        var h = OrbbecNative.ob_video_frame_height(frame, out var herr); OrbbecNative.SafeDeleteError(herr);
        var size = OrbbecNative.ob_frame_data_size(frame, out var serr); OrbbecNative.SafeDeleteError(serr);
        var data = OrbbecNative.ob_frame_data(frame, out var derr); OrbbecNative.SafeDeleteError(derr);
        if (w <= 0 || h <= 0 || size == 0 || data == IntPtr.Zero) return null;

        var managed = new byte[size];
        Marshal.Copy(data, managed, 0, (int)size);

        // CameraService 의 JPEG 변환 분기는 PixelFormat 문자열로 분기한다.
        string pixelFormat = format switch
        {
            OrbbecNative.OB_FORMAT_MJPG => "mjpg",
            OrbbecNative.OB_FORMAT_RGB  => "rgb24",
            _                            => $"raw_{format}",
        };
        return new CameraFrame(managed, w, h, pixelFormat, DateTime.UtcNow);
    }

    private static CameraFrame? ExtractDepth16(IntPtr frame)
    {
        var w = OrbbecNative.ob_video_frame_width(frame, out var werr); OrbbecNative.SafeDeleteError(werr);
        var h = OrbbecNative.ob_video_frame_height(frame, out var herr); OrbbecNative.SafeDeleteError(herr);
        var size = OrbbecNative.ob_frame_data_size(frame, out var serr); OrbbecNative.SafeDeleteError(serr);
        var data = OrbbecNative.ob_frame_data(frame, out var derr); OrbbecNative.SafeDeleteError(derr);
        if (w <= 0 || h <= 0 || size == 0 || data == IntPtr.Zero) return null;

        var managed = new byte[size];
        Marshal.Copy(data, managed, 0, (int)size);
        return new CameraFrame(managed, w, h, "depth16", DateTime.UtcNow);
    }

    private IntPtr PickDevice(IntPtr devList, uint count)
    {
        if (string.IsNullOrWhiteSpace(_settings.DeviceSerial))
        {
            var d = OrbbecNative.ob_device_list_get_device(devList, 0, out var err);
            OrbbecNative.ThrowIfError(err, "device_list_get_device(0)");
            return d;
        }

        for (uint i = 0; i < count; i++)
        {
            var d = OrbbecNative.ob_device_list_get_device(devList, i, out var err);
            if (err != IntPtr.Zero || d == IntPtr.Zero) { OrbbecNative.SafeDeleteError(err); continue; }

            var info = OrbbecNative.ob_device_get_device_info(d, out var ierr); OrbbecNative.SafeDeleteError(ierr);
            var snPtr = info != IntPtr.Zero ? OrbbecNative.ob_device_info_serial_number(info, out var serr) : IntPtr.Zero;
            OrbbecNative.SafeDeleteError(IntPtr.Zero); // 호환용
            var sn = snPtr != IntPtr.Zero ? Marshal.PtrToStringAnsi(snPtr) : null;
            OrbbecNative.SafeDeleteDeviceInfo(info);

            if (sn == _settings.DeviceSerial) return d;
            OrbbecNative.SafeDeleteDevice(d);
        }

        throw new InvalidOperationException($"시리얼 {_settings.DeviceSerial} 인 카메라를 찾지 못했습니다.");
    }

    /// <summary>
    /// 파이프라인의 스트림 프로파일 리스트를 열거해 로그에 찍은 뒤, 선호 포맷 + 설정 해상도/FPS
    /// 로 직접 매칭한다. SDK 의 <c>ob_stream_profile_list_get_video_stream_profile</c> 는 1.10.x 의
    /// libOrbbecSDK 에서 와일드카드(-1) 또는 0 입력에 "Invalid input, No matched video stream
    /// profile found!" 로 자주 실패하므로 사용하지 않는다.
    /// 3단계 fallback: 1) w/h/fps/format 완전 일치 → 2) 포맷만 일치 → 3) 리스트의 첫 프로파일.
    /// </summary>
    private int EnableStream(int streamType, string label, int[] preferredFormats)
    {
        var profileList = OrbbecNative.ob_pipeline_get_stream_profile_list(_pipeline, streamType, out var listErr);
        OrbbecNative.ThrowIfError(listErr, $"get_stream_profile_list({label})");
        if (profileList == IntPtr.Zero)
            throw new InvalidOperationException($"{label} 프로파일 리스트가 비어 있습니다");

        try
        {
            var count = OrbbecNative.ob_stream_profile_list_count(profileList, out var cntErr);
            OrbbecNative.SafeDeleteError(cntErr);
            _logger.LogInformation("{Label}: {Count}개 프로파일 사용 가능", label, count);

            // 한 번에 메타데이터(w,h,fps,fmt) 를 모아두고 로그와 매칭에 동시에 쓴다.
            var meta = new (int W, int H, int Fps, int Fmt)[count];
            for (uint i = 0; i < count; i++)
            {
                var p = OrbbecNative.ob_stream_profile_list_get_profile(profileList, (int)i, out var pErr);
                OrbbecNative.SafeDeleteError(pErr);
                if (p == IntPtr.Zero) { meta[i] = (0, 0, 0, OrbbecNative.OB_FORMAT_UNKNOWN); continue; }
                try
                {
                    var w = (int)OrbbecNative.ob_video_stream_profile_width(p, out var werr); OrbbecNative.SafeDeleteError(werr);
                    var h = (int)OrbbecNative.ob_video_stream_profile_height(p, out var herr); OrbbecNative.SafeDeleteError(herr);
                    var fps = (int)OrbbecNative.ob_video_stream_profile_fps(p, out var ferr); OrbbecNative.SafeDeleteError(ferr);
                    var fmt = OrbbecNative.ob_stream_profile_format(p, out var fmerr); OrbbecNative.SafeDeleteError(fmerr);
                    meta[i] = (w, h, fps, fmt);
                    if (i < 40u)
                        _logger.LogInformation("  [{Idx}] {W}x{H}@{Fps}fps fmt={Fmt}", i, w, h, fps, fmt);
                }
                finally { OrbbecNative.SafeDeleteStreamProfile(p); }
            }

            bool isColor = streamType == OrbbecNative.OB_STREAM_COLOR;
            int wantW = isColor ? _settings.ColorWidth : _settings.DepthWidth;
            int wantH = isColor ? _settings.ColorHeight : _settings.DepthHeight;
            int wantFps = isColor ? _settings.ColorFps : _settings.DepthFps;

            int matchIdx = -1;
            int chosenFmt = OrbbecNative.OB_FORMAT_UNKNOWN;

            // Pass 1: 완전 일치 (w/h/fps + 선호 포맷 순서).
            foreach (var pref in preferredFormats)
            {
                for (uint i = 0; i < count; i++)
                {
                    var m = meta[i];
                    if (m.W == wantW && m.H == wantH && m.Fps == wantFps && m.Fmt == pref)
                    { matchIdx = (int)i; chosenFmt = pref; break; }
                }
                if (matchIdx >= 0) break;
            }
            // Pass 2: 포맷만 일치.
            if (matchIdx < 0)
            {
                foreach (var pref in preferredFormats)
                {
                    for (uint i = 0; i < count; i++)
                    {
                        if (meta[i].Fmt == pref) { matchIdx = (int)i; chosenFmt = pref; break; }
                    }
                    if (matchIdx >= 0) break;
                }
            }
            // Pass 3: 무조건 첫 프로파일.
            if (matchIdx < 0 && count > 0) { matchIdx = 0; chosenFmt = meta[0].Fmt; }

            if (matchIdx < 0)
                throw new InvalidOperationException($"{label}: 매칭되는 프로파일이 없습니다");

            var picked = OrbbecNative.ob_stream_profile_list_get_profile(profileList, matchIdx, out var pe);
            OrbbecNative.SafeDeleteError(pe);
            if (picked == IntPtr.Zero)
                throw new InvalidOperationException($"{label}: 프로파일 핸들 획득 실패 (idx={matchIdx})");

            try
            {
                var mm = meta[matchIdx];
                _logger.LogInformation("{Label} 선택: [{Idx}] {W}x{H}@{Fps}fps fmt={Fmt}",
                    label, matchIdx, mm.W, mm.H, mm.Fps, chosenFmt);

                OrbbecNative.ob_config_enable_stream(_config, picked, out var enErr);
                OrbbecNative.ThrowIfError(enErr, $"enable_stream({label})");
            }
            finally { OrbbecNative.SafeDeleteStreamProfile(picked); }

            return chosenFmt;
        }
        finally
        {
            OrbbecNative.SafeDeleteStreamProfileList(profileList);
        }
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
        if (_streaming && _pipeline != IntPtr.Zero)
        {
            OrbbecNative.ob_pipeline_stop(_pipeline, out var stopErr);
            OrbbecNative.LogIfError(_logger, stopErr, "pipeline_stop");
        }
        _streaming = false;
        _connected = false;

        OrbbecNative.SafeDeleteConfig(_config); _config = IntPtr.Zero;
        OrbbecNative.SafeDeletePipeline(_pipeline); _pipeline = IntPtr.Zero;
        OrbbecNative.SafeDeleteDevice(_device); _device = IntPtr.Zero;
        OrbbecNative.SafeDeleteContext(_context); _context = IntPtr.Zero;

        Interlocked.Exchange(ref _latestColor, null);
        Interlocked.Exchange(ref _latestDepth, null);
    }

    public void Dispose()
    {
        Disconnect();
        _gate.Dispose();
    }
}

/// <summary>
/// OrbbecSDK (libOrbbecSDK) 의 C ABI 에 대한 P/Invoke 정의 + 자주 쓰는 안전 헬퍼. 네이티브
/// 라이브러리는 동일 디렉터리 또는 시스템 라이브러리 경로에서 검색된다. 함수 시그니처는 OrbbecSDK
/// 공개 헤더(예: <c>ObContext.h</c>, <c>Pipeline.h</c>, <c>Frame.h</c>) 기준.
/// </summary>
internal static class OrbbecNative
{
    private const string Lib = "OrbbecSDK";

    // 스트림 / 포맷 상수 (OrbbecSDK 1.10+ 기준). 펌웨어 버전에 따라 값이 달라지면 여기를 조정.
    // OBStreamType 열거값. OrbbecSDK 1.10.x: VIDEO=0, IR=1, COLOR=2, DEPTH=3, ACCEL=4, GYRO=5.
    public const int OB_STREAM_COLOR = 2;
    public const int OB_STREAM_DEPTH = 3;
    public const int OB_FORMAT_MJPG = 5;    // Gemini 2 컬러 센서의 네이티브 포맷.
    public const int OB_FORMAT_Y16 = 8;
    public const int OB_FORMAT_RGB = 22;
    public const int OB_FORMAT_UNKNOWN = 0xff;

    // ── 핸들 생성/소멸 ────────────────────────────────────────────────────
    [DllImport(Lib)] public static extern IntPtr ob_create_context(out IntPtr error);
    [DllImport(Lib)] public static extern void   ob_delete_context(IntPtr context, out IntPtr error);

    [DllImport(Lib)] public static extern IntPtr ob_query_device_list(IntPtr context, out IntPtr error);
    [DllImport(Lib)] public static extern uint   ob_device_list_device_count(IntPtr list, out IntPtr error);
    [DllImport(Lib)] public static extern IntPtr ob_device_list_get_device(IntPtr list, uint index, out IntPtr error);
    [DllImport(Lib)] public static extern void   ob_delete_device_list(IntPtr list, out IntPtr error);
    [DllImport(Lib)] public static extern void   ob_delete_device(IntPtr device, out IntPtr error);

    [DllImport(Lib)] public static extern IntPtr ob_device_get_device_info(IntPtr device, out IntPtr error);
    [DllImport(Lib)] public static extern IntPtr ob_device_info_serial_number(IntPtr info, out IntPtr error);
    [DllImport(Lib)] public static extern void   ob_delete_device_info(IntPtr info, out IntPtr error);

    [DllImport(Lib)] public static extern IntPtr ob_create_pipeline(out IntPtr error);
    [DllImport(Lib)] public static extern IntPtr ob_create_pipeline_with_device(IntPtr device, out IntPtr error);
    [DllImport(Lib)] public static extern void   ob_delete_pipeline(IntPtr pipeline, out IntPtr error);

    [DllImport(Lib)] public static extern IntPtr ob_create_config(out IntPtr error);
    [DllImport(Lib)] public static extern void   ob_delete_config(IntPtr config, out IntPtr error);

    [DllImport(Lib)] public static extern void ob_config_enable_video_stream(
        IntPtr config, int streamType, int width, int height, int fps, int format, out IntPtr error);
    [DllImport(Lib)] public static extern void   ob_config_enable_stream(IntPtr config, IntPtr profile, out IntPtr error);

    // 스트림 프로파일 열거 — 카메라가 실제로 노출하는 프로파일을 직접 받아 매칭한다.
    [DllImport(Lib)] public static extern IntPtr ob_pipeline_get_stream_profile_list(IntPtr pipeline, int sensorType, out IntPtr error);
    [DllImport(Lib)] public static extern uint   ob_stream_profile_list_count(IntPtr list, out IntPtr error);
    [DllImport(Lib)] public static extern IntPtr ob_stream_profile_list_get_profile(IntPtr list, int index, out IntPtr error);
    [DllImport(Lib)] public static extern IntPtr ob_stream_profile_list_get_video_stream_profile(
        IntPtr list, int width, int height, int format, int fps, out IntPtr error);
    [DllImport(Lib)] public static extern void   ob_delete_stream_profile_list(IntPtr list, out IntPtr error);
    [DllImport(Lib)] public static extern void   ob_delete_stream_profile(IntPtr profile, out IntPtr error);
    [DllImport(Lib)] public static extern uint   ob_video_stream_profile_width(IntPtr profile, out IntPtr error);
    [DllImport(Lib)] public static extern uint   ob_video_stream_profile_height(IntPtr profile, out IntPtr error);
    [DllImport(Lib)] public static extern uint   ob_video_stream_profile_fps(IntPtr profile, out IntPtr error);
    [DllImport(Lib)] public static extern int    ob_stream_profile_format(IntPtr profile, out IntPtr error);

    [DllImport(Lib)] public static extern void   ob_pipeline_start_with_config(IntPtr pipeline, IntPtr config, out IntPtr error);
    [DllImport(Lib)] public static extern void   ob_pipeline_stop(IntPtr pipeline, out IntPtr error);
    [DllImport(Lib)] public static extern IntPtr ob_pipeline_wait_for_frameset(IntPtr pipeline, uint timeoutMs, out IntPtr error);

    [DllImport(Lib)] public static extern IntPtr ob_frameset_color_frame(IntPtr frameset, out IntPtr error);
    [DllImport(Lib)] public static extern IntPtr ob_frameset_depth_frame(IntPtr frameset, out IntPtr error);

    [DllImport(Lib)] public static extern int    ob_video_frame_width(IntPtr frame, out IntPtr error);
    [DllImport(Lib)] public static extern int    ob_video_frame_height(IntPtr frame, out IntPtr error);
    [DllImport(Lib)] public static extern IntPtr ob_frame_data(IntPtr frame, out IntPtr error);
    [DllImport(Lib)] public static extern uint   ob_frame_data_size(IntPtr frame, out IntPtr error);
    [DllImport(Lib)] public static extern void   ob_delete_frame(IntPtr frame, out IntPtr error);

    // ── 에러 객체 헬퍼 ────────────────────────────────────────────────────
    [DllImport(Lib)] public static extern IntPtr ob_error_message(IntPtr error);
    [DllImport(Lib)] public static extern void   ob_delete_error(IntPtr error);

    // ── 안전 래퍼 ────────────────────────────────────────────────────────
    public static IntPtr CheckPtr(IntPtr handle, IntPtr error, string what)
    {
        ThrowIfError(error, what);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException($"OrbbecSDK {what} 반환이 null 입니다.");
        return handle;
    }

    public static void ThrowIfError(IntPtr error, string what)
    {
        if (error == IntPtr.Zero) return;
        string msg;
        try
        {
            var ptr = ob_error_message(error);
            msg = ptr != IntPtr.Zero ? Marshal.PtrToStringAnsi(ptr) ?? "(unknown)" : "(unknown)";
        }
        catch { msg = "(error_message 호출 실패)"; }
        finally { try { ob_delete_error(error); } catch { /* ignore */ } }
        throw new InvalidOperationException($"OrbbecSDK {what} 실패: {msg}");
    }

    public static void LogIfError(ILogger logger, IntPtr error, string what)
    {
        if (error == IntPtr.Zero) return;
        try
        {
            var ptr = ob_error_message(error);
            var msg = ptr != IntPtr.Zero ? Marshal.PtrToStringAnsi(ptr) : null;
            logger.LogWarning("OrbbecSDK {What} 경고: {Msg}", what, msg ?? "(unknown)");
        }
        catch { /* swallow */ }
        finally { try { ob_delete_error(error); } catch { /* ignore */ } }
    }

    public static void SafeDeleteError(IntPtr error)
    {
        if (error == IntPtr.Zero) return;
        try { ob_delete_error(error); } catch { /* ignore */ }
    }

    public static void SafeDeleteFrame(IntPtr p)
    {
        if (p == IntPtr.Zero) return;
        try { ob_delete_frame(p, out var e); SafeDeleteError(e); } catch { }
    }
    public static void SafeDeleteConfig(IntPtr p)
    {
        if (p == IntPtr.Zero) return;
        try { ob_delete_config(p, out var e); SafeDeleteError(e); } catch { }
    }
    public static void SafeDeletePipeline(IntPtr p)
    {
        if (p == IntPtr.Zero) return;
        try { ob_delete_pipeline(p, out var e); SafeDeleteError(e); } catch { }
    }
    public static void SafeDeleteDevice(IntPtr p)
    {
        if (p == IntPtr.Zero) return;
        try { ob_delete_device(p, out var e); SafeDeleteError(e); } catch { }
    }
    public static void SafeDeleteDeviceList(IntPtr p)
    {
        if (p == IntPtr.Zero) return;
        try { ob_delete_device_list(p, out var e); SafeDeleteError(e); } catch { }
    }
    public static void SafeDeleteDeviceInfo(IntPtr p)
    {
        if (p == IntPtr.Zero) return;
        try { ob_delete_device_info(p, out var e); SafeDeleteError(e); } catch { }
    }
    public static void SafeDeleteContext(IntPtr p)
    {
        if (p == IntPtr.Zero) return;
        try { ob_delete_context(p, out var e); SafeDeleteError(e); } catch { }
    }
    public static void SafeDeleteStreamProfile(IntPtr p)
    {
        if (p == IntPtr.Zero) return;
        try { ob_delete_stream_profile(p, out var e); SafeDeleteError(e); } catch { }
    }
    public static void SafeDeleteStreamProfileList(IntPtr p)
    {
        if (p == IntPtr.Zero) return;
        try { ob_delete_stream_profile_list(p, out var e); SafeDeleteError(e); } catch { }
    }
}
