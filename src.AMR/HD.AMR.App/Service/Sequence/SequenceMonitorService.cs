namespace HD.AMR.App.Service.Sequence;

/// <summary>
/// 시퀀스 모니터링 상태 허브 — <b>싱글톤</b>. 실행 엔진(<see cref="SequenceService"/>, 서킷별 scoped)이
/// 스텝 시작/진행/종료를 발행하고, 별도 브라우저 창(/sequence-monitor, 다른 서킷)이 구독해 표시한다.
/// 별도 창은 페이지의 scoped 상태를 볼 수 없으므로 모니터 상태를 여기로 승격했다.
/// 시퀀스는 물리적으로 동시에 1개만 돌므로 단일 상태로 충분하다.
/// </summary>
public class SequenceMonitorService
{
    private readonly object _lock = new();
    private readonly List<string> _lines = new();
    private const int LogCap = 200;

    /// <summary>상태 변경 통지 — 모니터 페이지가 구독해 StateHasChanged 호출.</summary>
    public event Action? Changed;

    /// <summary>런(풀오토 또는 단일 스텝) 진행 중 여부.</summary>
    public bool RunActive { get; private set; }

    /// <summary>직전 런이 실패로 끝났는지 — 실패면 모니터 창을 자동 닫지 않는다(원인 확인용).</summary>
    public bool LastEndedInFailure { get; private set; }

    /// <summary>모니터 창 닫기 요청 — 마지막 스텝(MonitorCloseStep)이 세우고 창 페이지가 감지해 self-close.</summary>
    public bool CloseRequested { get; private set; }

    /// <summary>현재 실행 중 스텝 키/이름/표시 번호(원형 숫자). 스텝 사이에는 직전 값 유지.</summary>
    public string? StepKey { get; private set; }
    public string StepName { get; private set; } = "";
    public string StepNo { get; private set; } = "";

    /// <summary>③ 거리 패널용 목표 거리(mm) — StartStep 시 context 값으로 갱신.</summary>
    public double CameraTargetDistanceMm { get; private set; } = 400;

    /// <summary>로그 스냅샷(타임스탬프 포함, 최신이 마지막).</summary>
    public IReadOnlyList<string> SnapshotLines()
    {
        lock (_lock) return _lines.ToArray();
    }

    public void BeginRun()
    {
        lock (_lock)
        {
            RunActive = true;
            LastEndedInFailure = false;
            CloseRequested = false;   // 새 런 시작 — 재오픈된 창이 즉시 닫히지 않게 리셋.
        }
        Changed?.Invoke();
    }

    /// <summary>모니터 창 닫기 요청 — 창 페이지가 Changed 를 받고 window.close() 한다.</summary>
    public void RequestClose()
    {
        lock (_lock) CloseRequested = true;
        Log("■ 모니터링 창을 닫습니다.");
    }

    public void StartStep(string key, string name, int stepNo1Based, double targetDistanceMm)
    {
        lock (_lock)
        {
            StepKey = key;
            StepName = name;
            StepNo = Circle(stepNo1Based);
            CameraTargetDistanceMm = targetDistanceMm;
            _lines.Clear();
        }
        Log($"▶ {name} 시작");
    }

    public void EndStep(bool success, string? message)
        => Log(success ? $"✅ 완료 — {message}" : $"❌ 실패 — {message}");

    public void EndRun(bool anyFailure)
    {
        lock (_lock)
        {
            RunActive = false;
            LastEndedInFailure = anyFailure;
        }
        // 종료 요약은 배너가 아니라 로그 라인으로 — 모니터 창 레이아웃을 밀지 않는다.
        Log(anyFailure ? "■ 시퀀스 종료 — 실패. 마지막 상태를 유지합니다." : "■ 시퀀스 종료 (성공).");
    }

    /// <summary>진행 라인 추가 — SequenceContext.Progress sink 로 스텝/공용 루틴이 호출(임의 스레드).</summary>
    public void Log(string line)
    {
        lock (_lock)
        {
            _lines.Add($"[{DateTime.Now:HH:mm:ss}] {line}");
            if (_lines.Count > LogCap) _lines.RemoveAt(0);
        }
        Changed?.Invoke();
    }

    /// <summary>원형 숫자 표기 (①~㊿) — Sequence 페이지 StepCircle 과 동일 규칙.</summary>
    private static string Circle(int n) => n switch
    {
        >= 1 and <= 20 => char.ConvertFromUtf32(0x2460 + n - 1),    // ①~⑳
        >= 21 and <= 35 => char.ConvertFromUtf32(0x3251 + n - 21),  // ㉑~㉟
        >= 36 and <= 50 => char.ConvertFromUtf32(0x32B1 + n - 36),  // ㊱~㊿
        _ => $"({n})",
    };
}
