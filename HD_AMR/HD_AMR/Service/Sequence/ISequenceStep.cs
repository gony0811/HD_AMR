namespace HD_AMR.Service.Sequence;

/// <summary>
/// 시퀀스 단계 하나를 나타내는 인터페이스. 각 단계는 DI에 등록되며,
/// <see cref="SequenceService"/>가 순서대로 실행한다.
/// </summary>
public interface ISequenceStep
{
    /// <summary>고유 식별 키 (예: "amrMove", "cobotInspection").</summary>
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

/// <summary>단계 간 공유 컨텍스트. 공용 파라미터와 티칭 위치를 담는다.</summary>
public class SequenceContext
{
    /// <summary>활성 tool 번호.</summary>
    public int Tool { get; set; } = 1;

    /// <summary>이동 속도 (%).</summary>
    public int Velocity { get; set; } = 20;

    /// <summary>티칭된 위치 목록 (Key → TeachingPosition). 시퀀스 시작 시 로드.</summary>
    public Dictionary<string, Data.Entities.TeachingPosition> Positions { get; set; } = new();

    /// <summary>단계 간 임시 데이터 전달용.</summary>
    public Dictionary<string, object> Bag { get; set; } = new();
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
