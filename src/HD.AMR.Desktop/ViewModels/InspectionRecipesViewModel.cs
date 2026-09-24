using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.AMR.App.Data.Entities;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Inspection;
using HD.AMR.App.Service.Sequence;
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
            var pool = LoadStepPool(scope);
            var keepId = SelectedRow?.Id;
            Rows.Clear();
            foreach (var r in list)
                Rows.Add(new RecipeRow(r, profiles, pool));
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
            var unknown = row.SelectedSteps.Where(st => !st.Known).Select(st => st.Key).ToList();
            Notify(unknown.Count == 0
                ? $"'{row.Id}' 저장 완료 — {row.StepSummary}"
                : $"'{row.Id}' 저장 완료 — 등록되지 않은 스텝 {string.Join(", ", unknown)} 은(는) 실행 시 무시됩니다.",
                unknown.Count > 0);
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

    /// <summary>
    /// 시퀀스 풀 — DI 에 등록된 <see cref="ISequenceStep"/> 전체를 실행 순서(DefaultOrder)로 나열한다.
    /// 스텝 Key·표시명·순서의 진실 원천은 스텝 클래스 자신이므로 카탈로그를 따로 두지 않고 DI 에서 읽는다.
    /// </summary>
    private static IReadOnlyList<StepPick> LoadStepPool(IServiceScope scope)
        => scope.ServiceProvider.GetServices<ISequenceStep>()
            .Select(st => new StepPick(st.Key, st.DisplayName, st.DefaultOrder))
            .OrderBy(st => st.DefaultOrder)
            .ToList();

    /// <summary>레시피 1행만 DB 에서 다시 읽어 목록에서 교체하고 선택을 유지한다(다른 행 편집 보존).</summary>
    private async Task ReplaceRowAsync(IServiceScope scope, string id)
    {
        var entity = await scope.ServiceProvider.GetRequiredService<InspectionRecipeService>().GetAsync(id);
        var index = Rows.ToList().FindIndex(r => r.Id == id);
        if (entity is null || index < 0) return;
        var profiles = await scope.ServiceProvider.GetRequiredService<DrawingService>().ListProfilesAsync();
        var wasSelected = SelectedRow?.Id == id;
        var fresh = new RecipeRow(entity, profiles, LoadStepPool(scope));
        Rows[index] = fresh;
        if (wasSelected) SelectedRow = fresh;
    }

    // 저장 전 수치 필드 검증. 실행 스텝은 풀에서 고른 값이 직렬화되므로 형식 오류가 날 수 없고,
    // 대신 '런타임에 실행할 스텝이 하나도 안 남는' 구성을 막는다.
    private static string? Validate(RecipeRow row)
    {
        row.StepKeysJson = string.IsNullOrWhiteSpace(row.StepKeysJson) ? null : row.StepKeysJson.Trim();

        // 등록되지 않은 키만 담긴 구성은 실행 시 "실행할 단계가 없습니다" 로 실패한다 — 저장 단계에서 막는다.
        if (row.SelectedSteps.Count > 0 && row.SelectedSteps.All(st => !st.Known))
            return "담긴 스텝이 전부 등록되지 않은 키입니다 — 실행 시 단계가 하나도 남지 않습니다. "
                 + "풀에서 스텝을 담거나 목록을 비워(= 풀시퀀스) 저장하세요.";

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

    /// <summary>실행 스텝 — 목록 편집 결과가 <see cref="StepKeysJson"/> 으로 직렬화된다(저장값은 종전과 동일).</summary>
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

    // ── 실행 스텝: 시퀀스 풀 ↔ 포함 목록 ────────────────────────────
    // JSON 을 손으로 쓰는 대신 등록된 스텝을 끌어다 담는다. 두 목록 모두 실행 순서(DefaultOrder)로
    // 정렬되어 있고, 담는 순서는 실행 순서에 영향을 주지 않는다 — 런타임이 항상 DefaultOrder 로
    // 정렬해 실행하기 때문(SequenceService.RunSequenceAsync).

    /// <summary>아직 담지 않은 스텝 — 여기서 끌어온다.</summary>
    public ObservableCollection<StepPick> StepPool { get; } = new();

    /// <summary>이 레시피가 실행할 스텝. 비어 있으면 풀시퀀스 전체 실행(저장값 null).</summary>
    public ObservableCollection<StepPick> SelectedSteps { get; } = new();

    /// <summary>담긴 스텝이 없다 = 풀시퀀스 전체 실행.</summary>
    public bool IsFullSequence => SelectedSteps.Count == 0;

    /// <summary>좌측 목록·요약에 쓰는 한 줄 설명.</summary>
    public string StepSummary => IsFullSequence
        ? "풀시퀀스 전체"
        : $"{SelectedSteps.Count}개 스텝" + (SelectedSteps.Any(st => !st.Known) ? " (⚠ 미등록 포함)" : "");

    [RelayCommand]
    private void AddStep(StepPick? item)
    {
        if (item is null || !StepPool.Remove(item)) return;
        InsertOrdered(SelectedSteps, item);
        OnStepsChanged();
    }

    [RelayCommand]
    private void RemoveStep(StepPick? item)
    {
        if (item is null || !SelectedSteps.Remove(item)) return;
        // 미등록(알 수 없는) 키는 풀로 되돌리지 않는다 — 풀은 '지금 실행 가능한 스텝' 목록이어야 한다.
        if (item.Known) InsertOrdered(StepPool, item);
        OnStepsChanged();
    }

    /// <summary>풀 전체를 담는다 — 풀시퀀스와 실행 결과는 같지만, 나중에 스텝이 추가돼도 이 목록은 변하지 않는다.</summary>
    [RelayCommand]
    private void AddAllSteps()
    {
        foreach (var item in StepPool.ToList()) InsertOrdered(SelectedSteps, item);
        StepPool.Clear();
        OnStepsChanged();
    }

    /// <summary>비우면 풀시퀀스 전체 실행(저장값 null)으로 돌아간다.</summary>
    [RelayCommand]
    private void ClearSteps()
    {
        foreach (var item in SelectedSteps.Where(st => st.Known)) InsertOrdered(StepPool, item);
        SelectedSteps.Clear();
        OnStepsChanged();
    }

    private static void InsertOrdered(ObservableCollection<StepPick> list, StepPick item)
    {
        var i = 0;
        while (i < list.Count && list[i].SortKey <= item.SortKey) i++;
        list.Insert(i, item);
    }

    /// <summary>목록 편집 → 저장값(JSON) 재생성. IsDirty 는 StepKeysJson 변경으로 자동 설정된다.</summary>
    private void OnStepsChanged()
    {
        StepKeysJson = SelectedSteps.Count == 0
            ? null
            : JsonSerializer.Serialize(SelectedSteps.Select(st => st.Key).ToArray());
        OnPropertyChanged(nameof(IsFullSequence));
        OnPropertyChanged(nameof(StepSummary));
    }

    public RecipeRow(InspectionRecipe e, IEnumerable<InspectionProfile> profiles, IReadOnlyList<StepPick> stepPool)
    {
        _entity = e;
        _enabled = e.Enabled;
        _stepKeysJson = e.StepKeysJson;

        // 저장된 StepKeysJson 을 풀/포함 두 목록으로 나눈다. 풀에 없는 키(스텝 제거·오타로 남은 값)는
        // 조용히 버리지 않고 '미등록' 으로 표시해 담아 둔다 — 사용자가 보고 지울 수 있어야 한다.
        var saved = ParseKeys(e.StepKeysJson);
        var picked = saved
            .Select(key => stepPool.FirstOrDefault(st => st.Key == key) ?? new StepPick(key, key, int.MaxValue, Known: false))
            .OrderBy(st => st.SortKey);     // 저장 배열 순서는 런타임이 무시하므로 실행 순서로 보여 준다
        foreach (var st in picked) SelectedSteps.Add(st);
        foreach (var st in stepPool.Where(st => !saved.Contains(st.Key)))
            StepPool.Add(st);
        _visionFailRatioMax = e.VisionFailRatioMax;
        _cameraTargetDistanceMm = e.CameraTargetDistanceMm;

        var want = InspectionRecipeResolver.ProfileSeamTypeOf(e.Id);
        ProfileOptions = new List<ProfileOption> { ProfileOption.None };
        ProfileOptions.AddRange(profiles
            .Where(p => InspectionRecipeService.IsSeamMatch(p, want))
            .Select(p => new ProfileOption(p.Id, $"{p.Name} ({CountWaypoints(p.WaypointsJson)}점 · {p.UpdatedAt.ToLocalTime():MM-dd HH:mm})")));
        _selectedProfile = ProfileOptions.FirstOrDefault(o => o.Id == e.InspectionProfileId) ?? ProfileOption.None;
    }

    /// <summary>저장된 StepKeysJson → 키 목록. null/빈/깨진 값은 빈 목록(= 풀시퀀스).</summary>
    private static List<string> ParseKeys(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try
        {
            return JsonSerializer.Deserialize<string[]>(json)?.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct().ToList()
                   ?? new List<string>();
        }
        catch (JsonException) { return new List<string>(); }
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

/// <summary>
/// 시퀀스 풀 항목 — 등록된 <see cref="ISequenceStep"/> 하나의 표시용 스냅샷.
/// <paramref name="Known"/> 이 false 면 저장값에만 남아 있고 현재 등록되지 않은 키다(이름 변경·스텝 제거 흔적).
/// </summary>
public sealed record StepPick(string Key, string DisplayName, int DefaultOrder, bool Known = true)
{
    /// <summary>목록 정렬 키 — 미등록 키는 항상 끝으로.</summary>
    public int SortKey => Known ? DefaultOrder : int.MaxValue;

    /// <summary>실행 순서 배지. 런타임이 이 번호 순으로 실행한다.</summary>
    public string OrderText => Known ? DefaultOrder.ToString() : "?";

    public string Label => Known ? DisplayName : $"{Key} — 등록되지 않은 스텝";
}

/// <summary>레시피 행의 티칭 프로필 선택지. Id=null 은 미지정.</summary>
public sealed record ProfileOption(int? Id, string Label)
{
    public static readonly ProfileOption None = new(null, "— 미지정 —");
}
