using HD.AMR.App.Service;
using OpenCvSharp;

namespace HD.AMR.App.Service.Vision;

/// <summary>
/// DL 라벨링용 데이터 폴더 접근 서비스. 라벨링 폴더(<see cref="LabelDirKey"/>, 미지정이면 캡처 저장 폴더)에서
/// 촬영 이미지 목록을 만들고, 파일을 안전하게 읽고(경로 탈출 차단), 면/선 라벨을 <b>각각 다른 파일</b>로 저장한다
/// (<see cref="LabelFiles"/> 규칙 — 한쪽 저장이 다른 쪽을 지우지 않는다).
/// </summary>
public class LabelDataService
{
    public const string CaptureDirKey = "Camera.Capture.Dir";
    /// <summary>비드 라벨링 대상 폴더. 비어 있으면 캡처 저장 폴더를 쓴다.</summary>
    public const string LabelDirKey = "Vision.Label.Dir";
    private readonly ParameterService _param;

    public LabelDataService(ParameterService param) => _param = param;

    /// <summary>라벨링 폴더(미지정이면 캡처 저장 폴더). 둘 다 없으면 null.</summary>
    public async Task<string?> GetDirAsync()
    {
        var dir = await _param.GetAsync(LabelDirKey);
        if (string.IsNullOrWhiteSpace(dir)) dir = await _param.GetAsync(CaptureDirKey);
        return string.IsNullOrWhiteSpace(dir) ? null : dir;
    }

    /// <summary>라벨링 폴더 지정(빈값 = 캡처 저장 폴더 사용).</summary>
    public Task SetDirAsync(string? dir)
        => _param.SetAsync(LabelDirKey, dir?.Trim() ?? "", "비드 라벨링 대상 폴더(빈값=캡처 저장 폴더)");

    /// <summary>폴더의 촬영본 목록(최신순). 각 stem 의 rgb/ir 이미지·면/선 라벨 존재 여부. 예전 _maskdraft 는 먼저 정리.</summary>
    public async Task<List<CaptureEntry>> ListAsync()
    {
        var dir = await GetDirAsync();
        return dir is null ? new() : List(dir);
    }

