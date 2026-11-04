using System.IO.Ports;

namespace HD.AMR.App.Communication.GasDetector;

/// <summary>가스 디텍터 통신 경로.</summary>
public enum GasDetectorTransportKind
{
    /// <summary>시리얼(RS485/RS232) — USB 변환기 등 COM 포트.</summary>
    Serial,
    /// <summary>이더넷(TCP) — 시리얼-이더넷 디바이스 서버의 TCP 투명 모드(Raw socket).</summary>
    Tcp,
}

/// <summary>
/// 헬륨 가스 디텍터(INFICON XL3000flex) 설정. <c>appsettings.json</c> 의 <c>GasDetector</c> 섹션에 바인딩한다.
/// 통신 경로·포트·판정 기준은 화면(SETTINGS > Gas Detector)에서 바꿀 수 있고, 바꾼 값은 DB 파라미터
/// (<c>GasDetector.*</c>)로 저장되어 이 기본값보다 우선한다.
/// 프로토콜은 두 경로 모두 ASCII(사양 기본)로 같고, 전송 계층만 다르다.
/// </summary>
public class GasDetectorSettings
{
    public string Name { get; set; } = "GasDetector";

    /// <summary>표시용 모델명.</summary>
    public string Model { get; set; } = "XL3000flex";

    public GasDetectorTransportKind Transport { get; set; } = GasDetectorTransportKind.Serial;

    // ── 시리얼(RS485) ──
    public string PortName { get; set; } = "COM5";
    public int BaudRate { get; set; } = Xl3000AsciiProtocol.DefaultBaudRate;
    public Parity Parity { get; set; } = Parity.None;
    public int DataBits { get; set; } = 8;
    public StopBits StopBits { get; set; } = StopBits.One;

    // ── 이더넷(TCP) ──
    public string Host { get; set; } = "192.168.0.50";
    public int TcpPort { get; set; } = 4001;
    public int ConnectTimeoutMs { get; set; } = 3000;

    // ── 타이밍 ──
    /// <summary>명령 1건 응답 대기(ms). 사양 권장 1500.</summary>
    public int ResponseTimeoutMs { get; set; } = Xl3000AsciiProtocol.RecommendedTimeoutMs;

    /// <summary>실시간 값 폴링 주기(ms). 사양 권장 100 이상.</summary>
    public int PollIntervalMs { get; set; } = 500;

    /// <summary>연결 실패 시 재시도 간격(ms).</summary>
    public int ReconnectDelayMs { get; set; } = 5000;

    /// <summary>연속 무응답이 이 횟수에 이르면 포트를 닫고 다시 연다.</summary>
    public int MaxConsecutiveTimeouts { get; set; } = 3;

    // ── 경유점 측정·판정 ──
    /// <summary>
    /// 리크 판정 기준 [mbar·l/s] — 측정 최대값이 이 값 이상이면 NG. 장비 단위 설정과 무관하게 mbar·l/s 로 읽어 비교한다.
    /// 기본값은 임시값이다 — 현장 합격 기준으로 정해야 한다.
    /// </summary>
    public double LeakRateLimitMbarLs { get; set; } = 1.0E-5;

    /// <summary>경유점 도착 후 첫 샘플까지 대기(ms) — 스니퍼 응답 지연(가스가 프로브까지 오는 시간) 흡수.</summary>
    public int MeasureDwellMs { get; set; } = 2000;

    /// <summary>경유점 1곳에서 읽는 샘플 수.</summary>
    public int MeasureSampleCount { get; set; } = 5;

    /// <summary>샘플 간격(ms).</summary>
    public int MeasureSampleIntervalMs { get; set; } = 200;

    public GasDetectorSettings Clone() => (GasDetectorSettings)MemberwiseClone();

    /// <summary>화면·로그용 연결 대상 요약.</summary>
    public string EndpointText => Transport == GasDetectorTransportKind.Serial
        ? $"RS485 {PortName} @ {BaudRate} {DataBits}{Parity.ToString()[0]}{(StopBits == StopBits.Two ? 2 : 1)}"
        : $"TCP {Host}:{TcpPort}";
}
