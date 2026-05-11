using HD_AMR.Data;
using HD_AMR.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using netDxf;
using netDxf.Blocks;
using netDxf.Entities;

namespace HD_AMR.Service;

public record LineSegment(double X1, double Y1, double X2, double Y2);

public class DrawingService
{
    private const double SegmentLengthMm = 10.0;

    private readonly HdAmrDbContext _db;
    private readonly DrawingStorageOptions _options;
    private readonly IDwgConverter _converter;

    public DrawingService(HdAmrDbContext db, IOptions<DrawingStorageOptions> options, IDwgConverter converter)
    {
        _db = db;
        _options = options.Value;
        _converter = converter;
        Directory.CreateDirectory(_options.UploadDirectory);
    }

    public async Task<Drawing> SaveAsync(string name, string fileName, Stream content, CancellationToken ct = default)
    {
        var safeName = MakeSafeFileName(fileName);
        var prefix = Guid.NewGuid().ToString("N");
        var storedName = $"{prefix}_{safeName}";
        var fullOriginalPath = Path.Combine(_options.UploadDirectory, storedName);

        await using (var fs = File.Create(fullOriginalPath))
        {
            await content.CopyToAsync(fs, ct);
        }

        string? dxfPath = null;
        string? conversionError = null;

        if (IsDwg(fileName))
        {
            if (!_converter.IsAvailable)
            {
                conversionError = $"ODA File Converter가 설정되지 않았거나 찾을 수 없습니다 (경로: {_converter.ConfiguredPath ?? "(미설정)"}). .dwg 파일은 보관만 됩니다.";
            }
            else
            {
                var result = await _converter.ConvertAsync(fullOriginalPath, _options.UploadDirectory, ct);
                if (result.Success && result.DxfPath != null)
                {
                    var dxfFileName = Path.GetFileName(result.DxfPath);
                    var finalFullPath = Path.Combine(_options.UploadDirectory, dxfFileName);
                    if (!string.Equals(result.DxfPath, finalFullPath, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Move(result.DxfPath, finalFullPath, overwrite: true);
                    }
                    dxfPath = dxfFileName;
                }
                else
                {
                    conversionError = result.Error ?? "알 수 없는 변환 오류";
                }
            }
        }
        else if (IsDxf(fileName))
        {
            dxfPath = storedName;
        }
        else
        {
            conversionError = $"지원하지 않는 확장자입니다: {Path.GetExtension(fileName)}";
        }

        var drawing = new Drawing
        {
            Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(fileName) : name,
            FileName = fileName,
            FilePath = storedName,
            DxfPath = dxfPath,
            ConversionError = conversionError,
            UploadedAt = DateTime.UtcNow
        };
        _db.Drawings.Add(drawing);
        await _db.SaveChangesAsync(ct);
        return drawing;
    }

    public Task<List<Drawing>> ListAsync(CancellationToken ct = default) =>
        _db.Drawings.AsNoTracking().OrderByDescending(d => d.Id).ToListAsync(ct);

    public Task<Drawing?> GetAsync(int id, CancellationToken ct = default) =>
        _db.Drawings
            .Include(d => d.Segments.OrderBy(s => s.Number))
            .Include(d => d.ExcludedRegions)
            .FirstOrDefaultAsync(d => d.Id == id, ct);

    public Task<List<ExcludedRegion>> ListRegionsAsync(int drawingId, CancellationToken ct = default) =>
        _db.ExcludedRegions.AsNoTracking()
            .Where(r => r.DrawingId == drawingId)
            .OrderBy(r => r.Id)
            .ToListAsync(ct);

    public async Task<ExcludedRegion> AddRegionAsync(int drawingId, double minX, double minY, double maxX, double maxY, CancellationToken ct = default)
    {
        if (minX > maxX) (minX, maxX) = (maxX, minX);
        if (minY > maxY) (minY, maxY) = (maxY, minY);

        var region = new ExcludedRegion
        {
            DrawingId = drawingId,
            MinX = minX,
            MinY = minY,
            MaxX = maxX,
            MaxY = maxY,
            CreatedAt = DateTime.UtcNow
        };
        _db.ExcludedRegions.Add(region);

        // 영역이 바뀌면 기존 분석 결과는 stale → 비움
        var staleSegments = _db.DrawingSegments.Where(s => s.DrawingId == drawingId);
        _db.DrawingSegments.RemoveRange(staleSegments);

        await _db.SaveChangesAsync(ct);
        return region;
    }

    public async Task ClearRegionsAsync(int drawingId, CancellationToken ct = default)
    {
        var regions = _db.ExcludedRegions.Where(r => r.DrawingId == drawingId);
        _db.ExcludedRegions.RemoveRange(regions);

        var staleSegments = _db.DrawingSegments.Where(s => s.DrawingId == drawingId);
        _db.DrawingSegments.RemoveRange(staleSegments);

        await _db.SaveChangesAsync(ct);
    }

    public List<LineSegment> GetLines(Drawing drawing)
    {
        if (string.IsNullOrEmpty(drawing.DxfPath)) return new();
        var fullPath = Path.Combine(_options.UploadDirectory, drawing.DxfPath);
        if (!File.Exists(fullPath)) return new();

        var doc = DxfDocument.Load(fullPath);
        if (doc == null) return new();

        var lines = ExtractLines(doc);

        var regions = drawing.ExcludedRegions;
        if (regions == null || regions.Count == 0) return lines;

        return lines.Where(ln => !IsFullyInsideAnyRegion(ln, regions)).ToList();
    }

    private static bool IsFullyInsideAnyRegion(LineSegment ln, IReadOnlyList<ExcludedRegion> regions)
    {
        foreach (var r in regions)
        {
            if (ln.X1 >= r.MinX && ln.X1 <= r.MaxX && ln.Y1 >= r.MinY && ln.Y1 <= r.MaxY &&
                ln.X2 >= r.MinX && ln.X2 <= r.MaxX && ln.Y2 >= r.MinY && ln.Y2 <= r.MaxY)
            {
                return true;
            }
        }
        return false;
    }

    public async Task<List<DrawingSegment>> AnalyzeAsync(int drawingId, CancellationToken ct = default)
    {
        var drawing = await _db.Drawings.Include(d => d.Segments).FirstOrDefaultAsync(d => d.Id == drawingId, ct)
            ?? throw new InvalidOperationException($"Drawing {drawingId} not found");

        if (string.IsNullOrEmpty(drawing.DxfPath))
            throw new InvalidOperationException(drawing.ConversionError ?? "DXF가 없어 분석할 수 없습니다.");

        var lines = GetLines(drawing);

        if (drawing.Segments.Count > 0)
        {
            _db.DrawingSegments.RemoveRange(drawing.Segments);
            drawing.Segments.Clear();
        }

        int number = 1;
        foreach (var ln in lines)
        {
            foreach (var seg in SplitLine(ln, SegmentLengthMm))
            {
                _db.DrawingSegments.Add(new DrawingSegment
                {
                    DrawingId = drawing.Id,
                    Number = number++,
                    StartX = seg.X1,
                    StartY = seg.Y1,
                    EndX = seg.X2,
                    EndY = seg.Y2
                });
            }
        }

        await _db.SaveChangesAsync(ct);

        return await _db.DrawingSegments
            .Where(s => s.DrawingId == drawingId)
            .OrderBy(s => s.Number)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var drawing = await _db.Drawings.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (drawing == null) return;
        TryDelete(Path.Combine(_options.UploadDirectory, drawing.FilePath));
        if (!string.IsNullOrEmpty(drawing.DxfPath) && drawing.DxfPath != drawing.FilePath)
            TryDelete(Path.Combine(_options.UploadDirectory, drawing.DxfPath));
        _db.Drawings.Remove(drawing);
        await _db.SaveChangesAsync(ct);
    }

    internal static IEnumerable<LineSegment> SplitLine(LineSegment line, double stepMm)
    {
        var dx = line.X2 - line.X1;
        var dy = line.Y2 - line.Y1;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length <= 0) yield break;

        var ux = dx / length;
        var uy = dy / length;

        int full = (int)Math.Floor(length / stepMm);
        for (int i = 0; i < full; i++)
        {
            var sx = line.X1 + i * stepMm * ux;
            var sy = line.Y1 + i * stepMm * uy;
            var ex = line.X1 + (i + 1) * stepMm * ux;
            var ey = line.Y1 + (i + 1) * stepMm * uy;
            yield return new LineSegment(sx, sy, ex, ey);
        }
        var remainder = length - full * stepMm;
        if (remainder > 1e-9)
        {
            var sx = line.X1 + full * stepMm * ux;
            var sy = line.Y1 + full * stepMm * uy;
            yield return new LineSegment(sx, sy, line.X2, line.Y2);
        }
    }

