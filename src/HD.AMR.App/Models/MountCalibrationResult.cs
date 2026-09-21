namespace HD.AMR.App.Models;

/// <summary>
/// 6-DoF 장착 캘리브레이션(T_A_B, AMR 차체 → 코봇 BASE) 산출 결과.
///
/// 고정 세계점 1개를 여러 AMR 자세에서 코봇 TCP 로 터치한 표본에서
/// (rx, ry) 는 코봇 BASE 기준 터치점 평면의 법선(총최소자승)으로,
/// (rz, tx, ty) 는 그 기울기로 되돌린 터치점의 평면 문제(<c>MapCalibration.SolveMount2D</c>)로 산출한다.
///
/// ⚠ <b>tz 는 원리상 관측 불가</b> — AMR pose 가 평면(yaw·x·y)이라 z 방정식이
/// <c>q_z = r3·p_k + t_z</c> 하나로 분리되고, q_z 와 t_z 는 항상 <c>d = q_z − t_z</c> 로만 묶여 나온다.
/// 타깃 높이(<see cref="TargetZmm"/>, AMR 차체 원점 기준)를 입력해야 <c>tz = q_z − d</c> 로 닫힌다.
/// 미입력이면 <see cref="TzObserved"/>=false, <see cref="MountPose"/>[2]=0, <see cref="PlaneOffsetDmm"/> 만 유효하다.
///
/// 정밀도 특성: z 방정식에 AMR pose 가 전혀 들어가지 않으므로 <b>rx/ry 는 SLAM 잡음과 무관</b>하고
/// 터치 반복도(≈1mm)와 터치점 펼침만으로 결정된다(±0.05~0.2° 기대). 반면 rz/tx/ty 는 SLAM pose
/// 잡음이 지배해 ±10~25mm 수준이며 표본 수의 √N 로만 줄어든다. tz 정확도는 입력 높이 정확도와 1:1.
/// </summary>
/// <param name="Success">산출 성공 여부. 실패해도 예외가 아니라 이 값이 false 로 온다.</param>
/// <param name="Error">실패 사유(한국어). 성공 시 null.</param>
/// <param name="MountPose">T_A_B [x,y,z,rx,ry,rz] (mm/도) — FrameMath ZYX 규약.</param>
/// <param name="TzObserved"><see cref="TargetZmm"/> 입력으로 tz 가 확정됐는지.</param>
/// <param name="TargetZmm">입력한 타깃 높이(AMR 차체 원점 기준, mm). 미입력이면 0.</param>
/// <param name="PlaneOffsetDmm">d = q_z − t_z. BASE 기준 터치평면 오프셋 — 높이 후입력 시 tz 재계산용.</param>
/// <param name="WorldPointMm">추정 고정 세계점 q=[qx,qy,qz] (맵 mm). qz 는 TzObserved 일 때만 유효.</param>
/// <param name="ResidualsMm">표본별 ‖e_k‖ (3D, 맵 mm).</param>
/// <param name="PlaneResidualsMm">표본별 부호 있는 평면 잔차 r3·p_k − d — <b>터치</b> 잡음 지표.</param>
/// <param name="PlanarResidualsMm">표본별 xy 잔차 크기 — <b>SLAM</b> 잡음 지표.</param>
/// <param name="RmsMm">전체 잔차 RMS = √(PlanarRms² + PlaneRms²).</param>
/// <param name="MaxAbsMm">최대 |잔차|(mm).</param>
/// <param name="PlaneRmsMm">평면 적합 잔차 RMS = √(λ₃/N).</param>
/// <param name="PlanarRmsMm">평면내(xy) 잔차 RMS — SolveMount2D 반환값.</param>
/// <param name="PlaneSpanMm">√(λ₂/N). 터치점의 평면 내 <b>약축</b> 펼침 — 공선 퇴화 지표.</param>
/// <param name="PlaneLambdaMinMm2">λ₃ (mm²) — 원시 지표.</param>
/// <param name="TiltSigmaDeg">rx/ry 1σ ≈ atan(σ_touch /(PlaneSpan·√N)).</param>
/// <param name="YawSpanDeg">AMR yaw 원형 범위(360 − 최대 공백).</param>
/// <param name="N">사용한 표본 수.</param>
/// <param name="DeltaPose">inv(현재 T_A_B)·산출 T_A_B 의 pose. 현재값 미제공 시 null.</param>
/// <param name="DeltaPosMm">현재값 대비 위치 변화량(mm).</param>
/// <param name="DeltaAngleDeg">현재값 대비 회전 변화량(도) — 성분 차가 아닌 상대 변환의 회전각.</param>
/// <param name="Warnings">주의 메시지(표본 부족, yaw/펼침 부족, 잔차 과대, 타깃 높이 미입력 등).</param>
public sealed record MountCalibrationResult(
    bool Success,
    string? Error,
    double[] MountPose,
    bool TzObserved,
    double TargetZmm,
    double PlaneOffsetDmm,
    double[] WorldPointMm,
    IReadOnlyList<double> ResidualsMm,
    IReadOnlyList<double> PlaneResidualsMm,
    IReadOnlyList<double> PlanarResidualsMm,
    double RmsMm,
    double MaxAbsMm,
    double PlaneRmsMm,
    double PlanarRmsMm,
    double PlaneSpanMm,
    double PlaneLambdaMinMm2,
    double TiltSigmaDeg,
    double YawSpanDeg,
    int N,
    double[]? DeltaPose,
    double DeltaPosMm,
    double DeltaAngleDeg,
    IReadOnlyList<string> Warnings)
{
    /// <summary>실패 결과 헬퍼.</summary>
    public static MountCalibrationResult Fail(string error) =>
        new(false, error, new double[6], false, 0, 0, new double[3],
            Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>(),
            0, 0, 0, 0, 0, 0, 0, 0, 0, null, 0, 0, Array.Empty<string>());
}
