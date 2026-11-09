using HD.AMR.App.Service.Vision;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace HD.AMR.Tests;

/// <summary>면/선 라벨 파일 분리 규칙과 예전 _maskdraft 정리.</summary>
public class LabelFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hdamr_lbl_" + Guid.NewGuid().ToString("N"));

    public LabelFilesTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private void Touch(string name) => File.WriteAllBytes(Path.Combine(_dir, name), new byte[] { 1 });
    private bool Has(string name) => File.Exists(Path.Combine(_dir, name));

    [Fact]
    public void MigrateLegacy_PaintedMask_BecomesArea_LineMask_IsArchived()
    {
        Touch("001_a_rgb.png"); Touch("001_a_rgb_maskdraft.png");                          // 칠한 면 → _area
        Touch("002_b_ir.png"); Touch("002_b_ir_maskdraft.png"); Touch("002_b_ir_line.json"); // 선에서 만든 마스크 → 보관

        var (toArea, archived) = LabelFiles.MigrateLegacy(_dir);

        Assert.Equal(1, toArea);
        Assert.Equal(1, archived);
        Assert.True(Has("001_a_rgb_area.png"));
        Assert.False(Has("001_a_rgb_maskdraft.png"));
        Assert.True(Has("002_b_ir_line.json"));
        Assert.False(Has("002_b_ir_maskdraft.png"));
        Assert.True(Has(Path.Combine(LabelFiles.LegacyFolder, "002_b_ir_maskdraft.png")));
        Assert.Equal((0, 0), LabelFiles.MigrateLegacy(_dir));   // 재실행 무변화
    }

    [Fact]
    public void MigrateLegacy_DoesNotOverwriteExistingArea()
    {
        File.WriteAllBytes(Path.Combine(_dir, "003_c_rgb_area.png"), new byte[] { 7 });
        Touch("003_c_rgb_maskdraft.png");

        LabelFiles.MigrateLegacy(_dir);

        Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(Path.Combine(_dir, "003_c_rgb_area.png")));
        Assert.True(Has(Path.Combine(LabelFiles.LegacyFolder, "003_c_rgb_maskdraft.png")));
    }

    [Fact]
    public void LabeledStems_SeparatesAreaAndLine_AndRequiresImage()
    {
        Touch("010_x_rgb.png"); Touch("010_x_rgb_area.png"); Touch("010_x_rgb_line.json");
        Touch("011_y_rgb.png"); Touch("011_y_rgb_line.json");
        Touch("012_z_rgb_area.png");   // 원본 없음

        Assert.Equal(new[] { "010_x" }, LabelFiles.LabeledStems(_dir, "rgb", LabelKind.Area));
        Assert.Equal(new[] { "010_x", "011_y" }, LabelFiles.LabeledStems(_dir, "rgb", LabelKind.Line));
        Assert.Equal(new[] { "010_x", "012_z" }, LabelFiles.LabeledStems(_dir, "rgb", LabelKind.Area, requireImage: false));
        Assert.Empty(LabelFiles.LabeledStems(_dir, "ir", LabelKind.Line));
    }

    [Fact]
    public void List_ReportsAreaAndLineFlagsIndependently()
    {
        Touch("020_p_rgb.png"); Touch("020_p_ir.png");
        Touch("020_p_rgb_area.png"); Touch("020_p_ir_line.json");

        var e = Assert.Single(LabelDataService.List(_dir));
        Assert.True(e.RgbArea);
        Assert.False(e.RgbLine);
        Assert.False(e.IrArea);
        Assert.True(e.IrLine);
    }

    [Fact]
    public void WriteLineMaskPng_MatchesRasterize()
    {
        var line = new WeldLineLabel { Width = 40, Height = 20, Thickness = 5, Points = { new[] { 2.0, 10.0 }, new[] { 37.0, 10.0 } } };
        var path = Path.Combine(_dir, "m.png");

        LabelFiles.WriteLineMaskPng(line, path);

        using var img = Image.Load<L8>(path);
        Assert.Equal(40, img.Width);
        Assert.Equal(20, img.Height);
        var expected = line.Rasterize();
        for (int y = 0; y < 20; y++)
            for (int x = 0; x < 40; x++)
                Assert.Equal(expected[y * 40 + x], img[x, y].PackedValue);
    }

    [Fact]
    public void NormalizeDirs_RemovesBlankAndDuplicates()
    {
        var list = LabelFiles.NormalizeDirs(new[] { _dir, "", "  ", _dir + Path.DirectorySeparatorChar, null });
        Assert.Single(list);
        Assert.True(LabelFiles.SameDir(list[0], _dir));
    }
}
