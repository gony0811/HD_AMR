namespace HD.AMR.App.Communication.Vision;

/// <summary>
/// 코봇 촬영 pose(벽면 workpiece/wobj 좌표계) → 면-로컬 (u, v, h) mm 정수 변환.
///
/// 결정 1(코봇 wobj 직접): (u, v, h)는 ACS 글로벌에서 투영해 오는 값이 아니라, 코봇·비전이
/// 공유하는 벽면 측정 좌표계 그 자체다. 따라서 별도 조회(ACS walls API)나 글로벌→면-로컬 투영이
/// 필요 없고, 코봇이 이미 그 좌표계에서 갖고 있는 pose 성분을 그대로 싣는다.
///
/// 축·부호 대응(코봇 X/Y/Z ↔ u/v/h)은 <b>wobj 티칭 규약</b>으로 흡수한다. 규약 정본은
/// vision_interface_v3.2 §5(=SAIGE v2.6 부록 A)이며, h=0 은 벽 표면(코봇 z=0 기준면과 일치),
/// +h 는 선창 안쪽이어야 한다(§3.1.1 — 물리 캘리브레이션 확인 대상).
///
/// 현 구현은 축 정렬이 wobj 티칭으로 이미 맞춰졌다고 보고 <b>항등 매핑</b>(u=X, v=Y, h=Z)한다.
/// 벽별 축 교환/부호 반전이 필요하다고 확정되면 이 한 곳(<see cref="ToFaceLocal"/>)만 고치면 된다.
/// </summary>
public static class FaceLocalMapper
{
    /// <summary>코봇 wobj pose(mm) → 면-로컬 (u, v, h) mm 정수.</summary>
    /// <param name="wallId">Wall ID 1~10 (벽별 축 규약을 적용할 때 사용).</param>
    /// <param name="x">코봇 wobj X (mm).</param>
    /// <param name="y">코봇 wobj Y (mm).</param>
    /// <param name="z">코봇 wobj Z (mm) — 벽 표면 기준 높이.</param>
    public static (int u, int v, int h) ToFaceLocal(ushort wallId, double x, double y, double z)
    {
        // TODO(연동 시험): 벽별 축 교환·부호가 필요하면 wallId 로 분기. 현재는 wobj 티칭이
        //   v3.2 §5 축에 맞춰졌다고 가정한 항등 매핑.
        _ = wallId;
        return (Round(x), Round(y), Round(z));
    }

    private static int Round(double mm) => (int)Math.Round(mm);
}
