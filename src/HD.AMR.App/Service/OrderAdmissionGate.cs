using HD.AMR.App.Communication.Vda5050;
using HD.AMR.App.Enums;

namespace HD.AMR.App.Service;

/// <summary>
/// Order 수락 게이트(사양 docs/HD_AMR_배터리관리_사양.md §4).
///
/// 수신 Order 를 미션 상태머신에 넘기기 전 통과시키는 필터. 전력 상태(<see cref="PowerMode"/>) 와
/// VDA5050 operatingMode(AUTOMATIC/MANUAL), 그리고 포함된 action 타입을 보고 ACCEPT/REJECT 를 결정한다.
///
/// 결정 규칙:
/// <list type="bullet">
///   <item>PowerMode == <see cref="PowerMode.Normal"/> → ACCEPT (모든 Order).</item>
///   <item>PowerMode in {LOW, CRITICAL}:
///     <list type="bullet">
///       <item>operatingMode != "AUTOMATIC" → REJECT (MANUAL 에서는 ACS 가 Order 를 자동 발행하지 않는 전제).</item>
///       <item>액션 없는 Order(순수 주행) → REJECT (잔여 주행이 복귀 여유 잠식).</item>
///       <item>단 하나의 action 이라도 <see cref="AllowedInLowActionTypes"/> 에 없으면 REJECT.</item>
///       <item>그 외 ACCEPT.</item>
///     </list>
///   </item>
/// </list>
///
/// Phase 2 범위에서 <see cref="AllowedInLowActionTypes"/> 는 Phase 3 의 <c>BatterySwapMoveAction</c> 하나만을
/// 선반영한다. 실제 핸들러가 Phase 3 에 들어오기 전까지는 ACS 가 이 액션 타입으로 Order 를 보내도
/// 어댑터가 "미지원 노드 액션"으로 FAIL 하지만, 게이트의 forward-compat 를 미리 맞춰 둔다.
///
/// InstantActions(cancelOrder/startPause/stopPause/stateRequest/factsheetRequest/emergencyStop)는
/// 상시 허용(사양 §4) — 이 게이트는 Order 전용이고 instantActions 경로를 가로채지 않는다.
/// </summary>
public static class OrderAdmissionGate
{
    /// <summary>저전력(LOW/CRITICAL) + AUTO 에서 허용되는 action 타입. ACS 사양서(VDA5050_INTERFACE_SPEC.md §8.8) 명문.</summary>
    public static readonly IReadOnlySet<string> AllowedInLowActionTypes = new HashSet<string>
    {
        "batterySwapMove",
    };

    public enum Decision
    {
        Accept,
        /// <summary>저전력 사유 — <c>orderRejectedBatteryLow</c> WARNING 보고 대상.</summary>
        RejectBatteryLow,
    }

    public static Decision Evaluate(PowerMode powerMode, string operatingMode, Vda5050Order order)
    {
        if (powerMode == PowerMode.Normal) return Decision.Accept;

        // 저전력 상태에서는 AUTO 모드가 아니면 모든 Order 거부.
        if (!string.Equals(operatingMode, "AUTOMATIC", StringComparison.Ordinal))
            return Decision.RejectBatteryLow;

        // 액션이 하나도 없는 Order(순수 주행)는 거부 — 복귀 여유 잠식 방지.
        var hasAny = false;
        foreach (var node in order.Nodes)
        {
            foreach (var action in node.Actions)
            {
                hasAny = true;
                if (!AllowedInLowActionTypes.Contains(action.ActionType))
                    return Decision.RejectBatteryLow;
            }
        }
        return hasAny ? Decision.Accept : Decision.RejectBatteryLow;
    }
}
