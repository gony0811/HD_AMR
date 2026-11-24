using System.Text.Json;
using HD.AMR.App.Communication.Vda5050;
using HD.AMR.App.Service;

namespace HD.AMR.Tests;

public class AcsOrderRunStateTests
{
    private const string TaskId = "3f2b8c1e-1111-4222-8333-444455556666";

    private static VdaAction Inspection(string actionId, string wallCode = "SM") =>
        JsonSerializer.Deserialize<VdaAction>($$"""
        {
          "actionType": "startWeldInspection",
          "actionId": "{{actionId}}",
          "actionParameters": [
            { "key": "jobRef", "value": "JOB-CT1-L2-{{wallCode}}-S07" },
            { "key": "position", "value": {
                "seamStartW": [12.5, 5.9, 1.4], "seamEndW": [13.3, 5.9, 1.4],
                "drawingPos": { "tank": "CT1", "level": 2, "wall_code": "{{wallCode}}",
                                "x": 3.1, "y": 0.0, "z": 1.4 } } },
            { "key": "params", "value": {
                "seamType": "LINE", "sectionDxfId": "DXF-1", "inspectionProfileId": "P1",
                "anchorGroupId": "G1", "seqInGroup": 1,
                "taskId": "{{TaskId}}", "attempt": 2 } }
          ]
        }
        """)!;

    private static VdaAction Other(string actionId, string type = "batterySwapMove") =>
        new() { ActionId = actionId, ActionType = type };

    private static (AcsOrderRunState State, Func<DateTime> Now, Action<TimeSpan> Advance) NewState()
    {
        var now = new DateTime(2026, 11, 24, 9, 0, 0);
        var state = new AcsOrderRunState(() => now);
        return (state, () => now, d => now += d);
    }

    [Fact]
    public void Initial_IsIdle()
    {
        var state = new AcsOrderRunState();

        var s = state.Current;
        Assert.Equal(AcsOrderRunStatus.Idle, s.Status);
        Assert.Null(s.OrderId);
        Assert.Empty(s.Tasks);
        Assert.Equal("대기", s.StatusText);
        Assert.False(s.IsActive);
        Assert.Equal(0, s.ProgressPercent);
    }

    [Fact]
    public void BeginOrder_ParsesTaskIdAndSurface()
    {
        var (state, now, _) = NewState();

        state.BeginOrder("order-1", "N07", "CT1-L2", new[] { Inspection("a1"), Other("a2") });

        var s = state.Current;
        Assert.Equal(AcsOrderRunStatus.Running, s.Status);
        Assert.Equal("order-1", s.OrderId);
        Assert.Equal("N07", s.NodeId);
        Assert.Equal(now(), s.StartedAt);
        Assert.Null(s.EndedAt);
        Assert.Equal(2, s.TotalCount);

        var t = s.Tasks[0];
        Assert.Equal(TaskId, t.TaskId);
        Assert.Equal("3f2b8c1e", t.TaskIdShort);
        Assert.Equal("SM", t.WallCode);
        Assert.Equal(0x04, t.SurfaceId);
        Assert.Equal("0x04 (SM)", t.SurfaceText);
        Assert.Equal("Line", t.SeamType);
        Assert.Equal(2, t.Attempt);
        Assert.Equal("WAITING", t.Status);
        Assert.Equal(1, t.Number);

        var other = s.Tasks[1];
        Assert.Null(other.TaskId);
        Assert.Null(other.SurfaceId);
        Assert.Equal("-", other.SurfaceText);
    }

    [Fact]
    public void BeginOrder_InvalidInspectionParams_StillListsAction()
    {
        var (state, _, _) = NewState();

        state.BeginOrder("order-1", "N1", "CT1-L1", new[] { Other("bad", "startWeldInspection") });

        var t = Assert.Single(state.Current.Tasks);
        Assert.Equal("startWeldInspection", t.ActionType);
        Assert.Null(t.TaskId);
        Assert.Null(t.SurfaceId);
    }

    [Fact]
    public void ActionProgress_TracksCurrentTaskAndCounts()
    {
        var (state, now, advance) = NewState();
        state.BeginOrder("o", "N", "M", new[] { Inspection("a1"), Inspection("a2", "B") });

        state.SetActionStatus("a1", "RUNNING");
        state.SetPhase("startWeldInspection 1/2");
        Assert.Equal("a1", state.Current.CurrentTask?.ActionId);
        Assert.Equal(now(), state.Current.Tasks[0].StartedAt);
        Assert.Equal("startWeldInspection 1/2", state.Current.Phase);

        advance(TimeSpan.FromSeconds(30));
        state.SetActionStatus("a1", "FINISHED", "ok");
        var s = state.Current;
        Assert.Null(s.CurrentTask);
        Assert.Equal(1, s.FinishedCount);
        Assert.Equal(50, s.ProgressPercent);
        Assert.Equal("ok", s.Tasks[0].ResultDescription);
        Assert.Equal(now(), s.Tasks[0].EndedAt);
        Assert.Equal(TimeSpan.FromSeconds(30), s.Elapsed(now()));
    }

    [Fact]
    public void Complete_AllSucceeded_IsCompleted()
    {
        var (state, now, _) = NewState();
        state.BeginOrder("o", "N", "M", new[] { Inspection("a1") });
        state.SetActionStatus("a1", "FINISHED", "ok");

        state.Complete();

        var s = state.Current;
        Assert.Equal(AcsOrderRunStatus.Completed, s.Status);
        Assert.Equal(now(), s.EndedAt);
        Assert.Equal(100, s.ProgressPercent);
        Assert.False(s.IsActive);
    }

