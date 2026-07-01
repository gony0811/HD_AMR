namespace HD_AMR.Communication;

/// <summary>
/// 레이저 변위 센서(Laser Displacement Sensor) 통신 설정. EtherNet/IP 로 접속한다.
/// <c>appsettings.json</c> 의 <c>"LaserDisplacementSensor"</c> 섹션에서 바인딩되며,
/// 페이지에서 접속 전 IP/포트를 직접 편집할 수 있다. 다른 장치 설정 POCO(<see cref="ModbusTcpSettings"/>)
/// 와 동일하게 플랫 구조 + 기본값을 갖는다.
/// </summary>
public class LaserDisplacementSensorSettings
{
    /// <summary>표시용 이름.</summary>
    public string Name { get; set; } = "LaserDisplacementSensor";

    /// <summary>대상 장치 IP.</summary>
    public string IpAddress { get; set; } = "192.168.0.1";

    /// <summary>EtherNet/IP 표준 TCP 포트(0xAF12 = 44818).</summary>
    public int Port { get; set; } = 44818;

    /// <summary>기동 시 자동 접속 여부. false 면 사용자가 [연결] 버튼을 눌러야 접속을 시작한다.
    /// 자동 재접속 루프는 이 값(및 페이지의 연결/해제)이 켠 <c>_enabled</c> 상태일 때만 동작한다.</summary>
    public bool AutoConnect { get; set; } = false;

    /// <summary>세션 등록(RegisterSession)·ForwardOpen 타임아웃(ms).</summary>
    public int ConnectTimeoutMs { get; set; } = 3000;

    /// <summary>연결이 활성 상태일 때 재접속 시도 간격(ms).</summary>
    public int ReconnectDelayMs { get; set; } = 5000;

    /// <summary>태그 데이터 링크(Class1 implicit) 패킷 주기(RPI, ms). 장치 사양 1~10000ms, 기본 50ms.
    /// EEIP 의 <c>RequestedPacketRate</c>(µs) = <c>RpiMs * 1000</c> 로 양방향에 적용된다.</summary>
    public int RpiMs { get; set; } = 50;

    /// <summary>읽고 표시할 채널 수(CH1부터). ZP-LS300S 3점 측정이라 기본 3.</summary>
    public int ChannelCount { get; set; } = 3;

    /// <summary>ForwardOpen 의 Configuration Assembly 인스턴스 ID. 장치/EDS 에 따라 다를 수 있어
    /// 하드웨어에서 튜닝 대상. 기본 1.</summary>
    public int ConfigAssemblyInstanceId { get; set; } = 1;

    /// <summary>측정 원시값(32bit signed) → 물리값 스케일. <c>value = raw * MeasurementScale</c>.
    /// 스케일/단위는 이 통신 매뉴얼에 없고 앰프의 소수점 설정에 따르므로 하드웨어에서 튜닝한다.
    /// 기본 0.001 은 raw 가 µm 단위라고 가정(→ mm).</summary>
    public double MeasurementScale { get; set; } = 0.001;

    /// <summary>측정값 표시 단위 라벨.</summary>
    public string MeasurementUnit { get; set; } = "mm";

    /// <summary>페이지 실시간 갱신 주기(ms). SignalR 부하와 응답성의 절충값.</summary>
    public int UiRefreshMs { get; set; } = 200;
}
