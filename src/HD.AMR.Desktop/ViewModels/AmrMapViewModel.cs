using System.Text.Json;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.AMR.App.Communication;
using HD.AMR.App.Models;
using Microsoft.Extensions.Options;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>Shared map snapshot for Settings and Dashboard. Accessed on the UI thread.</summary>
public sealed partial class AmrMapViewModel : ObservableObject, IDisposable
{
    private readonly AmrRestClient _rest;
    private readonly AmrRestSettings _settings;
    private double _originX, _originY;
    private bool _hasOrigin;
    private RobotPose? _pose;
    private string? _loadedMapName;
    private int _telemetryBusy;
    [ObservableProperty] private string _mapName;
    [ObservableProperty] private Bitmap? _mapImage;
    [ObservableProperty] private string _mapLoadStatus = "맵을 가져오세요.";
    [ObservableProperty] private string _mapSummary = "";
    [ObservableProperty] private decimal? _resolution;
    [ObservableProperty] private double _zoom = 1.5;
    [ObservableProperty] private IReadOnlyList<AmrLidarPoint> _lidarPoints = Array.Empty<AmrLidarPoint>();
    [ObservableProperty] private string _lidarStatus = "라이다 수신 대기";
    [ObservableProperty] private string _confidenceText = "맵 신뢰도 --";
    [ObservableProperty] private double _confidencePercent;
    [ObservableProperty] private bool _hasConfidence;
    [ObservableProperty] private bool _showRobot;
    [ObservableProperty] private double _robotLeft;
    [ObservableProperty] private double _robotTop;
    [ObservableProperty] private double _headingDegrees;
    [ObservableProperty] private string _poseText = "AMR 위치 수신 대기";
    [ObservableProperty] private string _positionStatus = "맵 수신 대기";

    public AmrMapViewModel(AmrRestClient rest, IOptions<AmrRestSettings> settings)
    {
        _rest = rest;
        _settings = settings.Value;
        _mapName = _settings.MapName;
        _resolution = _settings.MapResolution > 0 ? (decimal)_settings.MapResolution : null;
    }

    public bool HasMapImage => MapImage is not null;
    public double ImageWidth => MapImage?.PixelSize.Width ?? 1;
    public double ImageHeight => MapImage?.PixelSize.Height ?? 1;
    public double CanvasWidth => ImageWidth * Zoom;
    public double CanvasHeight => ImageHeight * Zoom;
    public string ZoomText => $"{Zoom * 100:F0}%";
    public double OriginX => _originX;
    public double OriginY => _originY;
    public double RobotX => _pose?.X ?? 0;
    public double RobotY => _pose?.Y ?? 0;
    public double RobotHeading => _pose?.Angle ?? 0;
    public double ResolutionValue => (double)(Resolution ?? 0);
    public string MapRequestTarget => $"{_settings.BaseUrl.TrimEnd('/')}/{_settings.MapContentPath.Trim('/')}/{{맵 이름}}";
    partial void OnResolutionChanged(decimal? value)
    {
        OnPropertyChanged(nameof(ResolutionValue));
        UpdatePose(_pose);
    }
    partial void OnZoomChanged(double value)
    {
        var clamped = Math.Clamp(value, 0.5, 5.0);
        if (Math.Abs(clamped - value) > .0001) { Zoom = clamped; return; }
        OnPropertyChanged(nameof(CanvasWidth));
        OnPropertyChanged(nameof(CanvasHeight));
        OnPropertyChanged(nameof(ZoomText));
    }

    [RelayCommand] private void ZoomIn() => Zoom = Math.Min(5, Zoom + .25);
    [RelayCommand] private void ZoomOut() => Zoom = Math.Max(.5, Zoom - .25);
    [RelayCommand] private void ResetZoom() => Zoom = 1.0;

    public void UpdatePose(RobotPose? pose)
    {
        _pose = pose;
        ShowRobot = false;
        PoseText = pose is null ? "AMR 위치 수신 대기 / 연결 확인" :
            $"X {pose.X:F3} m · Y {pose.Y:F3} m · 방향 {pose.Angle * 180 / Math.PI:F1}°";
        if (!HasMapImage) { PositionStatus = "맵 수신 대기"; return; }
        if (!_hasOrigin) { PositionStatus = "맵 원점 정보가 없어 위치를 표시할 수 없습니다."; return; }
        if (Resolution is not > 0) { PositionStatus = "맵 해상도(m/픽셀)를 입력하면 위치가 표시됩니다."; return; }
        if (pose is null) { PositionStatus = "AMR 위치 수신 대기 / 연결 확인"; return; }
        if (!MapProjection.TryProject(pose, (double)Resolution.Value, _originX, _originY,
                (int)ImageWidth, (int)ImageHeight, out var x, out var y, out var angle))
        {
            PositionStatus = "위치가 맵 밖이거나 유효하지 않습니다. 맵 이름과 해상도를 확인하세요.";
            return;
        }
        RobotLeft = x - 8;
        RobotTop = y - 8;
        HeadingDegrees = angle;
        ShowRobot = true;
        OnPropertyChanged(nameof(RobotX));
        OnPropertyChanged(nameof(RobotY));
        OnPropertyChanged(nameof(RobotHeading));
        PositionStatus = "● AMR 현재 위치 · 빨간 점은 라이다 반사점 · 1초 갱신";
    }

