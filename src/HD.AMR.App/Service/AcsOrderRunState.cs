using HD.AMR.App.Communication.Vda5050;
using HD.AMR.App.Models;
using HD.AMR.App.Service.Inspection;

namespace HD.AMR.App.Service;

/// <summary>대시보드에 표시하는 ACS order 실행 상태.</summary>
public enum AcsOrderRunStatus
{
    /// <summary>order 없음(부팅 후 무수신).</summary>
    Idle,
    /// <summary>order 수신 후 주행·검사 진행 중.</summary>
    Running,
    /// <summary>진행 중 임무 일시정지(통신 단절·startPause).</summary>
    Paused,
    /// <summary>모든 액션 성공으로 완결.</summary>
    Completed,
    /// <summary>완결했지만 일부 액션 실패.</summary>
    CompletedWithFailures,
    /// <summary>임무 실패(주행 실패·장치 알람·비상정지·배터리 CRITICAL 등).</summary>
    Failed,
    /// <summary>임무 취소(cancelOrder·작업자 정지·신규 order 교체).</summary>
    Cancelled,
}

/// <summary>order 안 액션(task) 1건의 표시 정보. 파라미터에서 읽지 못한 항목은 null.</summary>
public sealed record AcsOrderTaskInfo(
    int Index,
    string ActionId,
    string ActionType,
    string? TaskId,
    int? SurfaceId,
    string? WallCode,
    string? SeamType,
    string? JobRef,
    int? Attempt,
    string Status,
    string? ResultDescription,
    DateTime? StartedAt,
    DateTime? EndedAt)
{
    /// <summary>1부터 세는 표시 번호(order 배열 순서 = 실행 순서).</summary>
    public int Number => Index + 1;

    /// <summary>"0x05 (SM)" 처럼 표시. 없으면 "-".</summary>
    public string SurfaceText => SurfaceId is { } id
        ? $"0x{id:X2}" + (WallCode is { Length: > 0 } c ? $" ({c})" : "")
        : WallCode ?? "-";

    /// <summary>taskId 앞 8자리(GUID 축약). 없으면 "-".</summary>
    public string TaskIdShort => TaskId is { Length: > 8 } t ? t[..8] : TaskId ?? "-";

    public string StatusText => Status switch
    {
        "WAITING" => "대기",
        "RUNNING" => "진행",
        "PAUSED" => "정지",
        "FINISHED" => "완료",
        "FAILED" => "실패",
        _ => Status,
    };

    public bool IsTerminal => Status is "FINISHED" or "FAILED";
}

/// <summary>대시보드 카드용 불변 스냅샷.</summary>
public sealed record AcsOrderRunSnapshot(
    long Version,
    AcsOrderRunStatus Status,
    string? OrderId,
    string? NodeId,
    string? MapId,
    string Phase,
    DateTime? StartedAt,
    DateTime? EndedAt,
    string? Message,
    IReadOnlyList<AcsOrderTaskInfo> Tasks)
{
    public static readonly AcsOrderRunSnapshot Empty =
        new(0, AcsOrderRunStatus.Idle, null, null, null, "대기", null, null, null, Array.Empty<AcsOrderTaskInfo>());

    public int TotalCount => Tasks.Count;
    public int FinishedCount => Tasks.Count(t => t.Status == "FINISHED");
    public int FailedCount => Tasks.Count(t => t.Status == "FAILED");
    public int DoneCount => Tasks.Count(t => t.IsTerminal);

    /// <summary>진행 중(RUNNING/PAUSED) 액션. 없으면 null.</summary>
    public AcsOrderTaskInfo? CurrentTask => Tasks.FirstOrDefault(t => t.Status is "RUNNING" or "PAUSED");

    public bool IsActive => Status is AcsOrderRunStatus.Running or AcsOrderRunStatus.Paused;

    /// <summary>진행률 0~100(액션 없음이면 완결 시 100).</summary>
    public double ProgressPercent => TotalCount == 0
        ? (Status is AcsOrderRunStatus.Idle || IsActive ? 0 : 100)
        : 100.0 * DoneCount / TotalCount;

    public string StatusText => Status switch
    {
        AcsOrderRunStatus.Idle => "대기",
        AcsOrderRunStatus.Running => "진행 중",
        AcsOrderRunStatus.Paused => "일시정지",
        AcsOrderRunStatus.Completed => "완료",
        AcsOrderRunStatus.CompletedWithFailures => "완료(일부 실패)",
        AcsOrderRunStatus.Failed => "실패",
        AcsOrderRunStatus.Cancelled => "취소",
        _ => Status.ToString(),
    };

    /// <summary>경과 시간(진행 중이면 now 기준).</summary>
    public TimeSpan? Elapsed(DateTime now) => StartedAt is { } s ? (EndedAt ?? now) - s : null;
}

