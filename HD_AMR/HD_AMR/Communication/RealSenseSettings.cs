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

    /// <summary>
    /// D400 Visual Preset. ""/null 이면 펌웨어 기본 유지. 유효값: Custom/Default/Hand/
    /// HighAccuracy/HighDensity/MediumDensity. HighAccuracy 는 센서 ASIC 신뢰도 임계값을 올려
    /// 정반사 등으로 불확실한 픽셀을 쓰레기값 대신 0(무효)으로 출력한다 — 반사면 측정 권장.
    /// 프리셋은 EmitterEnabled/LaserPower 를 덮어쓰므로 다른 옵션보다 먼저 적용된다.
    /// </summary>
    public string? VisualPreset { get; set; } = "HighAccuracy";

    /// <summary>
    /// 레이저(도트 프로젝터) 파워. -1 = 프리셋/펌웨어 값 유지. D435 범위 0~360 (step 30).
    /// 반사가 심하면 낮춰 포화/멀티패스를 완화하고, 어두운 무광면은 올려 SNR 을 개선한다.
    /// </summary>
    public float LaserPower { get; set; } = -1f;

    /// <summary>깊이 후처리(홀 복원/스무딩) 필터 체인 설정. <see cref="DepthFilterSettings"/> 참고.</summary>
    public DepthFilterSettings DepthFilters { get; set; } = new();
}

/// <summary>
/// librealsense 내장 깊이 후처리 필터 설정. 금속 정반사 등으로 depth 가 0(무효)으로 뚫린 홀을
/// 주변 픽셀/이전 프레임으로 복원한다. 체인 순서(librealsense 권장):
/// (decimation) → depth→disparity → spatial → temporal → disparity→depth → (hole-filling).
/// 기본값은 보수적 — 작은 정반사 홀만 국소 복원하고, 큰 홀은 0 으로 유지해 측정 로직이
/// 계속 걸러내게 한다(용접 비드 측정 왜곡 방지).
/// </summary>
public class DepthFilterSettings
{
    /// <summary>마스터 토글. false 면 필터 체인을 아예 생성하지 않는다(원시 깊이 그대로).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>DecimationFilter. 해상도와 intrinsics 가 바뀌어 D2C 정합(CameraD2CParams)이
    /// 깨지므로 기본 OFF. 진단/실험 전용.</summary>
    public bool UseDecimation { get; set; } = false;

    /// <summary>Decimation 축소 배율 (2~8).</summary>
    public int DecimationMagnitude { get; set; } = 2;

    /// <summary>ThresholdFilter — 작업 거리 창 밖의 깊이를 0(무효)으로 클램프. 정반사가 만드는
    /// 비정상 원/근거리 스파이크(유령값) 제거의 1차 수단. Spatial 이전(depth 도메인)에 적용해
    /// 스파이크가 스무딩으로 이웃에 번지기 전에 제거한다.</summary>
    public bool UseThreshold { get; set; } = true;

    /// <summary>유효 최소 거리(mm). D435 848x480 의 물리적 min-z ≈ 195mm — 이보다 가까운 값은
    /// 반사 아티팩트일 수밖에 없다.</summary>
    public float ThresholdMinMm { get; set; } = 150f;

    /// <summary>유효 최대 거리(mm). 용접 작업거리 ~500mm 기준 3배 여유. 측정 로직은 전부
    /// 근거리에서 동작하므로 원거리 배경/유령값은 과감히 제거(0 은 소비자가 스킵).</summary>
    public float ThresholdMaxMm { get; set; } = 1500f;

    /// <summary>Spatial/Temporal 을 disparity 도메인에서 수행(librealsense 권장 — 거리에 따른
    /// 오차 특성이 균일해짐). false 면 depth 도메인에서 직접 수행.</summary>
    public bool UseDisparityDomain { get; set; } = true;

    /// <summary>SpatialFilter(에지 보존 스무딩 + 국소 홀 필링) 사용 여부.</summary>
    public bool UseSpatial { get; set; } = true;

    /// <summary>Spatial 반복 횟수 (1~5).</summary>
    public int SpatialMagnitude { get; set; } = 2;

    /// <summary>Spatial 지수 이동평균 알파 (0.25~1). 낮을수록 강한 스무딩.</summary>
    public float SpatialSmoothAlpha { get; set; } = 0.5f;

    /// <summary>Spatial 에지 보존 임계값 (1~50, disparity 단위). 이보다 큰 단차는 에지로 보고 보존.</summary>
    public float SpatialSmoothDelta { get; set; } = 20f;

    /// <summary>Spatial 홀 필링 반경: 0=끔, 1=2px, 2=4px, 3=8px, 4=16px, 5=무제한.
    /// 국소 반경 기반이라 가장 보수적인 홀 복원 — 정반사 스펙클 홀 복원의 1차 수단.</summary>
    public int SpatialHolesFill { get; set; } = 2;

    /// <summary>TemporalFilter(프레임 간 시간 스무딩 + 간헐 드롭 픽셀 유지) 사용 여부.</summary>
    public bool UseTemporal { get; set; } = true;

    /// <summary>Temporal 지수 이동평균 알파 (0~1). 낮을수록 과거 프레임 가중이 큼.</summary>
    public float TemporalSmoothAlpha { get; set; } = 0.4f;

    /// <summary>Temporal 에지 보존 임계값 (1~100).</summary>
    public float TemporalSmoothDelta { get; set; } = 20f;

    /// <summary>persistency index 0~8 (Option.HolesFill 로 설정). 3 = 최근 4프레임 중 2회
    /// 유효했던 픽셀은 이번 프레임에서 뚫려도 직전 값으로 유지.</summary>
    public int TemporalPersistence { get; set; } = 3;

    /// <summary>HoleFillingFilter — 남은 모든 홀을 이웃값으로 채운다. 측정되지 않은 기하를
    /// '지어내는' 필터라 용접 측정에는 위험. 기본 OFF(화면용으로 완전한 맵이 필요할 때만).</summary>
    public bool UseHoleFilling { get; set; } = false;

    /// <summary>0=fill_from_left, 1=farest_from_around, 2=nearest_from_around. 켤 경우 2 권장:
    /// 비드 정점의 정반사 홀에 farest(1)를 쓰면 주변 모재 깊이로 채워져 비드가 파인 것처럼 왜곡된다.</summary>
    public int HoleFillingMode { get; set; } = 2;
}
