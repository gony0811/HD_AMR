namespace HD_AMR.Communication;

/// <summary>
/// Orbbec Gemini 2 (USB 3.0 RGB-D 카메라) 연결/스트림 설정. <c>appsettings.json</c> 의
/// <c>Camera</c> 섹션에 매핑되어 IOptions 로 주입된다. AMR/Cobot Settings 와 동일 컨벤션.
/// </summary>
public class OrbbecGeminiSettings
{
    public string Name { get; set; } = "Camera";

    /// <summary>특정 디바이스 시리얼 번호. null/빈 문자열이면 첫 번째로 감지된 디바이스를 사용.</summary>
    public string? DeviceSerial { get; set; }

    // 컬러 스트림
    public int ColorWidth { get; set; } = 1280;
    public int ColorHeight { get; set; } = 720;
    public int ColorFps { get; set; } = 30;
    public bool EnableColor { get; set; } = true;
    public bool MirrorColor { get; set; } = false;

    // 깊이 스트림
    public int DepthWidth { get; set; } = 1280;
    public int DepthHeight { get; set; } = 720;
    public int DepthFps { get; set; } = 30;
    public bool EnableDepth { get; set; } = true;

    /// <summary>깊이 컬러라이즈 LUT 의 가까운 끝(mm). 이 거리보다 가까운 값은 모두 따뜻한 색.</summary>
    public int DepthMinMm { get; set; } = 200;

    /// <summary>깊이 컬러라이즈 LUT 의 먼 끝(mm). 이 거리보다 먼 값은 모두 차가운 색.</summary>
    public int DepthMaxMm { get; set; } = 4000;

    /// <summary>MJPEG 송출 JPEG 품질 (1~100).</summary>
    public int JpegQuality { get; set; } = 75;

    /// <summary>연결 실패/끊김 시 재시도 간격(ms).</summary>
    public int ReconnectDelayMs { get; set; } = 5000;

    /// <summary>MJPEG 엔드포인트가 클라이언트로 송출하는 최대 FPS 상한.</summary>
    public int MjpegFps { get; set; } = 15;

    /// <summary><c>pipeline.WaitForFrameset</c> 의 한 번 대기 한도(ms).</summary>
    public int FrameWaitTimeoutMs { get; set; } = 1000;

    /// <summary>
    /// 스트리밍 중 이 시간(ms) 동안 프레임이 한 장도 안 들어오면 워치독이 강제로 연결을 끊어
    /// 상위 재연결 루프가 파이프라인을 새 장치 핸들로 재구성하게 한다. USB 재열거(장치가 버스에서
    /// 잠깐 빠졌다 돌아옴) 후 <c>wait_for_frameset</c> 가 예외 없이 타임아웃만 반복하는 상황을 복구.
    /// </summary>
    public int FrameStarvationReconnectMs { get; set; } = 4000;

    /// <summary>
    /// 깊이 노이즈 제거 필터(<c>OB_PROP_DEPTH_NOISE_REMOVAL_FILTER</c>)를 끈다. 가까이서 빠르게
    /// 움직이는 물체로 깊이 노이즈가 폭증하면 이 CPU 소프트웨어 필터의 큐가 포화되어
    /// (<c>Source frameset queue fulled</c>) 프레임이 백업 → UVC 버퍼 오버플로 → 장치 재열거로
    /// 영상이 멈추는 원인이 된다. 기본 끔.
    /// </summary>
    public bool DisableDepthNoiseRemoval { get; set; } = true;

    /// <summary>깊이 soft/speckle 필터(<c>OB_PROP_DEPTH_SOFT_FILTER</c>)를 끈다. 위와 동일 취지. 기본 끔.</summary>
    public bool DisableDepthSoftFilter { get; set; } = true;

    /// <summary>
    /// 장치측 프레임 스킵 모드(<c>OB_PROP_SKIP_FRAME</c>)를 켠다. 백로그 발생 시 호스트 버퍼가
    /// 넘치기 전에 장치가 프레임을 드롭하게 한다. 기본 끔(옵션).
    /// </summary>
    public bool EnableDeviceFrameSkip { get; set; } = false;
}