/// <summary>
/// ACS(VDA5050) order 실행 정보 — orderId, 액션별 taskId·surfaceId, 진행 상태·현재 단계·시작 시각.
/// <see cref="Vda5050OrderExecutor"/> 가 수명주기 지점마다 갱신하고 대시보드(Desktop·Web)가 표시한다.
/// VDA state 보고(§6)와는 별개인 표시 전용 상태다 — 완결 후에도 다음 order 수신까지 마지막 결과를 유지한다.
/// 스레드 안전: 갱신은 잠금 안에서, 읽기는 불변 스냅샷으로.
/// </summary>
public sealed class AcsOrderRunState
{
    private readonly object _gate = new();
    private readonly Func<DateTime> _clock;
    private AcsOrderRunSnapshot _snapshot = AcsOrderRunSnapshot.Empty;
    private AcsOrderRunStatus _statusBeforePause = AcsOrderRunStatus.Running;

    public AcsOrderRunState() : this(() => DateTime.Now) { }

    /// <summary>테스트용 시계 주입.</summary>
    public AcsOrderRunState(Func<DateTime> clock) => _clock = clock;

    /// <summary>상태 변경(수신·단계·액션·종결). 임무 스레드에서 호출되므로 UI 는 디스패처로 넘겨야 한다.</summary>
    public event Action? Changed;

    public AcsOrderRunSnapshot Current
    {
        get { lock (_gate) return _snapshot; }
    }

    /// <summary>order 수리 — 이전 order 표시를 대체한다.</summary>
    public void BeginOrder(string orderId, string? nodeId, string? mapId, IReadOnlyList<VdaAction> actions)
    {
        var tasks = actions.Select((a, i) => DescribeTask(i, a)).ToList();
        Update(s => new AcsOrderRunSnapshot(s.Version, AcsOrderRunStatus.Running, orderId, nodeId, mapId,
            "수신", _clock(), null, null, tasks));
    }

    /// <summary>현재 단계 문구 갱신(예: "주행 중", "도착", "검사 2/5"). 진행 중일 때만.</summary>
    public void SetPhase(string phase)
    {
        Update(s => s.IsActive && s.Phase != phase ? s with { Phase = phase } : null);
    }

    /// <summary>액션 상태 갱신(VDA actionStatus 문자열: WAITING/RUNNING/PAUSED/FINISHED/FAILED).</summary>
    public void SetActionStatus(string actionId, string status, string? resultDescription = null)
    {
        Update(s =>
        {
            var idx = IndexOf(s, actionId);
            if (idx < 0) return null;
            var t = s.Tasks[idx];
            var now = _clock();
            var updated = t with
            {
                Status = status,
                ResultDescription = resultDescription ?? t.ResultDescription,
                StartedAt = status == "RUNNING" ? t.StartedAt ?? now : t.StartedAt,
                EndedAt = status is "FINISHED" or "FAILED" ? now : t.EndedAt,
            };
            if (updated == t) return null;
            var list = s.Tasks.ToList();
            list[idx] = updated;
            return s with { Tasks = list };
        });
    }

    /// <summary>일시정지/재개 — 진행 중 액션도 PAUSED↔RUNNING 으로 같이 바꾼다.</summary>
    public void SetPaused(bool paused)
    {
        Update(s =>
        {
            if (paused && s.Status == AcsOrderRunStatus.Running)
            {
                _statusBeforePause = s.Status;
                return s with
                {
                    Status = AcsOrderRunStatus.Paused,
                    Tasks = s.Tasks.Select(t => t.Status == "RUNNING" ? t with { Status = "PAUSED" } : t).ToList(),
                };
            }
            if (!paused && s.Status == AcsOrderRunStatus.Paused)
            {
                return s with
                {
                    Status = _statusBeforePause,
                    Tasks = s.Tasks.Select(t => t.Status == "PAUSED" ? t with { Status = "RUNNING" } : t).ToList(),
                };
            }
            return null;
        });
    }

