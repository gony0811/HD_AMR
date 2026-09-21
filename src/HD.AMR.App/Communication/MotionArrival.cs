namespace HD.AMR.App.Communication;

/// <summary>
/// 이동 명령 뒤 실제 TCP 가 목표에 도달했는지의 판정 결과(<see cref="FairinoRpcClient.WaitUntilReachedAsync"/>).
/// 이동 RPC 가 rc=0 을 돌려줘도 "명령 수락"과 "도달 완료"는 다를 수 있으므로, 캡처·측정 전에 이 결과로
/// 확인한다.
/// </summary>
/// <param name="Reached">목표 허용오차 이내에서 정지했는가.</param>
/// <param name="NeverMoved">시작 자세에서 사실상 벗어나지 않았는가(명령이 수락만 되고 실행되지 않은 경우).</param>
/// <param name="Stationary">마지막 두 폴링 사이에 정지해 있었는가(타임아웃 시 "멈춰 있으나 목표와 다름"과 "아직 이동 중"을 구분).</param>
/// <param name="PosErrMm">최종 위치 오차(mm).</param>
/// <param name="RotErrDeg">최종 회전 오차(도).</param>
/// <param name="MovedMm">시작 자세 대비 총 이동(mm).</param>
/// <param name="TurnedDeg">시작 자세 대비 총 회전(도).</param>
/// <param name="Elapsed">대기 시간.</param>
/// <param name="FinalPose">마지막으로 읽은 TCP pose(BASE 기준).</param>
public sealed record MotionArrival(
    bool Reached, bool NeverMoved, bool Stationary,
    double PosErrMm, double RotErrDeg,
    double MovedMm, double TurnedDeg,
    TimeSpan Elapsed, double[] FinalPose)
{
    /// <summary>진단 문자열 — 오류 메시지·로그에 붙인다.</summary>
    public string Describe() =>
        $"오차 {PosErrMm:0.#}mm/{RotErrDeg:0.##}°, 이동 {MovedMm:0.#}mm/{TurnedDeg:0.##}°, {Elapsed.TotalSeconds:0.0}s";
}
