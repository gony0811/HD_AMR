namespace HD_AMR.Communication.Vda5050;

/// <summary>
/// HD_AMR VDA 5050 어댑터 설정. appsettings.json 의 <c>Vda5050</c> 섹션에 매핑.
/// 어댑터는 ACS(마스터)에 대해 AGV 에이전트로 동작한다: connection/state 발행, order/instantActions 수신.
/// </summary>
public class Vda5050AdapterSettings
{
    /// <summary>어댑터 활성화. false 면 호스티드 서비스가 유휴(브로커 미접속).</summary>
    public bool Enabled { get; set; } = false;

    // ── MQTT 브로커 ──
    public string BrokerHost { get; set; } = "127.0.0.1";
    public int BrokerPort { get; set; } = 1883;
    public string? Username { get; set; }
    public string? Password { get; set; }

    // ── 토픽 요소(ACS와 일치해야 함) ──
    public string TopicPrefix { get; set; } = "uagv";
    public string TopicVersion { get; set; } = "v2";
    public string Manufacturer { get; set; } = "adentrobot";
    public string SerialNumber { get; set; } = "TARS-M-01";

    // ── 발행 정책 ──
    /// <summary>state 주기 발행 간격(ms). 사양 2초. (변화 시 즉시 발행은 후속.)</summary>
    public int StatePeriodMs { get; set; } = 2000;

    /// <summary>재접속 재시도 간격(ms).</summary>
    public int ReconnectDelayMs { get; set; } = 5000;

    // ── 상태 매핑 파라미터 ──
    /// <summary>현재 층 mapId(`{tank}-L{level}`). AMR이 맵ID를 미노출하므로 어댑터가 보유(수동/설정). </summary>
    public string MapId { get; set; } = "CT1-L1";

    /// <summary>측위 완료 판정 임계(맵 일치율 %). 이상이면 positionInitialized=true.</summary>
    public double LocalizedThresholdPercent { get; set; } = 80.0;
}
