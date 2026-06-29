using System.Runtime.InteropServices;
using HD_AMR.Communication;
using HD_AMR.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace HD_AMR.Service;

/// <summary>
/// Orbbec Gemini 2 깊이 카메라 연결을 유지하는 백그라운드 서비스. <see cref="CobotService"/>·
/// <see cref="AMRService"/> 와 동일하게 5초 재연결 루프. 컬러/깊이 최신 프레임을
/// <see cref="OrbbecGeminiClient"/> 에서 노출하고, JPEG 인코딩(컬러: ImageSharp 로 RGB24→JPEG,
/// 깊이: 선형 cold→hot LUT 적용 후 JPEG) 헬퍼를 MJPEG 엔드포인트에 제공한다.
/// </summary>
public class CameraService : BackgroundService
{
    private readonly OrbbecGeminiSettings _settings;
    private readonly OrbbecGeminiClient _client;
    private readonly ILogger<CameraService> _logger;

    public CameraService(IOptions<OrbbecGeminiSettings> options, ILoggerFactory loggerFactory)
    {
        _settings = options.Value;
        _logger = loggerFactory.CreateLogger<CameraService>();
        _client = new OrbbecGeminiClient(_settings, loggerFactory.CreateLogger<OrbbecGeminiClient>());
    }

