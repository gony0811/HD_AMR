namespace HD_AMR.Contracts.Lidar;

/// <summary>
/// Jetson LiDAR 서비스의 HTTP 경로. 서버(Jetson)와 클라이언트(HD_AMR)가 같은 상수를
/// 참조하게 해서, 경로 오타로 런타임에야 드러나는 실수를 컴파일 시점에 막는다.
///
/// 제어 프로그램(HD_AMR)이 실제로 쓰는 것은 <see cref="Measure"/> 와 <see cref="Status"/>
/// 뿐이다. 정지 상태에서 요청/응답 1회로 끝나므로 WebSocket 이나 스트리밍이 필요 없다.
///
/// <c>preview/*</c> 계열은 Jetson 자체 모니터링 프론트엔드 전용이다. HD_AMR 은 호출하지
/// 않는다 — 영상은 제어에 쓰지 않기로 한 설계다.
/// </summary>
public static class LidarApiRoutes
{
    /// <summary>API 공통 접두사.</summary>
    public const string Base = "/api/lidar";

    /// <summary>POST — 1회 측정 실행. <see cref="MeasureRequest"/> → <see cref="MeasureResponse"/>.</summary>
    public const string Measure = Base + "/measure";

    /// <summary>GET — 서비스/센서 상태. → <see cref="LidarStatus"/>.</summary>
    public const string Status = Base + "/status";

    /// <summary>GET — 현재 센서 설정 전체. → <see cref="LidarConfig"/>.</summary>
    public const string Config = Base + "/config";

    /// <summary>PATCH — 센서 설정 부분 변경(휘발성). <see cref="LidarConfigPatch"/>.</summary>
    public const string ConfigPatch = Base + "/config";

    /// <summary>POST — 현재 설정을 장비에 영구 저장(nanolib <c>nsl_saveConfiguration</c>).</summary>
    public const string ConfigPersist = Base + "/config/persist";

    /// <summary>GET — 라이브니스 프로브. 서비스 프로세스가 살아 있는지만 확인한다.</summary>
    public const string Health = Base + "/health";

    // ── 아래는 Jetson 모니터링 프론트엔드 전용 ──────────────────────────────
    // 폴링으로 구성했다. WebSocket 을 쓰지 않는 이유는 이미지가 별도 HTTP 리소스라
    // 어차피 요청이 나가고, 그러면 소켓은 메타데이터 몇 줄만 나르면서 재연결 처리와
    // 백프레셔 관리를 떠안게 되기 때문이다. 관찰자는 브라우저 한둘뿐이라 이득이 없다.

    /// <summary>GET — 최신 미리보기 프레임의 메타데이터(픽셀 통계, 검출 결과, 렌더 스케일).</summary>
    public const string Preview = Base + "/preview";

    /// <summary>GET — 최신 프레임의 거리 의사색상 이미지(PNG).</summary>
    public const string PreviewDistance = Base + "/preview/distance.png";

    /// <summary>GET — 최신 프레임의 진폭(적외선) 이미지(PNG).</summary>
    public const string PreviewAmplitude = Base + "/preview/amplitude.png";

    /// <summary>GET — 검출 결과 오버레이(투명 배경 PNG). 두 평면 인라이어와 능선 띠.</summary>
    public const string PreviewOverlay = Base + "/preview/overlay.png";

    /// <summary>GET/PATCH — 미리보기 렌더링·주기 설정.</summary>
    public const string PreviewOptions = Base + "/preview/options";

    /// <summary>GET/PATCH — 능선 검출기 파라미터. 현장 튜닝용이며 휘발성이다.</summary>
    public const string Detector = Base + "/detector";
}
