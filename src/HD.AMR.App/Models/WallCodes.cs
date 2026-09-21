namespace HD.AMR.App.Models;

/// <summary>`wall_code` → 면 자세 5군 (사양 §8.5.1 (2), INSPECTION_TYPES.md §2).</summary>
public enum SurfaceOrientation
{
    Floor,          // B
    Ceiling,        // T
    Wall,           // SM PM F A
    ChamferLower,   // SL PL (45°)
    ChamferUpper,   // SU PU (45°)
    Any,            // CORNER3 — 면 자세 무관 단일 레시피
}

/// <summary>검사 면 1종의 정의 — ACS 코드, Wall ID(= 비전 Surface ID = Teaching Wall ID), 레시피 면 자세, 이름, 비전 U/V 축.</summary>
/// <param name="Code">ACS wall_code (B/T/PM/SM/F/A/PL/SL/PU/SU).</param>
/// <param name="SurfaceId">Wall ID — 비전 CAPTURE_REQ Surface ID(vision_interface.md §5) 이자 Teaching 검사 준비 위치의 Wall ID.</param>
/// <param name="Orientation">레시피 매핑용 면 자세 5군(§8.5.1).</param>
/// <param name="Label">면 이름(비전 사양 시트 5 표기).</param>
/// <param name="EnglishName">영문 이름(사양에 있는 면만). 없으면 null.</param>
/// <param name="Axes">면-로컬 U/V 축 방향(비전 사양 시트 5).</param>
public sealed record WallCodeInfo(
    string Code, int SurfaceId, SurfaceOrientation Orientation,
    string Label, string? EnglishName, string Axes)
{
    /// <summary>"바닥 (Bottom)" 형태 표시 이름. 영문명이 없으면 한글명만.</summary>
    public string DisplayName => EnglishName is null ? Label : $"{Label} ({EnglishName})";
}

/// <summary>
/// 검사 면(wall_code / Wall ID) 정본 10종 <b>단일 정의</b>(ACS 확정 2026-09-15, 비전 사양 시트 5 "Surface ID 정의").
/// 아래 모든 경로는 이 표만 참조한다 — 표를 따로 두면 서로 어긋나 엉뚱한 면 자세로 이동하거나 잘못된 Surface ID 를 보고한다.
/// <list type="bullet">
/// <item>ACS 액션 처리: wall_code → Wall ID (<c>WeldInspectionOrchestrator</c>)</item>
/// <item>레시피 매핑: wall_code → 면 자세 (<c>InspectionRecipeResolver</c>)</item>
/// <item>Teaching 검사 준비 위치 고정 슬롯 (<c>TeachingService</c>)</item>
/// <item>비전 Surface ID 카탈로그 (<c>Communication.Vision.SurfaceCatalog</c>)</item>
/// <item>검사 매핑 요약 화면</item>
/// </list>
/// S* = 우현(Starboard), P* = 좌현(Port). 순서 = Wall ID 순.
/// </summary>
public static class WallCodes
{
    public static readonly IReadOnlyList<WallCodeInfo> All = new WallCodeInfo[]
    {
        new("B",  0x01, SurfaceOrientation.Floor,        "바닥",           "Bottom",    "U: 선수→선미, V: 좌현→우현"),
        new("T",  0x02, SurfaceOrientation.Ceiling,      "천장",           "Top",       "U: 선수→선미, V: 좌현→우현"),
        new("PM", 0x03, SurfaceOrientation.Wall,         "좌현벽",         "Port",      "U: 선수→선미, V: 바닥→천장"),
        new("SM", 0x04, SurfaceOrientation.Wall,         "우현벽",         "Starboard", "U: 선수→선미, V: 바닥→천장"),
        new("F",  0x05, SurfaceOrientation.Wall,         "전벽",           "Forward",   "U: 좌현→우현, V: 바닥→천장"),
        new("A",  0x06, SurfaceOrientation.Wall,         "후벽",           "Aft",       "U: 좌현→우현, V: 바닥→천장"),
        new("PL", 0x07, SurfaceOrientation.ChamferLower, "하부 좌현 챔퍼", null,        "U: 선수→선미, V: 바닥→좌현벽"),
        new("SL", 0x08, SurfaceOrientation.ChamferLower, "하부 우현 챔퍼", null,        "U: 선수→선미, V: 바닥→우현벽"),
        new("PU", 0x09, SurfaceOrientation.ChamferUpper, "상부 좌현 챔퍼", null,        "U: 선수→선미, V: 천장→좌현벽"),
        new("SU", 0x0A, SurfaceOrientation.ChamferUpper, "상부 우현 챔퍼", null,        "U: 선수→선미, V: 천장→우현벽"),
    };

    /// <summary>wall_code 로 조회(대소문자 구분 — ACS 정본 대문자). 미정의면 null.</summary>
    public static WallCodeInfo? Find(string? code) => All.FirstOrDefault(w => w.Code == code);

    /// <summary>Wall ID(Surface ID) 로 조회. 정본 범위(0x01~0x0A) 밖이면 null.</summary>
    public static WallCodeInfo? FindBySurfaceId(int surfaceId) => All.FirstOrDefault(w => w.SurfaceId == surfaceId);
}
