using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.AMR.App.Service;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>
/// 운영 로그 페이지 — 수동(UI)/ACS 발 동작(order·시퀀스·스텝)의 이력과 실패 원인을 표시.
/// 데이터 원본은 <see cref="OperationLogService"/>(DB 영속, 싱글톤) — 새 로그가 기록되면
/// Changed 이벤트로 실시간 갱신된다.
/// </summary>
public sealed partial class LogsViewModel : ViewModelBase
{
    private const int MaxRows = 300;

    private readonly OperationLogService _logs;
    private bool _reloadQueued;

    public LogsViewModel(OperationLogService logs) => _logs = logs;

    public ObservableCollection<LogRow> Rows { get; } = new();

    public string[] SourceOptions { get; } = ["전체", "ACS", "수동(UI)"];

    [ObservableProperty] private string _selectedSource = "전체";
    [ObservableProperty] private bool _onlyFailures;
    [ObservableProperty] private bool _busy;

    partial void OnSelectedSourceChanged(string value) => _ = ReloadAsync();
    partial void OnOnlyFailuresChanged(bool value) => _ = ReloadAsync();

    public override void OnActivated()
    {
        _logs.Changed += OnLogsChanged;
        _ = ReloadAsync();
    }

    public override void OnDeactivated() => _logs.Changed -= OnLogsChanged;

    private void OnLogsChanged() => Dispatcher.UIThread.Post(() => _ = ReloadAsync());

    [RelayCommand]
    private async Task ReloadAsync()
    {
        if (Busy) { _reloadQueued = true; return; }
        Busy = true;
        try
        {
            do
            {
                _reloadQueued = false;
                var source = SelectedSource switch
                {
                    "ACS" => OperationLogService.SourceAcs,
                    "수동(UI)" => OperationLogService.SourceUi,
                    _ => null,
                };
                var list = await _logs.QueryAsync(MaxRows, source, OnlyFailures);
                Rows.Clear();
                foreach (var l in list)
                    Rows.Add(new LogRow(
                        l.TimestampUtc.ToLocalTime().ToString("MM-dd HH:mm:ss"),
                        l.Source == OperationLogService.SourceAcs ? "ACS" : "수동",
                        l.Category,
                        l.Name,
                        l.Success switch { true => "성공", false => "실패", _ => "정보" },
                        l.Detail,
                        l.Success == false,
                        l.Success == true,
                        l.CorrelationId ?? ""));
            } while (_reloadQueued);
        }
        finally { Busy = false; }
    }
}

/// <summary>로그 페이지 표시 행 — Result 배지는 IsSuccess/IsFailure 로 색을 가른다.</summary>
public sealed record LogRow(string Time, string Source, string Category, string Name,
    string Result, string Detail, bool IsFailure, bool IsSuccess, string CorrelationId);
