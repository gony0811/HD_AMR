namespace HD_AMR.Models;

/// <summary>
/// order 노드(nodeId) ↔ TARS-M 사전 티칭 Job/Task 인덱스 매핑 1행.
/// TARS-M은 좌표 goto가 없어 인덱스로 이동하므로, 어댑터가 order nodeId를 이 표로 조회해
/// Job Index(Holding 32) + 상태제어 시작(30=2)으로 실행한다.
/// 좌표(MapX/Y/ThetaRad)는 ACS 동기화값으로 참조·도착판정·수동티칭 대조용이다.
/// 사양: HD_ACS docs/VDA5050_NODE_INDEX_TRANSMISSION.md
/// </summary>
public sealed class AmrNodeMapping
{
    public string NodeId { get; set; } = "";
    public string MapId { get; set; } = "";
    public string Name { get; set; } = "";

    // ACS 동기화 참조값 (도면→맵 T_W_D 결과)
    public double MapX { get; set; }
    public double MapY { get; set; }
    public double ThetaRad { get; set; }

    // 편집 대상 — 수동 티칭 후 회수한 인덱스
    public int? JobIndex { get; set; }
    public int? TaskIndex { get; set; }
    public string GotoMode { get; set; } = "INDEX";

    /// <summary>마지막 수정 출처: "ACS"(동기화) | "LOCAL"(현장 편집).</summary>
    public string Source { get; set; } = "LOCAL";
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
