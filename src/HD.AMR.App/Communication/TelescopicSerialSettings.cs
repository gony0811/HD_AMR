namespace HD.AMR.App.Communication;

/// <summary>
/// Z축 텔레스코픽(EBLUM 3채널 홀 동기 컨트롤러) RS232/TTL 설정.
/// <c>appsettings.json</c> 의 <c>"Telescopic"</c> 섹션으로 바인딩한다.
///
/// 컨트롤러 포트는 <b>TTL 레벨</b>이므로 PC 와 연결하려면 TTL↔RS232(또는 TTL↔USB) 변환 모듈이 필요하다.
/// RJ45 8핀 중 3=RX1, 4=TX1 을 외부 기기의 TX/RX 와 <b>교차</b> 결선하고 8=GND 를 공통으로 잇는다.
/// </summary>
public class TelescopicSerialSettings
{
    /// <summary>표시용 이름.</summary>
    public string Name { get; set; } = "Telescopic";

    /// <summary>시리얼 포트 이름. Windows 는 <c>COM4</c> 형태, macOS/Linux 는 <c>/dev/tty.*</c>.</summary>
    public string PortName { get; set; } = "COM4";

    /// <summary>보율 — 사양서 고정값 9600. 변경할 이유가 없지만 현장 변종 대비로 열어 둔다.</summary>
    public int BaudRate { get; set; } = TelescopicProtocol.BaudRate;

    /// <summary>읽기 타임아웃(ms). 문답식이므로 한 줄 응답을 이 시간 안에 받아야 한다.</summary>
    public int ReadTimeoutMs { get; set; } = 1000;

    /// <summary>쓰기 타임아웃(ms).</summary>
    public int WriteTimeoutMs { get; set; } = 1000;

    /// <summary>상태 폴링 주기(ms). 컨트롤러가 자동 통보하지 않으므로 필수.</summary>
    public int PollIntervalMs { get; set; } = 500;

    /// <summary>연결 실패 시 재시도 간격(ms).</summary>
    public int ReconnectDelayMs { get; set; } = 5000;

    /// <summary>
    /// <c>Target:</c> 높이 데이터 자릿수. 사양서는 3바이트(0~999mm)로 규정하는데 응답 높이는 4바이트라
    /// 비대칭이다. 행정이 1000mm 이면 3자리로는 최상단을 지령할 수 없으므로 벤더 확인이 필요하다
    /// (<c>docs/TELESCOPIC_LIFT.md</c> 미확정 사항).
    /// </summary>
    public int TargetDigits { get; set; } = 3;

    /// <summary>운영 하한(mm) — 지정 높이 이동 시 이 값 밖이면 명령을 보내지 않는다.</summary>
    public int MinHeightMm { get; set; }

    /// <summary>운영 상한(mm). 기구 행정(약 1000mm)보다 보수적으로 두는 것이 안전하다.</summary>
    public int MaxHeightMm { get; set; } = 999;

    /// <summary>
    /// 조그(상승/하강) 유지 명령 재전송 주기(ms). 사양서 FAQ 1 에 상승 명령 반복 전송은 무해하다고 명시.
    /// </summary>
    public int JogKeepAliveMs { get; set; } = 200;

    /// <summary>
    /// 조그 데드맨 타임아웃(ms) — UI 가 유지 신호를 끊으면 이 시간 안에 자동 정지한다.
    /// 화면이 멈추거나 페이지가 닫혀도 1m 리프트가 계속 올라가지 않게 하는 안전장치.
    /// </summary>
    public int JogDeadmanMs { get; set; } = 700;

    /// <summary>조그 1회 최대 연속 시간(ms) — 버튼이 눌린 채 방치되는 상황 대비 상한.</summary>
    public int JogMaxDurationMs { get; set; } = 30_000;
}
