using HD.AMR.App.Service.Sequence;

namespace HD.AMR.App.Service.Inspection;

/// <summary>
/// ACS 용접선 좌표(<c>seamStartW</c>/<c>seamEndW</c>, VDA5050 §8.1) → 코봇 BASE 좌표 환산.
///
/// 계약상 seam 좌표는 <b>맵(SLAM) 프레임</b>이고 AMR 은 도면을 해석하지 않는다(부록 B). 따라서 필요한 것은
/// 도면 정합이 아니라 AMR 자기 측위 pose 하나뿐이며, 변환 사슬은 다음과 같다:
///
///   p_B = (T_W_A · T_A_B)⁻¹ · p_W
///     T_W_A = AMR SLAM pose (맵 → AMR 차체, x·y·yaw)
///     T_A_B = 장착 보정 (AMR 차체 → 코봇 BASE) — tz 는 텔레스코픽 스트로크에 따라 변한다
///
/// <b>z 기준(중요).</b> ACS 의 T_W_D 는 2D(x,y,yaw) 강체변환이라 z 는 변환되지 않고 <b>도면 전역 z</b>
/// (선창 바닥 = 0)가 그대로 실려 온다. AMR 은 자기 바닥을 0 으로 보므로 L2 이상에서는 층 바닥 높이
/// (<c>level_z</c>)만큼 어긋난다 — <see cref="SeamBaseInput.ZDatumOffsetMm"/> 로 그 값을 빼서 맞춘다.
/// L1 은 level_z=0 이라 오차가 드러나지 않는다.
///
/// <b>standoff.</b> 용접선은 벽면 위의 점이므로 TOOL 을 그 점으로 그대로 보내면 벽과 간섭한다.
/// 벽 정면 방향(정차 노드 theta, 없으면 AMR yaw)의 반대로 <see cref="SeamBaseInput.StandoffMm"/> 만큼
/// 물러난 <b>접근점</b>을 이동 목표로 삼는다.
///
/// 이 클래스는 순수 계산만 한다(하드웨어 접촉 없음) — 시험 화면과 단위 테스트가 같은 경로를 쓴다.
/// </summary>
public static class SeamBaseTransform
{
    /// <summary>기본 standoff [mm] — 레시피 카메라 목표거리(400mm)와 같은 값.</summary>
    public const double DefaultStandoffMm = 400.0;

    /// <summary>이 값 미만의 standoff 는 벽 간섭 위험으로 경고한다 [mm].</summary>
    public const double MinSafeStandoffMm = 100.0;

    public static SeamBaseTarget Resolve(SeamBaseInput input)
    {
        var notes = new List<string>();

        if (input.SeamStartW is not { Length: 3 })
            throw new ArgumentException("seamStartW 는 [x,y,z] 3요소여야 합니다.", nameof(input));
        if (input.MountAtHome is not { Length: 6 })
            throw new ArgumentException("T_A_B 는 6요소여야 합니다.", nameof(input));

        // ── ① seam 시작점: m → mm, z 기준 보정 ────────────────────────
        var seamMap = new[]
        {
            input.SeamStartW[0] * 1000.0,
            input.SeamStartW[1] * 1000.0,
            input.SeamStartW[2] * 1000.0 - input.ZDatumOffsetMm,
        };

        if (Math.Abs(input.ZDatumOffsetMm) < 1e-9)
            notes.Add("z 보정 0 — ACS z 가 도면 전역(선창 바닥) 기준이면 L2 이상에서 층 바닥 높이(level_z)만큼 " +
                      "높게 잡힙니다. 1층이 아니면 z 기준 오프셋을 입력하세요.");

        // ── ② 벽 정면 방향 — 노드 theta 우선, 없으면 AMR yaw ──────────
        var facing = input.WallFacingThetaRad ?? input.AmrYawRad;
        if (input.WallFacingThetaRad is null)
            notes.Add("노드 theta 미입력 — AMR yaw 를 벽 정면 방향으로 사용했습니다(정차 자세가 벽을 향한다는 전제).");

        // ── ③ 접근점: 벽 정면의 반대로 standoff 만큼 후퇴 ─────────────
        var approachMap = new[]
        {
            seamMap[0] - input.StandoffMm * Math.Cos(facing),
            seamMap[1] - input.StandoffMm * Math.Sin(facing),
            seamMap[2],
        };

        if (input.StandoffMm < MinSafeStandoffMm)
            notes.Add($"standoff {input.StandoffMm:0}mm < {MinSafeStandoffMm:0}mm — 벽 간섭 위험 구간입니다.");

        // ── ④ 변환 사슬 ──────────────────────────────────────────────
        var amrPose = MapCalibration.AmrPoseToMmDeg(input.AmrXm, input.AmrYm, input.AmrYawRad);
        var mount = MapCalibration.MountPoseAtStroke(input.MountAtHome, input.TelescopicStrokeMm);

        if (input.MountAtHome.All(v => Math.Abs(v) < 1e-9))
            notes.Add("T_A_B 가 전부 0 입니다 — 장착 보정 미수행. 환산 결과는 AMR 차체 기준과 같아 의미가 없습니다.");

        var seamBase = MapCalibration.MapPointToBase(amrPose, mount, seamMap);
        var approachBase = MapCalibration.MapPointToBase(amrPose, mount, approachMap);

        // ── ⑤ 거리·방향 진단 ─────────────────────────────────────────
        var planar = Math.Sqrt(approachBase[0] * approachBase[0] + approachBase[1] * approachBase[1]);
        var dist = Math.Sqrt(planar * planar + approachBase[2] * approachBase[2]);

        InspectionMoveDirection? direction = null;
        string? directionReason = null;
        if (input.SeamEndW is { Length: 3 })
        {
            direction = SeamDirectionResolver.Resolve(input.SeamStartW, input.SeamEndW, facing, out var reason);
            directionReason = reason;
        }

        return new SeamBaseTarget(
            SeamStartMapMm: seamMap,
            ApproachMapMm: approachMap,
            SeamStartBaseMm: seamBase,
            ApproachBaseMm: approachBase,
            MountUsed: mount,
            PlanarDistanceMm: planar,
            DistanceMm: dist,
            Direction: direction,
            DirectionReason: directionReason,
            Notes: notes);
    }
}

