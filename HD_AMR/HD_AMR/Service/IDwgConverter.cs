namespace HD_AMR.Service;

public record ConversionResult(bool Success, string? DxfPath, string? Error);

public interface IDwgConverter
{
    bool IsAvailable { get; }
    string? ConfiguredPath { get; }
    Task<ConversionResult> ConvertAsync(string dwgPath, string outputDir, CancellationToken ct = default);
}
