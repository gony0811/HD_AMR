using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace HD.AMR.App.Service.Vision;

/// <summary>라벨 종류. 면 = 칠한 영역 마스크(<c>_area.png</c>), 선 = 중심선 폴리라인(<c>_line.json</c>).</summary>
public enum LabelKind { Area, Line }

/// <summary>
/// 촬영본 라벨 파일 규칙. 면·선 라벨을 <b>서로 다른 파일</b>로 두어 한쪽을 저장해도 다른 쪽이 지워지지 않게 한다.
/// <list type="bullet">
/// <item><c>{stem}_{mod}.png</c> — 원본 이미지</item>
/// <item><c>{stem}_{mod}_area.png</c> — 면 라벨(흑백 이진 마스크, 흰색=비드)</item>
/// <item><c>{stem}_{mod}_line.json</c> — 선 라벨(중심선 점 + 두께). 학습 마스크는 변환 시 래스터화한다.</item>
/// </list>
/// 예전 규칙의 <c>_maskdraft.png</c>(면·선 공용 파일)는 <see cref="MigrateLegacy"/> 로 정리한다.
/// </summary>
public static class LabelFiles
{
    public const string LegacyFolder = "_legacy_maskdraft";

    public static string Mod(string modality) => modality == "ir" ? "ir" : "rgb";
    public static string ImageName(string stem, string mod) => $"{stem}_{Mod(mod)}.png";
    public static string AreaSuffix(string mod) => $"_{Mod(mod)}_area.png";
    public static string LineSuffix(string mod) => $"_{Mod(mod)}_line.json";
    public static string LegacySuffix(string mod) => $"_{Mod(mod)}_maskdraft.png";
    public static string AreaName(string stem, string mod) => stem + AreaSuffix(mod);
    public static string LineName(string stem, string mod) => stem + LineSuffix(mod);

    public static string KindText(LabelKind k) => k == LabelKind.Line ? "선" : "면";
    public static string DatasetFolder(LabelKind k) => k == LabelKind.Line ? "dataset_line" : "dataset_area";

    /// <summary>폴더에서 해당 종류 라벨이 있는 stem(정렬). <paramref name="requireImage"/> 면 원본 이미지도 있는 것만.</summary>
    public static List<string> LabeledStems(string dir, string mod, LabelKind kind, bool requireImage = true)
    {
        if (!Directory.Exists(dir)) return new();
        var suffix = kind == LabelKind.Line ? LineSuffix(mod) : AreaSuffix(mod);
        return Directory.GetFiles(dir, "*" + suffix)
            .Select(f => Path.GetFileName(f)[..^suffix.Length])
            .Where(s => !requireImage || File.Exists(Path.Combine(dir, ImageName(s, mod))))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 예전 <c>_maskdraft.png</c> 정리(파일 삭제 없음).
    /// 같은 stem 에 <c>_line.json</c> 이 있으면 그 마스크는 선에서 만든 것이므로 <see cref="LegacyFolder"/> 로 옮기고,
    /// 없으면 칠한 면 라벨이므로 <c>_area.png</c> 로 이름을 바꾼다(이미 <c>_area.png</c> 가 있으면 보관 폴더로).
    /// </summary>
    /// <returns>(면 라벨로 바꾼 수, 보관 폴더로 옮긴 수)</returns>
    public static (int ToArea, int Archived) MigrateLegacy(string dir)
    {
        int toArea = 0, archived = 0;
        if (!Directory.Exists(dir)) return (0, 0);
        foreach (var mod in new[] { "rgb", "ir" })
        {
            var suffix = LegacySuffix(mod);
            foreach (var f in Directory.GetFiles(dir, "*" + suffix))
            {
                var stem = Path.GetFileName(f)[..^suffix.Length];
                var area = Path.Combine(dir, AreaName(stem, mod));
                bool fromLine = File.Exists(Path.Combine(dir, LineName(stem, mod)));
                try
                {
                    if (!fromLine && !File.Exists(area)) { File.Move(f, area); toArea++; continue; }
                    var bak = Path.Combine(dir, LegacyFolder);
                    Directory.CreateDirectory(bak);
                    var dst = Path.Combine(bak, Path.GetFileName(f));
                    if (File.Exists(dst)) File.Delete(dst);   // 보관본 중복 — 최신 것으로 교체
                    File.Move(f, dst);
                    archived++;
                }
                catch (IOException) { /* 사용 중 파일 등 — 다음 기회에 다시 시도 */ }
            }
        }
        return (toArea, archived);
    }

    /// <summary>선 라벨을 래스터화한 흑백 PNG(흰색=비드). 학습 데이터셋 마스크용.</summary>
    public static void WriteLineMaskPng(WeldLineLabel line, string path)
    {
        var m = line.Rasterize();
        using var img = Image.LoadPixelData<L8>(m, line.Width, line.Height);
        img.SaveAsPng(path);
    }

    /// <summary>폴더 목록 정규화 — 공백·중복·없는 폴더 제거, 전체 경로로.</summary>
    public static List<string> NormalizeDirs(IEnumerable<string?> dirs)
        => dirs.Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => Path.GetFullPath(d!.Trim().Trim('"')).TrimEnd(Path.DirectorySeparatorChar))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static bool SameDir(string? a, string? b)
        => a is not null && b is not null &&
           string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
               Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
}
