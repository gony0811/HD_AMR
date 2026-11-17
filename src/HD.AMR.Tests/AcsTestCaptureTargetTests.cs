using HD.AMR.App.Service.Inspection;

namespace HD.AMR.Tests;

/// <summary>코봇 이동 시험으로 도달한 용접선의 taskId·attempt 를 수동 촬영(CAPTURE_REQ)에 싣는 보관소.</summary>
public class AcsTestCaptureTargetTests
{
    private static readonly Guid Task = Guid.Parse("8e9b34ae-0625-5779-9c1d-2f0a6b3e4d51");

    [Fact]
    public void 보관_중이면_시험_taskId_attempt_와_이어지는_captureSeq_를_쓴다()
    {
        var t = new AcsTestCaptureTarget();
        ushort manual = 0;
        Assert.Equal((Guid.Empty, (byte)1, (ushort)1), t.IdsForManualCapture(true, () => ++manual));   // 보관 전 = 종전

        t.Set(Task, 4, "TEST-SEAM-x", "SM");
        Assert.Equal((Task, (byte)4, (ushort)1), t.IdsForManualCapture(true, () => ++manual));
        Assert.Equal((ushort)2, t.NextCaptureSeq());                                                    // 시퀀스 화면과 같은 번호열
        Assert.Equal((Task, (byte)4, (ushort)3), t.IdsForManualCapture(true, () => ++manual));
        Assert.Equal((Guid.Empty, (byte)1, (ushort)2), t.IdsForManualCapture(false, () => ++manual));  // 체크를 끄면 종전

        t.Set(Task, 5, "TEST-SEAM-x", "SM");                                                            // 새 시험 — captureSeq 다시 1부터
        Assert.Equal((Task, (byte)5, (ushort)1), t.IdsForManualCapture(true, () => ++manual));
    }

    [Fact]
    public void 해제하면_종전_값으로_돌아가고_알린다()
    {
        var t = new AcsTestCaptureTarget();
        var seen = new List<AcsTestCaptureTarget.Target?>();
        t.Changed += seen.Add;
        t.Set(Task, 2, "TEST-SEAM-x", "T");
        t.Clear();
        t.Clear();   // 이미 비었으면 알리지 않는다
        Assert.Equal(2, seen.Count);
        Assert.Null(seen[1]);
        Assert.Null(t.Current);
        Assert.Null(t.NextCaptureSeq());
        Assert.Equal(Guid.Empty, t.IdsForManualCapture(true, () => 7).TaskId);
    }
}
