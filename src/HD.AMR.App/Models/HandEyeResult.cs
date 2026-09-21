namespace HD.AMR.App.Models;

/// <summary>
/// 눈-손(hand-eye) 캘리브레이션 결과 — 코봇 <b>플랜지 → 카메라 광학 프레임</b> 변환 <c>T_F_C</c>.
///
/// 카메라에 컨트롤러 TOOL 을 정의할 필요가 없도록 <b>플랜지(tool 0 = identity) 기준</b>으로 정의한다.
/// 마커를 고정하고 AMR·리프트를 세워 둔 채 코봇 자세만 바꿔 <c>AX = XB</c> 로 푼다 —
/// AMR pose 가 소거되므로 <c>T_A_B</c> 를 몰라도 풀린다(순환 의존 없음).
///
/// 이 값의 오차는 <c>T_A_B</c> 로 1:1 전파되고, 다시 TOOL1 의 모든 이동에 계통 오프셋으로 들어간다.
/// </summary>
/// <param name="Success">산출 성공 여부. 실패해도 예외가 아니라 이 값이 false 로 온다.</param>
/// <param name="Error">실패 사유(한국어). 성공 시 null.</param>
/// <param name="PoseFC">T_F_C [x,y,z,rx,ry,rz] (mm/도, FrameMath ZYX). 실패 시 0 배열.</param>
/// <param name="N">사용한 자세 수.</param>
/// <param name="PairCount">유효 상대 운동 쌍 수(회전이 너무 작은 쌍은 제외).</param>
/// <param name="RotationRmsDeg">AX−XB 회전 잔차 RMS(도).</param>
/// <param name="RotationMaxDeg">회전 잔차 최댓값(도).</param>
/// <param name="TranslationRmsMm">AX−XB 병진 잔차 RMS(mm).</param>
/// <param name="TranslationMaxMm">병진 잔차 최댓값(mm).</param>
/// <param name="AxisSpreadDeg">
/// 상대 회전축의 유효 펼침각(도). <b>AX=XB 가 풀리는지를 좌우하는 유일한 조건</b> —
/// 모든 축이 평행하면 0 에 수렴하고, 그때는 표본을 아무리 늘려도 해가 없다.
/// </param>
/// <param name="Warnings">주의 메시지(자세 부족, 축 다양성 부족, 잔차 과대).</param>
/// <param name="SampleRotationResidualDeg">
/// 표본별 회전 잔차(도) — 그 표본이 포함된 유효 쌍들의 AX−XB 회전 잔차 RMS. 입력 순서와 같은 길이이며,
/// 어떤 쌍에도 들지 못한 표본은 NaN. 한 표본만 튀면 그 자세(흔들림·자세 플립)를 삭제하고 재산출한다.
/// </param>
/// <param name="SampleTranslationResidualMm">표본별 병진 잔차(mm) — 위와 같은 정의.</param>
public sealed record HandEyeResult(
    bool Success,
    string? Error,
    double[] PoseFC,
    int N,
    int PairCount,
    double RotationRmsDeg,
    double RotationMaxDeg,
    double TranslationRmsMm,
    double TranslationMaxMm,
    double AxisSpreadDeg,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<double> SampleRotationResidualDeg,
    IReadOnlyList<double> SampleTranslationResidualMm)
{
    /// <summary>실패 결과 헬퍼.</summary>
    public static HandEyeResult Fail(string error) =>
        new(false, error, new double[6], 0, 0, 0, 0, 0, 0, 0, Array.Empty<string>(),
            Array.Empty<double>(), Array.Empty<double>());
}
