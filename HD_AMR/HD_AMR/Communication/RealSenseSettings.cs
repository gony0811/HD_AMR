namespace HD_AMR.Communication;

/// <summary>
/// Intel RealSense D435/D435i (USB 3.0 RGB-D 카메라) 연결/스트림 설정. <c>appsettings.json</c> 의
/// <c>Camera</c> 섹션에 매핑되어 IOptions 로 주입된다. AMR/Cobot Settings 와 동일 컨벤션.
/// </summary>
public class RealSenseSettings
{
    public string Name { get; set; } = "D435";

    /// <summary>특정 디바이스 시리얼 번호. null/빈 문자열이면 첫 번째로 감지된 디바이스를 사용.</summary>
    public string? DeviceSerial { get; set; }

    // 컬러 스트림 (RGB8 → "rgb24")
    public int ColorWidth { get; set; } = 1280;
    public int ColorHeight { get; set; } = 720;
    public int ColorFps { get; set; } = 15;
    public bool EnableColor { get; set; } = true;

    // 깊이 스트림 (Z16 → "depth16"). D435 유효 모드: 1280x720/848x480/640x480/640x360/480x270/424x240.
    public int DepthWidth { get; set; } = 848;
    public int DepthHeight { get; set; } = 480;
    public int DepthFps { get; set; } = 15;
    public bool EnableDepth { get; set; } = true;

    // IR(적외선) 스트림. D435 좌측 이미저(Y8 그레이스케일, 깊이 센서와 동일 모드 권장). 활성화
    // 실패(프로파일 없음/대역폭 부족)는 치명적이지 않게 처리되어 컬러/깊이 스트림에는 영향을 주지 않는다.
    public int IrWidth { get; set; } = 848;
    public int IrHeight { get; set; } = 480;
    public int IrFps { get; set; } = 15;
    public bool EnableIr { get; set; } = true;

    /// <summary>깊이 컬러라이즈 LUT 의 가까운 끝(mm). 이 거리보다 가까운 값은 모두 따뜻한 색.</summary>
    public int DepthMinMm { get; set; } = 280;

    /// <summary>깊이 컬러라이즈 LUT 의 먼 끝(mm). 이 거리보다 먼 값은 모두 차가운 색.</summary>
    public int DepthMaxMm { get; set; } = 4000;

    /// <summary>MJPEG 송출 JPEG 품질 (1~100).</summary>
    public int JpegQuality { get; set; } = 75;

    /// <summary>연결 실패/끊김 시 재시도 간격(ms).</summary>
    public int ReconnectDelayMs { get; set; } = 5000;

    /// <summary>MJPEG 엔드포인트가 클라이언트로 송출하는 최대 FPS 상한.</summary>
    public int MjpegFps { get; set; } = 15;

    /// <summary><c>pipeline.TryWaitForFrames</c> 의 한 번 대기 한도(ms).</summary>
    public int FrameWaitTimeoutMs { get; set; } = 2000;

    /// <summary>
    /// 스트리밍 중 이 시간(ms) 동안 프레임이 한 장도 안 들어오면 워치독이 강제로 연결을 끊어
    /// 상위 재연결 루프가 파이프라인을 새 장치 핸들로 재구성하게 한다. USB 재열거(장치가 버스에서
    /// 잠깐 빠졌다 돌아옴) 후 <c>TryWaitForFrames</c> 가 예외 없이 타임아웃만 반복하는 상황을 복구.
    /// </summary>
    public int FrameStarvationReconnectMs { get; set; } = 4000;

    /// <summary>
    /// IR 프로젝터(도트 패턴 이미터) on/off. 켜면 깊이 품질이 좋아지지만 IR 영상에 도트 패턴이
    /// 찍히고, 끄면 IR 영상은 깨끗하지만 무늬 없는 표면의 깊이 품질이 떨어진다.
    /// </summary>
    public bool EnableEmitter { get; set; } = true;
}
