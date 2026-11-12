using HD.AMR.App.Communication.Vda5050;
using HD.AMR.App.Service.Sequence;

namespace HD.AMR.App.Service;

/// <summary>
/// 수동 비드 입력 팝업이 떠 있는 동안 작업자에게 알리는 수단 두 가지의 공통 규칙.
/// <list type="bullet">
/// <item>ACS 알림 — state.errors[] 에 <see cref="ErrorType"/>(WARNING)을 팝업이 떠 있는 동안만 싣는다. 실패가 아니라
/// "작업자 입력 대기"이며, 입력이 끝나면(확정·자동 결과 사용·실패 처리·시간 초과·중단) 사라진다 [VDA §6.4].</item>
/// <item>타워램프 — 녹색을 1초에 한 번 깜빡인다(<see cref="BlinkOn"/>). 알람(적색)·일시정지(황색)가 우선한다.</item>
/// </list>
/// </summary>
public static class ManualBeadNotice
{
    public const string ErrorType = "manualBeadInputRequired";

    /// <summary>점멸 반주기 [ms] — 켜짐 0.5초, 꺼짐 0.5초(1초에 한 번).</summary>
    public const int BlinkHalfPeriodMs = 500;

    /// <summary>이 시각에 녹색이 켜져 있어야 하는지. IO 폴링(200ms)이 이 값을 따라 출력을 바꾼다.</summary>
    public static bool BlinkOn(long tickMs) => tickMs / BlinkHalfPeriodMs % 2 == 0;

    /// <summary>팝업 요청 → state.errors[] 항목.</summary>
    public static VdaError ToError(ManualBeadRequest req) => new()
    {
        ErrorType = ErrorType,
        ErrorLevel = "WARNING",
        ErrorDescription = $"{req.Stage}: 수동 비드 입력 대기 — {req.Reason} " +
                           $"(AMR 화면에서 비드선 지정, {req.DeadlineUtc.ToLocalTime():HH:mm:ss} 까지)",
        ErrorReferences = new List<VdaErrorReference>
        {
            new() { ReferenceKey = "stage", ReferenceValue = req.Stage },
            new() { ReferenceKey = "deadline", ReferenceValue = req.DeadlineUtc.ToString("yyyy-MM-ddTHH:mm:ssZ") },
        },
    };
}
