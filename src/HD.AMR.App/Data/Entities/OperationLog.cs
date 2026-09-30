namespace HD.AMR.App.Data.Entities;

/// <summary>운영 로그 — 수동(UI)/ACS 발 동작(order·시퀀스·스텝)의 시작/성공/실패를 영속 기록.
/// UI 로그 페이지의 데이터 원본. 실패 항목은 Detail 에 원인 전문을 담는다.</summary>
public class OperationLog
{
    public long Id { get; set; }

    /// <summary>발생 시각(UTC). 표시 시 로컬 변환.</summary>
    public DateTime TimestampUtc { get; set; }

    /// <summary>동작 출처 — "UI"(수동) 또는 "ACS".</summary>
    public string Source { get; set; } = "";

    /// <summary>분류 — "ORDER"(VDA5050 order 수명주기) / "SEQUENCE"(시퀀스 실행) /
    /// "STEP"(개별 스텝) / "ACTION"(VDA5050 노드 액션).</summary>
    public string Category { get; set; } = "";

    /// <summary>대상 이름 — order 이벤트명, 스텝 키, 액션 타입 등.</summary>
    public string Name { get; set; } = "";

    /// <summary>true=성공, false=실패, null=정보성(시작/수신 등 결과 아님).</summary>
    public bool? Success { get; set; }

    /// <summary>설명 — 실패면 원인 전문.</summary>
    public string Detail { get; set; } = "";

    /// <summary>연관 식별자(VDA5050 orderId 등) — 수동 동작이면 null.</summary>
    public string? CorrelationId { get; set; }
}
