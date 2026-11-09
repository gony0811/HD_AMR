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

    /// <summary>라벨 출처 — null = 사람이 직접 그림, "transfer:ir"/"transfer:rgb" = 반대 영상 라벨을 깊이·캘리브레이션으로
    /// 옮긴 것. 옮긴 라벨은 사람이 그린 라벨을 덮어쓰지 않는다.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Origin { get; set; }

    /// <summary>반대 영상에서 옮겨 만든 라벨인지.</summary>
    [JsonIgnore]
    public bool IsTransferred => Origin?.StartsWith("transfer", StringComparison.Ordinal) == true;

    /// <summary>
    /// 끊김 위치 — 여기 적힌 인덱스의 점은 앞 점과 잇지 않고 새 선을 시작한다(떨어진 비드를 한 라벨에).
    /// 없으면(null) 모든 점을 한 줄로 잇는다 — 예전 파일과 호환. 저장 시 비어 있으면 생략.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<int>? Breaks { get; set; }

    [JsonIgnore]
    public bool IsValid => Width > 0 && Height > 0 && Points.All(p => p is { Length: 2 }) && Segments().Any(s => s.Count >= 2);

    /// <summary>끊김으로 나눈 선 조각들(점 1개짜리 조각 포함). 각 조각은 이어진 폴리라인.</summary>
    public List<List<double[]>> Segments()
    {
        var segs = new List<List<double[]>>();
        var cur = new List<double[]>();
        var br = Breaks is { Count: > 0 } ? Breaks.ToHashSet() : null;
        for (int i = 0; i < Points.Count; i++)
        {
            if (i > 0 && br?.Contains(i) == true && cur.Count > 0) { segs.Add(cur); cur = new(); }
            cur.Add(Points[i]);
        }
        if (cur.Count > 0) segs.Add(cur);
        return segs;
    }

    /// <summary>이어진 선분(a→b)들. 끊긴 곳은 건너뛴다.</summary>
    public IEnumerable<(double[] A, double[] B)> Edges()
    {
        foreach (var s in Segments())
            for (int i = 0; i + 1 < s.Count; i++) yield return (s[i], s[i + 1]);
    }

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
    /// 끊긴 곳(<see cref="Breaks"/>)은 칠하지 않고, 점 1개짜리 조각도 칠하지 않는다.
    /// </summary>
    public byte[] Rasterize()
    {
        var mask = new byte[Width * Height];
        double r = Math.Max(0.5, Thickness / 2.0);
        double r2 = r * r;
        foreach (var (pa, pb) in Edges())
        {
            double ax = pa[0], ay = pa[1], bx = pb[0], by = pb[1];
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
            Breaks = Breaks is null ? null : new List<int>(Breaks),
            Origin = Origin,
        };
    }
}
