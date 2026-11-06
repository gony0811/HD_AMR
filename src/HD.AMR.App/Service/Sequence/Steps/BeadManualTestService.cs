using System.Runtime.InteropServices;
using HD.AMR.App.Models;
using HD.AMR.App.Service.Vision;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace HD.AMR.App.Service.Sequence.Steps;

/// <summary>
/// 수동 비드선을 학습 데이터로 저장한다. 라이브 스냅샷이면 캡처 폴더에 새 촬영본(프레임 4장)을 만들고 그 위에 라벨을,
/// 저장된 촬영본을 연 것이면 그 촬영본 옆에 라벨(<c>{stem}_{modality}_line.json</c>·<c>_maskdraft.png</c>)만 쓴다(덮어쓰기).
/// </summary>
public static class ManualBeadLabelWriter
{
    /// <returns>저장된 stem.</returns>
    /// <exception cref="InvalidOperationException">캡처 폴더 미설정·영상 없음·선 무효.</exception>
    public static async Task<string> SaveAsync(CameraService camera, LabelDataService labels, ManualBeadSnapshot s,
        string modality, IReadOnlyList<PixelPoint> points, string tag, CancellationToken ct)
    {
        modality = modality == "rgb" ? "rgb" : "ir";
        var guide = s.GuideFor(modality) ?? throw new InvalidOperationException($"{modality.ToUpperInvariant()} 영상이 없습니다.");
        var label = new WeldLineLabel
        {
            Width = guide.Width,
            Height = guide.Height,
            Points = points.Select(p => new[] { Math.Round(p.X, 1), Math.Round(p.Y, 1) }).ToList(),
        };
        if (!label.IsValid) throw new InvalidOperationException("선 라벨은 2점 이상이어야 합니다.");
        var mask = await Task.Run(() => MaskPng(label), ct);

        if (s.Frames.SavedPath is { } saved)
        {
            await File.WriteAllTextAsync(Path.Combine(saved.Dir, $"{saved.Stem}_{modality}_line.json"), label.ToJson(), ct);
            await File.WriteAllBytesAsync(Path.Combine(saved.Dir, $"{saved.Stem}_{modality}_maskdraft.png"), mask, ct);
            return saved.Stem;
        }

        var dir = await labels.GetDirAsync()
                  ?? throw new InvalidOperationException("캡처 폴더가 설정되지 않았습니다(Camera 페이지에서 폴더 선택).");
        var cap = await camera.SaveFramesAsync(dir, s.Color, s.Ir, s.Depth, tag, ct);
        await labels.SaveLineAsync(cap.Timestamp, modality, label);
        await labels.SaveMaskAsync(cap.Timestamp, modality, mask);
        return cap.Timestamp;
    }

    private static byte[] MaskPng(WeldLineLabel label)
    {
        using var img = Image.LoadPixelData<L8>(label.Rasterize(), label.Width, label.Height);
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        return ms.ToArray();
    }
}

/// <summary>
/// 수동 비드 입력 <b>테스트</b>(Dashboard 의 수동 팝업). 라이브 프레임 또는 저장된 촬영본으로 팝업을 띄워
/// 선을 그리고 d 를 확인한다. 시퀀스 측정 슬롯(M1/M2)·Bag 은 건드리지 않는다. ROI 는 시퀀스와 같은 파라미터.
/// </summary>
public sealed class BeadManualTestService
{
    private readonly WeldTrackingService _weld;
    private readonly CameraService _camera;
    private readonly ParameterService _param;
    private readonly LabelDataService _labels;

    public BeadManualTestService(WeldTrackingService weld, CameraService camera, ParameterService param, LabelDataService labels)
    {
        _weld = weld;
        _camera = camera;
        _param = param;
        _labels = labels;
    }

    /// <summary>용접 추적 화면의 현재 진행축(테스트 팝업 기본값).</summary>
    public WeldProgressAxis DefaultAxis => _weld.Params.ProgressAxis;

    /// <summary>현재 검출 입력 모드("ir"/"rgb") — 팝업이 처음 보여줄 영상.</summary>
    public string DefaultModality => _weld.Params.Mode == WeldImageMode.Ir ? "ir" : "rgb";

    public Task<string?> GetCaptureDirAsync() => _labels.GetDirAsync();

    /// <summary>현재 라이브 프레임 스냅샷. 스트림이 없으면 null.</summary>
    public async Task<ManualBeadSnapshot?> CaptureLiveAsync(WeldProgressAxis axis, CancellationToken ct = default)
    {
        var frames = _weld.LiveManualFrames();
        return frames is null ? null : await RebuildAsync(frames, axis, ct);
    }

