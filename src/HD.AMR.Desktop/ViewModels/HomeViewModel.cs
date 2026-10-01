using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.AMR.App.Communication;
using HD.AMR.App.Enums;
using HD.AMR.App.Service;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>
/// 대시보드(상태 개요). 기존 Home.razor 이식 — AMR/Cobot/Camera/배터리/안전등/ACS 카드.
/// ACS 카드는 Vda5050AdapterService 의 브로커 접속·생존 신호를 표시하고 수동 층 전환(mapId)을 제공한다.
/// </summary>
public sealed partial class HomeViewModel : ViewModelBase
{
    private readonly AMRService _amr;
    private readonly CobotService _cobot;
    private readonly CameraService _camera;
    private readonly IoModuleService _io;
    private readonly Vda5050AdapterService _vda;
    private readonly FloorChangeService _floor;
    private readonly DispatcherTimer _timer;
    public AmrMapViewModel Map { get; }

    public ObservableCollection<string> MapIdOptions { get; } = new();
    [ObservableProperty] private string _selectedMapId = "";
    [ObservableProperty] private string? _mapIdStatus;

    public HomeViewModel(AMRService amr, CobotService cobot, CameraService camera, IoModuleService io,
        Vda5050AdapterService vda, FloorChangeService floor, AmrMapViewModel map)
    {
        _amr = amr;
        _cobot = cobot;
        _camera = camera;
        _io = io;
        _vda = vda;
        _floor = floor;
        Map = map;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    public override void OnActivated()
    {
        RebuildMapOptions();
        SelectedMapId = _vda.CurrentMapId;
        _ = RefreshAsync();
        _timer.Start();
        _ = Map.EnsureActiveMapLoadedAsync();
    }

    public override void OnDeactivated() => _timer.Stop();

    private async Task RefreshAsync()
    {
        await Map.RefreshTelemetryAsync();
        OnPropertyChanged(string.Empty);
    }

    public string NowText => DateTime.Now.ToString("yyyy-MM-dd HH:mm");

    // AMR
    public bool AmrConnected => _amr.IsConnected;
    public string AmrStatusText => _amr.IsConnected ? "주행 대기" : "오프라인";

    // Cobot
    public bool CobotConnected => _cobot.IsConnected;
    public bool CobotStateConnected => _cobot.IsStateConnected;

    /// <summary>상태 패킷(20004)에서 읽은 활성 공구/작업물 좌표계 번호. 상태 미수신이면 -1(미상).</summary>
    public string CobotFrameText => _cobot.State is { } s && s.Tool >= 0 && s.User >= 0
        ? $"툴 #{s.Tool} / 작업물 #{s.User}{(s.User == 0 ? " (베이스)" : "")}"
        : "툴/작업물 미상";

    /// <summary>활성 작업물이 베이스(#0)가 아님 — 조그·티칭이 프레임 기준으로 동작하므로 눈에 띄게 표시.</summary>
    public bool CobotUserNotBase => _cobot.State?.User is > 0;

    // Camera
    public bool CameraConnected => _camera.IsConnected;
    public bool CameraStreaming => _camera.IsStreaming;
    public string? CameraConnectionType => _camera.ConnectionType;

    // 배터리 (AMR 캐시)
    public int? BatteryPercent =>
        _amr.LatestStatus is { } s ? (int)Math.Round(s.Battery.LevelPercent) : null;
    public bool IsCharging => _amr.LatestStatus?.Battery.ChargingState == ChargingState.Charging;
    public string BatteryPercentText => BatteryPercent?.ToString() ?? "--";
    public string BatterySubText => BatteryPercent is null
        ? "텔레메트리 연동 예정"
        : IsCharging ? "AMR 배터리 충전 중" : "AMR 배터리 잔량";

    // 안전 신호등 — IO Module 출력 되읽기(OUT 0/1/2)를 그대로 표시한다.
    private bool LampOutput(int index) =>
        _io.GetState() is { Outputs: var outputs } && index < outputs.Length && outputs[index];
    public bool LampRed => LampOutput(IoPointMap.Out.TowerLampRed);
    public bool LampYellow => LampOutput(IoPointMap.Out.TowerLampYellow);
    public bool LampGreen => LampOutput(IoPointMap.Out.TowerLampGreen);

    // ACS — TopBar 배지와 동일 기준(생존 신호 우선, 신호 없으면 활동 폴백).
    public bool AcsOn =>
        _vda.IsBrokerConnected &&
        _vda.AcsConnectionLiveness is not (AcsLiveness.Offline or AcsLiveness.Broken) &&
        (_vda.AcsConnectionLiveness == AcsLiveness.Online || _vda.AcsRecentlyActive);
    public string AcsPillText =>
        !_vda.IsBrokerConnected ? "미연결"
        : _vda.AcsConnectionLiveness == AcsLiveness.Offline ? "OFFLINE"
        : _vda.AcsConnectionLiveness == AcsLiveness.Broken ? "CONNECTIONBROKEN"
        : _vda.AcsConnectionLiveness == AcsLiveness.Online || _vda.AcsRecentlyActive ? "연결" : "대기";
    public string CurrentMapText => $"현재 맵 {_vda.CurrentMapId}";
    public string MapIdHint => MapIdStatus ??
        "엘리베이터 하차 위치(층별 initpose)에 AMR 을 세운 뒤 전환 — 재측위 검증 통과 시에만 층이 바뀝니다";
    public bool MapIdFailed { get; private set; }
    public bool CanApplyMap => !string.IsNullOrEmpty(SelectedMapId) && SelectedMapId != _vda.CurrentMapId &&
                               !_floor.IsRunning;
    public string ApplyMapButtonText => _floor.IsRunning ? "전환 중…" : "층 전환";

    partial void OnSelectedMapIdChanged(string value) => ApplyMapIdCommand.NotifyCanExecuteChanged();

    private void RebuildMapOptions()
    {
        MapIdOptions.Clear();
        if (!_vda.AvailableMapIds.Contains(_vda.CurrentMapId)) MapIdOptions.Add(_vda.CurrentMapId);
        foreach (var id in _vda.AvailableMapIds) MapIdOptions.Add(id);
    }

    // 층 전환 — 층별 initpose 재측위 → 수렴 검증 → 통과 시 mapId 변경 + state 즉시 발행(ACS 회신).
    [RelayCommand(CanExecute = nameof(CanApplyMap))]
    private async Task ApplyMapIdAsync()
    {
        var target = SelectedMapId;
        MapIdFailed = false;
        MapIdStatus = $"{target} 전환 시작…";
        OnPropertyChanged(string.Empty);
        ApplyMapIdCommand.NotifyCanExecuteChanged();
        FloorChangeResult r;
        try
        {
            r = await _floor.ChangeFloorAsync(target,
                m => Dispatcher.UIThread.Post(() => { MapIdStatus = m; OnPropertyChanged(nameof(MapIdHint)); }),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            r = FloorChangeResult.Fail($"층 전환 오류: {ex.Message}", relocalized: false);
        }
        MapIdFailed = !r.Success;
        MapIdStatus = $"{DateTime.Now:HH:mm:ss} {(r.Success ? "✓" : "✗")} {r.Message}";
        if (!r.Success) SelectedMapId = _vda.CurrentMapId;
        OnPropertyChanged(string.Empty);
        ApplyMapIdCommand.NotifyCanExecuteChanged();
    }
}
