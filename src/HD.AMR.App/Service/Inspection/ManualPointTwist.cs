using HD.AMR.App.Communication;

namespace HD.AMR.App.Service.Inspection;

/// <summary>
/// 검사 프로파일 페이지(X-Y 평면 티칭)가 작업물 좌표계(user=N)로 경유점을 보낼 때의 광축 둘레 비틀림(0/180°) 보정.
///
/// 3점 교시는 프레임 X 를 점1→점2 방향으로 정하므로, 교시 당시 툴 +X 가 반대를 보고 있었으면 툴은 프레임 대비 Rz≈±180° 다.
/// 페이지가 경유점의 Rz(0)를 그대로 보내면 같은 위치에서 툴을 광축 둘레로 180° 돌린 자세가 되어, 현재 손목 방향에서는
/// J6 이 사양(±360°) 밖(약 −393°)이 필요해 컨트롤러 자동 IK 가 errcode=112 를 낸다
/// (실기 2026-11-27 11:44 CROSS-4-R-V1, user=#5 — 오프라인 IK 로 해는 8개 있음을 확인). 시퀀스 <c>InspectionRunStep</c> 은
/// 첫 점에서 비틀림 0/180° 후보를 IK 로 풀어 J6 안전 범위 안의 쪽을 고른다 — 페이지도 같은 규칙을 쓴다.
/// </summary>
public static class ManualPointTwist
{
    /// <summary>비틀림 후보 [도].</summary>
    public static readonly double[] Candidates = { 0.0, 180.0 };

    /// <summary>
    /// 작업물 좌표계 기준 경유점 <paramref name="pagePose"/> 의 위치·광축 방향은 그대로 두고, 광축(툴 Z) 둘레로
    /// <paramref name="twistDeg"/> 만 돌린 pose(같은 좌표계 기준). 0 이면 그대로.
    /// </summary>
    public static double[] Apply(double[] pagePose, double twistDeg)
        => twistDeg == 0.0
            ? pagePose
            : FrameMath.FromFrame(new[] { 0.0, 0.0, 0.0, 0.0, 0.0, twistDeg }, pagePose);

    /// <summary>작업물 좌표계(<paramref name="frame"/>=T_N, 베이스 기준) 경유점을 비틀림 적용 뒤 베이스 pose 로.</summary>
    public static double[] ToBase(double[] pagePose, double[] frame, double twistDeg)
        => FrameMath.FromFrame(Apply(pagePose, twistDeg), frame);
}
