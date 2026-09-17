using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.AMR.App.Service;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>셸 뷰모델. 좌측 NavMenu 항목 + 현재 페이지(<see cref="Navigation"/>)를 노출.
/// 상단 상태바(연결 배지 + 비상정지 토글)도 여기서 구동한다.</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    /// <summary>EMO 비상정지 출력 포인트 (IO 모듈 OUT 8, 0-based).</summary>
    private const int EmoOutputIndex = 8;

    private readonly AMRService _amr;
    private readonly CobotService _cobot;
    private readonly IoModuleService _io;
    private readonly DispatcherTimer _timer;

    public INavigationService Navigation { get; }

    public ObservableCollection<NavItem> NavItems { get; }

    [ObservableProperty]
    private NavItem? _selectedItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AmrStatusText))]
    private bool _isAmrConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CobotStatusText))]
    private bool _isCobotConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IoStatusText))]
    [NotifyCanExecuteChangedFor(nameof(ToggleEmoCommand))]
    private bool _isIoConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmoButtonText))]
    private bool _isEmoActive;

    [ObservableProperty]
    private string? _emoMessage;

    public string AmrStatusText => IsAmrConnected ? "연결" : "미연결";
    public string CobotStatusText => IsCobotConnected ? "연결" : "미연결";
    public string IoStatusText => IsIoConnected ? "연결" : "미연결";
    public string EmoButtonText => IsEmoActive ? "🛑 비상정지 해제" : "🛑 비상정지";

    public MainWindowViewModel(INavigationService navigation, AMRService amr, CobotService cobot, IoModuleService io)
    {
        Navigation = navigation;
        _amr = amr;
        _cobot = cobot;
        _io = io;

        // 기존 HD.AMR.Web/Components/Layout/NavMenu.razor 의 18개 항목 이식.
        // 아직 포팅되지 않은 페이지는 Target = null (클릭 시 "준비 중" 플레이스홀더로 이동).
        NavItems = new ObservableCollection<NavItem>
        {
            new("Dashboard",     "🏠", typeof(HomeViewModel)),
            new("AMR",           "🚚", typeof(AmrViewModel)),
            new("Teaching",      "✏️", typeof(TeachingViewModel)),
            new("Inspection",    "📋", null),
            new("검사 레시피",   "📗", typeof(InspectionRecipesViewModel)),
            new("검사 포인트(X-Y)", "📍", typeof(InspectionPointsViewModel)),
            new("검사 매핑 요약", "🗂", typeof(InspectionMapViewModel)),
            new("Cobot",         "🤖", typeof(CobotViewModel)),
            new("Sequence",      "🔀", typeof(SequenceViewModel)),
            new("Camera",        "🎥", typeof(CameraViewModel)),
            new("용접 추적",     "📈", typeof(WeldTrackingViewModel)),
            new("Vision Interface", "🖧", typeof(VisionInterfaceViewModel)),
            new("비드 라벨링",   "🏷", typeof(LabelEditorViewModel)),
            new("비전 학습",     "🧠", typeof(VisionTrainingViewModel)),
            new("QR Pose Teaching", "🧭", typeof(CalibrationViewModel)),
            new("Parameter",     "🎛", typeof(ParametersViewModel)),
            new("Laser Sensor",  "📏", typeof(LaserViewModel)),
            new("IO Module",     "🔌", typeof(IoModuleViewModel)),
        };

        SelectedItem = NavItems[0];
        Navigation.NavigateTo(typeof(HomeViewModel));

        // 셸 VM 은 앱 수명 싱글턴 — 타이머를 여기서 시작하고 멈추지 않는다.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => RefreshStatus();
        _timer.Start();
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        IsAmrConnected = _amr.IsConnected;
        IsCobotConnected = _cobot.IsConnected;
        IsIoConnected = _io.IsConnected;

        // 표시 상태는 클릭이 아니라 폴링된 출력 스냅샷 기준 → 쓰기 미반영 시 자동 원복.
        var state = _io.GetState();
        IsEmoActive = state is not null && state.Outputs.Length > EmoOutputIndex && state.Outputs[EmoOutputIndex];
    }

    private bool CanToggleEmo() => IsIoConnected;

    [RelayCommand(CanExecute = nameof(CanToggleEmo))]
    private async Task ToggleEmoAsync()
    {
        var target = !IsEmoActive;
        EmoMessage = null;
        try
        {
            var confirmed = await _io.WriteOutputAsync(EmoOutputIndex, target);
            EmoMessage = confirmed
                ? $"EMO(OUT {EmoOutputIndex}) {(target ? "출력 ON" : "리셋(OFF)")} — 되읽기 반영 확인"
                : "쓰기는 수락됐지만 반영되지 않음 — RAPIEnet 상태 확인 필요";
        }
        catch (Exception ex)
        {
            EmoMessage = $"EMO 출력 실패: {ex.Message}";
        }
        RefreshStatus();
    }

    partial void OnSelectedItemChanged(NavItem? value)
    {
        if (value is not null)
            Navigate(value);
    }

    [RelayCommand]
    private void Navigate(NavItem item)
    {
        if (item.Target is { } vmType)
            Navigation.NavigateTo(vmType);
        else
            Navigation.NavigateTo(typeof(ComingSoonViewModel));
    }
}

/// <summary>NavMenu 한 항목. <paramref name="Target"/> 이 null 이면 아직 포팅 전 페이지.</summary>
public sealed record NavItem(string Label, string Icon, Type? Target);
