using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.AMR.App.Data.Entities;
using HD.AMR.App.Service;
using Microsoft.Extensions.DependencyInjection;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>
/// 검사 레시피 17종 조회·편집·저장·기본값 재적용. 기존 InspectionRecipes.razor 이식.
/// <see cref="InspectionRecipeService"/> 는 Scoped 라 작업마다 scope 를 연다.
/// </summary>
public sealed partial class InspectionRecipesViewModel : ViewModelBase
{
    private readonly IServiceScopeFactory _scopeFactory;

    public ObservableCollection<RecipeRow> Rows { get; } = new();

    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isError;

    public int TotalCount => Rows.Count;

    public InspectionRecipesViewModel(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public override async void OnActivated() => await ReloadAsync();

    [RelayCommand]
    private async Task ReloadAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<InspectionRecipeService>();
            var list = await svc.ListAsync();
            Rows.Clear();
            foreach (var r in list)
                Rows.Add(new RecipeRow(r));
            OnPropertyChanged(nameof(TotalCount));
        }
        catch (Exception ex) { Notify($"목록 로드 실패: {ex.Message}", true); }
    }

    [RelayCommand]
    private void ToggleExpand(RecipeRow row) => row.IsExpanded = !row.IsExpanded;

    [RelayCommand]
    private async Task SaveAsync(RecipeRow row)
    {
        if (Busy) return;
        if (Validate(row) is { } error)
        {
            Notify($"'{row.Id}' 저장 거부 — {error}", true);
            return;
        }
        Busy = true;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<InspectionRecipeService>();
            await svc.SaveAsync(row.Apply());
            await ReloadAsync();
            Notify($"'{row.Id}' 저장 완료", false);
        }
        catch (Exception ex) { Notify($"저장 실패: {ex.Message}", true); }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task ResetToDefaultAsync(RecipeRow row)
    {
        if (Busy) return;
        Busy = true;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<InspectionRecipeService>();
            var ok = await svc.ResetToDefaultAsync(row.Id);
            await ReloadAsync();
            Notify(ok ? $"'{row.Id}' 기본값 재적용 완료" : $"'{row.Id}' 는 카탈로그에 없는 레시피입니다.", !ok);
        }
        catch (Exception ex) { Notify($"기본값 재적용 실패: {ex.Message}", true); }
        finally { Busy = false; }
    }

    // 저장 전 JSON·수치 필드 형식 검증(razor ValidateRow 이식). 빈 문자열은 null 로 정규화.
    private static string? Validate(RecipeRow row)
    {
        row.StepKeysJson = string.IsNullOrWhiteSpace(row.StepKeysJson) ? null : row.StepKeysJson.Trim();
        row.ApproachTeachingKey = row.ApproachTeachingKey?.Trim() ?? "";

        if (row.StepKeysJson is not null)
        {
            try
            {
                if (JsonSerializer.Deserialize<string[]>(row.StepKeysJson) is not { Length: > 0 })
                    return "StepKeysJson 은 비어 있지 않은 문자열 배열이어야 합니다.";
            }
            catch (JsonException ex) { return $"StepKeysJson 파싱 실패: {ex.Message}"; }
        }
        if (row.VisionFailRatioMax is < 0 or > 1) return "비전 실패율 상한은 0~1 범위여야 합니다.";
        if (row.DefaultStandoffMm < 0) return "Standoff 폴백은 0 이상이어야 합니다.";
        if (row.CameraTargetDistanceMm is <= 0) return "카메라 목표거리는 0 보다 커야 합니다(빈 값 = 전역 기본).";
        if (row.SurfaceOverride is > 2) return "Surface 강제값은 0/1/2 또는 자동(빈 값)만 허용합니다.";
        if (row.AlignRetryCount < 0) return "정렬 재시도 횟수는 0 이상이어야 합니다.";
        return null;
    }

    private void Notify(string msg, bool error)
    {
        Message = msg;
        IsError = error;
    }
}

/// <summary>편집 가능한 레시피 행. 원본 엔티티를 감싸고, 저장 시 <see cref="Apply"/> 로 값을 반영한다.</summary>
public sealed partial class RecipeRow : ObservableObject
{
    private readonly InspectionRecipe _entity;

    public string Id => _entity.Id;
    public string DisplayName => _entity.DisplayName;
    public string SeamType => _entity.SeamType.ToString();
    public string Orientation => _entity.Orientation.ToString();

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string? _stepKeysJson;
    [ObservableProperty] private double _visionFailRatioMax;
    [ObservableProperty] private string _approachTeachingKey;
    [ObservableProperty] private double _defaultStandoffMm;
    [ObservableProperty] private double? _cameraTargetDistanceMm;
    [ObservableProperty] private int _alignRetryCount;
    [ObservableProperty] private bool _isExpanded;

    // Surface 강제: 콤보 인덱스(0=자동, 1=Flat(0), 2=Corner(1), 3=Corrugation(2)).
    [ObservableProperty] private int _surfaceIndex;

    public byte? SurfaceOverride => SurfaceIndex <= 0 ? null : (byte)(SurfaceIndex - 1);

    public RecipeRow(InspectionRecipe e)
    {
        _entity = e;
        _enabled = e.Enabled;
        _stepKeysJson = e.StepKeysJson;
        _visionFailRatioMax = e.VisionFailRatioMax;
        _approachTeachingKey = e.ApproachTeachingKey;
        _defaultStandoffMm = e.DefaultStandoffMm;
        _cameraTargetDistanceMm = e.CameraTargetDistanceMm;
        _alignRetryCount = e.AlignRetryCount;
        _surfaceIndex = e.SurfaceOverride is { } b && b <= 2 ? b + 1 : 0;
    }

    /// <summary>편집값을 원본 엔티티에 반영해 저장용으로 반환.</summary>
    public InspectionRecipe Apply()
    {
        _entity.Enabled = Enabled;
        _entity.StepKeysJson = StepKeysJson;
        _entity.VisionFailRatioMax = VisionFailRatioMax;
        _entity.ApproachTeachingKey = ApproachTeachingKey;
        _entity.DefaultStandoffMm = DefaultStandoffMm;
        _entity.CameraTargetDistanceMm = CameraTargetDistanceMm;
        _entity.AlignRetryCount = AlignRetryCount;
        _entity.SurfaceOverride = SurfaceOverride;
        return _entity;
    }
}
