namespace HD_AMR.Data.Entities;

public class ExcludedRegion
{
    public int Id { get; set; }
    public int DrawingId { get; set; }
    public double MinX { get; set; }
    public double MinY { get; set; }
    public double MaxX { get; set; }
    public double MaxY { get; set; }
    public DateTime CreatedAt { get; set; }

    public Drawing? Drawing { get; set; }
}
