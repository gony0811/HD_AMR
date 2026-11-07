namespace HD.AMR.App.Models;

/// <summary>
/// 카메라의 Depth↔Color 정합용 공장 캘리브레이션(내부 + 외부) 파라미터의 관리형 스냅샷.
/// librealsense 의 intrinsics/extrinsics 조회 결과를 SDK 타입 노출 없이 담는다.
/// 회전 <see cref="Rot"/>(3×3, row-major 9개) · 평행이동 <see cref="Trans"/>(3, mm)은
/// <b>Depth → Color</b> 변환이다.
/// </summary>
public sealed record CameraD2CParams(
    double DepthFx, double DepthFy, double DepthCx, double DepthCy, int DepthW, int DepthH,
    double ColorFx, double ColorFy, double ColorCx, double ColorCy, int ColorW, int ColorH,
    double[] Rot,
    double[] Trans)
{
    /// <summary>내부 파라미터가 유효한지(초점거리 양수).</summary>
    public bool IsValid => DepthFx > 0 && ColorFx > 0 && Rot.Length == 9 && Trans.Length == 3;
}

/// <summary>
/// <see cref="CameraD2CParams"/> JSON 파일 입출력 — 촬영본마다 <c>{stem}_calib.json</c> 으로 함께 저장해, 카메라가 없는
/// PC 나 다른 날에도 저장본의 RGB 안내선·IR↔RGB 라벨 변환이 가능하게 한다.
/// </summary>
public static class CameraD2CFile
{
    public const string Suffix = "_calib.json";

    private static readonly System.Text.Json.JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string PathFor(string dir, string stem) => System.IO.Path.Combine(dir, stem + Suffix);

    public static string ToJson(CameraD2CParams p) => System.Text.Json.JsonSerializer.Serialize(p, Json);

    /// <summary>JSON → 파라미터. 깨졌거나 유효하지 않으면 null.</summary>
    public static CameraD2CParams? FromJson(string json)
    {
        try
        {
            var p = System.Text.Json.JsonSerializer.Deserialize<CameraD2CParams>(json);
            return p is { Rot: not null, Trans: not null } && p.IsValid ? p : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>파일에서 읽기. 없거나 깨졌으면 null.</summary>
    public static CameraD2CParams? TryLoad(string path)
    {
        try { return System.IO.File.Exists(path) ? FromJson(System.IO.File.ReadAllText(path)) : null; }
        catch (System.IO.IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>파일로 쓰기(유효한 값만). 실패는 무시하지 않고 예외로 둔다 — 호출측이 판단.</summary>
    public static void Save(string path, CameraD2CParams p)
    {
        if (!p.IsValid) return;
        System.IO.File.WriteAllText(path, ToJson(p));
    }
}
