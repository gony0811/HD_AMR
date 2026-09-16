using Avalonia.Threading;
using HD.AMR.App.Enums;
using HD.AMR.App.Service;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>
/// 대시보드(상태 개요). 기존 Home.razor 이식 — AMR/Cobot/Camera/배터리/안전등 카드.
/// ACS(Vda5050) 카드는 어댑터 의존성 체인이 커서 후속 티어에서 배선 예정(현재 플레이스홀더).
/// </summary>
public sealed partial class HomeViewModel : ViewModelBase
{
    private readonly AMRService _amr;
    private readonly CobotService _cobot;
    private readonly CameraService _camera;
    private readonly DispatcherTimer _timer;

    public HomeViewModel(AMRService amr, CobotService cobot, CameraService camera)
    {
        _amr = amr;
        _cobot = cobot;
        _camera = camera;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => OnPropertyChanged(string.Empty);
    }

    public override void OnActivated() { OnPropertyChanged(string.Empty); _timer.Start(); }
    public override void OnDeactivated() => _timer.Stop();

    public string NowText => DateTime.Now.ToString("yyyy-MM-dd HH:mm");

    // AMR
    public bool AmrConnected => _amr.IsConnected;
    public string AmrStatusText => _amr.IsConnected ? "주행 대기" : "오프라인";

    // Cobot
    public bool CobotConnected => _cobot.IsConnected;
    public bool CobotStateConnected => _cobot.IsStateConnected;

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

    // 안전 신호등 (텔레메트리 연동 전 — 플레이스홀더)
    public bool LampRed => false;
    public bool LampYellow => false;
    public bool LampGreen => true;
}
