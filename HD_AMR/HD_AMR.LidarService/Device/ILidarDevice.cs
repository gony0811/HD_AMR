using HD_AMR.Contracts.Lidar;

namespace HD_AMR.LidarService.Device;

/// <summary>
/// 센서 접근 추상화.
///
/// 이 인터페이스가 존재하는 이유는 <b>하드웨어 없이 개발하기 위해서</b>다. 실제 구현
/// (<see cref="NslLidarDevice"/>)은 젯슨에서만 동작하지만, 덤프 재생 구현으로 바꿔 끼우면
/// 능선 검출 알고리즘과 API 를 Windows 에서 그대로 개발·디버깅할 수 있다.
///
/// ⚠ 모든 메서드는 <b>동기</b>이고 <b>단일 스레드에서만</b> 호출해야 한다. nanolib 의
///   스레드 안전성이 아직 확인되지 않았으므로(진단 보고서 5-4), 네이티브 호출을 전용
///   스레드 하나에 가두는 보수적 설계를 택했다. 스레드 안전이 확인되면 완화할 수 있다.
/// </summary>
internal interface ILidarDevice : IDisposable
{
    bool IsOpen { get; }

    /// <summary>연결 후 읽어낸 장비 정보. <see cref="Open"/> 전에는 null.</summary>
    LidarDeviceInfo? Info { get; }

    /// <summary>센서를 열고 장비 정보를 읽는다.</summary>
    /// <exception cref="LidarDeviceException">연결 실패 시.</exception>
    void Open();

    void Close();

    /// <summary>스트리밍 시작. 정지 측정이라도 프레임을 받으려면 켜야 한다.</summary>
    void StartStreaming();

    void StopStreaming();

    /// <summary>
    /// 한 프레임 수신. 타임아웃이면 null 을 반환한다(예외 아님 — 일시적 손실은 정상 상황이라
    /// 호출 측이 재시도로 흡수한다).
    /// </summary>
    LidarCapture? Capture(int timeoutMs);

    /// <summary>현재 센서 설정을 읽는다.</summary>
    LidarConfig ReadConfig();

    /// <summary>센서 설정을 부분 변경한다(휘발성). 영구 저장은 <see cref="PersistConfig"/>.</summary>
    void ApplyConfig(LidarConfigPatch patch);

    /// <summary>
    /// 현재 설정을 장비 EEPROM 에 영구 기록한다.
    /// ⚠ 플래시 쓰기 수명이 있으므로 명시적 요청에서만 호출할 것.
    /// </summary>
    void PersistConfig();

    /// <summary>전압/전류/온도 등 상태 값을 읽는다.</summary>
    LidarStatus ReadStatus();
}

/// <summary>연결 시점에 확정되는 장비 정보. 로그와 상태 보고에 쓴다.</summary>
internal sealed record LidarDeviceInfo
{
    public required string Model { get; init; }
    public required NslNative.LidarTypeOption LidarType { get; init; }
    public required LidarLensType LensType { get; init; }
    public required int FirmwareRelease { get; init; }
    public required int ChipId { get; init; }
    public required string SdkVersion { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
}

/// <summary>센서 연결/통신 실패. 일시적 프레임 손실은 이 예외가 아니라 null 반환으로 표현한다.</summary>
internal sealed class LidarDeviceException : Exception
{
    public NslNative.NslError? ErrorCode { get; }

    public LidarDeviceException(string message, NslNative.NslError? code = null) : base(
        code is null ? message : $"{message} (nanolib: {code} = {(int)code})")
    {
        ErrorCode = code;
    }
}