    private static List<LineSegment> ExtractLines(DxfDocument doc)
    {
        var result = new List<LineSegment>();
        var identity = Transform2D.Identity;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in doc.Entities.Lines)
            AddLine(result, line, identity);

        foreach (var poly in doc.Entities.Polylines2D)
            AddPolyline2D(result, poly, identity);

        foreach (var poly in doc.Entities.Polylines3D)
            AddPolyline3D(result, poly, identity);

        foreach (var ins in doc.Entities.Inserts)
            ExpandInsert(result, ins, identity, visited);

        // Fallback: 모델 공간에 직접 보이는 형상이 없으면 사용자 정의 블록을 평탄화해서 보여준다.
        // (DWG→DXF 변환에서 INSERT 참조가 사라진 케이스 등)
        if (result.Count == 0)
        {
            foreach (var block in doc.Blocks)
            {
                if (IsLayoutBlock(block.Name)) continue;
                FlattenBlock(result, block, identity, visited);
            }
        }

        return result;
    }

    private static bool IsLayoutBlock(string? name) =>
        name != null && (
            name.StartsWith("*Model_Space", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("*Paper_Space", StringComparison.OrdinalIgnoreCase));

    private static void FlattenBlock(List<LineSegment> result, Block block, Transform2D t, HashSet<string> visited)
    {
        var name = block.Name ?? "";
        if (!visited.Add(name)) return;
        try
        {
            foreach (var entity in block.Entities)
            {
                switch (entity)
                {
                    case Line line:
                        AddLine(result, line, t);
                        break;
                    case Polyline2D poly2D:
                        AddPolyline2D(result, poly2D, t);
                        break;
                    case Polyline3D poly3D:
                        AddPolyline3D(result, poly3D, t);
                        break;
                    case Insert child:
                        ExpandInsert(result, child, t, visited);
                        break;
                }
            }
        }
        finally
        {
            visited.Remove(name);
        }
    }

    private static void ExpandInsert(List<LineSegment> result, Insert ins, Transform2D parent, HashSet<string> visited)
    {
        var block = ins.Block;
        if (block == null) return;

        var blockName = block.Name ?? "";
        if (!visited.Add(blockName)) return;
        try
        {
            var local = Transform2D.Compose(parent, Transform2D.FromInsert(ins));

            foreach (var entity in block.Entities)
            {
                switch (entity)
                {
                    case Line line:
                        AddLine(result, line, local);
                        break;
                    case Polyline2D poly2D:
                        AddPolyline2D(result, poly2D, local);
                        break;
                    case Polyline3D poly3D:
                        AddPolyline3D(result, poly3D, local);
                        break;
                    case Insert child:
                        ExpandInsert(result, child, local, visited);
                        break;
                }
            }
        }
        finally
        {
            visited.Remove(blockName);
        }
    }

    private static void AddLine(List<LineSegment> result, Line line, Transform2D t)
    {
        AddSegment(result, t, line.StartPoint.X, line.StartPoint.Y, line.EndPoint.X, line.EndPoint.Y);
    }

    private static void AddPolyline2D(List<LineSegment> result, Polyline2D poly, Transform2D t)
    {
        var vs = poly.Vertexes;
        if (vs.Count < 2) return;
        for (int i = 0; i < vs.Count - 1; i++)
        {
            AddSegment(result, t,
                vs[i].Position.X, vs[i].Position.Y,
                vs[i + 1].Position.X, vs[i + 1].Position.Y);
        }
        if (poly.IsClosed)
        {
            var first = vs[0].Position;
            var last = vs[^1].Position;
            AddSegment(result, t, last.X, last.Y, first.X, first.Y);
        }
    }

    private static void AddPolyline3D(List<LineSegment> result, Polyline3D poly, Transform2D t)
    {
        var vs = poly.Vertexes;
        if (vs.Count < 2) return;
        for (int i = 0; i < vs.Count - 1; i++)
        {
            AddSegment(result, t,
                vs[i].X, vs[i].Y,
                vs[i + 1].X, vs[i + 1].Y);
        }
        if (poly.IsClosed)
        {
            var first = vs[0];
            var last = vs[^1];
            AddSegment(result, t, last.X, last.Y, first.X, first.Y);
        }
    }

    private static void AddSegment(List<LineSegment> result, Transform2D t, double x1, double y1, double x2, double y2)
    {
        var (sx, sy) = t.Apply(x1, y1);
        var (ex, ey) = t.Apply(x2, y2);
        result.Add(new LineSegment(sx, sy, ex, ey));
    }

    private readonly record struct Transform2D(double Sx, double Sy, double Cos, double Sin, double Tx, double Ty)
    {
        public static Transform2D Identity => new(1, 1, 1, 0, 0, 0);

        public (double x, double y) Apply(double x, double y)
        {
            var sx = x * Sx;
            var sy = y * Sy;
            return (Cos * sx - Sin * sy + Tx, Sin * sx + Cos * sy + Ty);
        }

        public static Transform2D FromInsert(Insert ins)
        {
            var rad = ins.Rotation * Math.PI / 180.0;
            return new Transform2D(
                ins.Scale.X, ins.Scale.Y,
                Math.Cos(rad), Math.Sin(rad),
                ins.Position.X, ins.Position.Y);
        }

        public static Transform2D Compose(Transform2D outer, Transform2D inner)
        {
            var origin = outer.Apply(inner.Tx, inner.Ty);
            var ex = outer.Apply(inner.Tx + inner.Sx * inner.Cos, inner.Ty + inner.Sx * inner.Sin);
            var ey = outer.Apply(inner.Tx - inner.Sy * inner.Sin, inner.Ty + inner.Sy * inner.Cos);

            var dxX = ex.x - origin.x;
            var dxY = ex.y - origin.y;
            var dyX = ey.x - origin.x;
            var dyY = ey.y - origin.y;

            var sx = Math.Sqrt(dxX * dxX + dxY * dxY);
            var sy = Math.Sqrt(dyX * dyX + dyY * dyY);
            var cos = sx > 1e-12 ? dxX / sx : 1.0;
            var sin = sx > 1e-12 ? dxY / sx : 0.0;

            return new Transform2D(sx, sy, cos, sin, origin.x, origin.y);
        }
    }

    private static bool IsDxf(string fileName) =>
        fileName.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase);

    private static bool IsDwg(string fileName) =>
        fileName.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase);

    private static string MakeSafeFileName(string fileName)
    {
        var name = Path.GetFileName(fileName);
        foreach (var ch in Path.GetInvalidFileNameChars())
            name = name.Replace(ch, '_');
        return name;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
