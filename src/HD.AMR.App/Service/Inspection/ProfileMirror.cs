using HD.AMR.App.Communication.Vision;
using HD.AMR.App.Data.Entities;
using HD.AMR.App.Models;

namespace HD.AMR.App.Service.Inspection;

/// <summary>
/// 티칭 프로필 경유점의 거울 반전(작업물 Y) — 우현 면에서 좌현에서 교시한 프로필을 그대로 쓰기 위한 규칙.
///
/// 작업물 좌표계는 X = 용접선 방향, Z = 면 바깥(선창 안쪽), Y = Z × X 다. 좌현·우현 모두 X 를 선미→선수로 잡으면
/// 좌현은 Y+ 가 위쪽, 우현은 Y+ 가 아래쪽이 된다(<see cref="FaceLocalMapper.IsLeftHanded"/>). 그런데 우현 멤브레인은
/// 좌현의 거울상이라, 좌현에서 교시한 경유점을 우현에서 그대로 실행하면 X 에서 벗어난 점(CROSS 의 세로 팔)이
/// 위아래로 뒤집힌 자리에 간다. CROSS-4-V1 은 세로 팔 양 끝 top 이 Y −195 / +170 mm 로 비대칭이라 우현에서
/// 25 mm 어긋난다(2026-11-17 현장 확인: 교시 = 좌현·선미→선수, 우현 = 거울상).
///
/// 반전은 작업물 XZ 평면에 대한 거울이다 — 위치 Y 부호를 바꾸고, 자세는 R → M·R·M (M = diag(1, −1, 1)).
/// RPY(ZYX) 에서 M·Rz(c)·Ry(b)·Rx(a)·M = Rz(−c)·Ry(b)·Rx(−a) 이므로 RzDeg·RxDeg 부호만 바꾸고 θ(Ry)는 그대로다.
/// Y = 0·Rz = 0·Rx = 0 인 LINE 프로필은 반전해도 같다.
/// </summary>
public static class ProfileMirror
{
    /// <summary>우현 면에서 프로필을 거울 반전할지(기본 true). false 면 종전처럼 교시값 그대로 실행한다.</summary>
    public const string EnabledKey = "inspection.mirrorProfileOnStarboard";

    /// <summary>거울 반전 대상 면 — 좌현 면(PM·PL·PU)의 거울상인 우현 면. 천장 T 도 왼손 좌표계지만 좌현의 거울상인지
    /// 확인되지 않아 넣지 않았다.</summary>
    public static readonly IReadOnlySet<string> StarboardWalls = new HashSet<string> { "SM", "SL", "SU" };

    /// <summary>
    /// 이 면·용접선 방향에서 프로필을 거울 반전해야 하는지. 우현 면이고 용접선이 면 축의 + 방향(교시와 같은 방향)일 때만
    /// 반전한다. 방향 정보가 없으면(화면 단독 실행 등) + 방향으로 본다. − 방향은 X 도 뒤집혀 있어 Y 반전만으로 맞출 수
    /// 없으므로 건드리지 않는다.
    /// </summary>
    public static bool ShouldMirror(string? wallCode, FaceSeamPlacement? placement, bool enabled = true)
    {
        if (!enabled || wallCode is null || !StarboardWalls.Contains(wallCode)) return false;
        return placement?.Direction is null or FaceSeamDirection.PlusU or FaceSeamDirection.PlusV;
    }

    /// <summary>면 ID(<see cref="WallCodes"/>)로 판정하는 <see cref="ShouldMirror(string?, FaceSeamPlacement?, bool)"/>.</summary>
    public static bool ShouldMirror(int surfaceId, FaceSeamPlacement? placement, bool enabled = true)
        => ShouldMirror(WallCodes.FindBySurfaceId(surfaceId)?.Code, placement, enabled);

    /// <summary>경유점 하나를 작업물 XZ 평면에 대해 거울 반전한다 — Y·RzDeg·RxDeg 부호 반전, 나머지는 그대로.</summary>
    public static InspectionWaypoint Mirror(InspectionWaypoint w)
        => w with { Y = Negate(w.Y), RzDeg = Negate(w.RzDeg), RxDeg = Negate(w.RxDeg) };

    /// <summary>경유점 목록 전체를 거울 반전한 새 목록.</summary>
    public static List<InspectionWaypoint> Mirror(IEnumerable<InspectionWaypoint> waypoints)
        => waypoints.Select(Mirror).ToList();

    // 0 은 0 그대로(−0 을 만들지 않는다 — 로그·비교에 섞이지 않게).
    private static double Negate(double v) => v == 0 ? 0 : -v;
}
