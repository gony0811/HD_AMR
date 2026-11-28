using static HD.AMR.App.Service.Sequence.Steps.SequenceEntry;

namespace HD.AMR.Tests;

/// <summary>
/// 복귀 MoveL rc=74 거부 재시도 판정 — 실기 2026-11-28 14:01 PU L4-R02C23 #5: 검사를 다 마친 뒤 면 방향 역주행이
/// rc=74(결함 main=1/sub=2 동반, 즉시 해제)로 거부돼 액션이 실패로 보고됐고, 실패 처리 복귀가 같은 목표로 바로 성공했다.
/// </summary>
public class RetraceMoveRejectRetryTests
{
    [Fact]
    public void Retries_when_reset_ok_and_fault_cleared()
        => Assert.Null(RejectRetryBlocker(resetRc: 0, faultMain: 0, faultSub: 0));

    [Fact]
    public void Does_not_retry_when_fault_persists_after_reset()
        => Assert.Contains("main=1", RejectRetryBlocker(0, 1, 2));

    [Fact]
    public void Does_not_retry_when_reset_fails()
        => Assert.Contains("ResetAllError", RejectRetryBlocker(14, 0, 0));

    [Fact]
    public void Does_not_retry_without_cobot_state()
        => Assert.NotNull(RejectRetryBlocker(0, null, null));

    [Fact]
    public void Only_rc74_is_treated_as_rejection()
        => Assert.Equal(74, MoveRejectedRc);
}
