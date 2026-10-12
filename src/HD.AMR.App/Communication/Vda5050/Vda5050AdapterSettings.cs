namespace HD.AMR.App.Communication.Vda5050;

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
    public string Manufacturer { get; set; } = "HHI";
    public string SerialNumber { get; set; } = "AMR-01";

    // ── ACS 생존 신호 identity(§7.2 N12) — ACS 인스턴스당 1개, 로봇 토픽과 별개 ──
    public string AcsManufacturer { get; set; } = "HD_ACS";
    public string AcsSerialNumber { get; set; } = "hd-acs-master";

    // ── 발행 정책 ──
    /// <summary>state 주기 발행 간격(ms). 사양 2초. (변화 시 즉시 발행은 후속.)</summary>
    public int StatePeriodMs { get; set; } = 2000;

    /// <summary>재접속 재시도 간격(ms).</summary>
    public int ReconnectDelayMs { get; set; } = 5000;

    /// <summary>MQTT keep-alive(초). 통신 단절 사양 §2.2 — 5 s 명시(감지 지연 최대 7.5 s). 로봇·관제 동일.</summary>
    public int KeepAliveSec { get; set; } = 5;

    /// <summary>통신 단절 Fail-Safe(docs/HD_AMR_통신단절_FailSafe_사양.md §8).</summary>
    public CommLossSettings CommLoss { get; set; } = new();

    /// <summary>
    /// ACS "최근 활동" 판정 창(초). 마지막 order/instantActions 수신이 이 시간 이내면 활동 중으로 표시.
    /// VDA5050 에는 ACS 하트비트가 없어 경과해도 두절 단정은 불가 — UI 표시용.
    /// </summary>
    public int AcsActiveWindowSec { get; set; } = 60;

    // ── 상태 매핑 파라미터 ──
    /// <summary>현재 층 mapId(`{tank}-L{level}`). AMR이 맵ID를 미노출하므로 어댑터가 보유(수동/설정). </summary>
    public string MapId { get; set; } = "CT1-L1";

    /// <summary>수동 층 전환 UI(대시보드)의 선택지 목록. 비우면 현재 MapId 단일 항목만 표시.</summary>
    public string[] AvailableMapIds { get; set; } = ["CT1-L1", "CT1-L2", "CT1-L3", "CT1-L4"];

    /// <summary>측위 완료 판정 임계(맵 일치율 %). 이상이면 positionInitialized=true.</summary>
    public double LocalizedThresholdPercent { get; set; } = 80.0;
}

/// <summary>통신 단절 Fail-Safe 파라미터(사양 §8). appsettings 의 <c>Vda5050:CommLoss</c> 섹션.</summary>
public class CommLossSettings
{
    /// <summary>기능 사용. LAB 디버깅용으로만 해제 — 현장 운영은 항상 true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>T1 — 단절 판정(t0)부터 PAUSE 진입까지(초).</summary>
    public double PauseAfterSec { get; set; } = 30;

    /// <summary>T2 — 단절 판정(t0)부터 HOLD(부저 경보) 진입까지(초). T1 보다 커야 한다.</summary>
    public double HoldAfterSec { get; set; } = 60;
}
