namespace HD_AMR.Data.Entities;

public class DrawingSegment
{
    public int Id { get; set; }
    public int DrawingId { get; set; }
    public int Number { get; set; }
    public double StartX { get; set; }
    public double StartY { get; set; }
    public double EndX { get; set; }
    public double EndY { get; set; }

    public Drawing? Drawing { get; set; }
}
