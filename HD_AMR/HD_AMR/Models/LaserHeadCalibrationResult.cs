namespace HD_AMR.Models;

/// <summary>
/// 헤드 1개(채널)의 틸트 응답 캘리브레이션 산출 결과. 좌표는 툴 좌표계(appsettings 규약) mm.
/// </summary>
/// <param name="Channel">채널 번호(1-based).</param>
/// <param name="MeasuredX">측정된 헤드 X 오프셋(mm).</param>
/// <param name="MeasuredY">측정된 헤드 Y 오프셋(mm).</param>
/// <param name="CurrentX">현재 설정(appsettings)의 X 오프셋(mm).</param>
/// <param name="CurrentY">현재 설정(appsettings)의 Y 오프셋(mm).</param>
/// <param name="SlopeStdXMm">X 산출값의 샘플 노이즈 전파 표준편차(mm).</param>
/// <param name="SlopeStdYMm">Y 산출값의 샘플 노이즈 전파 표준편차(mm).</param>
/// <param name="EvenModeResidualMm">짝수모드 잔차(mm) — ±θ 평균과 기준의 차이에서 공통항(중앙값)을 뺀 값.
/// 크면 TCP가 툴 원점과 어긋났거나 표면 곡률/미끄러짐 의심.</param>
/// <param name="Confidence">신뢰도 판정: "양호" / "주의" / "불량".</param>
public record HeadCalibrationEntry(
    int Channel,
    double MeasuredX,
    double MeasuredY,
    double CurrentX,
    double CurrentY,
    double SlopeStdXMm,
    double SlopeStdYMm,
    double EvenModeResidualMm,
    string Confidence);

/// <summary>
/// 틸트 응답 헤드 캘리브레이션 전체 결과. <see cref="Success"/>=false 면 <see cref="Error"/> 에 사유.
/// 설정 파일에는 쓰지 않으며 <see cref="AppSettingsJsonSnippet"/> 을 수동 반영한다.
/// </summary>
/// <param name="Success">전 과정(모션·측정·검증 항등식) 성공 여부.</param>
/// <param name="Error">실패 사유(성공이면 null).</param>
/// <param name="Heads">헤드별 산출 결과(성공 시 3개).</param>
/// <param name="BaselineRxDeg">최종 회차 기준(무틸트) 자세 Rx(° — <b>측정된</b> 오프셋 기준).</param>
/// <param name="BaselineRyDeg">최종 회차 기준 자세 Ry(°).</param>
/// <param name="MeanDistanceMm">기준 3채널 평균 거리(mm).</param>
/// <param name="SignCheckPassed">+Z 이동 부호 검증 통과 여부(생략 시 false).</param>
/// <param name="XAxisFlipped">검증 항등식에서 X(전 헤드) 부호 반전이 필요했는지 — 정상이면 false.</param>
/// <param name="YAxisFlipped">검증 항등식에서 Y(전 헤드) 부호 반전이 필요했는지 — 정상이면 false.</param>
/// <param name="TriangleAreaMm2">측정 오프셋으로 만든 삼각형 면적(mm²) — 퇴화 판정용.</param>
/// <param name="LevelIterations">사용한 프로브 회차 수(자동 수평 반복 포함, 1~3).</param>
/// <param name="FinalTiltDeg">최종 산출 시점의 잔여 기울기(°, 측정 오프셋 기준 max(|Rx|,|Ry|)).</param>
/// <param name="Warnings">경고 메시지 목록.</param>
/// <param name="AppSettingsJsonSnippet">appsettings.json 에 붙여넣을 Head*Offset* 6줄.</param>
/// <param name="ReturnedToAnchor">종료 시 앵커 pose 복귀 성공 여부 — false 면 수동 확인 필요.
/// 자동 수평이 동작한 경우 앵커 = 수평 보정된 마지막 자세(시작 자세 아님).</param>
public record LaserHeadCalibrationResult(
    bool Success,
    string? Error,
    IReadOnlyList<HeadCalibrationEntry> Heads,
    double BaselineRxDeg,
    double BaselineRyDeg,
    double MeanDistanceMm,
    bool SignCheckPassed,
    bool XAxisFlipped,
    bool YAxisFlipped,
    double TriangleAreaMm2,
    int LevelIterations,
    double FinalTiltDeg,
    IReadOnlyList<string> Warnings,
    string AppSettingsJsonSnippet,
    bool ReturnedToAnchor)
{
    /// <summary>실패 결과 헬퍼.</summary>
    public static LaserHeadCalibrationResult Fail(string error, bool returnedToAnchor = true) =>
        new(false, error, Array.Empty<HeadCalibrationEntry>(), 0, 0, 0,
            false, false, false, 0, 0, 0, Array.Empty<string>(), string.Empty, returnedToAnchor);
}
