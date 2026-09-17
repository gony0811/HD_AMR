using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.AMR.App.Data.Entities;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Inspection;
using Microsoft.Extensions.DependencyInjection;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>
/// 검사 레시피 17종 조회·편집·저장·기본값 재적용 — 좌측 목록 선택 + 우측 상세 편집 구조.
/// 행 편집값은 <see cref="RecipeRow"/> 에 유지되므로 저장은 선택 행만 처리하고 다른 행의 미저장 편집은 보존한다.
/// <see cref="InspectionRecipeService"/> 는 Scoped 라 작업마다 scope 를 연다.
/// </summary>
public sealed partial class InspectionRecipesViewModel : ViewModelBase
{
    private readonly IServiceScopeFactory _scopeFactory;

    public ObservableCollection<RecipeRow> Rows { get; } = new();

    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isError;

    /// <summary>좌측 목록에서 선택한 레시피 — 우측 상세 편집 대상.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private RecipeRow? _selectedRow;

    public bool HasSelection => SelectedRow is not null;

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
            var profiles = await scope.ServiceProvider.GetRequiredService<DrawingService>().ListProfilesAsync();
            var keepId = SelectedRow?.Id;
            Rows.Clear();
            foreach (var r in list)
                Rows.Add(new RecipeRow(r, profiles));
            SelectedRow = Rows.FirstOrDefault(r => r.Id == keepId) ?? Rows.FirstOrDefault();
            OnPropertyChanged(nameof(TotalCount));
        }
        catch (Exception ex) { Notify($"목록 로드 실패: {ex.Message}", true); }
    }

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
            var entity = row.Apply();
            if (await svc.ValidateProfileAssignmentAsync(entity) is { } profileError)
            {
                Notify($"'{row.Id}' 저장 거부 — {profileError}", true);
                return;
            }
            await svc.SaveAsync(entity);
            // 전체 재로드 대신 이 행만 저장 상태로 — 다른 레시피의 미저장 편집을 잃지 않도록.
            row.MarkSaved();
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
            if (ok) await ReplaceRowAsync(scope, row.Id);
            Notify(ok ? $"'{row.Id}' 기본값 재적용 완료 (티칭 프로필 지정은 유지)" : $"'{row.Id}' 는 카탈로그에 없는 레시피입니다.", !ok);
        }
        catch (Exception ex) { Notify($"기본값 재적용 실패: {ex.Message}", true); }
        finally { Busy = false; }
    }

    /// <summary>레시피 1행만 DB 에서 다시 읽어 목록에서 교체하고 선택을 유지한다(다른 행 편집 보존).</summary>
    private async Task ReplaceRowAsync(IServiceScope scope, string id)
    {
        var entity = await scope.ServiceProvider.GetRequiredService<InspectionRecipeService>().GetAsync(id);
        var index = Rows.ToList().FindIndex(r => r.Id == id);
        if (entity is null || index < 0) return;
        var profiles = await scope.ServiceProvider.GetRequiredService<DrawingService>().ListProfilesAsync();
        var wasSelected = SelectedRow?.Id == id;
        var fresh = new RecipeRow(entity, profiles);
        Rows[index] = fresh;
        if (wasSelected) SelectedRow = fresh;
    }

    // 저장 전 JSON·수치 필드 형식 검증(razor ValidateRow 이식). 빈 문자열은 null 로 정규화.
    private static string? Validate(RecipeRow row)
    {
        row.StepKeysJson = string.IsNullOrWhiteSpace(row.StepKeysJson) ? null : row.StepKeysJson.Trim();

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
        if (row.CameraTargetDistanceMm is <= 0) return "카메라 목표거리는 0 보다 커야 합니다(빈 값 = 전역 기본).";
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
    [ObservableProperty] private double? _cameraTargetDistanceMm;

    /// <summary>티칭 프로필 선택지 — [0]=미지정, 이후 레시피 타입과 같은 SeamType 프로필(최신 저장순).</summary>
    public List<ProfileOption> ProfileOptions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProfileMissing))]
    private ProfileOption? _selectedProfile;

    /// <summary>생성(로드) 이후 편집되었고 아직 저장하지 않음 — 목록에 ● 표시.</summary>
    [ObservableProperty] private bool _isDirty;

    /// <summary>CORNER3 은 고정 티칭 슬롯을 쓰므로 프로필 선택 칸 대신 안내 문구를 보인다.</summary>
    public bool IsCorner3 => _entity.Id == RecipeIds.Corner3;
    public bool CanPickProfile => !IsCorner3;

    /// <summary>LINE/CROSS 레시피인데 티칭 프로필 미지정 — 이 상태로 ACS 액션이 오면 FAILED. 목록에 ⚠ 표시.</summary>
    public bool ProfileMissing => !IsCorner3 && SelectedProfile?.Id is null;

    public string SeamOrientationText => $"seamType={SeamType} · 면자세={Orientation}";

    partial void OnEnabledChanged(bool value) => IsDirty = true;
    partial void OnStepKeysJsonChanged(string? value) => IsDirty = true;
    partial void OnVisionFailRatioMaxChanged(double value) => IsDirty = true;
    partial void OnCameraTargetDistanceMmChanged(double? value) => IsDirty = true;
    partial void OnSelectedProfileChanged(ProfileOption? value) => IsDirty = true;

    /// <summary>저장 완료 — 미저장 표시 해제.</summary>
    public void MarkSaved() => IsDirty = false;

    public RecipeRow(InspectionRecipe e, IEnumerable<InspectionProfile> profiles)
    {
        _entity = e;
        _enabled = e.Enabled;
        _stepKeysJson = e.StepKeysJson;
        _visionFailRatioMax = e.VisionFailRatioMax;
        _cameraTargetDistanceMm = e.CameraTargetDistanceMm;

        var want = InspectionRecipeResolver.ProfileSeamTypeOf(e.Id);
        ProfileOptions = new List<ProfileOption> { ProfileOption.None };
        ProfileOptions.AddRange(profiles
            .Where(p => InspectionRecipeService.IsSeamMatch(p, want))
            .Select(p => new ProfileOption(p.Id, $"{p.Name} ({CountWaypoints(p.WaypointsJson)}점 · {p.UpdatedAt.ToLocalTime():MM-dd HH:mm})")));
        _selectedProfile = ProfileOptions.FirstOrDefault(o => o.Id == e.InspectionProfileId) ?? ProfileOption.None;
    }

    private static int CountWaypoints(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : 0;
        }
        catch (JsonException) { return 0; }
    }

    /// <summary>편집값을 원본 엔티티에 반영해 저장용으로 반환.</summary>
    public InspectionRecipe Apply()
    {
        _entity.Enabled = Enabled;
        _entity.StepKeysJson = StepKeysJson;
        _entity.VisionFailRatioMax = VisionFailRatioMax;
        _entity.CameraTargetDistanceMm = CameraTargetDistanceMm;
        _entity.InspectionProfileId = IsCorner3 ? null : SelectedProfile?.Id;
        return _entity;
    }
}

/// <summary>레시피 행의 티칭 프로필 선택지. Id=null 은 미지정.</summary>
public sealed record ProfileOption(int? Id, string Label)
{
    public static readonly ProfileOption None = new(null, "— 미지정 —");
}
