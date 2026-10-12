namespace HD.AMR.App.Service;

/// <summary>통신 단절 단계(사양 §3). Grace = 단절 판정 후 정지 전(계속 진행), Pause = 감속정지, Hold = 정지 유지 + 부저.</summary>
public enum CommLossStage { Normal, Grace, Pause, Hold }

/// <summary><see cref="CommLossFailSafe.Evaluate"/> 1회에서 일어난 전이.</summary>
public enum CommLossEvent
{
    /// <summary>단절 판정(t0 기록).</summary>
    Lost,
    /// <summary>통신 복구(브로커 접속 AND ACS ONLINE). Pause/Hold 중이면 단계는 유지된다(자동 재개 없음, §4.1).</summary>
    Recovered,
    /// <summary>t0 + T1 도달 — 감속정지.</summary>
    EnterPause,
    /// <summary>t0 + T2 도달 — 부저 경보.</summary>
    EnterHold,
}

/// <summary>
/// 통신 단절 판정·단계 상태기(docs/HD_AMR_통신단절_FailSafe_사양.md §2–3) — 순수 로직, 시각은 호출자가 넣는다.
///
/// 단절(하나라도): A = MQTT 브로커 연결 두절, B = ACS connection 토픽 OFFLINE/CONNECTIONBROKEN.
/// ACS 상태 <see cref="AcsLiveness.Unknown"/>(수신 이력 없음 — 재접속 직후 retained 수신 전)은 단절로 보지 않는다.
/// 복구는 브로커 접속 AND ACS ONLINE 일 때만(§2.3) — 단절 중 Unknown 은 복구가 아니다.
///
/// 정지(Pause/Hold)는 <c>applicable</c>(AUTOMATIC + 수행 중 Order)일 때만 — 그 외에는 단절 기록·표시만 한다(§1).
/// Pause 이후 복구돼도 단계를 유지하고, 재개·Order 취소 시 호출자가 <see cref="Reset"/> 한다.
/// </summary>
public sealed class CommLossFailSafe
{
    private readonly TimeSpan _pauseAfter;
    private readonly TimeSpan _holdAfter;

    public CommLossFailSafe(TimeSpan pauseAfter, TimeSpan holdAfter)
    {
        _pauseAfter = pauseAfter;
        _holdAfter = holdAfter > pauseAfter ? holdAfter : pauseAfter + TimeSpan.FromSeconds(30);
    }

    public TimeSpan PauseAfter => _pauseAfter;
    public TimeSpan HoldAfter => _holdAfter;

    public CommLossStage Stage { get; private set; } = CommLossStage.Normal;

    /// <summary>현재 단절 상태(복구 판정 전까지 true).</summary>
    public bool IsLost { get; private set; }

    /// <summary>단절 판정 시각 t0(UTC). 단절 중이 아니면 마지막 단절의 t0, 이력 없으면 null.</summary>
    public DateTime? LostSinceUtc { get; private set; }

    /// <summary>단절 조건 — "A"(브로커 두절) / "B"(ACS OFFLINE·CONNECTIONBROKEN). 이력 없으면 null.</summary>
    public string? Condition { get; private set; }

    /// <summary>t0 기준 경과 시간(단절 중이 아니면 0).</summary>
    public TimeSpan Elapsed(DateTime nowUtc)
        => IsLost && LostSinceUtc is { } t0 ? nowUtc - t0 : TimeSpan.Zero;

    public IReadOnlyList<CommLossEvent> Evaluate(DateTime nowUtc, bool brokerConnected, AcsLiveness acs, bool applicable)
    {
        var events = new List<CommLossEvent>(2);
        var lost = !brokerConnected || acs is AcsLiveness.Offline or AcsLiveness.Broken;
        var online = brokerConnected && acs == AcsLiveness.Online;

        if (!IsLost && lost)
        {
            IsLost = true;
            LostSinceUtc = nowUtc;
            Condition = !brokerConnected ? "A" : "B";
            if (Stage == CommLossStage.Normal) Stage = CommLossStage.Grace;
            events.Add(CommLossEvent.Lost);
        }
        else if (IsLost && online)
        {
            IsLost = false;
            if (Stage == CommLossStage.Grace) Stage = CommLossStage.Normal;
            events.Add(CommLossEvent.Recovered);
            return events;
        }

        if (!IsLost) return events;

        var elapsed = nowUtc - LostSinceUtc!.Value;
        if (Stage == CommLossStage.Grace && applicable && elapsed >= _pauseAfter)
        {
            Stage = CommLossStage.Pause;
            events.Add(CommLossEvent.EnterPause);
        }
        if (Stage == CommLossStage.Pause && elapsed >= _holdAfter)
        {
            Stage = CommLossStage.Hold;
            events.Add(CommLossEvent.EnterHold);
        }
        return events;
    }

    /// <summary>일시정지 해소(재개·Order 취소·EMO 폐기). 아직 단절 중이면 t0 를 지금으로 다시 잡아 GRACE 부터 재무장한다.</summary>
    public void Reset(DateTime nowUtc)
    {
        if (IsLost)
        {
            LostSinceUtc = nowUtc;
            Stage = CommLossStage.Grace;
        }
        else
        {
            Stage = CommLossStage.Normal;
        }
    }
}
