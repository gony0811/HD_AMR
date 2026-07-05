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

    /// <summary>측정값(32bit signed)이 시작되는 Input Assembly 바이트 오프셋. 채널 N(1-based)의 값은
    /// <c>MeasurementByteOffset + (N-1) * MeasurementStride</c>. 매뉴얼 Z496 기준 CH1=48이지만
    /// 실제 프레임(런/아이들 헤더 유무 등)에 따라 밀릴 수 있어 실장비에서 튜닝 가능하도록 설정으로 노출.</summary>
    public int MeasurementByteOffset { get; set; } = 48;

    /// <summary>채널당 측정값 바이트 수(int32 → 4). 오프셋 계산 stride.</summary>
    public int MeasurementStride { get; set; } = 4;

    /// <summary>측정 원시값(32bit signed) → 물리값 스케일. <c>value = raw * MeasurementScale</c>.
    /// 스케일/단위는 이 통신 매뉴얼에 없고 앰프의 소수점 설정에 따르므로 하드웨어에서 튜닝한다.
    /// 기본 0.001 은 raw 가 µm 단위라고 가정(→ mm).</summary>
    public double MeasurementScale { get; set; } = 0.001;

    /// <summary>측정값 표시 단위 라벨.</summary>
    public string MeasurementUnit { get; set; } = "mm";

    // ── Input Assembly(110) 상태 비트 오프셋 ──────────────────────────────────
    // 채널 N(1-based)의 비트 = byte(offset + (N-1)/8) 의 bit((N-1)%8). 매뉴얼 Z496 기준값이나
    // PDF 표 추출이 모호해 실장비 hex 로 확정 가능하도록 설정으로 노출한다.

    /// <summary>Sensor Error Status 비트 오프셋. 기본 2.</summary>
    public int SensorErrorByteOffset { get; set; } = 2;

    /// <summary>Sensor Warning Status 비트 오프셋. 기본 4.</summary>
    public int SensorWarningByteOffset { get; set; } = 4;

    /// <summary>Sensor Enable(측정범위 내/유효) 비트 오프셋. 매뉴얼 재구성상 byte 10(byte 8-9는 Reserved).
    /// 기본 10. 실장비 hex 에서 측정 중 ON 되는 byte 로 확정.</summary>
    public int SensorEnableByteOffset { get; set; } = 10;

    /// <summary>판정 HIGH(Sensor Output 1) 비트 오프셋. 기본 18(라이브 확인 필요).</summary>
    public int OutputHighByteOffset { get; set; } = 18;

    /// <summary>판정 LOW(Sensor Output 2) 비트 오프셋. 기본 20(라이브 확인 필요).</summary>
    public int OutputLowByteOffset { get; set; } = 20;

    /// <summary>판정 PASS(Sensor Output 3) 비트 오프셋. 기본 22(라이브 확인 필요).</summary>
    public int OutputPassByteOffset { get; set; } = 22;

    /// <summary>페이지 실시간 갱신 주기(ms). SignalR 부하와 응답성의 절충값.</summary>
    public int UiRefreshMs { get; set; } = 200;

    // ── 평면 기울기 계산용 헤드 기하 (cobot base 좌표계) ────────────────────────
    // 3개 레이저 헤드의 base XY 위치(mm). 규약: Ch1→Ch3 = base +X, Ch2(상단) = +Y쪽,
    // 빔 = base −Z. 기본값은 등변삼각형(한 변 100mm) placeholder — TODO: 실장비에서 확정.

    /// <summary>Ch1(좌측 꼭지점) 헤드 X(mm, base). 기본 −50.</summary>
    public double Head1OffsetXmm { get; set; } = -50.0;

    /// <summary>Ch1(좌측 꼭지점) 헤드 Y(mm, base). 기본 0.</summary>
    public double Head1OffsetYmm { get; set; } = 0.0;

    /// <summary>Ch2(상단 꼭지점) 헤드 X(mm, base). 기본 0.</summary>
    public double Head2OffsetXmm { get; set; } = 0.0;

    /// <summary>Ch2(상단 꼭지점) 헤드 Y(mm, base). 기본 86.6(등변삼각형 높이).</summary>
    public double Head2OffsetYmm { get; set; } = 86.6;

    /// <summary>Ch3(우측 꼭지점) 헤드 X(mm, base). 기본 50.</summary>
    public double Head3OffsetXmm { get; set; } = 50.0;

    /// <summary>Ch3(우측 꼭지점) 헤드 Y(mm, base). 기본 0.</summary>
    public double Head3OffsetYmm { get; set; } = 0.0;

    /// <summary>변위 +가 base +Z(위, 센서쪽)에 대응하는지. 빔=−Z + 가까워짐=+ 규약이면 true.
    /// 실장비 극성이 반대로 나오면 false 로 뒤집는다(통신 매뉴얼에 극성 미확정).</summary>
    public bool TiltReadingSignForUp { get; set; } = true;

    // ── 삼각형 평면 중심 pose(툴 좌표계) 계산용 ──────────────────────────────
    // 헤드 툴-XY 는 위 Head{1,2,3}Offset{X,Y}mm 를 툴 좌표로 재해석, 빔 = 툴 +Z 평행 가정.

    /// <summary>공칭 standoff(mm) — 헤드 평면(툴 Z=0)에서 기준면까지의 툴 Z 거리. 중심 pose 의 z =
    /// Standoff + 변위평균. 기본 100 placeholder — TODO 실장비 확정.</summary>
    public double TiltStandoffMm { get; set; } = 100.0;

    /// <summary>평면 법선(Z축)을 센서쪽(표면 외향, 툴 −Z측)으로 향하게 할지. true 면 수평 기준 rx≈±180°.
    /// false 면 표면쪽(툴 접근방향)으로 뒤집혀 rx≈0. 기본 true.</summary>
    public bool TiltNormalTowardSensor { get; set; } = true;
}
