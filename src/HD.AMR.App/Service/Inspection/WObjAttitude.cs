using HD.AMR.App.Communication;

namespace HD.AMR.App.Service.Inspection;

/// <summary>
/// 작업물 좌표계(user=N) 기준 경유점 자세 합성 — 시퀀스 ⑫ <c>InspectionRunStep</c> 과 검사 프로파일 페이지
/// (/inspection) 가 공유하는 규칙.
///
/// 3점 교시는 원점 위치와 축 방향만 정하고 "교시 당시 툴 자세"를 자세 0 으로 만들어 주지 않는다.
/// 프레임 X 는 항상 점1→점2 방향이라, 점1 캡처 때 툴 +X 가 반대를 보고 있었으면 툴은 프레임 대비
/// RZ ≈ ±180° 상태다. 이때 rz=0 을 명령하면 같은 위치에서 툴을 Z 둘레로 180° 돌린 자세가 되어
/// 손목 한계/도달 불가(112)가 난다. 그래서 시작 시점의 프레임 기준 RZ(<see cref="Rz0"/>)를 읽어
/// 유지하고, 도면 회전(rz)은 그 위에 더한다.
///
/// 틸트 θ 는 프레임 Y 둘레 회전(ry)인데, ZYX 규약에서 Rz(rz0)·Ry(θ) 는 rz0≈±180 이면 툴 기준으로
/// 부호가 뒤집힌다(Ry_frame(θ)·Rz(rz0) = Rz(rz0)·Ry(±θ)). 그래서 <see cref="TiltSign"/> = sign(cos rz0).
/// </summary>
public readonly record struct WObjAttitude(double Rz0, double TiltSign)
{
    /// <summary>보정 없음 — 베이스(user=0) 실행처럼 pose 를 절대값으로 그대로 명령할 때.</summary>
    public static readonly WObjAttitude Identity = new(0.0, 1.0);

    /// <summary>현재 베이스 pose 와 프레임 T_N(베이스 기준)으로 유지 RZ·틸트 부호를 구한다.</summary>
    public static WObjAttitude FromCurrent(double[] curBasePose, double[] framePose)
    {
        var rel = FrameMath.ToFrame(curBasePose, framePose);
        var rz0 = rel[5];
        var sign = Math.Cos(rz0 * Math.PI / 180.0) >= 0 ? 1.0 : -1.0;
        return new WObjAttitude(rz0, sign);
    }

    /// <summary>컨트롤러에서 현재 TCP(공구 <paramref name="tool"/>)와 작업물 <paramref name="wobjId"/> 를 읽어 구한다.
    /// wobjId ≤ 0(베이스)이면 <see cref="Identity"/>. 프레임 미등록(원점=0)이면 예외.</summary>
    public static async Task<WObjAttitude> ResolveAsync(FairinoRpcClient rpc, int wobjId, int tool, CancellationToken ct = default)
    {
        if (wobjId <= 0) return Identity;
        var frame = await rpc.GetWObjCoordAsync(wobjId, ct);
        if (frame.Length != 6 || frame.All(v => v == 0))
            throw new InvalidOperationException($"작업물 좌표계 #{wobjId} 가 등록되지 않았습니다(원점=0) — 좌표계를 먼저 등록하세요.");
        var cur = await rpc.GetTcpPoseInBaseAsync(tool, ct);
        return FromCurrent(cur, frame);
    }

    /// <summary>경유점 pose 합성: 위치 그대로, rx=0, ry=틸트부호·θ, rz=rz0+도면 회전.</summary>
    public double[] Pose(double x, double y, double z, double thetaDeg, double rzDeg)
        => new[] { x, y, z, 0.0, TiltSign * thetaDeg, Rz0 + rzDeg };

    /// <summary>
    /// <see cref="AbsoluteTwistDeg"/> 를 직접 지정(0 또는 180°). null 이면 cos rz0 부호로 정한다.
    /// 툴 X 가 프레임 X 와 수직(rz0 ≈ ±90°)이면 cos 부호가 의미 없어 회전 방향이 사실상 임의가 되므로,
    /// 시퀀스는 <see cref="SelectSafeTwist"/> 로 고른 값을 여기에 넣는다.
    /// </summary>
    public double? TwistOverrideDeg { get; init; }

    /// <summary>
    /// 절대 6-DOF 경유점(RzDeg 가 프레임 X 기준)의 광축 둘레 보정각 — 0 또는 180°.
    /// 프레임 X 는 점1→점2(검사 이동 방향)라, 반대 방향으로 검사하면 rz=0 이 툴을 반 바퀴 돌린 자세가
    /// 되어 J6 가 90°↔270° 로 뒤집힌다(센서 헤드–링크암 간섭). 시작 자세의 툴 X 가 프레임 X 와 반대면
    /// (cos rz0 &lt; 0) 툴 Z 둘레 180° 를 더해 ②에서 맞춘 J6(수평 90°/수직 180°)를 유지한다.
    /// </summary>
    public double AbsoluteTwistDeg => TwistOverrideDeg ?? (TiltSign < 0 ? 180.0 : 0.0);

    /// <summary>시작 자세에서 허용하는 J6 증가(시계방향) 여유 [도] — IK·자세 오차 흡수용.</summary>
    public const double ClockwiseToleranceDeg = 5.0;

    /// <summary>시작 자세에서 허용하는 J6 감소(반시계방향) 최대량 [도] — 설계상 −90°(대기 180° → 수평 90°) + 여유.</summary>
    public const double MaxCounterClockwiseDeg = 100.0;

    /// <summary>센서 헤드–링크암 간섭이 없는 J6 물리 범위 [도](360° 주기) — 수평 90° ~ 수직(대기) 180°.</summary>
    public const double SafeJ6MinDeg = 90.0, SafeJ6MaxDeg = 180.0;

    /// <summary>안전 범위 경계 여유 [도] — IK·정차 방향 오차 흡수용.</summary>
    public const double SafeJ6MarginDeg = 5.0;

    /// <summary>J6 [도] 가 안전 범위(<see cref="SafeJ6MinDeg"/>~<see cref="SafeJ6MaxDeg"/> ± 여유, 360° 주기) 안인지.</summary>
    public static bool IsInSafeJ6Range(double j6Deg)
    {
        var a = ((j6Deg % 360.0) + 360.0) % 360.0;
        return a >= SafeJ6MinDeg - SafeJ6MarginDeg && a <= SafeJ6MaxDeg + SafeJ6MarginDeg;
    }

    /// <summary>
    /// 절대 모드 비틀림(0/180°) 후보 중 센서 헤드–링크암 간섭이 없는 회전을 고른다. 센서 헤드가 플랜지 한쪽으로
    /// 나와 있어 J6 를 안전 범위(물리 90°~180°) 밖으로 돌리면 간섭한다(실기 2026-10-13: 수평 비드에서
    /// rz0=−94° 일 때 cos 부호로 180° 를 골라 J6 −179.6°→−92° 로 시계 회전).
    /// 허용(둘 중 하나):
    ///  · 범위 내 이동 — 현재·목표 J6 가 모두 안전 범위 안이고 |ΔJ6| ≤ <see cref="MaxCounterClockwiseDeg"/>.
    ///    범위 폭이 90°라 경로도 범위 안에 머문다. 수평 자세(90°, 범위 끝)에서 정차 방향 오차 때문에
    ///    대기 자세 쪽으로 몇 도 되돌아가는 경우가 여기 해당한다(실기 2026-10-15 PL: Δ+6.2°).
    ///  · 반시계 회전 — −<see cref="MaxCounterClockwiseDeg"/> ≤ ΔJ6 ≤ +<see cref="ClockwiseToleranceDeg"/>
    ///    (현재 J6 가 범위 밖일 때의 종전 규칙).
    /// 허용 후보 중 <b>관절 총 변위</b>가 가장 작은 것을 고르고(총 변위를 모르면 |ΔJ6| 최소), 없으면 null
    /// (호출측은 움직이지 않고 실패). 손목을 뒤집은 해(J4·J5 크게 이동)는 MoveL 로 갈 수 없는데 J6 변화만
    /// 보면 그쪽이 작게 나올 수 있다(실기 2026-10-15 F-A0002: Δ0° 동률·총 변위 87° vs 308°,
    /// 천장 T: Δ−7.3°·369° vs Δ+9.8°·55° 에서 손목 반전 해를 골라 IK 112).
    /// </summary>
    /// <param name="currentJ6">현재 J6 [도].</param>
    /// <param name="candidates">비틀림 후보, 그 목표의 IK J6(현재 와인딩 기준, 도달 불가면 null),
    /// 현재 관절 대비 총 변위 [도](모르면 null — 동률 판정에서 뒤로 밀린다).</param>
    public static (double TwistDeg, double DeltaJ6)? SelectSafeTwist(
        double currentJ6, IReadOnlyList<(double TwistDeg, double? J6, double? TotalDeg)> candidates)
    {
        var currentSafe = IsInSafeJ6Range(currentJ6);
        (double TwistDeg, double DeltaJ6, double Total)? best = null;
        foreach (var (twist, j6, totalDeg) in candidates)
        {
            if (j6 is not { } j) continue;
            var d = j - currentJ6;
            var withinRange = currentSafe && IsInSafeJ6Range(j) && Math.Abs(d) <= MaxCounterClockwiseDeg;
            var counterClockwise = d <= ClockwiseToleranceDeg && d >= -MaxCounterClockwiseDeg;
            if (!withinRange && !counterClockwise) continue;
            var cost = totalDeg ?? Math.Abs(d);
            if (best is not { } b || cost < b.Total)
                best = (twist, d, cost);
        }
        return best is { } r ? (r.TwistDeg, r.DeltaJ6) : null;
    }

    /// <inheritdoc cref="SelectSafeTwist(double, IReadOnlyList{ValueTuple{double, double?, double?}})"/>
    public static (double TwistDeg, double DeltaJ6)? SelectSafeTwist(
        double currentJ6, IReadOnlyList<(double TwistDeg, double? J6)> candidates)
        => SelectSafeTwist(currentJ6, candidates.Select(c => (c.TwistDeg, c.J6, (double?)null)).ToList());


    /// <summary>절대 모드 경유점의 BASE 목표에 <see cref="AbsoluteTwistDeg"/> 를 툴 프레임에서 적용한다.
    /// 위치·광축 방향은 그대로이고 광축 둘레 비틀림만 바뀐다.</summary>
    public double[] KeepTwist(double[] basePose)
        => AbsoluteTwistDeg == 0.0
            ? basePose
            : FrameMath.FromFrame(new[] { 0.0, 0.0, 0.0, 0.0, 0.0, AbsoluteTwistDeg }, basePose);

    public override string ToString() => $"RZ 유지={Rz0:0.0}° (틸트부호 {TiltSign:+0;-0})";
}
