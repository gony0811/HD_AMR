using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using HD.AMR.App.Communication;
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

    /// <summary>접점 이름 맵의 근거 각주 — 배선표 출처를 화면에 남긴다.</summary>
    public string PointMapNote { get; } = $"접점 이름: {IoPointMap.SourceNote}";

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
        Sync(Inputs, state.Inputs, "IN", IoPointMap.InputLabel, IoPointMap.Input);
        Sync(Outputs, state.Outputs, "OUT", IoPointMap.OutputLabel, IoPointMap.Output);
    }

    // 컬렉션 전체 재생성 없이 항목을 제자리 갱신(깜빡임/GC 최소화).
    // 라벨(접점 이름)은 배선표 고정값이라 최초 생성 시에만 계산한다.
    private static void Sync(ObservableCollection<IoBit> target, bool[] values, string prefix,
        Func<int, string> nameOf, Func<int, IoPoint?> pointOf)
    {
        for (var i = 0; i < values.Length; i++)
        {
            if (i < target.Count)
                target[i].On = values[i];
            else
                target.Add(new IoBit($"{prefix} {i:00} · {nameOf(i)}", pointOf(i) is not null)
                {
                    On = values[i]
                });
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

    /// <summary>배선된 접점이면 true — 미배선 접점은 흐리게 표시한다.</summary>
    public bool Assigned { get; }

    [ObservableProperty] private bool _on;

    public IoBit(string label, bool assigned = true)
    {
        Label = label;
        Assigned = assigned;
    }
}
