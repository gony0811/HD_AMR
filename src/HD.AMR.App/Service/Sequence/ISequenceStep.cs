namespace HD.AMR.App.Service.Sequence;

/// <summary>
/// 시퀀스 단계 하나를 나타내는 인터페이스. 각 단계는 DI에 등록되며,
/// <see cref="SequenceService"/>가 순서대로 실행한다.
/// </summary>
public interface ISequenceStep
{
    /// <summary>고유 식별 키 (예: "cobotInspection", "inspectionRun").</summary>
    string Key { get; }

    /// <summary>UI 표시명 (예: "AMR 검사위치 이동").</summary>
    string DisplayName { get; }

    /// <summary>기본 실행 순서. 설정으로 오버라이드 가능.</summary>
    int DefaultOrder { get; }

    /// <summary>선행조건 검증 (티칭 여부, 장비 연결 상태 등).</summary>
    StepValidation Validate(SequenceContext context);

    /// <summary>단계 실행. 성공하면 <see cref="StepResult.Success"/>, 실패하면 메시지 포함.</summary>
    Task<StepResult> ExecuteAsync(SequenceContext context, CancellationToken ct);
}

/// <summary>단계 실행 전 선행조건 검증 결과.</summary>
public record StepValidation(bool IsValid, string? Message = null)
{
    public static StepValidation Ok() => new(true);
    public static StepValidation Fail(string message) => new(false, message);
}

/// <summary>단계 실행 결과.</summary>
public record StepResult(bool Success, string Message)
{
    public static StepResult Ok(string message) => new(true, message);
    public static StepResult Fail(string message) => new(false, message);
}

/// <summary>② 검사위치 이동 방향. Vertical 이면 대기위치 기준 툴 RZ −90° 회전을 합성.</summary>
public enum InspectionMoveDirection
{
    Horizontal = 0,
    Vertical = 1,
}

/// <summary>단계 간 공유 컨텍스트. 공용 파라미터와 티칭 위치를 담는다.</summary>
public class SequenceContext
{
    /// <summary>활성 tool 번호.</summary>
    public int Tool { get; set; } = 1;

    /// <summary>이동 속도 (%).</summary>
    public int Velocity { get; set; } = 20;

    /// <summary>검사위치 이동 수평 오프셋 u (mm). 좌(+)/우(−) → TOOL X+/X− (실측 확인 매핑).</summary>
    public double InspectionOffsetU { get; set; }

    /// <summary>검사위치 이동 수직 오프셋 v (mm). 상(+)/하(−) → TOOL Y+/Y− (실측 확인 매핑).</summary>
    public double InspectionOffsetV { get; set; }

    /// <summary>검사위치 이동 방향 (수평/수직). 수직이면 툴 RZ −90° 회전 합성.</summary>
    public InspectionMoveDirection InspectionDirection { get; set; } = InspectionMoveDirection.Horizontal;

    /// <summary>③ 카메라 거리 정렬 목표 거리(mm).</summary>
    public double CameraTargetDistanceMm { get; set; } = 400;

    /// <summary>④ 평탄면 센터링: 카메라 광축 → 레이저 3점 측정 중심 보정 횡이동(mm, 툴 Y).
    /// 레이저 중심이 카메라보다 좌측(툴 +Y)에 장착된 만큼 센터링 후 툴 −Y로 이동. 기본 −65mm.</summary>
    public double CameraToLaserShiftYmm { get; set; } = -65.0;

    /// <summary>⑱ 검사 수행: 대상 도면 id (드롭박스 선택값, 티칭설정 목록 필터용).</summary>
    public int InspectionDrawingId { get; set; }

    /// <summary>⑱ 검사 수행: 실행할 티칭설정(InspectionProfile) id — 웨이포인트·솎기/실행 파라미터 소스.</summary>
    public int InspectionProfileId { get; set; }

