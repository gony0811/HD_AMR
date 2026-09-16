using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.AMR.App.Service;
using Microsoft.Extensions.DependencyInjection;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>
/// RealSense D435 카메라 뷰. 기존 CameraView.razor 이식(핵심).
/// MJPEG 대신 CameraService 의 JPEG 인코더를 ~10fps 로 폴링해 Avalonia Bitmap 으로 바인딩한다.
/// 깊이 hover 프로브(GetLatestDepthMmAt)·깊이 ROI 통계(ComputeDepthRoiStats)·학습 캡처(SaveCaptureAsync)를 지원.
/// 후속(Tier 3 확장): Bead/Peak ROI 영속, IR 노출 튜닝, 평탄면 정렬 루틴, 코봇 조그 팝업.
/// </summary>
public sealed partial class CameraViewModel : ViewModelBase
{
    private readonly CameraService _svc;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _cts = new();
    private bool _polling;

    private const string CaptureDirKey = "Camera.Capture.Dir";
    private const string RoiEnabledKey = "Camera.Depth.Roi.Enabled";
    private const string RoiXKey = "Camera.Depth.Roi.X";
    private const string RoiYKey = "Camera.Depth.Roi.Y";
    private const string RoiWKey = "Camera.Depth.Roi.W";
    private const string RoiHKey = "Camera.Depth.Roi.H";

    public CameraViewModel(CameraService svc, IServiceScopeFactory scopeFactory)
    {
        _svc = svc;
        _scopeFactory = scopeFactory;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += async (_, _) => await PollAsync();
    }

    [ObservableProperty] private Bitmap? _colorImage;
    [ObservableProperty] private Bitmap? _irImage;
    [ObservableProperty] private Bitmap? _depthImage;

    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isError;

    // 캡처
    [ObservableProperty] private string _captureDir = "";
    [ObservableProperty] private int _captureCount;
    [ObservableProperty] private string? _lastCaptureInfo;

    // 깊이 ROI
    [ObservableProperty] private bool _roiEnabled;
    [ObservableProperty] private double _roiXPct;
    [ObservableProperty] private double _roiYPct;
    [ObservableProperty] private double _roiWPct;
    [ObservableProperty] private double _roiHPct;
    [ObservableProperty] private string? _statsText;
    [ObservableProperty] private string _probeText = "— mm";

    public bool IsConnected => _svc.IsConnected;
    public bool IsStreaming => _svc.IsStreaming;
    public bool IsIrActive => _svc.IsIrActive;
    public string SettingsSummary =>
        $"{_svc.Settings.ColorWidth}×{_svc.Settings.ColorHeight} @{_svc.Settings.ColorFps}fps · 깊이 {_svc.Settings.DepthMinMm}~{_svc.Settings.DepthMaxMm} mm";

    // 정규화 ROI (0~1)
    public double RoiX => Math.Clamp(RoiXPct / 100, 0, 1);
    public double RoiY => Math.Clamp(RoiYPct / 100, 0, 1);
    public double RoiW => Math.Clamp(RoiWPct / 100, 0, 1);
    public double RoiH => Math.Clamp(RoiHPct / 100, 0, 1);

    public override async void OnActivated()
    {
        _timer.Start();
        RefreshStatus();
        await LoadAsync();
    }

    public override void OnDeactivated()
    {
        _timer.Stop();
        ColorImage = IrImage = DepthImage = null;
    }

