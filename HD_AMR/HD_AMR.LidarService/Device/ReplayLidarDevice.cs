using System.Text.Json;
using HD_AMR.Contracts.Lidar;

namespace HD_AMR.LidarService.Device;

/// <summary>
/// 젯슨에서 수집한 샘플 프레임 덤프를 재생하는 <see cref="ILidarDevice"/> 구현.
///
/// <b>존재 이유는 하드웨어 없이 개발하기 위해서다.</b> 능선 검출 알고리즘은 반복 수정이
/// 필연적인데, 그때마다 코봇 팔 끝에 센서가 달린 장비 앞에 붙어 있을 수는 없다. 실제
/// 센서 데이터를 파일로 재생하면 알고리즘 개발과 회귀 테스트를 Windows 에서 그대로 할 수 있다.
///
/// 덤프 형식(젯슨 <c>p_scene.cpp</c> 가 생성):
/// <code>
/// scene_NN/
///   meta.json        장면 설명 + 센서 설정
///   dist_00..09.bin  int32 LE, width x height, row-major
///   ampl_00..09.bin  int32 LE, 동일
///   xyz_00..02.bin   double LE, X 전체 -> Y 전체 -> Z 전체
/// </code>
///
/// ⚠ 덤프의 배열은 라이브러리 메모리 레이아웃과 달리 <b>stride 가 800 이 아니라 width</b> 다.
///   유효 영역만 잘라 저장했기 때문이다.
/// </summary>
internal sealed class ReplayLidarDevice : ILidarDevice
{
    private readonly ReplayDeviceOptions _options;
    private readonly ILogger<ReplayLidarDevice> _log;

    private readonly List<LidarCapture> _frames = new();
    private int _cursor;
    private LidarConfig _config = new();

    public ReplayLidarDevice(ReplayDeviceOptions options, ILogger<ReplayLidarDevice> log)
    {
        _options = options;
        _log = log;
    }

    public bool IsOpen { get; private set; }

    public LidarDeviceInfo? Info { get; private set; }

    public void Open()
    {
        if (IsOpen) return;

        var dir = _options.ScenePath;
        if (!Directory.Exists(dir))
            throw new LidarDeviceException($"덤프 장면 폴더를 찾을 수 없다: {dir}");

        var meta = LoadMeta(Path.Combine(dir, "meta.json"));
        LoadFrames(dir, meta.Width, meta.Height);

        if (_frames.Count == 0)
            throw new LidarDeviceException($"재생할 프레임이 없다: {dir}");

        Info = new LidarDeviceInfo
        {
            Model = $"{meta.Device} (replay: {Path.GetFileName(dir)})",
            LidarType = NslNative.LidarTypeOption.TypeA,
            LensType = LidarLensType.StandardField,
            FirmwareRelease = 0,
            ChipId = 0,
            SdkVersion = "replay",
            Width = meta.Width,
            Height = meta.Height,
        };

        _config = new LidarConfig
        {
            IntegrationTime3D = meta.IntegrationTime3D,
            MinAmplitude = meta.MinAmplitude,
            Roi = new RoiRect { XMin = 0, YMin = 0, XMax = meta.Width - 1, YMax = meta.Height - 1 },
        };

        IsOpen = true;
        _log.LogInformation(
            "덤프 재생 시작: {Scene} ({Frames}프레임, {W}x{H}) — {Desc}",
            Path.GetFileName(dir), _frames.Count, meta.Width, meta.Height, meta.Description);
    }

    public void StartStreaming() { }

    public void StopStreaming() { }

    /// <summary>
    /// 프레임을 순환 재생한다. 실제 센서처럼 매번 다른 프레임을 주므로 다중 프레임 평균
    /// 경로도 그대로 검증된다.
    /// </summary>
    public LidarCapture? Capture(int timeoutMs)
    {
        if (!IsOpen) throw new LidarDeviceException("재생 장치가 열려 있지 않다.");

        var frame = _frames[_cursor];
        _cursor = (_cursor + 1) % _frames.Count;

        // 캡처 시각은 재생 시점으로 갱신한다. 원본 수집 시각을 쓰면 신선도 검사가
        // 항상 실패한다.
        return frame with { CapturedAt = DateTimeOffset.UtcNow };
    }

    public LidarConfig ReadConfig() => _config;

    public void ApplyConfig(LidarConfigPatch patch) =>
        _log.LogInformation("재생 장치는 설정 변경을 무시한다.");

    public void PersistConfig() =>
        _log.LogInformation("재생 장치는 설정 저장을 무시한다.");

