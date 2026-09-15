using HD.AMR.App.Communication.Vda5050;

namespace HD.AMR.App.Service.Inspection;

/// <summary>
/// `startWeldInspection` 노드 액션 1건의 실행 결과.
/// <see cref="ErrorType"/>은 사양 §6.4 확정 7종 중 검사 경로 3종만 사용:
/// orderValidationError(계약 위반) / equipmentError(설비 불능) / inspectionFailed(실행 불가·실패).
/// </summary>
public sealed record InspectionActionResult(
    bool Success,
    string ResultDescription,
    string? ErrorType = null,
    string? ErrorDescription = null)
{
    public static InspectionActionResult Ok(string description) => new(true, description);

    public static InspectionActionResult Fail(string errorType, string description) =>
        new(false, description, errorType, description);
}

/// <summary>
/// VDA5050 `startWeldInspection` 액션 실행기 — <see cref="Service.Vda5050OrderExecutor"/>(singleton)가
/// 노드 도달 후 액션별로 위임한다. 구현체는 scope 를 생성해 scoped 시퀀스 서비스들을 사용한다.
/// </summary>
public interface IWeldInspectionExecutor
{
    /// <summary>액션 1건 실행 — 파싱→레시피 매핑→프로필 조회→시퀀스 실행→결과. 예외를 던지지 않는다.</summary>
    /// <param name="nodeThetaRad">정차 노드 theta(벽 정면 방향, rad) — seam 벡터→검사 방향 자동 유도(§4.4)의
    /// 기준. null(액션 없는 Order 등)이면 현행 기본(Horizontal)으로 폴백.</param>
    Task<InspectionActionResult> ExecuteAsync(VdaAction action, string orderId, double? nodeThetaRad, CancellationToken ct);

    /// <summary>정렬(anchor) 캐시 무효화 — 주행 발생·신규 order 시 호출(사양 §8.1 anchorGroupId 계약).</summary>
    void InvalidateAnchor();

    /// <summary>진행 중 검사 시퀀스 즉시 중단(emergencyStop 경로) — 시퀀스 취소 + 코봇 모션 정지.</summary>
    Task AbortAsync();
}
