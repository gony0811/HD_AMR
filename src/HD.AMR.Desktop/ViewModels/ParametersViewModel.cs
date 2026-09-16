using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.AMR.App.Data.Entities;
using HD.AMR.App.Service;
using Microsoft.Extensions.DependencyInjection;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>
/// 범용 파라미터(key/value) 조회·수정·삭제·추가. 기존 Parameters.razor 이식.
/// DbContext 를 직접 쓰는 <see cref="ParameterService"/> 는 Scoped 라, 작업마다 scope 를 연다.
/// </summary>
public sealed partial class ParametersViewModel : ViewModelBase
{
    private readonly IServiceScopeFactory _scopeFactory;

    private readonly List<Parameter> _all = new();
    public ObservableCollection<ParameterRow> Rows { get; } = new();

    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isError;

    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string _newValue = "";
    [ObservableProperty] private string _newDescription = "";

    public int TotalCount => _all.Count;

    public ParametersViewModel(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public override async void OnActivated() => await ReloadAsync();

    partial void OnFilterChanged(string value) => ApplyFilter();

    [RelayCommand]
    private async Task ReloadAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<ParameterService>();
            var list = await svc.GetAllAsync();
            _all.Clear();
            _all.AddRange(list);
            ApplyFilter();
            OnPropertyChanged(nameof(TotalCount));
        }
        catch (Exception ex)
        {
            Notify($"목록 로드 실패: {ex.Message}", true);
        }
    }

    private void ApplyFilter()
    {
        IEnumerable<Parameter> q = _all;
        if (!string.IsNullOrWhiteSpace(Filter))
            q = _all.Where(p =>
                p.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase) ||
                (p.Description?.Contains(Filter, StringComparison.OrdinalIgnoreCase) ?? false));

        Rows.Clear();
        foreach (var p in q)
            Rows.Add(new ParameterRow(p));
    }

    [RelayCommand]
    private async Task SaveAsync(ParameterRow row)
    {
        if (Busy) return;
        Busy = true;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<ParameterService>();
            await svc.SetAsync(row.Name, row.Value ?? "", row.Description ?? "");
            await ReloadAsync();
            Notify($"'{row.Name}' 저장 완료", false);
        }
        catch (Exception ex) { Notify($"저장 실패: {ex.Message}", true); }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task DeleteAsync(ParameterRow row)
    {
        if (Busy) return;
        Busy = true;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<ParameterService>();
            await svc.DeleteAsync(row.Id);
            await ReloadAsync();
            Notify($"'{row.Name}' 삭제 완료", false);
        }
        catch (Exception ex) { Notify($"삭제 실패: {ex.Message}", true); }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        if (Busy) return;
        var name = NewName.Trim();
        if (string.IsNullOrEmpty(name)) { Notify("이름을 입력하세요.", true); return; }
        if (_all.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            Notify($"이미 존재하는 이름입니다: {name}", true);
            return;
        }
        Busy = true;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<ParameterService>();
            await svc.SetAsync(name, NewValue, NewDescription);
            NewName = NewValue = NewDescription = "";
            await ReloadAsync();
            Notify($"'{name}' 추가 완료", false);
        }
        catch (Exception ex) { Notify($"추가 실패: {ex.Message}", true); }
        finally { Busy = false; }
    }

    private void Notify(string msg, bool error)
    {
        Message = msg;
        IsError = error;
    }
}

/// <summary>편집 가능한 파라미터 행(값/설명은 인라인 편집).</summary>
public sealed partial class ParameterRow : ObservableObject
{
    public int Id { get; }
    public string Name { get; }
    public DateTime UpdatedAt { get; }
    [ObservableProperty] private string _value;
    [ObservableProperty] private string? _description;

    public string UpdatedText => UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public ParameterRow(Parameter p)
    {
        Id = p.Id;
        Name = p.Name;
        UpdatedAt = p.UpdatedAt;
        _value = p.Value;
        _description = p.Description;
    }
}