    /// <summary>지정 폴더의 촬영본 목록(최신순).</summary>
    public static List<CaptureEntry> List(string dir)
    {
        if (!Directory.Exists(dir)) return new();
        LabelFiles.MigrateLegacy(dir);

        var entries = new Dictionary<string, CaptureEntry>();
        CaptureEntry Get(string stem) =>
            entries.TryGetValue(stem, out var e) ? e : (entries[stem] = new CaptureEntry { Stem = stem });
        void Scan(string suffix, Action<CaptureEntry> set, bool needImage)
        {
            foreach (var f in Directory.GetFiles(dir, "*" + suffix))
            {
                var stem = TrimSuffix(Path.GetFileName(f), suffix);
                if (stem is null || (needImage && !entries.ContainsKey(stem))) continue;
                set(Get(stem));
            }
        }

        Scan("_rgb.png", e => e.HasRgb = true, false);
        Scan("_ir.png", e => e.HasIr = true, false);
        Scan(LabelFiles.AreaSuffix("rgb"), e => e.RgbArea = true, true);
        Scan(LabelFiles.AreaSuffix("ir"), e => e.IrArea = true, true);
        Scan(LabelFiles.LineSuffix("rgb"), e => e.RgbLine = true, true);
        Scan(LabelFiles.LineSuffix("ir"), e => e.IrLine = true, true);

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

    /// <summary>면 라벨 저장 → {stem}_{modality}_area.png (덮어쓰기). <paramref name="png"/> 가 null 이면 면 라벨 삭제.</summary>
    public async Task<string?> SaveAreaAsync(string stem, string modality, byte[]? png)
    {
        var dir = await GetDirAsync() ?? throw new InvalidOperationException("라벨링 폴더가 설정되지 않았습니다.");
        if (!IsSafeName(stem + ".png")) throw new ArgumentException("잘못된 파일 stem 입니다.", nameof(stem));
        var name = LabelFiles.AreaName(stem, modality);
        var path = Path.Combine(dir, name);
        if (png is null) { if (File.Exists(path)) File.Delete(path); return null; }
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(path, png);
        return name;
    }

    /// <summary>선 라벨(정답) 읽기 → {stem}_{modality}_line.json. 없거나 손상이면 null.</summary>
    public async Task<WeldLineLabel?> ReadLineAsync(string stem, string modality)
    {
        var dir = await GetDirAsync();
        if (dir is null || !IsSafeName(stem + ".png")) return null;
        var path = Path.Combine(dir, LabelFiles.LineName(stem, modality));
        return File.Exists(path) ? WeldLineLabel.FromJson(await File.ReadAllTextAsync(path)) : null;
    }

    /// <summary>선 라벨 저장 → {stem}_{modality}_line.json. <paramref name="label"/> 이 null 이면 선 라벨 삭제(면 라벨은 그대로).</summary>
    public async Task<string?> SaveLineAsync(string stem, string modality, WeldLineLabel? label)
    {
        var dir = await GetDirAsync() ?? throw new InvalidOperationException("라벨링 폴더가 설정되지 않았습니다.");
        if (!IsSafeName(stem + ".png")) throw new ArgumentException("잘못된 파일 stem 입니다.", nameof(stem));
        var name = LabelFiles.LineName(stem, modality);
        var path = Path.Combine(dir, name);
        if (label is null) { if (File.Exists(path)) File.Delete(path); return null; }
        if (!label.IsValid) throw new ArgumentException("선 라벨은 2점 이상이어야 합니다.", nameof(label));
        await File.WriteAllTextAsync(path, label.ToJson());
        return name;
    }

    /// <summary>
    /// 외부/업로드 이미지를 라벨링 폴더에 새 촬영본으로 저장(실패 케이스 추가학습용). 파일명 규약을
    /// 지켜 저장하므로 목록·라벨링·데이터셋 변환이 자동 인식한다. 반환: 저장된 stem.
    /// </summary>
    public async Task<string> SaveCaptureImageAsync(byte[] bytes, string modality)
    {
        var dir = await GetDirAsync() ?? throw new InvalidOperationException("라벨링 폴더가 설정되지 않았습니다.");
        modality = modality == "ir" ? "ir" : "rgb";
        Directory.CreateDirectory(dir);

        // 카메라 캡처와 동일한 일련번호 접두("NNN_")를 붙이고, 실패 케이스 식별용 hard_ 표식을 유지.
        // → "NNN_hard_yyyyMMdd_HHmmss". 충돌 시 _2, _3… 를 덧붙여 덮어쓰기를 막는다.
        var prefix = CaptureNaming.Prefix(CaptureNaming.NextSeq(dir));
        var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var stem = $"{prefix}hard_{ts}";
        for (int k = 2; Directory.GetFiles(dir, stem + "_*").Length > 0; k++) stem = $"{prefix}hard_{ts}_{k}";

        var target = Path.Combine(dir, $"{stem}_{modality}.png");
        await Task.Run(() =>
        {
            // 디코드 후 PNG 로 재인코딩(채널/포맷 정규화). 실패하면 원본 바이트를 그대로 기록.
            // Cv2.ImWrite 는 한글 경로에서 마샬링 실패하므로 CvIo(유니코드 안전)로 기록한다.
            using var m = Cv2.ImDecode(bytes, ImreadModes.Unchanged);
            if (m.Empty()) File.WriteAllBytes(target, bytes);
            else CvIo.WriteMat(target, m);
        });
        return stem;
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
    /// <summary>면 라벨(_area.png) 존재 여부.</summary>
    public bool RgbArea { get; set; }
    public bool IrArea { get; set; }
    /// <summary>선 라벨(정답 JSON) 존재 여부.</summary>
    public bool RgbLine { get; set; }
    public bool IrLine { get; set; }
}