/// <summary>
/// <see cref="SeamBaseTransform.Resolve"/> 입력. 좌표 단위는 계약(§부록 B)대로 seam 은 m, 나머지는 mm/도.
/// </summary>
/// <param name="SeamStartW">용접선 시작점 [x,y,z] m — 맵(SLAM) 좌표, z 는 도면 전역 z.</param>
/// <param name="SeamEndW">용접선 끝점 [x,y,z] m — 방향 표시용(선택).</param>
/// <param name="AmrXm">AMR SLAM x [m].</param>
/// <param name="AmrYm">AMR SLAM y [m].</param>
/// <param name="AmrYawRad">AMR SLAM yaw [rad] — 맵 X축 기준 CCW.</param>
/// <param name="MountAtHome">T_A_B [x,y,z,rx,ry,rz] mm/도 — <b>완전 하강(스트로크 0) 기준 저장값</b>.</param>
/// <param name="TelescopicStrokeMm">현재 텔레스코픽 스트로크 [mm, 완전 하강 = 0].</param>
/// <param name="ZDatumOffsetMm">도면 전역 z → AMR 바닥 기준 보정 [mm] — 해당 층 바닥 높이(level_z)를 넣는다(뺄셈).</param>
/// <param name="StandoffMm">벽면 이격 [mm] — 접근점을 벽 정면 반대로 이만큼 물린다.</param>
/// <param name="WallFacingThetaRad">벽 정면 방향 [rad] — 정차 노드 theta. null 이면 AMR yaw 사용.</param>
public sealed record SeamBaseInput(
    double[] SeamStartW,
    double[]? SeamEndW,
    double AmrXm,
    double AmrYm,
    double AmrYawRad,
    double[] MountAtHome,
    double TelescopicStrokeMm,
    double ZDatumOffsetMm,
    double StandoffMm,
    double? WallFacingThetaRad);

/// <summary>환산 결과. 좌표는 전부 mm.</summary>
/// <param name="SeamStartMapMm">z 보정까지 반영한 맵 좌표 용접선 시작점.</param>
/// <param name="ApproachMapMm">standoff 를 적용한 맵 좌표 접근점.</param>
/// <param name="SeamStartBaseMm">코봇 BASE 기준 용접선 시작점(참고 — 벽면 위라 이동 목표 아님).</param>
/// <param name="ApproachBaseMm">코봇 BASE 기준 접근점 — <b>이동 목표 위치</b>.</param>
/// <param name="MountUsed">스트로크를 반영해 실제 사용한 T_A_B.</param>
/// <param name="PlanarDistanceMm">BASE 원점에서 접근점까지 수평 거리 — 리치 판단용.</param>
/// <param name="DistanceMm">BASE 원점에서 접근점까지 3D 거리.</param>
/// <param name="Direction">seamEnd 를 준 경우의 검사 방향 유도 결과(§4.4).</param>
/// <param name="DirectionReason">방향 판정 근거 문자열.</param>
/// <param name="Notes">해석 시 붙은 경고·주의 문구.</param>
public sealed record SeamBaseTarget(
    double[] SeamStartMapMm,
    double[] ApproachMapMm,
    double[] SeamStartBaseMm,
    double[] ApproachBaseMm,
    double[] MountUsed,
    double PlanarDistanceMm,
    double DistanceMm,
    InspectionMoveDirection? Direction,
    string? DirectionReason,
    IReadOnlyList<string> Notes);
