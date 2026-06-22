namespace HD_AMR.Communication.Vision;

/// <summary>
/// 비전 인터페이스(자동화↔비전 TCP 프로토콜) 설정. <c>appsettings.json</c> 의 <c>Vision</c>
/// 섹션에 매핑되어 IOptions 로 주입된다. AMR/Cobot/Camera Settings 와 동일 컨벤션.
/// 이 인터페이스는 테스트/수동 운용용이라 기동 시 자동 접속하지 않는다(연결은 UI 에서 수동).
/// </summary>
public class VisionInterfaceSettings
{
    public string Name { get; set; } = "Vision";

    // 자동화(Client) — 비전 서버로 접속할 대상.
    /// <summary>비전 S/W(서버) 호스트. 자동화 측이 접속할 IP.</summary>
    public string ServerHost { get; set; } = "127.0.0.1";

    /// <summary>비전 인터페이스 TCP 포트(클라이언트 접속 대상 포트).</summary>
    public int Port { get; set; } = 5000;

    /// <summary>Heartbeat 자동 송신 주기(ms). 사양 기본 5초.</summary>
    public int HeartbeatPeriodMs { get; set; } = 5000;

    /// <summary>클라이언트 자동 재연결 간격(ms). 사양 기본 3초.</summary>
    public int ReconnectDelayMs { get; set; } = 3000;

    /// <summary>클라이언트 자동 재연결 최대 횟수. 사양 기본 10회.</summary>
    public int MaxReconnectAttempts { get; set; } = 10;
}
