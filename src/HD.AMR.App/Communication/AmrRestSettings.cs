namespace HD.AMR.App.Communication;

/// <summary>
/// TARS-M v3 REST API 설정 — appsettings.json 의 <c>AmrRest</c> 섹션.
/// 확정 근거: docs/VDA5050_INTERFACE_SPEC_1.pdf 부록 D + docs/ADENT_TARSM_V3_OPENAPI.yaml (CONFIRMED 3종).
/// 응답 스키마(schedule/error 값 목록)는 미확정이라 경로만 설정으로 분리해 벤더 회신 시 코드 수정을 최소화한다.
/// </summary>
public class AmrRestSettings
{
    /// <summary>기본 <c>http://{AMR IP}/api/v3</c> — 80포트, 인증 없음.</summary>
    public string BaseUrl { get; set; } = "http://10.10.100.200/api/v3";

    /// <summary>HTTP 요청 타임아웃(ms).</summary>
    public int TimeoutMs { get; set; } = 5000;

    /// <summary>좌표 이동(비동기 · 큐 추가). POST {x,y,rz,stopFlag}.</summary>
    public string GoPath { get; set; } = "/robot/go";

    /// <summary>주행 정지(진행 이동 중단). POST {"state":"stop"} — emergencyStop·Order 교체 선행 단계.</summary>
    public string StatePath { get; set; } = "/robot/state";

    /// <summary>로봇 상태 조회. schedule(진행)·error(실패) 필드 폴링.</summary>
    public string StatusPath { get; set; } = "/robot/status";

    /// <summary>현재 위치·맵 좌표 라이다 점 조회.</summary>
    public string PosePath { get; set; } = "/robot/pose";

    /// <summary>맵 내용 조회 경로. 맵 이름은 URL 마지막 segment 로 붙인다.</summary>
    public string MapContentPath { get; set; } = "/map/content/name";

    /// <summary>새 맵 스캔 시작/종료 및 현재 맵 저장 경로.</summary>
    public string MapScanOnPath { get; set; } = "/map/scan/on";
    public string MapScanOffPath { get; set; } = "/map/scan/off";
    public string MapSavePath { get; set; } = "/map/save";

    /// <summary>
    /// AMR 운영 맵 이름(예: <c>260903_133609.map</c>).
    /// 현재 확인된 API/Modbus 계약은 활성 맵 이름을 노출하지 않아 설정으로 보유한다.
    /// </summary>
    public string MapName { get; set; } = "";

    /// <summary>맵 이미지 해상도(m/픽셀). TARS-M 맵 응답에는 이 값이 포함되지 않는다.</summary>
    public double MapResolution { get; set; } = 0.05;

    /// <summary>이동 중 status 폴링 간격(ms). 부록 D-6 검토값 200~500ms.</summary>
    public int StatusPollMs { get; set; } = 500;

    /// <summary>노드 주행 타임아웃(초). 초과 시 이동 실패 처리.</summary>
    public int DriveTimeoutSec { get; set; } = 600;
}
