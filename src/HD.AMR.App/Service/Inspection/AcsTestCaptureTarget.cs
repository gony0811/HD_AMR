namespace HD.AMR.App.Service.Inspection;

/// <summary>
/// ACS 코봇 이동 시험(moveToSeamStart, VDA §8.7 · 1.21)으로 마지막에 도달한 용접선 — 작업자가 그 자세에서 검사 프로필·시퀀스 화면으로
/// 수동 촬영할 때 비전 CAPTURE_REQ 에 ACS 가 발급한 taskId·attempt 를 싣기 위해 보관한다(앱 수명 동안만, 저장하지 않음).
/// attempt 는 ACS 가 본검사와 같은 taskId 별 누적 번호로 발급한다 — 그 뒤 본검사는 더 큰 번호로 나가므로 세이지에서 본검사 결과가 최신이 된다.
/// captureSeq 는 같은 (taskId, attempt) 안에서 겹치지 않게 화면 구분 없이 1부터 이어서 발번한다.
/// </summary>
public sealed class AcsTestCaptureTarget
{
    public sealed record Target(Guid TaskId, byte Attempt, string JobRef, string? WallCode, DateTimeOffset ReachedAt)
    {
        /// <summary>화면 표기 — "jobRef · 면 SM · attempt 2 · task 8e9b34ae…".</summary>
        public string Describe() =>
            $"{JobRef} · 면 {WallCode ?? "?"} · attempt {Attempt} · task {TaskId.ToString()[..8]}… ({ReachedAt.ToLocalTime():HH:mm:ss})";
    }

    private readonly object _sync = new();
    private Target? _current;
    private ushort _captureSeq;

    /// <summary>지금 보관 중인 시험 용접선(없으면 null).</summary>
    public Target? Current { get { lock (_sync) return _current; } }

    /// <summary>보관 값이 바뀌면(설정·해제) 알린다 — 화면이 표시를 갱신한다. 호출 스레드는 임의.</summary>
    public event Action<Target?>? Changed;

    /// <summary>시험 도달 — taskId·attempt 를 보관하고 captureSeq 를 처음부터 센다.</summary>
    public void Set(Guid taskId, byte attempt, string jobRef, string? wallCode)
    {
        Target t;
        lock (_sync)
        {
            t = new Target(taskId, attempt, jobRef, wallCode, DateTimeOffset.Now);
            _current = t;
            _captureSeq = 0;
        }
        Changed?.Invoke(t);
    }

    /// <summary>보관 해제 — 새 시험이 시작되거나 본검사가 시작되면(다른 용접선 자세로 바뀐다).</summary>
    public void Clear()
    {
        lock (_sync)
        {
            if (_current is null) return;
            _current = null;
            _captureSeq = 0;
        }
        Changed?.Invoke(null);
    }

    /// <summary>
    /// 수동 촬영 1회의 CAPTURE_REQ 식별자 — <paramref name="use"/> 이고 보관 값이 있으면 (시험 taskId, attempt, 이어지는 captureSeq),
    /// 아니면 종전 수동 값 (Guid.Empty, 1, <paramref name="fallbackSeq"/>()).
    /// </summary>
    public (Guid TaskId, byte Attempt, ushort CaptureSeq) IdsForManualCapture(bool use, Func<ushort> fallbackSeq)
    {
        if (use)
            lock (_sync)
                if (_current is { } t)
                {
                    if (_captureSeq < ushort.MaxValue) _captureSeq++;
                    return (t.TaskId, t.Attempt, _captureSeq);
                }
        return (Guid.Empty, 1, fallbackSeq());
    }

    /// <summary>보관 중인 (taskId, attempt) 의 다음 captureSeq(1부터, 65535 에서 멈춤). 보관 값이 없으면 null.</summary>
    public ushort? NextCaptureSeq()
    {
        lock (_sync)
        {
            if (_current is null) return null;
            if (_captureSeq < ushort.MaxValue) _captureSeq++;
            return _captureSeq;
        }
    }
}
