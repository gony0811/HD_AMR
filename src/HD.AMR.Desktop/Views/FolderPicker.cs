using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace HD.AMR.Desktop.Views;

/// <summary>폴더 선택 대화상자 공용 헬퍼. 취소하면 null.</summary>
internal static class FolderPicker
{
    public static async Task<string?> PickAsync(Visual owner, string title, string? startDir = null)
    {
        var top = TopLevel.GetTopLevel(owner);
        if (top is null) return null;
        IStorageFolder? start = null;
        if (!string.IsNullOrWhiteSpace(startDir) && Directory.Exists(startDir))
            start = await top.StorageProvider.TryGetFolderFromPathAsync(startDir);
        var picked = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        var path = picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }
}
