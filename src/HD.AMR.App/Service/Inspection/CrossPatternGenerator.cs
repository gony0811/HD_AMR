using HD.AMR.App.Data.Entities;

namespace HD.AMR.App.Service.Inspection;

/// <summary>CROSS 십자 스캔 패턴 파라미터. <b>교시 시작 템플릿 전용</b> — 런타임 실행 경로는 폐기(캡처
/// 교시 단일화). `/inspection`·`/inspection-points`의 "십자 패턴 채우기"가 이 파라미터로 초기 경유점을
/// 채운 뒤, 운영자가 6-DOF 로 조정·캡처한다.</summary>
/// <param name="ArmMm">교차점에서 각 arm 끝까지 길이 [mm].</param>
/// <param name="SpacingMm">arm 위 경유점 간격 [mm].</param>
/// <param name="PerpRzDeg">교차 arm 촬상 시 툴 RZ 추가 회전 [deg] — 세로 비드 대면(기본 −90).</param>
public sealed record CrossPatternParams(double ArmMm, double SpacingMm, double PerpRzDeg = -90.0);

/// <summary>
/// CROSS4 4-arm 십자 경유점 생성 (순수 함수). 좌표계는 정렬 체인이 등록한 <b>작업물(wobj) 프레임</b>:
/// X = 비드 진행 방향(정렬된 주 seam), Z = 면내 횡방향 — 교차점 = 프레임 원점 (0,0).
///
/// 주 arm 은 프레임 X 고정이다 — 검사 방향(수평/수직)은 정렬 체인(②~⑯⁺)이 이미 툴/프레임을
/// seam 방향으로 돌려놓았으므로, 프레임 안에서는 방향 구분이 필요 없다.
///
/// 순회 순서(백트래킹·회전 간섭 최소화):
///   ① 주 arm: X = −arm → +arm (spacing 간격, RzDeg 0)
///   ② 중심 복귀 + 제자리 회전: (0,0) RzDeg=perp — 이동 없이 회전만 일어나도록 중심에서 돌린다
///   ③ 교차 arm: Z = −arm → +arm (spacing 간격, RzDeg=perp)
/// 교차점(0,0)은 세 번 촬영된다(주 pass·회전점·교차 pass) — 십자 중심이 검사 핵심부라 중복 캡처 허용.
/// </summary>
public static class CrossPatternGenerator
{
    /// <summary>경유점 생성. 파라미터가 유효하지 않으면 false + error.</summary>
    public static bool TryGenerate(CrossPatternParams p, out List<InspectionWaypoint> waypoints, out string? error)
    {
        waypoints = new List<InspectionWaypoint>();
        error = null;

        if (p.ArmMm <= 0 || p.SpacingMm <= 0)
        {
            error = $"CROSS4 패턴 파라미터 이상 — ArmMm({p.ArmMm})·SpacingMm({p.SpacingMm}) 은 양수여야 함";
            return false;
        }
        if (p.SpacingMm > p.ArmMm)
        {
            error = $"CROSS4 패턴 파라미터 이상 — SpacingMm({p.SpacingMm}) > ArmMm({p.ArmMm})";
            return false;
        }

        var n = (int)Math.Floor(p.ArmMm / p.SpacingMm);   // arm 당 편측 점 수 (중심 제외)

        // ① 주 arm (프레임 X) — 촬상 회전 없음.
        for (var i = -n; i <= n; i++)
            waypoints.Add(new InspectionWaypoint(X: i * p.SpacingMm, Z: 0, Theta: 0, ThetaManual: false));

        // ② 중심 복귀 + 제자리 회전.
        waypoints.Add(new InspectionWaypoint(X: 0, Z: 0, Theta: 0, ThetaManual: false, RzDeg: p.PerpRzDeg));

        // ③ 교차 arm (프레임 Z) — 회전 유지.
        for (var i = -n; i <= n; i++)
            waypoints.Add(new InspectionWaypoint(X: 0, Z: i * p.SpacingMm, Theta: 0, ThetaManual: false, RzDeg: p.PerpRzDeg));

        return true;
    }
}
