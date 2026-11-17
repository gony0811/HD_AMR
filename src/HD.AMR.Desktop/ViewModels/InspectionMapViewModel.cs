using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Inspection;
using Microsoft.Extensions.DependencyInjection;
using HD.AMR.App.Models;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>
/// 검사 매핑 요약(읽기 전용). 기존 InspectionMap.razor 이식 —
/// ① wall_code × seamType → 레시피 매핑 레퍼런스, ② 레시피 카탈로그 상태, ③ 도면 티칭(경유점) 현황.
/// </summary>
public sealed partial class InspectionMapViewModel : ViewModelBase
{
    private readonly IServiceScopeFactory _scopeFactory;

    public ObservableCollection<WallMapRow> MapRows { get; } = new();
    public ObservableCollection<RecipeStatusRow> Recipes { get; } = new();
    public ObservableCollection<TeachingRow> Teaching { get; } = new();

    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string? _message;

    public InspectionMapViewModel(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public override async void OnActivated() => await ReloadAsync();

    [RelayCommand]
    private async Task ReloadAsync()
    {
        if (Busy) return;
        Busy = true; Message = null;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<InspectionRecipeService>();
            var recipes = await svc.ListAsync();
            var byId = recipes.ToDictionary(r => r.Id);
            var teaching = await svc.ListTeachingSummaryAsync();

            MapRows.Clear();
            foreach (var w in WallCodes.All)   // 정본 10코드 — App WallCodes 단일 정의
                MapRows.Add(new WallMapRow(w.Code, w.DisplayName,
                    Cell(SeamTypeKind.Line, w.Code, byId),
                    Cell(SeamTypeKind.Cross3R0, w.Code, byId), Cell(SeamTypeKind.Cross3R90, w.Code, byId),
                    Cell(SeamTypeKind.Cross3R180, w.Code, byId), Cell(SeamTypeKind.Cross3R270, w.Code, byId),
                    Cell(SeamTypeKind.Cross4L, w.Code, byId), Cell(SeamTypeKind.Cross4R, w.Code, byId),
                    Cell(SeamTypeKind.Corner2, w.Code, byId), Cell(SeamTypeKind.Corner, w.Code, byId)));

            Recipes.Clear();
            foreach (var r in recipes)
                Recipes.Add(new RecipeStatusRow(r.Id, r.SeamType.ToString(), r.Orientation.ToString(), r.Enabled,
                    r.SeamType switch
                    {
                        SeamTypeKind.Corner => "실행 시퀀스 제거됨",
                        SeamTypeKind.Corner2 => "실행 스텝 미구현 (게이트 OFF) — corner2 슬롯/캡처 후속",
                        _ => "레시피 지정 X-Y 티칭 프로필 (검사 레시피에서 지정)",
                    },
                    r.SeamType == SeamTypeKind.Corner ? "—"
                    : (r.InspectionProfileId is null ? "미지정"
                        : teaching.FirstOrDefault(t => t.ProfileId == r.InspectionProfileId) is { } t
                            ? $"{t.ProfileName} ({t.WaypointCount}점)"
                            : $"프로필 없음(id={r.InspectionProfileId})")
                      + (InspectionRecipeResolver.ParseWallProfiles(r.WallProfilesJson) is { Count: > 0 } wp
                          ? $" · 면별 {string.Join(", ", wp.Select(kv => $"{kv.Key}=#{kv.Value}"))}" : "")));

            Teaching.Clear();
            foreach (var d in teaching)
            {
                var usedBy = recipes.Where(r => InspectionRecipeResolver.UsesProfile(r, d.ProfileId)).Select(r => r.Id).ToList();
                Teaching.Add(new TeachingRow(d.DrawingName, d.FileName, d.ProfileName ?? "—",
                    string.IsNullOrWhiteSpace(d.SeamType) ? "LINE" : d.SeamType!,
                    usedBy.Count > 0 ? string.Join(", ", usedBy) : "—",
                    d.WaypointCount, d.TaughtAt?.ToString("yyyy-MM-dd HH:mm") ?? "—",
                    usedBy.Count == 0 ? "ACS 미사용" : d.WaypointCount >= 2 ? "사용 중" : "경유점 부족",
                    usedBy.Count > 0 && d.WaypointCount >= 2));
            }
        }
        catch (Exception ex) { Message = $"로드 실패: {ex.Message}"; }
        finally { Busy = false; }
    }

    // 매핑 셀 — 레시피 id(+CORNER 거울 side, 십자 면별 프로필) 와 Enabled 여부.
    private static MapCell Cell(SeamTypeKind seam, string wallCode, Dictionary<string, HD.AMR.App.Data.Entities.InspectionRecipe> byId)
    {
        var id = InspectionRecipeResolver.ResolveRecipeId(seam, wallCode);
        var recipe = id is not null && byId.TryGetValue(id, out var r) ? r : null;
        var on = recipe?.Enabled == true;
        var label = id ?? "—";
        if (recipe is not null && InspectionRecipeResolver.ParseWallProfiles(recipe.WallProfilesJson).TryGetValue(wallCode, out var wallProfile))
            label += $" · #{wallProfile}";
        if (seam is SeamTypeKind.Corner or SeamTypeKind.Corner2 && id is not null)
            label += $" ({InspectionRecipeResolver.ResolveCornerSide(wallCode, out _)})";
        return new MapCell(label, on);
    }
}

public sealed record MapCell(string Label, bool Enabled);
public sealed record WallMapRow(string Code, string Label, MapCell Line,
    MapCell Cross3R0, MapCell Cross3R90, MapCell Cross3R180, MapCell Cross3R270,
    MapCell Cross4L, MapCell Cross4R, MapCell Corner2, MapCell Corner3);
public sealed record RecipeStatusRow(string Id, string SeamType, string Orientation, bool Enabled, string Source, string AssignedProfile);
public sealed record TeachingRow(string DrawingName, string FileName, string ProfileName, string SeamType, string UsedBy,
    int WaypointCount, string TaughtAt, string Status, bool Runnable);