    public LidarStatus ReadStatus() => new()
    {
        Connected = IsOpen,
        LinkUp = null,
        Model = Info?.Model,
        SdkVersion = "replay",
        LensType = LidarLensType.StandardField,
        MeasuredFps = 15.0,
        LastFrameAt = DateTimeOffset.UtcNow,
    };

    public void Close()
    {
        IsOpen = false;
        _frames.Clear();
        _cursor = 0;
    }

    public void Dispose() => Close();

    private void LoadFrames(string dir, int width, int height)
    {
        var n = width * height;

        // xyz 는 dist/ampl 보다 적게 저장되어 있다(용량 때문에 3프레임만). 3D 좌표 없이는
        // 능선 검출을 할 수 없으므로 xyz 가 있는 프레임만 재생 대상으로 삼는다.
        for (int i = 0; ; i++)
        {
            var distPath = Path.Combine(dir, $"dist_{i:D2}.bin");
            var amplPath = Path.Combine(dir, $"ampl_{i:D2}.bin");
            var xyzPath = Path.Combine(dir, $"xyz_{i:D2}.bin");

            if (!File.Exists(distPath) || !File.Exists(amplPath) || !File.Exists(xyzPath)) break;

            var dist = ReadInt32(distPath, n);
            var ampl = ReadInt32(amplPath, n);
            var (x, y, z) = ReadXyz(xyzPath, n);

            var unfilled = dist.Count(d => d == 0);

            _frames.Add(new LidarCapture
            {
                Width = width,
                Height = height,
                CapturedAt = DateTimeOffset.UtcNow,
                TemperatureC = 0,
                Distance2D = dist,
                Amplitude = ampl,
                X = x,
                Y = y,
                Z = z,
                IsComplete = unfilled == 0,
            });
        }

        var distCount = Directory.GetFiles(dir, "dist_*.bin").Length;
        if (distCount > _frames.Count)
        {
            _log.LogInformation(
                "거리 프레임은 {Dist}개지만 3D 좌표가 {Xyz}개뿐이라 그만큼만 재생한다. " +
                "덤프가 용량 때문에 xyz 를 일부만 저장한 결과다.",
                distCount, _frames.Count);
        }
    }

    private static int[] ReadInt32(string path, int count)
    {
        var bytes = File.ReadAllBytes(path);
        var expected = count * sizeof(int);
        if (bytes.Length != expected)
            throw new LidarDeviceException($"{Path.GetFileName(path)} 크기가 {bytes.Length}바이트다. {expected} 기대.");

        var result = new int[count];
        Buffer.BlockCopy(bytes, 0, result, 0, expected);
        return result;
    }

    private static (double[] X, double[] Y, double[] Z) ReadXyz(string path, int count)
    {
        var bytes = File.ReadAllBytes(path);
        var expected = count * sizeof(double) * 3;
        if (bytes.Length != expected)
            throw new LidarDeviceException($"{Path.GetFileName(path)} 크기가 {bytes.Length}바이트다. {expected} 기대.");

        var x = new double[count];
        var y = new double[count];
        var z = new double[count];

        var planeBytes = count * sizeof(double);
        Buffer.BlockCopy(bytes, 0, x, 0, planeBytes);
        Buffer.BlockCopy(bytes, planeBytes, y, 0, planeBytes);
        Buffer.BlockCopy(bytes, planeBytes * 2, z, 0, planeBytes);

        return (x, y, z);
    }

    private static SceneMeta LoadMeta(string path)
    {
        if (!File.Exists(path))
            throw new LidarDeviceException($"meta.json 을 찾을 수 없다: {path}");

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var frame = root.GetProperty("frame");
        var config = root.GetProperty("config");

        return new SceneMeta(
            Width: frame.GetProperty("width").GetInt32(),
            Height: frame.GetProperty("height").GetInt32(),
            Device: root.TryGetProperty("device", out var d) ? d.GetString() ?? "unknown" : "unknown",
            Description: root.TryGetProperty("target", out var t) && t.TryGetProperty("description", out var desc)
                ? desc.GetString() ?? "" : "",
            IntegrationTime3D: config.TryGetProperty("integrationTime3D", out var it) ? it.GetInt32() : 0,
            MinAmplitude: config.TryGetProperty("minAmplitude", out var ma) ? ma.GetInt32() : 0);
    }

    private readonly record struct SceneMeta(
        int Width, int Height, string Device, string Description, int IntegrationTime3D, int MinAmplitude);
}

internal sealed class ReplayDeviceOptions
{
    /// <summary>재생할 장면 폴더(예: <c>C:\Project\HD_AMR_Data\dump\scene_04</c>).</summary>
    public string ScenePath { get; set; } = "";
}
