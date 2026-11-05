using System.Text.Json;
using System.Text.Json.Serialization;

namespace HD.AMR.App.Service.Vision;

/// <summary>
/// 용접선 <b>선 라벨</b>(정답). 브러시로 칠한 띠 대신 비드 중심선을 폴리라인(보통 두 끝점)으로 찍고,
/// 학습용 마스크는 이 선을 고정 두께로 래스터화해 만든다 → 라벨 두께가 항상 일정하고 실제 비드 폭과
/// 맞는다. 같은 파일이 검출 방식 비교평가의 정답(각도·위치 오차 기준)으로도 쓰인다.
/// 파일: <c>{stem}_{modality}_line.json</c>. 좌표는 원본 이미지 픽셀.
/// </summary>
public sealed class WeldLineLabel
{
    public const int DefaultThickness = 9;

    /// <summary>이미지 크기(픽셀). 회전 증강·크기 검증용.</summary>
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>마스크 래스터화 두께(px, 전체 폭).</summary>
    public int Thickness { get; set; } = DefaultThickness;

    /// <summary>중심선 폴리라인 점들 [x, y]. 2점 이상이어야 유효.</summary>
    public List<double[]> Points { get; set; } = new();

    [JsonIgnore]
    public bool IsValid => Width > 0 && Height > 0 && Points.Count >= 2 && Points.All(p => p is { Length: 2 });

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>JSON 파싱. 형식 오류·무효 라벨이면 null.</summary>
    public static WeldLineLabel? FromJson(string json)
    {
        try
        {
            var l = JsonSerializer.Deserialize<WeldLineLabel>(json, Json);
            return l is { IsValid: true } ? l : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// 폴리라인을 두께 <see cref="Thickness"/> 로 래스터화한 이진 마스크(row-major, 비드=255, 배경=0).
    /// 각 픽셀 중심에서 가장 가까운 선분까지 거리가 두께/2 이하면 비드. 끝은 둥글게 처리된다.
    /// </summary>
    public byte[] Rasterize()
    {
        var mask = new byte[Width * Height];
        double r = Math.Max(0.5, Thickness / 2.0);
        double r2 = r * r;
        for (int i = 0; i + 1 < Points.Count; i++)
        {
            double ax = Points[i][0], ay = Points[i][1], bx = Points[i + 1][0], by = Points[i + 1][1];
            int x0 = Math.Max(0, (int)Math.Floor(Math.Min(ax, bx) - r));
            int x1 = Math.Min(Width - 1, (int)Math.Ceiling(Math.Max(ax, bx) + r));
            int y0 = Math.Max(0, (int)Math.Floor(Math.Min(ay, by) - r));
            int y1 = Math.Min(Height - 1, (int)Math.Ceiling(Math.Max(ay, by) + r));
            double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    double t = len2 <= 0 ? 0 : Math.Clamp(((x - ax) * dx + (y - ay) * dy) / len2, 0, 1);
                    double ex = ax + t * dx - x, ey = ay + t * dy - y;
                    if (ex * ex + ey * ey <= r2) mask[y * Width + x] = 255;
                }
        }
        return mask;
    }

    /// <summary>90° 회전본(이미지 회전 증강과 쌍). clockwise=true 면 시계방향.</summary>
    public WeldLineLabel Rotate90(bool clockwise)
    {
        int w = Width, h = Height;
        return new WeldLineLabel
        {
            Width = h,
            Height = w,
            Thickness = Thickness,
            // OpenCV Rotate90Clockwise: (x,y) → (h-1-y, x). 반시계: (x,y) → (y, w-1-x).
            Points = Points.Select(p => clockwise
                ? new[] { h - 1 - p[1], p[0] }
                : new[] { p[1], w - 1 - p[0] }).ToList(),
        };
    }
}