    /// <summary>같은 프레임으로 진행축만 바꿔 다시 계산.</summary>
    public async Task<ManualBeadSnapshot> RebuildAsync(ManualBeadFrames frames, WeldProgressAxis axis, CancellationToken ct = default)
    {
        int w = frames.Ir.Width, h = frames.Ir.Height;
        var (peak, _) = await WeldSequenceSupport.GetPeakRoiAsync(_param, w, h);
        var (bead, _) = await WeldSequenceSupport.GetBeadRoiAsync(_param, w, h);
        var full = new RoiRect(0, 0, w, h);
        return await _weld.BuildManualSnapshotAsync(frames, peak ?? full, bead ?? full, axis, ct);
    }

    /// <summary>
    /// 저장된 촬영본 열기. <paramref name="path"/> 는 같은 stem 의 아무 파일(_ir.png·_rgb.png·_depth16.png…).
    /// IR 영상은 필수, Depth(<c>_depth16.png</c>)가 있으면 Peak 를 찾고, 컬러는 카메라 캘리브레이션이 있을 때만 쓴다.
    /// </summary>
    public async Task<ManualBeadSnapshot> LoadSavedAsync(string path, WeldProgressAxis axis, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(path) ?? throw new ArgumentException("잘못된 경로입니다.", nameof(path));
        var stem = StemOf(Path.GetFileName(path))
                   ?? throw new InvalidOperationException("촬영본 파일명이 아닙니다(…_ir.png / _rgb.png / _depth16.png).");
        string P(string suffix) => Path.Combine(dir, $"{stem}_{suffix}");

        var irPath = P("ir.png");
        if (!File.Exists(irPath))
            throw new InvalidOperationException($"IR 영상이 없습니다: {stem}_ir.png — 수동 입력은 IR 좌표 기준이라 IR 영상이 필요합니다.");

        var frames = await Task.Run(() =>
        {
            var t = File.GetLastWriteTime(irPath);
            using var irImg = Image.Load<L8>(irPath);
            var irBuf = new byte[irImg.Width * irImg.Height];
            irImg.CopyPixelDataTo(irBuf);
            var ir = new CameraFrame(irBuf, irImg.Width, irImg.Height, "ir8", t);

            CameraFrame? depth = null;
            if (File.Exists(P("depth16.png")))
            {
                using var d = Image.Load<L16>(P("depth16.png"));
                var px = new L16[d.Width * d.Height];
                d.CopyPixelDataTo(px);
                depth = new CameraFrame(MemoryMarshal.AsBytes(px.AsSpan()).ToArray(), d.Width, d.Height, "depth16", t);
            }

            CameraFrame? color = null;
            if (File.Exists(P("rgb.png")))
            {
                using var c = Image.Load<Rgb24>(P("rgb.png"));
                var buf = new byte[c.Width * c.Height * 3];
                c.CopyPixelDataTo(buf);
                color = new CameraFrame(buf, c.Width, c.Height, "rgb24", t);
            }
            return new ManualBeadFrames(ir, depth, color, _camera.GetD2CParams(), $"파일 {stem}", new ManualBeadSavedRef(dir, stem));
        }, ct);

        return await RebuildAsync(frames, axis, ct);
    }

    public ManualBeadEvaluation? Evaluate(ManualBeadSnapshot s, string modality, IReadOnlyList<PixelPoint> points)
        => _weld.EvaluateManualBead(s, modality, points);

    /// <summary>학습 데이터 저장. 반환: 저장된 stem.</summary>
    public Task<string> SaveAsync(ManualBeadSnapshot s, string modality, IReadOnlyList<PixelPoint> points, CancellationToken ct = default)
        => ManualBeadLabelWriter.SaveAsync(_camera, _labels, s, modality, points, tag: "test", ct);

    /// <summary>촬영본 파일명 → stem. 알 수 없는 형식이면 null.</summary>
    public static string? StemOf(string fileName)
    {
        string[] suffixes =
        [
            "_ir_maskdraft.png", "_rgb_maskdraft.png", "_ir_line.json", "_rgb_line.json",
            "_depth_vis.png", "_depth16.png", "_ir.png", "_rgb.png",
        ];
        foreach (var sfx in suffixes)
            if (fileName.EndsWith(sfx, StringComparison.OrdinalIgnoreCase) && fileName.Length > sfx.Length)
                return fileName[..^sfx.Length];
        return null;
    }
}