    /// <summary>모든 액션 처리 후 완결 — 실패 액션이 있으면 <see cref="AcsOrderRunStatus.CompletedWithFailures"/>.</summary>
    public void Complete(string? message = null)
    {
        Update(s =>
        {
            if (!s.IsActive) return null;
            var failed = s.Tasks.Count(t => t.Status == "FAILED");
            return s with
            {
                Status = failed == 0 ? AcsOrderRunStatus.Completed : AcsOrderRunStatus.CompletedWithFailures,
                Phase = "완결",
                EndedAt = _clock(),
                Message = message ?? $"성공 {s.Tasks.Count - failed}, 실패 {failed}",
            };
        });
    }

    /// <summary>임무 실패 — 미종결 액션은 FAILED 로 닫는다.</summary>
    public void Fail(string reason) => End(AcsOrderRunStatus.Failed, "실패", reason);

    /// <summary>임무 취소 — 미종결 액션은 FAILED 로 닫는다(VDA 보고와 같은 처리).</summary>
    public void Cancel(string reason) => End(AcsOrderRunStatus.Cancelled, "취소", reason);

    private void End(AcsOrderRunStatus status, string phase, string reason)
    {
        Update(s =>
        {
            if (!s.IsActive) return null;
            var now = _clock();
            return s with
            {
                Status = status,
                Phase = phase,
                EndedAt = now,
                Message = reason,
                Tasks = s.Tasks.Select(t => t.IsTerminal ? t
                    : t with { Status = "FAILED", ResultDescription = reason, EndedAt = now }).ToList(),
            };
        });
    }

    /// <summary>스냅샷 교체. mutate 가 null 을 돌려주면 변경 없음(이벤트도 없음).</summary>
    private void Update(Func<AcsOrderRunSnapshot, AcsOrderRunSnapshot?> mutate)
    {
        lock (_gate)
        {
            var next = mutate(_snapshot);
            if (next is null) return;
            _snapshot = next with { Version = _snapshot.Version + 1 };
        }
        Changed?.Invoke();
    }

    private static int IndexOf(AcsOrderRunSnapshot s, string actionId)
    {
        for (var i = 0; i < s.Tasks.Count; i++)
            if (s.Tasks[i].ActionId == actionId) return i;
        return -1;
    }

    /// <summary>액션 파라미터에서 taskId·면(wall_code→surfaceId)·seamType·jobRef·attempt 를 읽는다.
    /// 해석 실패(파라미터 오류)여도 표시는 해야 하므로 읽을 수 있는 것만 채운다.</summary>
    internal static AcsOrderTaskInfo DescribeTask(int index, VdaAction action)
    {
        string? taskId = null, wallCode = null, seamType = null, jobRef = null;
        int? attempt = null;
        if (action.ActionType == "startWeldInspection"
            && WeldInspectionActionParser.TryParse(action, out var req, out _) && req is not null)
        {
            taskId = req.TaskId?.ToString();
            wallCode = req.DrawingPos.WallCode;
            seamType = req.SeamType.ToString();
            jobRef = req.JobRef;
            attempt = req.Attempt;
        }
        else if (action.ActionType == "moveToSeamStart"
                 && WeldInspectionActionParser.TryParseSeamStart(action, out var seam, out _) && seam is not null)
        {
            taskId = seam.TaskId?.ToString();
            wallCode = seam.DrawingPos.WallCode;
            jobRef = seam.JobRef;
            attempt = seam.Attempt;
        }
        var surfaceId = WallCodes.Find(wallCode)?.SurfaceId;
        return new AcsOrderTaskInfo(index, action.ActionId, action.ActionType, taskId, surfaceId, wallCode,
            seamType, jobRef, attempt, "WAITING", null, null, null);
    }
}
