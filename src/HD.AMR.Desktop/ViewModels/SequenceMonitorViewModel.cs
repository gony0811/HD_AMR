using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using HD.AMR.App.Service.Sequence;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>
/// 시퀀스 모니터 창 뷰모델. 싱글톤 <see cref="SequenceMonitorService"/> 를 구독해 현재 단계·상태·로그를
/// 실시간 표시한다. 기존 SequenceMonitor.razor + SequenceMonitorView.razor 이식(카메라 오버레이는
/// Tier 3 CameraView 공용 컨트롤 완성 후 연결 예정 — 현재는 단계/상태/로그 콘솔).
/// </summary>
public sealed partial class SequenceMonitorViewModel : ObservableObject, IDisposable
{
    private readonly SequenceMonitorService _monitor;

    /// <summary>마지막 스텝(모니터 닫기)이 닫기를 요청하면 발생 — 창이 구독해 self-close.</summary>
    public event Action? CloseRequested;

    public SequenceMonitorViewModel(SequenceMonitorService monitor)
    {
        _monitor = monitor;
        _monitor.Changed += OnChanged;
        Refresh();
    }

    [ObservableProperty] private bool _hasStep;
    [ObservableProperty] private string _stepHeader = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private int _statusKind;   // 0 대기,1 실행,3 실패,2 완료
    [ObservableProperty] private string _targetDistanceText = "";
    [ObservableProperty] private string _logText = "";
    [ObservableProperty] private string _idleText = "시퀀스 대기 중… 시퀀스 페이지에서 실행하면 진행 상황이 여기에 표시됩니다.";

    private void OnChanged() => Dispatcher.UIThread.Post(() =>
    {
        Refresh();
        if (_monitor.CloseRequested) CloseRequested?.Invoke();
    });

    private void Refresh()
    {
        HasStep = _monitor.StepKey is not null;
        StepHeader = $"{_monitor.StepNo} {_monitor.StepName}".Trim();
        TargetDistanceText = $"{_monitor.CameraTargetDistanceMm:0} mm";
        LogText = string.Join("\n", _monitor.SnapshotLines());
        (StatusKind, StatusText) = _monitor switch
        {
            { RunActive: true } => (1, "실행 중"),
            { LastEndedInFailure: true } => (3, "실패로 종료"),
            { StepKey: not null } => (2, "완료"),
            _ => (0, "대기"),
        };
    }

    public void Dispose() => _monitor.Changed -= OnChanged;
}
