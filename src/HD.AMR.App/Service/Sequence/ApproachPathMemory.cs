namespace HD.AMR.App.Service.Sequence;

/// <summary>
/// ② 평면 경유 접근이 실제로 지나온 길 — 복귀(<see cref="Steps.SequenceEntry.ReturnToBaseAsync"/>)가 같은 길을 거슬러
/// 가도록 남긴다. 위치는 전부 코봇 BASE [mm].
/// </summary>
/// <param name="WallOut">면 바깥(벽에서 멀어지는) 단위 법선.</param>
/// <param name="ApproachPoint">② 접근점(면 이격 평면 위).</param>
/// <param name="RetreatMm">후퇴 평면 = 접근점 면 이격 평면 + 이 거리.</param>
/// <param name="Entry">진입점(후퇴 평면 위) — 진입 MoveJ 의 끝점. 복귀는 여기까지 MoveL 로 온 뒤 MoveJ 한다.</param>
/// <param name="WallCode">면 코드 — 로그용.</param>
/// <param name="StandoffMm">② 면 이격 [mm] — 접근점에서 면까지. 검사 중에는 툴이 이만큼 면 쪽(접근점 평면 뒤)에 있다. 0 = 모름.</param>
/// <param name="EntryPose">진입점에 섰을 때의 TCP 전체 자세(BASE [x,y,z,rx,ry,rz]). 복귀는 이 자세로 되돌려 진입 MoveJ 의
/// 역경로를 탄다 — 현재 자세 그대로 돌아가면 연속 검사로 손목이 돈 만큼(예: J6 −180°→−270°) 다른 관절 경로가 되어 면 쪽으로
/// 휜다(실기 2026-11-27 15:31 A L4-R01C09 #5). null = 모름(종전처럼 자세 유지).</param>
/// <param name="EntryJoints">진입점 관절각 — 복귀가 같은 해 가지로 돌아왔는지 확인한다.</param>
/// <param name="StopKey">기록 당시 정차 식별자(<see cref="Steps.SequenceEntry.StopKeyOf"/>) — 같은 정차의 다음 용접선 면 앞 이송 판정용.</param>
public sealed record PlanarApproachRecord(
    double[] WallOut, double[] ApproachPoint, double RetreatMm, double[] Entry, string? WallCode, double StandoffMm = 0,
    double[]? EntryPose = null, double[]? EntryJoints = null, string? StopKey = null);

/// <summary>
/// 마지막 ② 평면 경유 접근 기록. 복귀는 시퀀스 종료·AMR 주행 전 등 <b>다른 SequenceContext</b> 에서도 불리므로
/// 프로세스 전역으로 둔다(코봇은 하나). ② 가 다른 경로로 이동하거나 기준 자세에 도착하면 지운다.
/// </summary>
public static class ApproachPathMemory
{
    private static PlanarApproachRecord? _last;

    public static PlanarApproachRecord? Last => Volatile.Read(ref _last);

    public static void Record(PlanarApproachRecord record) => Volatile.Write(ref _last, record);

    public static void Clear() => Volatile.Write(ref _last, null);
}
