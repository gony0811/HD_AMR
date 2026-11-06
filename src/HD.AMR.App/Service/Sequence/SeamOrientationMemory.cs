namespace HD.AMR.App.Service.Sequence;

/// <summary>② 가 마지막으로 환산한 용접선 방향.</summary>
/// <param name="SeamDirBase">용접선 방향(시작점→끝점) 코봇 BASE 단위벡터.</param>
/// <param name="OpticalAxisBase">그 용접선을 볼 때의 광축(면 법선 자세의 광축 툴축) BASE 단위벡터 — 같은 면을 보고 있는지 판정용.</param>
/// <param name="WallCode">면 코드 — 로그용.</param>
public sealed record SeamOrientationRecord(double[] SeamDirBase, double[] OpticalAxisBase, string? WallCode);

/// <summary>
/// 마지막 ② 용접선 방향 기록. ⑤~⑫(Peak·비드 찾기/센터링)가 진행축(영상 가로/세로)을 현재 툴 자세에서 직접 계산하는 데 쓴다.
/// ② 는 ACS moveToSeamStart·본검사·시퀀스 페이지처럼 <b>서로 다른 SequenceContext</b> 에서 돌 수 있고, 이어지는 Peak 단계는
/// 페이지에서 따로 실행되기도 하므로 프로세스 전역으로 둔다(코봇은 하나 — <see cref="ApproachPathMemory"/> 와 같은 이유).
/// 예전에는 페이지 컨텍스트의 검사방향(기본 수평)으로 진행축을 정해, moveToSeamStart 로 수직 용접선에 접근한 뒤 페이지에서
/// Peak 센터링을 돌리면 툴이 용접선을 가로질러(수평으로) 움직였다(실기 2026-11-06).
/// </summary>
public static class SeamOrientationMemory
{
    private static SeamOrientationRecord? _last;

    public static SeamOrientationRecord? Last => Volatile.Read(ref _last);

    public static void Record(SeamOrientationRecord record) => Volatile.Write(ref _last, record);

    public static void Clear() => Volatile.Write(ref _last, null);
}
