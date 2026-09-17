using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Inspection;
using Microsoft.Extensions.DependencyInjection;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>
/// 검사 매핑 요약(읽기 전용). 기존 InspectionMap.razor 이식 —
/// ① wall_code × seamType → 레시피 매핑 레퍼런스, ② 레시피 카탈로그 상태, ③ 도면 티칭(경유점) 현황.
/// </summary>
public sealed partial class InspectionMapViewModel : ViewModelBase
{
    private readonly IServiceScopeFactory _scopeFactory;

    // 정본 10코드(§8.5.1 (2)) — ACS 발행값과 동일.
    private static readonly (string Code, string Label)[] WallCodes =
    {
        ("B", "바닥 (Floor)"), ("T", "천장 (Ceiling)"),
        ("SM", "수직벽 우현 (Starboard)"), ("PM", "수직벽 좌현 (Port)"),
        ("F", "선수 마구리 (Fore)"), ("A", "선미 마구리 (Aft)"),
        ("SL", "하부챔퍼 우현"), ("PL", "하부챔퍼 좌현"),
        ("SU", "상부챔퍼 우현"), ("PU", "상부챔퍼 좌현"),
    };

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
            var enabled = recipes.ToDictionary(r => r.Id, r => r.Enabled);
            var teaching = await svc.ListTeachingSummaryAsync();

            MapRows.Clear();
            foreach (var (code, label) in WallCodes)
                MapRows.Add(new WallMapRow(code, label,
                    Cell(SeamTypeKind.Line, code, enabled), Cell(SeamTypeKind.Cross3, code, enabled),
                    Cell(SeamTypeKind.Cross, code, enabled), Cell(SeamTypeKind.Corner2, code, enabled),
                    Cell(SeamTypeKind.Corner, code, enabled)));

            Recipes.Clear();
            foreach (var r in recipes)
                Recipes.Add(new RecipeStatusRow(r.Id, r.SeamType.ToString(), r.Orientation.ToString(), r.Enabled,
                    r.SeamType switch
                    {
                        SeamTypeKind.Corner => "고정 티칭 슬롯 corner3.* (도면 무관)",
                        SeamTypeKind.Corner2 => "실행 스텝 미구현 (게이트 OFF) — corner2 슬롯/캡처 후속",
                        SeamTypeKind.Cross => "X-Y 6-DOF 캡처 교시 프로필 (검사 포인트, CROSS4)",
                        SeamTypeKind.Cross3 => "X-Y 6-DOF 캡처 교시 프로필 (검사 포인트, CROSS3)",
                        _ => "도면 LINE 티칭 프로필 (sectionDxfId → 최신 InspectionProfile)",
                    }));

            Teaching.Clear();
            foreach (var d in teaching)
                Teaching.Add(new TeachingRow(d.DrawingName, d.FileName, d.ProfileName ?? "—",
                    d.HasProfile ? (string.IsNullOrWhiteSpace(d.SeamType) ? "LINE" : d.SeamType!) : "—",
                    d.WaypointCount, d.TaughtAt?.ToString("yyyy-MM-dd HH:mm") ?? "—",
                    d is { HasProfile: true, WaypointCount: > 0 } ? "실행 가능" : d.HasProfile ? "경유점 0" : "티칭 없음",
                    d is { HasProfile: true, WaypointCount: > 0 }));
        }
        catch (Exception ex) { Message = $"로드 실패: {ex.Message}"; }
        finally { Busy = false; }
    }

    // 매핑 셀 — 레시피 id(+CORNER 거울 side) 와 Enabled 여부.
    private static MapCell Cell(SeamTypeKind seam, string wallCode, Dictionary<string, bool> enabled)
    {
        var id = InspectionRecipeResolver.ResolveRecipeId(seam, wallCode);
        var on = id is not null && enabled.TryGetValue(id, out var e) && e;
        var label = id ?? "—";
        if (seam is SeamTypeKind.Corner or SeamTypeKind.Corner2 && id is not null)
            label += $" ({InspectionRecipeResolver.ResolveCornerSide(wallCode, out _)})";
        return new MapCell(label, on);
    }
}

public sealed record MapCell(string Label, bool Enabled);
public sealed record WallMapRow(string Code, string Label, MapCell Line, MapCell Cross3, MapCell Cross4, MapCell Corner2, MapCell Corner3);
public sealed record RecipeStatusRow(string Id, string SeamType, string Orientation, bool Enabled, string Source);
public sealed record TeachingRow(string DrawingName, string FileName, string ProfileName, string SeamType,
    int WaypointCount, string TaughtAt, string Status, bool Runnable);
