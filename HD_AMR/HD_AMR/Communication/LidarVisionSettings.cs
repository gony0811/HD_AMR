namespace HD_AMR.Communication;

/// <summary>
/// Jetson LiDAR 비전 서비스 접속 설정. <c>appsettings.json</c> 의 <c>"LidarVision"</c> 섹션에서
/// 바인딩된다. 다른 장치 설정 POCO(<see cref="LaserDisplacementSensorSettings"/> 등)와 동일하게
/// 플랫 구조 + 기본값을 갖는다.
///
/// 이 연결은 다른 장치들과 성격이 다르다. AMR/코봇/IO 는 상시 접속을 유지하지만 LiDAR 비전은
/// <b>정지 상태에서 요청/응답 1회</b>로 끝나므로, 상시 연결도 하트비트도 필요 없다. 그래서
/// 재접속 주기 대신 요청 재시도만 설정한다.
/// </summary>
public class LidarVisionSettings
{
    /// <summary>표시용 이름.</summary>
    public string Name { get; set; } = "LidarVision";

    /// <summary>
    /// Jetson LiDAR 서비스의 기준 URL. 서비스는 Kestrel 이 <c>0.0.0.0:8080</c> 에서 듣는다.
    /// </summary>
    public string BaseUrl { get; set; } = "http://192.168.0.30:8080";

    /// <summary>상태/헬스 같은 가벼운 요청의 HTTP 타임아웃(ms).</summary>
    public int RequestTimeoutMs { get; set; } = 3000;

    /// <summary>
    /// 평균에 사용할 프레임 수. 실측상 10프레임에서 단일 프레임 σ 8~12mm 가 4~7mm 로 떨어지고,
    /// 그 이상은 시간 상관 노이즈 때문에 이득이 거의 없다.
    /// </summary>
    public int MeasureFrames { get; set; } = 10;

    /// <summary>
    /// 측정 요청에 실어 보내는 <b>서버측</b> 타임아웃(ms). 젯슨이 프레임 수집을 포기하는 시점이다.
    /// </summary>
    public int MeasureTimeoutMs { get; set; } = 3000;

    /// <summary>
    /// 측정 요청의 HTTP 타임아웃에 얹는 여유(ms).
    ///
    /// HTTP 타임아웃이 서버측 타임아웃보다 짧으면, 젯슨이 정상적으로 끝냈을 측정을 클라이언트가
    /// 먼저 끊어버린다. 그러면 응답에 담겼을 실패 사유(포화 과다인지, 평면을 못 찾았는지)를
    /// 영영 못 보고 "타임아웃"이라는 정보량 없는 오류만 남는다. 실제 HTTP 타임아웃은
    /// <c>MeasureTimeoutMs + 이 값</c> 으로 잡는다.
    /// </summary>
    public int MeasureTimeoutMarginMs { get; set; } = 2000;

    /// <summary>
    /// 통신 실패 시 재시도 횟수(최초 시도 제외). 정지 측정이라 재시도가 안전하다 —
    /// 대상도 로봇도 움직이지 않으므로 다시 재는 것이 곧 같은 것을 재는 것이다.
    ///
    /// ⚠ 재시도 대상은 <b>통신 실패</b>뿐이다. 응답이 왔는데 <c>valid=false</c> 인 것은
    ///   정상적인 답이므로 재시도하지 않는다. 그걸 재시도하면 검출이 안 되는 상황을
    ///   조용히 반복 측정으로 덮게 된다.
    /// </summary>
    public int RetryCount { get; set; } = 2;

    /// <summary>재시도 간 대기(ms).</summary>
    public int RetryDelayMs { get; set; } = 300;

    // ── Hand-eye: 센서 → 툴(플랜지) 변환 ───────────────────────────────────────
    // 젯슨은 로봇 기구학을 전혀 모르고 센서 좌표계 값만 돌려준다. 로봇 좌표로 옮기는 것은
    // 전적으로 이쪽 몫이며, 그 변환이 여기 있다.
    //
    // ⚠ 센서 광학 좌표계는 통상적인 카메라 규약과 다르다 — NSL-1110AV 는 이미저가 본체 대비
    //   90° 회전 장착되어 있어 열 증가 = +X, 행 증가 = +Y, 광축 전방 = +Z 다. 이 90° 회전까지
    //   포함해 아래 회전값이 결정되어야 한다. "카메라니까 X가 오른쪽이겠지"로 값을 넣으면
    //   좌우와 상하가 통째로 뒤바뀐 채 그럴듯한 숫자가 나온다.

    /// <summary>
    /// hand-eye 캘리브레이션이 끝났는지.
    ///
    /// <b>false 이면 좌표 변환이 실패를 반환한다.</b> 기본값(단위변환)으로 조용히 계산하면
    /// 센서 좌표가 그대로 툴 좌표인 척하는 그럴듯한 숫자가 나오고, 그 숫자가 코봇을 움직인다.
    /// 캘리브레이션 전에는 실패하는 편이 안전하다.
    /// </summary>
    public bool HandEyeCalibrated { get; set; } = false;

    /// <summary>센서 원점의 툴 좌표 X(mm).</summary>
    public double MountOffsetXmm { get; set; } = 0.0;

    /// <summary>센서 원점의 툴 좌표 Y(mm).</summary>
    public double MountOffsetYmm { get; set; } = 0.0;

    /// <summary>센서 원점의 툴 좌표 Z(mm).</summary>
    public double MountOffsetZmm { get; set; } = 0.0;

    /// <summary>센서 자세 Rx(도). 규약은 <see cref="PoseMath"/> 와 동일한 ZYX RPY(고정축).</summary>
    public double MountRxDeg { get; set; } = 0.0;

    /// <summary>센서 자세 Ry(도).</summary>
    public double MountRyDeg { get; set; } = 0.0;

    /// <summary>센서 자세 Rz(도).</summary>
    public double MountRzDeg { get; set; } = 0.0;

    /// <summary>hand-eye 변환을 포즈 배열 [x,y,z,rx,ry,rz](mm + 도)로.</summary>
    public double[] ToMountPose() =>
    [
        MountOffsetXmm, MountOffsetYmm, MountOffsetZmm,
        MountRxDeg, MountRyDeg, MountRzDeg,
    ];

    /// <summary>측정 요청에 실제로 적용할 HTTP 타임아웃(ms).</summary>
    public int EffectiveMeasureHttpTimeoutMs =>
        Math.Max(RequestTimeoutMs, MeasureTimeoutMs + MeasureTimeoutMarginMs);
}
