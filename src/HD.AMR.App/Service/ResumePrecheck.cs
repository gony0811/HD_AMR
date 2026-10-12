using HD.AMR.App.Enums;

namespace HD.AMR.App.Service;

/// <summary>
/// 일시정지 재개 전 확인(통신 단절 사양 §4.2) — 하나라도 어긋나면 거부 사유를 반환한다(통과 시 null).
/// 거부 사유는 VDA5050 <c>resumeRejected</c> 에러 설명으로 그대로 보고된다(§5).
/// </summary>
public static class ResumePrecheck
{
    public sealed record Input(
        bool HasPausedMission,
        bool EmergencyStopActive,
        PowerMode PowerMode,
        bool PositionInitialized,
        string CurrentMapId,
        string? OrderMapId,
        string OperatingMode);

    public static string? Evaluate(Input i)
    {
        var reasons = new List<string>();
        if (!i.HasPausedMission) return "일시정지 중인 임무 없음";
        if (i.EmergencyStopActive) reasons.Add("비상정지 활성 — 해제 후 재개");
        if (i.PowerMode != PowerMode.Normal) reasons.Add($"배터리 PowerMode={i.PowerMode} (NORMAL 아님)");
        if (!i.PositionInitialized) reasons.Add("위치 추정 비정상");
        if (!string.Equals(i.CurrentMapId, i.OrderMapId, StringComparison.Ordinal))
            reasons.Add($"mapId 불일치 (현재={i.CurrentMapId}, order={i.OrderMapId})");
        if (i.OperatingMode != "AUTOMATIC") reasons.Add($"operatingMode={i.OperatingMode} (AUTOMATIC 아님)");
        return reasons.Count == 0 ? null : string.Join("; ", reasons);
    }
}
