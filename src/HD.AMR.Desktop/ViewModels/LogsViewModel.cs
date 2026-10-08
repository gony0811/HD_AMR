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

    /// <summary>출처 필터 — (코드, 표시명). 코드 null = 전체.</summary>
    public FilterOption[] SourceOptions { get; } =
    [
        new(null, "전체"),
        new(OperationLogService.SourceAcs, "ACS"),
        new(OperationLogService.SourceUi, "수동(UI)"),
        new(OperationLogService.SourceAmr, "AMR 장비"),
        new(OperationLogService.SourceIo, "현장 버튼"),
    ];

    /// <summary>분류 필터 — 기본 목록 + DB 에 기록된 분류(목록에 없는 코드 포함).</summary>
    public ObservableCollection<FilterOption> CategoryOptions { get; } = new();

    [ObservableProperty] private FilterOption? _selectedSource;
    [ObservableProperty] private FilterOption? _selectedCategory;
    [ObservableProperty] private bool _onlyFailures;
    [ObservableProperty] private bool _busy;

    partial void OnSelectedSourceChanged(FilterOption? value) => _ = ReloadAsync();
    partial void OnSelectedCategoryChanged(FilterOption? value) => _ = ReloadAsync();
    partial void OnOnlyFailuresChanged(bool value) => _ = ReloadAsync();

    public override void OnActivated()
    {
        SelectedSource ??= SourceOptions[0];
        _logs.Changed += OnLogsChanged;
        _ = LoadCategoriesAsync();
        _ = ReloadAsync();
    }

    private async Task LoadCategoriesAsync()
    {
        var keep = SelectedCategory?.Code;
        var options = new List<FilterOption> { new(null, "전체") };
        options.AddRange(OpCategory.All.Select(c => new FilterOption(c.Code, c.Label)));
        try
        {
            foreach (var code in await _logs.QueryCategoriesAsync())
                if (options.All(o => o.Code != code)) options.Add(new FilterOption(code, code));
        }
        catch { /* DB 미준비 — 기본 목록만 */ }
        CategoryOptions.Clear();
        foreach (var o in options) CategoryOptions.Add(o);
        SelectedCategory = CategoryOptions.FirstOrDefault(o => o.Code == keep) ?? CategoryOptions[0];
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
                var list = await _logs.QueryAsync(MaxRows, SelectedSource?.Code, OnlyFailures,
                    category: SelectedCategory?.Code);
                Rows.Clear();
                foreach (var l in list)
                    Rows.Add(new LogRow(
                        l.TimestampUtc.ToLocalTime().ToString("MM-dd HH:mm:ss"),
                        SourceLabel(l.Source),
                        OpCategory.LabelOf(l.Category),
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

    private static string SourceLabel(string source) => source switch
    {
        OperationLogService.SourceAcs => "ACS",
        OperationLogService.SourceUi => "수동",
        OperationLogService.SourceAmr => "AMR",
        OperationLogService.SourceIo => "버튼",
        _ => source,
    };
}

/// <summary>필터 드롭다운 항목 — Code null = 전체.</summary>
public sealed record FilterOption(string? Code, string Label)
{
    public override string ToString() => Label;
}

/// <summary>로그 페이지 표시 행 — Result 배지는 IsSuccess/IsFailure 로 색을 가른다.</summary>
public sealed record LogRow(string Time, string Source, string Category, string Name,
    string Result, string Detail, bool IsFailure, bool IsSuccess, string CorrelationId);
