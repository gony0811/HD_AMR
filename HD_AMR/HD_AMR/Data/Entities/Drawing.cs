namespace HD_AMR.Data.Entities;

public class Drawing
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string FileName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string? DxfPath { get; set; }
    public string? ConversionError { get; set; }
    public DateTime UploadedAt { get; set; }

    public List<DrawingSegment> Segments { get; set; } = new();
    public List<ExcludedRegion> ExcludedRegions { get; set; } = new();
}
