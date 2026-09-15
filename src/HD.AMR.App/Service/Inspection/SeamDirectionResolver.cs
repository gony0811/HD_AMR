using HD.AMR.App.Service.Sequence;

namespace HD.AMR.App.Service.Inspection;

/// <summary>
/// seam 벡터(seamStartW→seamEndW, 맵 좌표) → 검사 이동 방향(수평/수직) 자동 유도 (사양 §4.4·§8.1).
///
/// ACS 는 방향 정보를 보내지 않는다 — AMR 이 노드 theta(벽 정면 방향, stopFlag:true 로 보정 보장)를
/// 기준으로 seam 을 벽면-로컬 (u,v) 로 투영해 판정한다:
///
///   u(벽면 수평 탄젠트) = theta 의 좌회전 90° 수평 벡터 (-sinθ, cosθ)
///   du = seam 수평 성분의 u 투영, dv = 면내 잔여 성분 √(|seam|² − du²)
///
/// dv 를 잔여로 계산하므로 수직벽·45° 챔퍼·바닥/천장(접근 방향 성분) 모두 한 식으로 처리된다
/// (seam 이 해당 면 위에 있다는 전제 — ACS 용접선 정의와 동일).
///
/// 퇴화 케이스는 안전하게 현행 기본(Horizontal)으로 폴백한다:
///   · seam 길이 &lt; 10 mm (교차점 마커 등 방향 정보 없음)
///   · 판정각이 45°±10° 경계 (투영 오차로 오판 위험 — 폴백 + 경고 로그는 호출측)
/// </summary>
public static class SeamDirectionResolver
{
    /// <summary>seam 최소 길이 [m] — 미만이면 방향 정보 없음으로 보고 폴백.</summary>
    private const double MinSeamLengthM = 0.010;

    /// <summary>45° 경계 완충각 [deg] — |판정각−45°| ≤ 이 값이면 모호로 보고 폴백.</summary>
    private const double AmbiguousBandDeg = 10.0;

    /// <summary>
    /// 방향 판정. <paramref name="reason"/> 에 판정 근거(du/dv/각도 또는 폴백 사유)를 남긴다 — 로그/테스트용.
    /// </summary>
    public static InspectionMoveDirection Resolve(
        double[] seamStartW, double[] seamEndW, double nodeThetaRad, out string reason)
    {
        if (seamStartW is not { Length: 3 } || seamEndW is not { Length: 3 })
        {
            reason = "seam 좌표 형식 이상([x,y,z] 아님) — Horizontal 폴백";
            return InspectionMoveDirection.Horizontal;
        }

        var sx = seamEndW[0] - seamStartW[0];
        var sy = seamEndW[1] - seamStartW[1];
        var sz = seamEndW[2] - seamStartW[2];
        var len = Math.Sqrt(sx * sx + sy * sy + sz * sz);

        if (len < MinSeamLengthM)
        {
            reason = $"seam 길이 {len * 1000:0.#}mm < {MinSeamLengthM * 1000:0}mm — 방향 정보 없음, Horizontal 폴백";
            return InspectionMoveDirection.Horizontal;
        }

        // 벽면 수평 탄젠트 u = theta 좌회전 90° (부록 B: theta = 맵 X축 기준 CCW rad).
        var ux = -Math.Sin(nodeThetaRad);
        var uy = Math.Cos(nodeThetaRad);
        var du = sx * ux + sy * uy;
        var dv = Math.Sqrt(Math.Max(0.0, len * len - du * du));

        var angleDeg = Math.Atan2(dv, Math.Abs(du)) * 180.0 / Math.PI;   // 0°=수평, 90°=수직
        if (Math.Abs(angleDeg - 45.0) <= AmbiguousBandDeg)
        {
            reason = $"판정각 {angleDeg:0.#}° — 45°±{AmbiguousBandDeg:0}° 경계(모호), Horizontal 폴백 " +
                     $"(du={du * 1000:0.#}mm, dv={dv * 1000:0.#}mm)";
            return InspectionMoveDirection.Horizontal;
        }

        var direction = angleDeg > 45.0
            ? InspectionMoveDirection.Vertical
            : InspectionMoveDirection.Horizontal;
        reason = $"판정각 {angleDeg:0.#}° → {direction} (du={du * 1000:0.#}mm, dv={dv * 1000:0.#}mm, θ={nodeThetaRad:0.###}rad)";
        return direction;
    }
}
