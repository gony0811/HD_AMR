using HD_AMR.Service;

namespace HD_AMR.Web.Services;

/// <summary>
/// DL 라벨링용 캡처 폴더 접근 서비스. 캡처 저장 폴더(ParameterService 의 Camera.Capture.Dir)에서
/// 촬영 이미지 목록을 만들고, 파일을 안전하게 읽고(경로 탈출 차단), 편집한 마스크 초안을 저장한다.
/// 신규 캡처든 기존 촬영본이든 같은 규약(stem_modality[_maskdraft].png)으로 다룬다.
/// </summary>
public class LabelDataService
{
    public const string CaptureDirKey = "Camera.Capture.Dir";
    private readonly ParameterService _param;

    public LabelDataService(ParameterService param) => _param = param;

    /// <summary>설정된 캡처 저장 폴더. 미설정이면 null.</summary>
    public async Task<string?> GetDirAsync()
    {
        var dir = await _param.GetAsync(CaptureDirKey);
        return string.IsNullOrWhiteSpace(dir) ? null : dir;
    }

    /// <summary>폴더의 촬영본 목록(최신순). 각 stem 의 rgb/ir 이미지·마스크 존재 여부.</summary>
    public async Task<List<CaptureEntry>> ListAsync()
    {
        var dir = await GetDirAsync();
        if (dir is null || !Directory.Exists(dir)) return new();

        var entries = new Dictionary<string, CaptureEntry>();
        CaptureEntry Get(string stem) =>
            entries.TryGetValue(stem, out var e) ? e : (entries[stem] = new CaptureEntry { Stem = stem });

        foreach (var f in Directory.GetFiles(dir, "*_rgb.png"))
        {
            var stem = TrimSuffix(Path.GetFileName(f), "_rgb.png");
            if (stem is not null) Get(stem).HasRgb = true;
        }
        foreach (var f in Directory.GetFiles(dir, "*_ir.png"))
        {
            var stem = TrimSuffix(Path.GetFileName(f), "_ir.png");
            if (stem is not null) Get(stem).HasIr = true;
        }
        foreach (var f in Directory.GetFiles(dir, "*_rgb_maskdraft.png"))
        {
            var stem = TrimSuffix(Path.GetFileName(f), "_rgb_maskdraft.png");
            if (stem is not null) Get(stem).RgbMask = true;
        }
        foreach (var f in Directory.GetFiles(dir, "*_ir_maskdraft.png"))
        {
            var stem = TrimSuffix(Path.GetFileName(f), "_ir_maskdraft.png");
            if (stem is not null) Get(stem).IrMask = true;
        }

        return entries.Values.OrderByDescending(e => e.Stem).ToList();
    }

    /// <summary>폴더 내 파일 바이트를 읽는다(파일명만 허용 — 경로 분리자/상위경로 차단). 없으면 null.</summary>
    public async Task<byte[]?> ReadAsync(string name)
    {
        var dir = await GetDirAsync();
        if (dir is null || !IsSafeName(name)) return null;
        var path = Path.Combine(dir, name);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path) : null;
    }

    /// <summary>편집한 마스크 초안 저장 → {stem}_{modality}_maskdraft.png (덮어쓰기).</summary>
    public async Task<string?> SaveMaskAsync(string stem, string modality, byte[] png)
    {
        var dir = await GetDirAsync();
        if (dir is null) throw new InvalidOperationException("캡처 저장 폴더가 설정되지 않았습니다.");
        modality = modality == "ir" ? "ir" : "rgb";
        if (!IsSafeName(stem + ".png")) throw new ArgumentException("잘못된 파일 stem 입니다.", nameof(stem));
        Directory.CreateDirectory(dir);
        var name = $"{stem}_{modality}_maskdraft.png";
        var path = Path.Combine(dir, name);
        await File.WriteAllBytesAsync(path, png);
        return name;
    }

    private static string? TrimSuffix(string fileName, string suffix)
        => fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? fileName[..^suffix.Length] : null;

    // 파일명만 허용(디렉터리 탈출 방지).
    private static bool IsSafeName(string name)
        => !string.IsNullOrWhiteSpace(name)
           && name.IndexOfAny(new[] { '/', '\\' }) < 0
           && !name.Contains("..")
           && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
}

/// <summary>촬영본 한 건(같은 타임스탬프 stem)의 구성.</summary>
public class CaptureEntry
{
    public string Stem { get; set; } = "";
    public bool HasRgb { get; set; }
    public bool HasIr { get; set; }
    public bool RgbMask { get; set; }
    public bool IrMask { get; set; }
}