    private void RefreshStatus()
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsStreaming));
        OnPropertyChanged(nameof(IsIrActive));
    }

    private async Task LoadAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var param = scope.ServiceProvider.GetRequiredService<ParameterService>();
            CaptureDir = await param.GetAsync(CaptureDirKey) ?? "";
            RoiEnabled = await param.GetBoolAsync(RoiEnabledKey) ?? false;
            RoiXPct = Math.Round((await param.GetDoubleAsync(RoiXKey) ?? 0) * 100, 1);
            RoiYPct = Math.Round((await param.GetDoubleAsync(RoiYKey) ?? 0) * 100, 1);
            RoiWPct = Math.Round((await param.GetDoubleAsync(RoiWKey) ?? 0) * 100, 1);
            RoiHPct = Math.Round((await param.GetDoubleAsync(RoiHKey) ?? 0) * 100, 1);
        }
        catch { /* 파라미터 로드 실패는 무시(기본값 사용) */ }
    }

    private async Task PollAsync()
    {
        RefreshStatus();
        if (_polling) return;
        if (!_svc.IsStreaming)
        {
            if (ColorImage is not null) { ColorImage = IrImage = DepthImage = null; }
            return;
        }
        _polling = true;
        try
        {
            var q = _svc.Settings.JpegQuality;
            ColorImage = await DecodeAsync(() => _svc.GetLatestColorJpegAsync(q, _cts.Token)) ?? ColorImage;
            DepthImage = await DecodeAsync(() => _svc.GetLatestDepthJpegAsync(q, _cts.Token)) ?? DepthImage;
            if (_svc.IsIrActive)
                IrImage = await DecodeAsync(() => _svc.GetLatestIrJpegAsync(q, _cts.Token)) ?? IrImage;
        }
        catch { /* 프레임 디코드 오류 무시 — 다음 틱 재시도 */ }
        finally { _polling = false; }
    }

    private static async Task<Bitmap?> DecodeAsync(Func<Task<byte[]?>> getJpeg)
    {
        var bytes = await getJpeg();
        if (bytes is not { Length: > 0 }) return null;
        using var ms = new MemoryStream(bytes);
        return new Bitmap(ms);
    }

    // ── 깊이 프로브(hover) — 코드비하인드가 정규화 (u,v) 로 호출 ──
    public void ProbeAt(double u, double v)
    {
        var mm = _svc.GetLatestDepthMmAt(u, v);
        ProbeText = mm is { } m ? $"{m} mm" : "— mm";
    }

    // ── ROI 드래그(코드비하인드) → 정규화 좌표 반영 ──
    public void SetRoiFromDrag(double x, double y, double w, double h)
    {
        if (w < 0.01 || h < 0.01) return;
        RoiXPct = Math.Round(x * 100, 1);
        RoiYPct = Math.Round(y * 100, 1);
        RoiWPct = Math.Round(w * 100, 1);
        RoiHPct = Math.Round(h * 100, 1);
        RoiEnabled = true;
        OnPropertyChanged(nameof(RoiX)); OnPropertyChanged(nameof(RoiY));
        OnPropertyChanged(nameof(RoiW)); OnPropertyChanged(nameof(RoiH));
    }

    partial void OnRoiXPctChanged(double value) => NotifyRoi();
    partial void OnRoiYPctChanged(double value) => NotifyRoi();
    partial void OnRoiWPctChanged(double value) => NotifyRoi();
    partial void OnRoiHPctChanged(double value) => NotifyRoi();
    private void NotifyRoi()
    {
        OnPropertyChanged(nameof(RoiX)); OnPropertyChanged(nameof(RoiY));
        OnPropertyChanged(nameof(RoiW)); OnPropertyChanged(nameof(RoiH));
    }

    // ── 명령 ──
    [RelayCommand]
    private async Task StartStream()
    {
        Busy = true;
        try { await _svc.StartStreamAsync(_cts.Token); Notify("스트림 시작.", false); }
        catch (Exception ex) { Notify($"시작 실패: {ex.Message}", true); }
        finally { Busy = false; RefreshStatus(); }
    }

    [RelayCommand]
    private async Task StopStream()
    {
        Busy = true;
        try { await _svc.StopStreamAsync(_cts.Token); Notify("스트림 정지.", false); }
        catch (Exception ex) { Notify($"정지 실패: {ex.Message}", true); }
        finally { Busy = false; RefreshStatus(); }
    }

    [RelayCommand]
    private async Task SaveRoi()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var param = scope.ServiceProvider.GetRequiredService<ParameterService>();
            await param.SetBoolAsync(RoiEnabledKey, RoiEnabled, "깊이 ROI 사용");
            await param.SetDoubleAsync(RoiXKey, RoiX, "깊이 ROI X(0~1)");
            await param.SetDoubleAsync(RoiYKey, RoiY, "깊이 ROI Y(0~1)");
            await param.SetDoubleAsync(RoiWKey, RoiW, "깊이 ROI W(0~1)");
            await param.SetDoubleAsync(RoiHKey, RoiH, "깊이 ROI H(0~1)");
            Notify("깊이 ROI 저장 완료.", false);
        }
        catch (Exception ex) { Notify($"ROI 저장 실패: {ex.Message}", true); }
    }

    [RelayCommand]
    private void VerifyPlane()
    {
        var s = _svc.ComputeDepthRoiStats(RoiX, RoiY, RoiW, RoiH);
        StatsText = s is null
            ? "통계 없음 — 스트리밍/ROI/유효 깊이를 확인하세요."
            : $"min {s.MinMm} · avg {s.AvgMm:0} · max {s.MaxMm} mm · 유효 {s.ValidRatio * 100:0}% ({s.ValidCount}/{s.TotalCount})";
    }

    [RelayCommand]
    private async Task Capture()
    {
        if (string.IsNullOrWhiteSpace(CaptureDir)) { Notify("먼저 저장 폴더를 선택하세요.", true); return; }
        Busy = true;
        try
        {
            var r = await _svc.SaveCaptureAsync(CaptureDir, _cts.Token);
            CaptureCount++;
            LastCaptureInfo = $"{r.Timestamp} · {r.Files.Count}개 파일";
            Notify($"캡처 저장: {r.Dir}", false);
        }
        catch (Exception ex) { Notify($"캡처 실패: {ex.Message}", true); }
        finally { Busy = false; }
    }

    /// <summary>코드비하인드(StorageProvider)가 폴더를 고른 뒤 호출 — 경로 저장.</summary>
    public async Task SetCaptureDirAsync(string dir)
    {
        CaptureDir = dir;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var param = scope.ServiceProvider.GetRequiredService<ParameterService>();
            await param.SetAsync(CaptureDirKey, dir, "학습 데이터 캡처 저장 폴더");
        }
        catch { /* 무시 */ }
        Notify($"저장 폴더 지정: {dir}", false);
    }

    private void Notify(string msg, bool error) { Message = msg; IsError = error; }

    public override void Dispose() { base.Dispose(); _cts.Cancel(); _cts.Dispose(); }
}