    /// <summary>검사 Surface ID (0x01~0xFF) — 두 용도 공용: ② 이동 목표가 되는 티칭 위치 결정
    /// (SurfaceId 매칭, <see cref="Steps.CobotInspectionMoveStep.FindBySurfaceId"/>) 및
    /// ⑱ 검사 순회 시 비전 CAPTURE_REQ 에 보고할 Surface ID. 기본 0x01.</summary>
    public int InspectionSurfaceId { get; set; } = 0x01;

    /// <summary>UI 진행 로그 sink — 모니터링 팝업 콘솔용. 스텝은 사람이 읽을 진행 라인을
    /// <c>context.Progress?.Invoke(msg)</c> 로 남긴다 (ILogger 와 별개, 페이지가 연결/해제).</summary>
    public Action<string>? Progress { get; set; }

    /// <summary>티칭된 위치 목록 (Key → TeachingPosition). 시퀀스 시작 시 로드.</summary>
    public Dictionary<string, Data.Entities.TeachingPosition> Positions { get; set; } = new();

    /// <summary>단계 간 임시 데이터 전달용.</summary>
    public Dictionary<string, object> Bag { get; set; } = new();

    // ── ACS(VDA5050) 연동 필드 — UI 단독 실행 경로에서는 전부 null 유지 ──────────

    /// <summary>ACS 작업 역추적 키(action jobRef). 있으면 ⑱이 자기발급 대신 "{jobRef}-W{i}"를 비전에 전달.</summary>
    public string? AcsJobRef { get; set; }

    /// <summary>수신 VDA5050 orderId — 로깅/추적용.</summary>
    public string? AcsOrderId { get; set; }

    /// <summary>수신 액션 actionId — 로깅/추적용.</summary>
    public string? AcsActionId { get; set; }

    /// <summary>ACS 발급 검사 작업 식별자(taskId, GUID). `startWeldInspection` 액션의 taskId 를
    /// <see cref="Inspection.WeldInspectionOrchestrator"/>가 주입한다.
    /// null=미연동/수동 실행/GUID 아닌 값 → CAPTURE_REQ 에 <see cref="System.Guid.Empty"/>(미지정)로 전송.</summary>
    public Guid? AcsTaskId { get; set; }

    /// <summary>ACS 발급 시도 번호(attempt, 1부터). `startWeldInspection` 액션의 attempt 를 주입.
    /// null=미연동 → CAPTURE_REQ 에 1(첫 시도)로 전송.</summary>
    public byte? AcsAttempt { get; set; }

    /// <summary>정렬(anchor) 공유 그룹 id (params.anchorGroupId).</summary>
    public string? AnchorGroupId { get; set; }

    /// <summary>그룹 내 순번 (params.seqInGroup, 1부터).</summary>
    public int? SeqInGroup { get; set; }

    /// <summary>ACS standoffMm — 로깅/검증용(정차점 산출은 ACS 책임, AMR은 참고만).</summary>
    public double? StandoffMmOverride { get; set; }

    /// <summary>⑱ 경유점 비전 실패율 상한(0~1). 초과 시 스텝 Fail — ACS 경로에서 inspectionFailed 승격.
    /// null(UI 단독 실행)이면 현행대로 집계만 하고 실패 처리 안 함.</summary>
    public double? VisionFailRatioMax { get; set; }

    /// <summary>CORNER3 좌/우 거울 side ("L"/"R") — 코너 스텝의 티칭 슬롯 키 접두사 선택
    /// (corner3.L.* / corner3.R.*). CORNER 외 경로에서는 null.</summary>
    public string? CornerSide { get; set; }
}

/// <summary>시퀀스 전체 실행 상태.</summary>
public enum SequenceRunState
{
    Idle,
    Running,
    Stopping,
}

/// <summary>개별 단계의 실행 상태.</summary>
public enum StepState
{
    Pending,
    Running,
    Completed,
    Failed,
    Skipped,
}

/// <summary>UI 표시용 단계 상태 스냅샷.</summary>
public record StepStatus(string Key, string DisplayName, StepState State, string? Message = null);
