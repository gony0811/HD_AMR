namespace HD.AMR.App.Service;

/// <summary>검사 센서 종류 — 경유점마다 무엇으로 검사하는지.</summary>
public enum InspectionSensorMode
{
    /// <summary>비전 S/W 에 CAPTURE_REQ(TCP 바이너리 프로토콜) — 기존 AMR.</summary>
    Vision,
    /// <summary>헬륨 가스 디텍터(XL3000flex) 리크레이트 측정 — 가스 검출 AMR.</summary>
    GasDetector,
}

/// <summary>
/// 검사 센서 선택 — <c>appsettings.json</c> 의 <c>InspectionSensor</c> 섹션. 한 값으로
/// 검사 시퀀스의 측정 방식, 기동하는 통신 서비스(비전/가스 디텍터), 메뉴·상단 상태 배지, 장치 HEAVY 알람 대상이 함께 바뀐다.
/// </summary>
public class InspectionSensorSettings
{
    public InspectionSensorMode Mode { get; set; } = InspectionSensorMode.Vision;

    public bool UsesGasDetector => Mode == InspectionSensorMode.GasDetector;
    public bool UsesVision => Mode == InspectionSensorMode.Vision;
}
