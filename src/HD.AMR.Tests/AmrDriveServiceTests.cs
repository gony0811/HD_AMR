using HD.AMR.App.Enums;
using HD.AMR.App.Models;
using HD.AMR.App.Service;

namespace HD.AMR.Tests;

/// <summary>
/// AMR 좌표 주행의 <b>정차 판정</b> 회귀. 자동 보정은 "AMR 이 완전히 멈춘 뒤"에만 촬영해야 하고,
/// 정차 pose 자체가 표본의 기준이 되므로 이 판정이 틀리면 보정값이 조용히 오염된다.
/// </summary>
public class AmrDriveServiceTests
{
    private static readonly AmrDriveOptions Opt = new();

    private static RobotPose P(double x, double y, double deg)
        => new((float)x, (float)y, (float)(deg * Math.PI / 180));

    [Fact]
    public void Stopped_WhenIdleAndPoseHeldAfterMoving()
    {
        var cur = P(1.000, 2.000, 90);
        var prev = P(1.001, 2.000, 90);   // 1mm 변화 — 정지로 본다.
        Assert.True(AmrDriveService.IsStoppedTick(WorkStatus.Idle, cur, prev,
            movedObserved: true, requireMotion: true, Opt));
    }

    /// <summary>명령 직후 아직 출발하지 않은 순간(대기 + 정지)을 도착으로 오판하면 안 된다.</summary>
    [Fact]
    public void NotStopped_BeforeMotionObserved_WhenMotionRequired()
    {
        var cur = P(1.000, 2.000, 90);
        Assert.False(AmrDriveService.IsStoppedTick(WorkStatus.Idle, cur, cur,
            movedObserved: false, requireMotion: true, Opt));
    }

    /// <summary>이미 목표에 서 있는 경우(시작 자세 복귀 등)는 움직임 없이도 정차로 인정해야 한다 —
    /// 요구하면 영원히 기다린다.</summary>
    [Fact]
    public void Stopped_WhenAlreadyAtTarget_AndMotionNotRequired()
    {
        var cur = P(1.000, 2.000, 90);
        Assert.True(AmrDriveService.IsStoppedTick(WorkStatus.Idle, cur, cur,
            movedObserved: false, requireMotion: false, Opt));
    }

    [Fact]
    public void NotStopped_WhileMoving()
    {
        var cur = P(1.000, 2.000, 90);
        Assert.False(AmrDriveService.IsStoppedTick(WorkStatus.Moving, cur, cur,
            movedObserved: true, requireMotion: true, Opt));
    }

    [Fact]
    public void NotStopped_WhilePoseStillChanging()
    {
        var cur = P(1.000, 2.000, 90);
        var prev = P(1.050, 2.000, 90);   // 50mm 변화 — 아직 굴러가는 중.
        Assert.False(AmrDriveService.IsStoppedTick(WorkStatus.Idle, cur, prev,
            movedObserved: true, requireMotion: true, Opt));
    }

    [Fact]
    public void NotStopped_WhileYawStillChanging()
    {
        var cur = P(1.000, 2.000, 90);
        var prev = P(1.000, 2.000, 92);   // 2° 변화 — 제자리 회전 중.
        Assert.False(AmrDriveService.IsStoppedTick(WorkStatus.Idle, cur, prev,
            movedObserved: true, requireMotion: true, Opt));
    }

    /// <summary>첫 폴링에는 비교 대상이 없으므로 정차로 판정하지 않는다.</summary>
    [Fact]
    public void NotStopped_OnFirstPollWithoutPrevious()
    {
        Assert.False(AmrDriveService.IsStoppedTick(WorkStatus.Idle, P(1, 2, 90), null,
            movedObserved: true, requireMotion: false, Opt));
    }

    /// <summary>yaw 경계 넘김(179° → -179°)이 2° 변화로 읽혀야 한다 — 358° 로 읽으면 영원히 "회전 중".</summary>
    [Fact]
    public void YawWrapAround_IsMeasuredAsSmallChange()
    {
        var cur = P(1, 2, 179.9);
        var prev = P(1, 2, -179.95);
        Assert.True(AmrDriveService.IsStoppedTick(WorkStatus.Idle, cur, prev,
            movedObserved: true, requireMotion: true, Opt));
    }
}
