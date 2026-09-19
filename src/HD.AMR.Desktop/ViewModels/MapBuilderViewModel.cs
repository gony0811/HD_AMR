using System.Text.Json;
using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.AMR.App.Communication;
using HD.AMR.App.Models;
using Microsoft.Extensions.Options;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>실시간 맵 캐시와 현재 포즈 기준 라이다를 표시하며 신규 맵 작성을 제어한다.</summary>
public sealed partial class MapBuilderViewModel : ViewModelBase
{
    private readonly AmrRestClient _rest;
    private readonly AmrRestSettings _settings;
    private readonly DispatcherTimer _timer;
    private int _refreshBusy;
    private string? _lastCachePayload;
    private (int Width, int Height) _originEstimatedFor;

    public AmrMapViewModel Map { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLiveMap))]
    [NotifyPropertyChangedFor(nameof(ImageWidth))]
    [NotifyPropertyChangedFor(nameof(ImageHeight))]
    [NotifyPropertyChangedFor(nameof(CanvasWidth))]
    [NotifyPropertyChangedFor(nameof(CanvasHeight))]
    private Bitmap? _liveMapImage;
    [ObservableProperty] private string _cacheStatus = "실시간 맵 수신 대기";
    [ObservableProperty] private double _zoom = 1.5;
    [ObservableProperty] private IReadOnlyList<AmrLidarPoint> _liveLidarPoints = Array.Empty<AmrLidarPoint>();
    [ObservableProperty] private string _liveLidarStatus = "작성용 라이다 수신 대기";
    [ObservableProperty] private double _liveOriginX;
    [ObservableProperty] private double _liveOriginY;
    [ObservableProperty] private bool _autoCenterOrigin = true;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadSelectedMapCommand))]
    private StoredMapItem? _selectedStoredMap;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshMapListCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadSelectedMapCommand))]
    private bool _mapListBusy;
    [ObservableProperty] private string _mapListStatus = "저장 맵 목록 수신 대기";

    public ObservableCollection<StoredMapItem> StoredMaps { get; } = new();

    public MapBuilderViewModel(AmrRestClient rest, AmrMapViewModel map, IOptions<AmrRestSettings> settings)
    {
        _rest = rest;
        Map = map;
        _settings = settings.Value;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    public bool HasLiveMap => LiveMapImage is not null;
    public double ImageWidth => LiveMapImage?.PixelSize.Width ?? 1;
    public double ImageHeight => LiveMapImage?.PixelSize.Height ?? 1;
    public double CanvasWidth => ImageWidth * Zoom;
    public double CanvasHeight => ImageHeight * Zoom;
    public string ZoomText => $"{Zoom * 100:F0}%";

    partial void OnZoomChanged(double value)
    {
        var clamped = Math.Clamp(value, .5, 5);
        if (Math.Abs(clamped - value) > .0001) { Zoom = clamped; return; }
        OnPropertyChanged(nameof(CanvasWidth));
        OnPropertyChanged(nameof(CanvasHeight));
        OnPropertyChanged(nameof(ZoomText));
    }

    partial void OnAutoCenterOriginChanged(bool value)
    {
        if (value) UpdateEstimatedOrigin(force: true);
    }

    /// <summary>현재 포즈를 이미지 중앙으로 보고 원점을 다시 추정한다.</summary>
    [RelayCommand]
    private void ReestimateOrigin()
    {
        if (AutoCenterOrigin) UpdateEstimatedOrigin(force: true);
        else AutoCenterOrigin = true;
    }

    /// <summary>저장 맵과 같은 좌하단 (0,0) 원점을 사용한다.</summary>
    [RelayCommand]
    private void ZeroOrigin()
    {
        AutoCenterOrigin = false;
        LiveOriginX = 0;
        LiveOriginY = 0;
    }

    [RelayCommand] private void ZoomIn() => Zoom = Math.Min(5, Zoom + .25);
    [RelayCommand] private void ZoomOut() => Zoom = Math.Max(.5, Zoom - .25);
    [RelayCommand] private void ResetZoom() => Zoom = 1;

    private bool CanRefreshMapList() => !MapListBusy;
    private bool CanLoadSelectedMap() => !MapListBusy && SelectedStoredMap is not null;

    [RelayCommand(CanExecute = nameof(CanRefreshMapList))]
    private async Task RefreshMapListAsync()
    {
        MapListBusy = true;
        try
        {
            var result = await _rest.GetMapListAsync();
            if (!result.Ok || result.Data is not { ValueKind: JsonValueKind.Array } maps)
                throw new InvalidOperationException(result.Message ?? "맵 목록 응답이 올바르지 않습니다.");
            var selectedName = SelectedStoredMap?.Name ?? Map.MapName;
            var items = new List<StoredMapItem>();
            foreach (var item in maps.EnumerateArray())
            {
                if (!TryReadString(item, "name", out var name)) continue;
                TryReadString(item, "size", out var size);
                TryReadString(item, "time", out var time);
                items.Add(new StoredMapItem(name, size, time));
            }
            StoredMaps.Clear();
            foreach (var item in items) StoredMaps.Add(item);
            SelectedStoredMap = StoredMaps.FirstOrDefault(x => x.Name == selectedName) ?? StoredMaps.FirstOrDefault();
            MapListStatus = $"저장된 맵 {StoredMaps.Count}개 · 갱신 {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex) { MapListStatus = $"맵 목록 조회 실패: {ex.Message}"; }
        finally { MapListBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanLoadSelectedMap))]
    private async Task LoadSelectedMapAsync()
    {
        if (SelectedStoredMap is not { } selected) return;
        if (Map.IsScanning)
        {
            MapListStatus = "스캔을 종료한 뒤 저장 맵을 불러오세요.";
            return;
        }
        MapListBusy = true;
        MapListStatus = $"{selected.Name} 적용 중…";
        try
        {
            var result = await _rest.LoadMapAsync(selected.Name);
            if (!result.Ok)
                throw new InvalidOperationException(result.Message ?? $"맵 로드 API 오류 ({result.Code})");
            Map.MapName = selected.Name;
            Map.SetActiveMap(selected.Name);
            await Map.LoadMapCommand.ExecuteAsync(null);
            await Map.RefreshTelemetryAsync();
            MapListStatus = $"운영 맵 적용 완료 {DateTime.Now:HH:mm:ss} · {selected.Name}";
        }
        catch (Exception ex) { MapListStatus = $"맵 적용 실패: {ex.Message}"; }
        finally { MapListBusy = false; }
    }

    public override void OnActivated()
    {
        Map.PropertyChanged += OnMapPropertyChanged;
        _timer.Start();
        _ = RefreshAsync();
        _ = RefreshMapListAsync();
    }

    public override void OnDeactivated()
    {
        _timer.Stop();
        Map.PropertyChanged -= OnMapPropertyChanged;
    }

    private void OnMapPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // 새 스캔이 시작되면 SLAM 좌표계가 바뀌므로 다음 프레임에서 원점을 다시 추정한다.
        if (e.PropertyName == nameof(AmrMapViewModel.IsScanning) && Map.IsScanning)
            _originEstimatedFor = default;
    }

    private async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _refreshBusy, 1) != 0) return;
        try
        {
            // 스캔 중 /robot/pose.lidar가 이전 맵 좌표로 남는 펌웨어 동작이 있어
            // pose를 먼저 받고 원시 각도/거리를 현재 포즈 기준으로 직접 변환한다.
            await Map.RefreshTelemetryAsync();
            await Task.WhenAll(RefreshRawLidarAsync(), RefreshMapCacheAsync());
        }
        finally { Volatile.Write(ref _refreshBusy, 0); }
    }

    private async Task RefreshMapCacheAsync()
    {
        try
        {
            var result = await _rest.GetMapCacheAsync();
            if (!result.Ok || result.Data is not { ValueKind: JsonValueKind.String } data)
                throw new InvalidOperationException(result.Message ?? "실시간 맵 응답이 올바르지 않습니다.");
            var encoded = data.GetString()?.Trim();
            if (string.IsNullOrEmpty(encoded)) throw new InvalidOperationException("실시간 맵 이미지가 비어 있습니다.");
            var comma = encoded.IndexOf(',');
            if (encoded.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) && comma >= 0)
                encoded = encoded[(comma + 1)..];
            if (encoded == _lastCachePayload && LiveMapImage is not null)
            {
                UpdateEstimatedOrigin();
                return; // 변화 없는 프레임은 다시 디코딩하지 않는다.
            }
            // 수 MB base64/PNG 디코딩을 UI 스레드에서 하면 500ms마다 화면이 끊긴다.
            var bitmap = await Task.Run(() =>
            {
                var bytes = Convert.FromBase64String(encoded);
                if (!IsCompleteImage(bytes))
                    throw new InvalidOperationException("기록 중 잘린 프레임");
                using var stream = new MemoryStream(bytes);
                return new Bitmap(stream);
            });
            _lastCachePayload = encoded;
            var old = LiveMapImage;
            LiveMapImage = bitmap;
            UpdateEstimatedOrigin();
            CacheStatus = $"AMR 원본 캐시 {bitmap.PixelSize.Width}×{bitmap.PixelSize.Height}px · {DateTime.Now:HH:mm:ss.fff}";
            old?.Dispose();
        }
        catch (Exception ex)
        {
            CacheStatus = $"실시간 맵 조회 실패: {ex.Message}" + (HasLiveMap ? " · 이전 프레임 유지" : "");
        }
    }

    /// <summary>
    /// AMR가 캐시 PNG를 쓰는 도중 읽히면 끝이 잘린 파일이 온다. Skia는 이를 예외 없이
    /// 아래쪽이 비거나 깨진 이미지로 디코딩하므로 PNG 종료 청크(IEND)를 직접 확인한다.
    /// </summary>
    private static bool IsCompleteImage(byte[] bytes)
    {
        ReadOnlySpan<byte> pngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (bytes.Length < 8 || !bytes.AsSpan(0, 8).SequenceEqual(pngSignature)) return bytes.Length > 0;
        ReadOnlySpan<byte> iend = [0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82];
        return bytes.Length >= 20 && bytes.AsSpan(bytes.Length - 8).SequenceEqual(iend);
    }

    private async Task RefreshRawLidarAsync()
    {
        try
        {
            if (!Map.HasTelemetryPose) throw new InvalidOperationException("현재 포즈가 없습니다.");
            var result = await _rest.GetLidarAsync();
            if (!result.Ok || result.Data is not { ValueKind: JsonValueKind.Array } groups)
                throw new InvalidOperationException(result.Message ?? "라이다 원시 응답이 올바르지 않습니다.");

            var points = new List<AmrLidarPoint>();
            var robotCos = Math.Cos(Map.RobotHeading);
            var robotSin = Math.Sin(Map.RobotHeading);
            foreach (var group in groups.EnumerateArray())
            {
                if (group.ValueKind != JsonValueKind.Array || group.GetArrayLength() == 0) continue;
                foreach (var sample in group.EnumerateArray())
                {
                    if (!ReadNumber(sample, "angle", out var angle) || !ReadNumber(sample, "range", out var range) ||
                        range <= .02 || range > 50) continue;
                    var localX = _settings.LidarOffsetX + range * Math.Cos(_settings.LidarOffsetRz + angle);
                    var localY = _settings.LidarOffsetY + range * Math.Sin(_settings.LidarOffsetRz + angle);
                    points.Add(new AmrLidarPoint(
                        Map.RobotX + robotCos * localX - robotSin * localY,
                        Map.RobotY + robotSin * localX + robotCos * localY));
                }
                break; // 현재 장비는 첫 번째 라이다만 활성화되어 있다.
            }
            LiveLidarPoints = points;
            LiveLidarStatus = $"현재 포즈로 재계산한 라이다 {points.Count:N0}점";
        }
        catch (Exception ex)
        {
            LiveLidarPoints = Array.Empty<AmrLidarPoint>();
            LiveLidarStatus = $"작성용 라이다 조회 실패: {ex.Message}";
        }
    }

    private void UpdateEstimatedOrigin(bool force = false)
    {
        if (!AutoCenterOrigin || LiveMapImage is null || Map.ResolutionValue <= 0 || !Map.HasTelemetryPose) return;
        // /map/cache에는 origin이 없어 현재 포즈를 이미지 중앙으로 추정한다. 매 프레임 다시 추정하면
        // 로봇은 항상 중앙에 고정되고 라이다만 맵과 어긋나 움직이므로, 이미지 크기가 바뀌거나
        // 새 스캔이 시작될 때만 한 번 추정해 고정한다.
        var size = (LiveMapImage.PixelSize.Width, LiveMapImage.PixelSize.Height);
        if (!force && _originEstimatedFor == size) return;
        _originEstimatedFor = size;
        LiveOriginX = Map.RobotX - LiveMapImage.PixelSize.Width * Map.ResolutionValue / 2;
        LiveOriginY = Map.RobotY - LiveMapImage.PixelSize.Height * Map.ResolutionValue / 2;
    }

    private static bool ReadNumber(JsonElement element, string name, out double value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number &&
               property.TryGetDouble(out value) && double.IsFinite(value);
    }

    private static bool TryReadString(JsonElement element, string name, out string value)
    {
        value = "";
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString() ?? "";
        return value.Length > 0;
    }

    public override void Dispose()
    {
        _timer.Stop();
        LiveMapImage?.Dispose();
    }
}

public sealed record StoredMapItem(string Name, string Size, string Time);
