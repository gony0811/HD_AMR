using System.Text.Json;
using HD_AMR.Models;

namespace HD_AMR.Communication.Weld;

/// <summary>Peak ROI·Weld ROI 한 묶음 + 저장 당시 프레임 해상도(명세서 6.5.4).</summary>
public sealed class RoiProfile
{
    public string Name { get; set; } = "default";
    public RoiRect? PeakRoi { get; set; }
    public RoiRect? WeldRoi { get; set; }
    public int FrameWidth { get; set; }
    public int FrameHeight { get; set; }
    public DateTime SavedAt { get; set; }

    // 스케일: Depth 자동 기본 + 2점 보정계수(보강)
    public double ScaleCorrection { get; set; } = 1.0;
    public bool ScaleCorrectionEnabled { get; set; }
}

/// <summary>
/// ROI 프로파일을 JSON 파일로 Save/Load 한다(명세서 6.5.4 권장 방식). 프로파일명 = 파일명.
/// 해상도 변경 시 유효성 검사는 호출 측(<see cref="ValidateAndClamp"/>)에서 수행.
/// </summary>
public sealed class RoiProfileStore
{
    private readonly string _dir;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public RoiProfileStore(string directory)
    {
        _dir = directory;
        Directory.CreateDirectory(_dir);
    }

    public string Directory_ => _dir;

    private string PathFor(string name)
    {
        var safe = string.Concat(name.Split(Path.GetInvalidFileNameChars()));
        if (string.IsNullOrWhiteSpace(safe)) safe = "default";
        return Path.Combine(_dir, safe + ".json");
    }

    public IReadOnlyList<string> List()
    {
        if (!Directory.Exists(_dir)) return Array.Empty<string>();
        return Directory.GetFiles(_dir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .OrderBy(n => n)
            .ToArray();
    }

    public void Save(RoiProfile profile)
    {
        profile.SavedAt = DateTime.Now;
        File.WriteAllText(PathFor(profile.Name), JsonSerializer.Serialize(profile, JsonOpts));
    }

    public RoiProfile? Load(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<RoiProfile>(File.ReadAllText(path)); }
        catch { return null; }
    }

    public bool Delete(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    /// <summary>
    /// 현재 프레임 해상도에 맞춰 ROI 를 검사·보정한다. 범위를 벗어나면 clamp 하고 changed=true.
    /// </summary>
    public static RoiRect? ValidateAndClamp(RoiRect? roi, int frameW, int frameH, out bool changed)
    {
        changed = false;
        if (roi is null) return null;
        var clamped = roi.ClampTo(frameW, frameH);
        changed = clamped is null || clamped != roi;
        return clamped;
    }
}
