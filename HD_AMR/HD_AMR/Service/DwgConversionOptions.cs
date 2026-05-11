namespace HD_AMR.Service;

public class DwgConversionOptions
{
    public string? OdaFileConverterPath { get; set; }
    public string OutputVersion { get; set; } = "ACAD2018";
    public int TimeoutSeconds { get; set; } = 60;
}