    public async Task RefreshTelemetryAsync()
    {
        if (Interlocked.Exchange(ref _telemetryBusy, 1) != 0) return;
        try
        {
            var result = await _rest.GetPoseAsync();
            if (!result.Ok || result.Data is not { ValueKind: JsonValueKind.Object } data)
                throw new InvalidOperationException(result.Message ?? "포즈 응답이 올바르지 않습니다.");
            if (!ReadNumber(data, "x", out var x) || !ReadNumber(data, "y", out var y) ||
                !ReadNumber(data, "rz", out var heading))
                throw new InvalidOperationException("포즈 좌표가 없습니다.");

            UpdatePose(new RobotPose((float)x, (float)y, (float)heading));
            var points = new List<AmrLidarPoint>();
            if (data.TryGetProperty("lidar", out var lidar) && lidar.ValueKind == JsonValueKind.Array)
            {
                points.Capacity = lidar.GetArrayLength();
                foreach (var item in lidar.EnumerateArray())
                    if (ReadNumber(item, "x", out var lx) && ReadNumber(item, "y", out var ly))
                        points.Add(new AmrLidarPoint(lx, ly));
            }
            LidarPoints = points;
            LidarStatus = $"라이다 {points.Count:N0}점 · 수신 {DateTime.Now:HH:mm:ss}";

            if (ReadNumber(data, "fit", out var fit) && fit >= 0)
            {
                ConfidencePercent = Math.Clamp(fit * 100, 0, 100);
                ConfidenceText = $"맵 신뢰도 {ConfidencePercent:F1}%";
                HasConfidence = true;
            }
            else
            {
                ConfidenceText = "맵 신뢰도 --";
                HasConfidence = false;
            }
        }
        catch (Exception ex)
        {
            LidarStatus = $"라이다 조회 실패: {ex.Message}";
            LidarPoints = Array.Empty<AmrLidarPoint>();
            ShowRobot = false;
            ConfidenceText = "맵 신뢰도 --";
            HasConfidence = false;
        }
        finally { Volatile.Write(ref _telemetryBusy, 0); }
    }

    [RelayCommand]
    private async Task LoadMapAsync()
    {
        var name = MapName.Trim();
        MapLoadStatus = "맵을 가져오는 중…";
        try
        {
            var result = await _rest.GetMapContentAsync(name);
            if (!result.Ok || result.Data is not { ValueKind: JsonValueKind.Object } data)
                throw new InvalidOperationException(result.Message ?? "맵 응답이 올바르지 않습니다.");
            if (!data.TryGetProperty("image", out var image) || image.ValueKind != JsonValueKind.String)
                if (!data.TryGetProperty("mapping", out image) || image.ValueKind != JsonValueKind.String)
                    throw new InvalidOperationException("응답에 image(base64 PNG) 필드가 없습니다.");
            var encoded = image.GetString()!.Trim();
            const string prefix = "data:image/png;base64,";
            if (encoded.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) encoded = encoded[prefix.Length..];
            using var stream = new MemoryStream(Convert.FromBase64String(encoded));
            var bitmap = new Bitmap(stream);
            _hasOrigin = data.TryGetProperty("origin", out var origin) && origin.ValueKind == JsonValueKind.Object &&
                ReadNumber(origin, "x", out _originX) && ReadNumber(origin, "y", out _originY);
            OnPropertyChanged(nameof(OriginX));
            OnPropertyChanged(nameof(OriginY));
            // The robot's current response omits resolution. Never guess a scale.
            Resolution = ReadNumber(data, "resolution", out var scale) && scale > 0 && scale <= 1000
                ? (decimal)scale : name == _loadedMapName ? Resolution
                : _settings.MapResolution > 0 ? (decimal)_settings.MapResolution : null;
            _loadedMapName = name;
            var old = MapImage;
            MapImage = bitmap;
            var nodeCount = data.TryGetProperty("node", out var nodes) && nodes.ValueKind == JsonValueKind.Array
                ? nodes.GetArrayLength() : 0;
            var courseCount = data.TryGetProperty("course", out var courses) && courses.ValueKind == JsonValueKind.Array
                ? courses.GetArrayLength() : 0;
            MapSummary = $"{name} · {bitmap.PixelSize.Width}×{bitmap.PixelSize.Height}px · 노드 {nodeCount}개 · 코스 {courseCount}개";
            MapLoadStatus = $"가져옴 {DateTime.Now:HH:mm:ss}";
            OnPropertyChanged(nameof(HasMapImage));
            OnPropertyChanged(nameof(ImageWidth));
            OnPropertyChanged(nameof(ImageHeight));
            OnPropertyChanged(nameof(CanvasWidth));
            OnPropertyChanged(nameof(CanvasHeight));
            UpdatePose(_pose);
            old?.Dispose();
        }
        catch (Exception ex)
        {
            MapLoadStatus = $"맵 조회 실패: {ex.Message}" + (HasMapImage ? " · 이전 맵 표시 중" : "");
        }
    }

    private static bool ReadNumber(JsonElement element, string name, out double value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number &&
            property.TryGetDouble(out value) && double.IsFinite(value);
    }

    public void Dispose() => MapImage?.Dispose();
}