    public bool IsConnected => _client.IsConnected;
    public bool IsStreaming => _client.IsStreaming;
    public OrbbecGeminiSettings Settings => _settings;
    public CameraFrame? LatestColor => _client.LatestColor;
    public CameraFrame? LatestDepth => _client.LatestDepth;
    public CameraFrame? LatestIr => _client.LatestIr;
    /// <summary>IR 스트림이 실제로 활성화되어 프레임을 받는 중인지.</summary>
    public bool IsIrActive => _client.IsIrActive;
    public DateTime LastFrameAt => _client.LastFrameAt;
    public string? ConnectionType => _client.ConnectionType;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CameraService 시작 (device={Serial})",
            string.IsNullOrWhiteSpace(_settings.DeviceSerial) ? "<first>" : _settings.DeviceSerial);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_client.IsConnected)
                {
                    _logger.LogWarning("Orbbec 카메라 연결 시도");
                    await _client.ConnectAsync(stoppingToken);
                }
                if (_client.IsConnected && !_client.IsStreaming)
                {
                    await _client.StartStreamAsync(stoppingToken);
                    // 프레임 수신 루프는 Run 메서드가 완료될 때까지 백그라운드로 돈다. 끊기면
                    // 다음 5초 틱에서 IsStreaming==false 가 되어 재시도.
                    _ = _client.RunAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (DllNotFoundException ex)
            {
                _logger.LogError(ex,
                    "Orbbec 네이티브 라이브러리(libOrbbecSDK)를 찾을 수 없습니다 — 카메라는 비활성 상태로 유지됩니다.");
                // 네이티브가 없으면 재시도해도 결과가 같으므로 한 박자 길게 쉰다.
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                continue;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Orbbec 카메라 연결 실패 — {Sec}초 후 재시도",
                    _settings.ReconnectDelayMs / 1000);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(_settings.ReconnectDelayMs), stoppingToken);
        }
    }

    public Task StartStreamAsync(CancellationToken ct = default) => _client.StartStreamAsync(ct);
    public Task StopStreamAsync(CancellationToken ct = default) => _client.StopStreamAsync(ct);

    /// <summary>최신 컬러 프레임을 JPEG 바이트로 반환한다. 프레임이 없으면 null.
    /// PixelFormat 이 <c>"mjpg"</c> 이면 카메라가 이미 JPEG 으로 인코딩해 보내준 것이므로
    /// 바이트를 그대로 패스스루(추가 인코딩 비용 없음). <c>"rgb24"</c> 이면 ImageSharp 로
    /// 인코딩.</summary>
    public Task<byte[]?> GetLatestColorJpegAsync(int quality, CancellationToken ct = default)
    {
        var f = _client.LatestColor;
        if (f is null) return Task.FromResult<byte[]?>(null);
        if (f.PixelFormat == "mjpg") return Task.FromResult<byte[]?>(f.Pixels);
        return Task.Run<byte[]?>(() => EncodeRgb24ToJpeg(f, quality), ct);
    }

    /// <summary>
    /// 정규화 좌표 (u,v)∈[0,1] 위치의 깊이값(mm)을 반환한다. 프레임 없음/범위밖/무효(0)면 null.
    /// 정규화 좌표를 쓰므로 표시 스케일·네이티브 해상도(640×400/1280×800)와 무관하게 동작한다.
    /// </summary>
    public int? GetLatestDepthMmAt(double u, double v)
    {
        var f = _client.LatestDepth;
        if (f is null || u < 0 || u > 1 || v < 0 || v > 1) return null;
        int x = Math.Clamp((int)(u * f.Width), 0, f.Width - 1);
        int y = Math.Clamp((int)(v * f.Height), 0, f.Height - 1);
        var span = MemoryMarshal.Cast<byte, ushort>(f.Pixels);
        int idx = y * f.Width + x;
        if ((uint)idx >= (uint)span.Length) return null;
        int mm = span[idx];
        return mm == 0 ? (int?)null : mm;   // 0=무효 픽셀
    }

    /// <summary>
    /// 정규화 사각형 ROI(x,y,w,h ∈[0,1], 좌상단 기준) 안의 깊이 통계를 계산한다.
    /// 무효(0) 픽셀은 제외하고 최소/최대/평균(mm)·유효 픽셀 수·유효율을 구한다.
    /// 프레임이 없거나 ROI 가 프레임 밖이면 null. 정규화 좌표라 해상도와 무관하게 동작한다.
    /// </summary>
    public DepthRoiStats? ComputeDepthRoiStats(double x, double y, double w, double h)
    {
        var f = _client.LatestDepth;
        if (f is null) return null;

        // 정규화 → 픽셀. 음수/초과 입력도 프레임 안으로 클램프.
        int x0 = Math.Clamp((int)Math.Floor(x * f.Width), 0, f.Width - 1);
        int y0 = Math.Clamp((int)Math.Floor(y * f.Height), 0, f.Height - 1);
        int x1 = Math.Clamp((int)Math.Ceiling((x + w) * f.Width), 0, f.Width);
        int y1 = Math.Clamp((int)Math.Ceiling((y + h) * f.Height), 0, f.Height);
        if (x1 <= x0 || y1 <= y0) return null;

        var span = MemoryMarshal.Cast<byte, ushort>(f.Pixels);
        int min = int.MaxValue, max = 0, validCount = 0;
        int minPx = 0, minPy = 0;   // 최소값이 잡힌 픽셀 위치
        long sum = 0;
        int total = (x1 - x0) * (y1 - y0);

        for (int py = y0; py < y1; py++)
        {
            int rowBase = py * f.Width;
            for (int px = x0; px < x1; px++)
            {
                int idx = rowBase + px;
                if ((uint)idx >= (uint)span.Length) continue;
                int d = span[idx];
                if (d == 0) continue;   // 무효 픽셀 제외
                if (d < min) { min = d; minPx = px; minPy = py; }
                if (d > max) max = d;
                sum += d;
                validCount++;
            }
        }

        if (validCount == 0)
            return new DepthRoiStats(0, 0, 0, 0, total, 0, 0, 0);

        double avg = (double)sum / validCount;
        double ratio = total > 0 ? (double)validCount / total : 0;
        // 최소 픽셀을 정규화 좌표(전체 프레임 기준)로 변환 — 표시 해상도와 무관하게 오버레이에 정렬된다.
        double minU = (minPx + 0.5) / f.Width;
        double minV = (minPy + 0.5) / f.Height;
        return new DepthRoiStats(min, max, avg, validCount, total, ratio, minU, minV);
    }

    /// <summary>최신 깊이 프레임을 컬러라이즈 → JPEG 인코딩한다. 프레임이 없으면 null.</summary>
    public Task<byte[]?> GetLatestDepthJpegAsync(int quality, CancellationToken ct = default)
    {
        var f = _client.LatestDepth;
        if (f is null) return Task.FromResult<byte[]?>(null);
        return Task.Run<byte[]?>(() => EncodeDepth16ToJpeg(f, _settings.DepthMinMm, _settings.DepthMaxMm, quality), ct);
    }

    /// <summary>최신 IR 프레임을 그레이스케일 JPEG 으로 인코딩한다. 프레임이 없으면 null.
    /// <c>"ir8"</c> 은 8bit 그대로, <c>"ir16"</c> 은 프레임 최대값 기준 오토게인으로 8bit 축소.</summary>
    public Task<byte[]?> GetLatestIrJpegAsync(int quality, CancellationToken ct = default)
    {
        var f = _client.LatestIr;
        if (f is null) return Task.FromResult<byte[]?>(null);
        return Task.Run<byte[]?>(() => EncodeIrToJpeg(f, quality), ct);
    }

    private static byte[] EncodeRgb24ToJpeg(CameraFrame f, int quality)
    {
        // Pixels 는 RGB888 row-major. ImageSharp 의 Image<Rgb24>.LoadPixelData 로 직접 래핑.
        using var img = Image.LoadPixelData<Rgb24>(f.Pixels, f.Width, f.Height);
        using var ms = new MemoryStream();
        img.Save(ms, new JpegEncoder { Quality = Math.Clamp(quality, 1, 100) });
        return ms.ToArray();
    }

    private static byte[] EncodeDepth16ToJpeg(CameraFrame f, int minMm, int maxMm, int quality)
    {
        // depth16: little-endian uint16, 0=무효(검정 처리). [minMm, maxMm] 범위를 cold→hot 으로.
        var span = MemoryMarshal.Cast<byte, ushort>(f.Pixels);
        var rgb = new byte[f.Width * f.Height * 3];
        var range = Math.Max(1, maxMm - minMm);

        for (int i = 0; i < span.Length; i++)
        {
            int d = span[i];
            if (d == 0)
            {
                // 무효 픽셀: 검정 (기본값이라 별도 처리 불필요).
                continue;
            }
            // [0,1] 로 정규화. 가까울수록 작은 t.
            double t = Math.Clamp((double)(d - minMm) / range, 0.0, 1.0);
            ColorizeColdHot(t, out var r, out var g, out var b);
            int o = i * 3;
            rgb[o] = r; rgb[o + 1] = g; rgb[o + 2] = b;
        }

        using var img = Image.LoadPixelData<Rgb24>(rgb, f.Width, f.Height);
        using var ms = new MemoryStream();
        img.Save(ms, new JpegEncoder { Quality = Math.Clamp(quality, 1, 100) });
        return ms.ToArray();
    }

    /// <summary>
    /// IR 그레이스케일 → JPEG. ir8: 1바이트=1픽셀 그대로. ir16: little-endian uint16 이며 IR 강도의
    /// 실제 동적범위(8/10/16bit)가 카메라마다 달라, 프레임 내 최대값을 기준으로 8bit 로 오토게인한다.
    /// </summary>
    private static byte[] EncodeIrToJpeg(CameraFrame f, int quality)
    {
        int n = f.Width * f.Height;
        var gray = new byte[n];

        if (f.PixelFormat == "ir16")
        {
            var span = MemoryMarshal.Cast<byte, ushort>(f.Pixels);
            int count = Math.Min(n, span.Length);
            int max = 1;
            for (int i = 0; i < count; i++) if (span[i] > max) max = span[i];
            // 최대값을 255 로 맞추는 선형 스케일. (max 가 작으면 어두운 IR 도 또렷하게.)
            for (int i = 0; i < count; i++)
                gray[i] = (byte)(span[i] * 255 / max);
        }
        else // "ir8" 등 8bit 단일 채널
        {
            int count = Math.Min(n, f.Pixels.Length);
            Array.Copy(f.Pixels, gray, count);
        }

        using var img = Image.LoadPixelData<L8>(gray, f.Width, f.Height);
        using var ms = new MemoryStream();
        img.Save(ms, new JpegEncoder { Quality = Math.Clamp(quality, 1, 100) });
        return ms.ToArray();
    }

    /// <summary>
    /// t∈[0,1] 을 cold(파랑)→cyan→green→yellow→red(hot) 5단 그라디언트로 매핑. 1차 구현용 단순 LUT,
    /// 필요시 turbo/jet 으로 교체.
    /// </summary>
    private static void ColorizeColdHot(double t, out byte r, out byte g, out byte b)
    {
        // 가까울수록(=t 작음) 따뜻한 색, 멀수록 차가운 색이 직관적이라 t 를 반전.
        double u = 1.0 - t;
        // 5단계 보간.
        double x = u * 4.0;
        int seg = Math.Min(3, (int)x);
        double f = x - seg;
        (double R, double G, double B) c0, c1;
        switch (seg)
        {
            case 0: c0 = (0, 0, 0.5); c1 = (0, 1, 1); break;     // 차가운 끝 → cyan
            case 1: c0 = (0, 1, 1);  c1 = (0, 1, 0); break;       // cyan → green
            case 2: c0 = (0, 1, 0);  c1 = (1, 1, 0); break;       // green → yellow
            default: c0 = (1, 1, 0); c1 = (1, 0, 0); break;        // yellow → hot 끝
        }
        r = (byte)Math.Clamp((c0.R + (c1.R - c0.R) * f) * 255, 0, 255);
        g = (byte)Math.Clamp((c0.G + (c1.G - c0.G) * f) * 255, 0, 255);
        b = (byte)Math.Clamp((c0.B + (c1.B - c0.B) * f) * 255, 0, 255);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_client.IsStreaming) await _client.StopStreamAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "종료 중 스트림 정지 실패");
        }
        _client.Disconnect();
        _logger.LogInformation("CameraService 종료");
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _client.Dispose();
        base.Dispose();
    }
}
