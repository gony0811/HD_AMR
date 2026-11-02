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
public sealed record PlanarApproachRecord(
    double[] WallOut, double[] ApproachPoint, double RetreatMm, double[] Entry, string? WallCode);

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