    [Fact]
    public void Complete_WithFailedAction_IsCompletedWithFailures()
    {
        var (state, _, _) = NewState();
        state.BeginOrder("o", "N", "M", new[] { Inspection("a1"), Inspection("a2") });
        state.SetActionStatus("a1", "FINISHED");
        state.SetActionStatus("a2", "FAILED", "bead not found");

        state.Complete();

        Assert.Equal(AcsOrderRunStatus.CompletedWithFailures, state.Current.Status);
        Assert.Equal(1, state.Current.FailedCount);
    }

    [Fact]
    public void Complete_NoActions_ProgressFull()
    {
        var (state, _, _) = NewState();
        state.BeginOrder("o", "N", "M", Array.Empty<VdaAction>());
        Assert.Equal(0, state.Current.ProgressPercent);

        state.Complete();

        Assert.Equal(AcsOrderRunStatus.Completed, state.Current.Status);
        Assert.Equal(100, state.Current.ProgressPercent);
    }

    [Fact]
    public void Fail_ClosesOpenActionsAndKeepsFinished()
    {
        var (state, _, _) = NewState();
        state.BeginOrder("o", "N", "M", new[] { Inspection("a1"), Inspection("a2"), Inspection("a3") });
        state.SetActionStatus("a1", "FINISHED", "ok");
        state.SetActionStatus("a2", "RUNNING");

        state.Fail("주행 실패");

        var s = state.Current;
        Assert.Equal(AcsOrderRunStatus.Failed, s.Status);
        Assert.Equal("주행 실패", s.Message);
        Assert.Equal("FINISHED", s.Tasks[0].Status);
        Assert.Equal("ok", s.Tasks[0].ResultDescription);
        Assert.All(s.Tasks.Skip(1), t => Assert.Equal("FAILED", t.Status));
        Assert.All(s.Tasks.Skip(1), t => Assert.Equal("주행 실패", t.ResultDescription));
    }

    [Fact]
    public void Cancel_SetsCancelled_AndLaterTerminalCallsAreIgnored()
    {
        var (state, _, _) = NewState();
        state.BeginOrder("o", "N", "M", new[] { Inspection("a1") });

        state.Cancel("ACS cancelOrder");
        state.Complete();
        state.Fail("late");
        state.SetPhase("late phase");

        var s = state.Current;
        Assert.Equal(AcsOrderRunStatus.Cancelled, s.Status);
        Assert.Equal("ACS cancelOrder", s.Message);
        Assert.Equal("취소", s.Phase);
        Assert.Equal("FAILED", s.Tasks[0].Status);
    }

    [Fact]
    public void Terminal_WithoutActiveOrder_IsNoOp()
    {
        var state = new AcsOrderRunState();
        var events = 0;
        state.Changed += () => events++;

        state.Cancel("cancelOrder with no mission");
        state.Fail("x");
        state.Complete();

        Assert.Equal(AcsOrderRunStatus.Idle, state.Current.Status);
        Assert.Equal(0, events);
    }

    [Fact]
    public void Pause_Resume_TogglesStatusAndRunningActions()
    {
        var (state, _, _) = NewState();
        state.BeginOrder("o", "N", "M", new[] { Inspection("a1"), Inspection("a2") });
        state.SetActionStatus("a1", "RUNNING");

        state.SetPaused(true);
        Assert.Equal(AcsOrderRunStatus.Paused, state.Current.Status);
        Assert.Equal("PAUSED", state.Current.Tasks[0].Status);
        Assert.Equal("WAITING", state.Current.Tasks[1].Status);
        Assert.True(state.Current.IsActive);
        Assert.Equal("a1", state.Current.CurrentTask?.ActionId);

        state.SetPaused(false);
        Assert.Equal(AcsOrderRunStatus.Running, state.Current.Status);
        Assert.Equal("RUNNING", state.Current.Tasks[0].Status);
    }

    [Fact]
    public void Paused_OrderCanStillBeCancelled()
    {
        var (state, _, _) = NewState();
        state.BeginOrder("o", "N", "M", new[] { Inspection("a1") });
        state.SetActionStatus("a1", "RUNNING");
        state.SetPaused(true);

        state.Cancel("operator");

        Assert.Equal(AcsOrderRunStatus.Cancelled, state.Current.Status);
        Assert.Equal("FAILED", state.Current.Tasks[0].Status);
    }

    [Fact]
    public void NewOrder_ReplacesPreviousResult()
    {
        var (state, _, _) = NewState();
        state.BeginOrder("o1", "N1", "M", new[] { Inspection("a1") });
        state.Fail("x");

        state.BeginOrder("o2", "N2", "M", new[] { Inspection("b1"), Inspection("b2") });

        var s = state.Current;
        Assert.Equal(AcsOrderRunStatus.Running, s.Status);
        Assert.Equal("o2", s.OrderId);
        Assert.Null(s.Message);
        Assert.Null(s.EndedAt);
        Assert.Equal(new[] { "b1", "b2" }, s.Tasks.Select(t => t.ActionId));
    }

    [Fact]
    public void Changed_FiresOnlyOnRealChanges_AndVersionIncrements()
    {
        var (state, _, _) = NewState();
        var events = 0;
        state.Changed += () => events++;

        state.BeginOrder("o", "N", "M", new[] { Inspection("a1") });
        var v1 = state.Current.Version;
        state.SetPhase("주행 중");
        state.SetPhase("주행 중");                  // 같은 단계 — 변경 없음
        state.SetActionStatus("unknown", "RUNNING"); // 없는 액션 — 변경 없음
        state.SetPaused(false);                     // 정지 중이 아님 — 변경 없음

        Assert.Equal(2, events);
        Assert.Equal(v1 + 1, state.Current.Version);
    }
}
