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
    [ObservableProperty] private string _mapName;
    [ObservableProperty] private Bitmap? _mapImage;
    [ObservableProperty] private string _mapLoadStatus = "맵을 가져오세요.";
    [ObservableProperty] private string _mapSummary = "";
    [ObservableProperty] private decimal? _resolution;
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
    }

    public bool HasMapImage => MapImage is not null;
    public double ImageWidth => MapImage?.PixelSize.Width ?? 1;
    public double ImageHeight => MapImage?.PixelSize.Height ?? 1;
    public string MapRequestTarget => $"{_settings.BaseUrl.TrimEnd('/')}/{_settings.MapContentPath.Trim('/')}/{{맵 이름}}";
    partial void OnResolutionChanged(decimal? value) => UpdatePose(_pose);

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
        PositionStatus = "● AMR 현재 위치 · 화살표는 진행 방향 · 1초 갱신";
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
            // The robot's current response omits resolution. Never guess a scale.
            Resolution = ReadNumber(data, "resolution", out var scale) && scale > 0 && scale <= 1000
                ? (decimal)scale : name == _loadedMapName ? Resolution : null;
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
