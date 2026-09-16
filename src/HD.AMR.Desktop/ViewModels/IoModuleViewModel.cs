using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using HD.AMR.App.Service;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>
/// IO Module(LS산전 ModbusTCP) 모니터링 — Tier 1 파일럿.
/// 기존 IoModule.razor 의 상태 표시를 이식. StateHasChanged 주기 갱신 대신 DispatcherTimer 로
/// <see cref="IoModuleService"/> 스냅샷을 폴링해 바인딩 속성을 갱신한다.
/// </summary>
public sealed partial class IoModuleViewModel : ViewModelBase
{
    private readonly IoModuleService _svc;
    private readonly DispatcherTimer _timer;

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _connectionSummary = "";
    [ObservableProperty] private string? _lastError;
    [ObservableProperty] private string? _adapterLedSummary;
    [ObservableProperty] private string _lastUpdateText = "상태 수신 대기 중…";

    public ObservableCollection<IoBit> Inputs { get; } = new();
    public ObservableCollection<IoBit> Outputs { get; } = new();

    public IoModuleViewModel(IoModuleService svc)
    {
        _svc = svc;
        var s = svc.Settings;
        ConnectionSummary = $"{s.Name} — {s.IpAddress}:{s.Port}, SlaveId {s.SlaveId} " +
                            $"(자동 연결 — 실패 시 5초마다 재시도)";
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => Refresh();
    }

    public override void OnActivated()
    {
        Refresh();
        _timer.Start();
    }

    public override void OnDeactivated() => _timer.Stop();

    private void Refresh()
    {
        IsConnected = _svc.IsConnected;
        LastError = _svc.LastError;
        AdapterLedSummary = _svc.AdapterLedSummary;

        var state = _svc.GetState();
        if (state is null)
        {
            LastUpdateText = "상태 수신 대기 중…";
            return;
        }

        LastUpdateText = $"마지막 상태 갱신: {Age(state.UpdatedUtc)}";
        Sync(Inputs, state.Inputs, "IN");
        Sync(Outputs, state.Outputs, "OUT");
    }

    // 컬렉션 전체 재생성 없이 항목을 제자리 갱신(깜빡임/GC 최소화).
    private static void Sync(ObservableCollection<IoBit> target, bool[] values, string prefix)
    {
        for (var i = 0; i < values.Length; i++)
        {
            if (i < target.Count)
                target[i].On = values[i];
            else
                target.Add(new IoBit($"{prefix} {i:00}") { On = values[i] });
        }
        while (target.Count > values.Length)
            target.RemoveAt(target.Count - 1);
    }

    private static string Age(DateTime utc)
    {
        var s = (DateTime.UtcNow - utc).TotalSeconds;
        return s < 60 ? $"{s:0}초 전" : $"{s / 60:0}분 전";
    }
}

/// <summary>접점 1비트(라벨 + ON/OFF). ON 변경 시 UI 뱃지가 갱신되도록 ObservableObject.</summary>
public sealed partial class IoBit : ObservableObject
{
    public string Label { get; }
    [ObservableProperty] private bool _on;
    public IoBit(string label) => Label = label;
}
