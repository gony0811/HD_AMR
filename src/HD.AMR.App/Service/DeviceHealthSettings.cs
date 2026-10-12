namespace HD.AMR.App.Service;

/// <summary>
/// 장치 통신 HEAVY 알람 설정 — appsettings.json 의 <c>DeviceHealth</c> 섹션.
/// 장치가 끊기거나 상태 확인이 안 되면 HEAVY 알람(ACS FATAL) + order 수신 차단, RESET 으로만 해제.
/// </summary>
public class DeviceHealthSettings
{
    /// <summary>감시 사용. 현장 운영은 항상 true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>기동 직후 알람 유예(초) — 장치 최초 접속·첫 프레임 대기.</summary>
    public int StartupGraceSec { get; set; } = 30;

    /// <summary>비정상이 이 시간(초) 이상 이어져야 알람.</summary>
    public int LostAfterSec { get; set; } = 3;

    /// <summary>장치별 감시 대상 여부 — 미설치 장치는 false.</summary>
    public DeviceHealthTargets Devices { get; set; } = new();

    /// <summary>AMR Modbus 상태 읽기가 이 시간(초)보다 오래되면 비정상.</summary>
    public double AmrStaleSec { get; set; } = 3;

    /// <summary>코봇 상태 패킷(20004)이 이 시간(초)보다 오래되면 비정상.</summary>
    public double CobotStaleSec { get; set; } = 2;

    /// <summary>I/O 모듈 입력 읽기 성공이 이 시간(초)보다 오래되면 비정상.</summary>
    public double IoStaleSec { get; set; } = 2;

    /// <summary>Depth 카메라 마지막 프레임이 이 시간(초)보다 오래되면 비정상.</summary>
    public double DepthCameraStaleSec { get; set; } = 2;

    /// <summary>비전 서버 수신(HEARTBEAT 5초 주기)이 이 시간(초)보다 오래되면 비정상. 0 이면 TCP 연결 여부만 본다.</summary>
    public double VisionRxStaleSec { get; set; } = 15;

    /// <summary>레이저 생존 확인 성공이 이 시간(초)보다 오래되면 비정상.</summary>
    public double LaserStaleSec { get; set; } = 6;
}

public class DeviceHealthTargets
{
    public bool Vision { get; set; } = true;
    public bool DepthCamera { get; set; } = true;
    public bool Laser { get; set; } = true;
    public bool IoModule { get; set; } = true;
    public bool Cobot { get; set; } = true;
    public bool Amr { get; set; } = true;

    public IEnumerable<DeviceKind> EnabledKinds()
    {
        if (Vision) yield return DeviceKind.Vision;
        if (DepthCamera) yield return DeviceKind.DepthCamera;
        if (Laser) yield return DeviceKind.Laser;
        if (IoModule) yield return DeviceKind.IoModule;
        if (Cobot) yield return DeviceKind.Cobot;
        if (Amr) yield return DeviceKind.Amr;
    }
}
