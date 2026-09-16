using Avalonia.Data.Converters;

namespace HD.AMR.Desktop.ViewModels;

public static class RecipeConverters
{
    /// <summary>펼침 여부 → 셰브런(▾ 펼침 / ▸ 접힘).</summary>
    public static readonly IValueConverter Chevron =
        new FuncValueConverter<bool, string>(expanded => expanded ? "▾" : "▸");
}
