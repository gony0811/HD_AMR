namespace HD_AMR.Models;

/// <summary>
/// 터치 TCP 캘리브레이션의 접촉 기록 1건 — 팁이 평면 위에 있을 때 레이저로 실측한 평면을
/// <b>플랜지 좌표</b> 제약식 n·x = c 로 변환한 것.
/// </summary>
/// <param name="NormalFlange">평면 단위 법선(3, 플랜지 좌표) — 표면→센서 방향.</param>
/// <param name="C">평면 오프셋(mm): n·x = c.</param>
/// <param name="NormalToolZ">툴-1 좌표 법선의 z성분(= cos 틸트) — 출사면 높이 h 미지수의 계수.</param>
/// <param name="RxDeg">기록 시 보정 틸트 Rx(도, 모델 규약) — 자세 다양성 표시용.</param>
/// <param name="RyDeg">기록 시 보정 틸트 Ry(도, 모델 규약).</param>
/// <param name="MeanDistanceMm">기록 시 평균 측정거리(mm).</param>
/// <param name="ToolCoord1">기록 시점의 tool-1 정의 [x,y,z,rx,ry,rz] — 기록 간 변경 감지용.</param>
/// <param name="AtUtc">기록 시각(UTC).</param>
public sealed record TcpTouchRecord(
    double[] NormalFlange,
    double C,
    double NormalToolZ,
    double RxDeg,
    double RyDeg,
    double MeanDistanceMm,
    double[] ToolCoord1,
    DateTime AtUtc);

/// <summary>
/// 터치 TCP 캘리브레이션 최소자승 결과. 팁 X/Y 는 레이저로 산출한 <b>플랜지 좌표</b>(mm)이고,
/// <b>팁 Z 는 관측 불가 DOF 라서 외부 기준값을 그대로 반환</b>한다(빔이 툴 Z와 평행이라
/// 팁 z 와 출사면 높이 h 의 계수가 모든 제약식에서 비례 — 어떤 터치 조합으로도 분리 불가).
/// 대신 h(출사면 높이, 툴-1 Z)가 미지수로 흡수·추정되어 X/Y 는 무편향이다.
/// 정밀도는 접촉 정밀도가 지배한다(±0.5~1.5mm 기대). 어디에도 저장하지 않는다 —
/// 화면 표시 후 명시적 버튼으로만 컨트롤러 공구 좌표계에 쓴다.
/// </summary>
/// <param name="Success">산출 성공 여부.</param>
/// <param name="Error">실패 사유(성공이면 null).</param>
/// <param name="TipX">팁 X(mm, 플랜지) — 산출값.</param>
/// <param name="TipY">팁 Y(mm, 플랜지) — 산출값.</param>
/// <param name="TipZ">팁 Z(mm, 플랜지) — 입력한 기준값 그대로(관측 불가).</param>
/// <param name="HeadPlaneZmm">추정 출사면 높이 h(툴-1 Z, mm) — 터치 평균거리와 비슷해야 정상.</param>
/// <param name="ResidualsMm">터치별 부호 잔차(mm) — 해에서 각 평면까지 거리.</param>
/// <param name="RmsMm">잔차 RMS(mm).</param>
/// <param name="MaxAbsMm">최대 |잔차|(mm).</param>
/// <param name="LambdaMin">정규방정식 최소 고유값 — 법선 다양성 지표.</param>
/// <param name="SpreadDeg">법선 유효 펼침각(도) ≈ asin(√(λ_min/N)).</param>
/// <param name="DeltaFromTool1">팁 − tool-1 위치 (3, mm). tool-1 미제공 시 null.</param>
/// <param name="Warnings">주의 메시지(다양성 부족, 아웃라이어, tool-1 변경 등).</param>
public sealed record TcpTouchCalibrationResult(
    bool Success,
    string? Error,
    double TipX,
    double TipY,
    double TipZ,
    double HeadPlaneZmm,
    IReadOnlyList<double> ResidualsMm,
    double RmsMm,
    double MaxAbsMm,
    double LambdaMin,
    double SpreadDeg,
    double[]? DeltaFromTool1,
    IReadOnlyList<string> Warnings)
{
    /// <summary>실패 결과 헬퍼.</summary>
    public static TcpTouchCalibrationResult Fail(string error) =>
        new(false, error, 0, 0, 0, 0, Array.Empty<double>(), 0, 0, 0, 0, null, Array.Empty<string>());
}
