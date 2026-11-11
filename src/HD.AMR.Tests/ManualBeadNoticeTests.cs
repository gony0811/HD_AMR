using HD.AMR.App.Service;
using HD.AMR.App.Service.Sequence;

namespace HD.AMR.Tests;

/// <summary>수동 비드 입력 팝업 알림 — ACS 로 가는 state.errors[] 항목과 타워램프 녹색 점멸.</summary>
public class ManualBeadNoticeTests
{
    [Fact]
    public void 팝업_요청은_WARNING_오류_항목으로_바뀐다()
    {
        var req = new ManualBeadRequest
        {
            PeakId = 1, Stage = "Bead1 찾기", Reason = "자동 검출 신뢰도 낮음 — coverage 41% < 60%",
            DeadlineUtc = new DateTime(2026, 11, 12, 3, 0, 0, DateTimeKind.Utc),
        };

        var err = ManualBeadNotice.ToError(req);

        Assert.Equal("manualBeadInputRequired", err.ErrorType);
        Assert.Equal("WARNING", err.ErrorLevel);
        Assert.StartsWith("Bead1 찾기: 수동 비드 입력 대기 — 자동 검출 신뢰도 낮음", err.ErrorDescription);
        Assert.Contains(err.ErrorReferences!, r => r.ReferenceKey == "stage" && r.ReferenceValue == "Bead1 찾기");
        Assert.Contains(err.ErrorReferences!, r => r.ReferenceKey == "deadline" && r.ReferenceValue == "2026-11-12T03:00:00Z");
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(499, true)]
    [InlineData(500, false)]
    [InlineData(999, false)]
    [InlineData(1000, true)]     // 1초에 한 번 — 켜짐 0.5초, 꺼짐 0.5초
    [InlineData(1500, false)]
    public void 점멸은_1초_주기다(long tickMs, bool on)
        => Assert.Equal(on, ManualBeadNotice.BlinkOn(tickMs));

    [Fact]
    public void 입력_대기_중에는_운전_모드와_무관하게_녹색만_깜빡인다()
    {
        // AUTO(평소 녹색 점등)·MANUAL(평소 황색) 모두 녹색 점멸로 바뀐다.
        Assert.Equal(new TowerLampState(false, false, true), IoTowerLampPolicy.Resolve(true, false, false, attention: true, blinkOn: true));
        Assert.Equal(new TowerLampState(false, false, false), IoTowerLampPolicy.Resolve(true, false, false, attention: true, blinkOn: false));
        Assert.Equal(new TowerLampState(false, false, true), IoTowerLampPolicy.Resolve(false, true, false, attention: true, blinkOn: true));
        Assert.Equal(new TowerLampState(false, false, false), IoTowerLampPolicy.Resolve(false, true, false, attention: true, blinkOn: false));
    }

    [Fact]
    public void 알람과_일시정지가_점멸보다_우선한다()
    {
        Assert.Equal(new TowerLampState(true, false, false), IoTowerLampPolicy.Resolve(true, false, true, attention: true, blinkOn: true));
        Assert.Equal(new TowerLampState(false, true, false), IoTowerLampPolicy.Resolve(true, false, false, paused: true, attention: true, blinkOn: true));
    }

    [Fact]
    public void 입력이_끝나면_원래_운전_모드_표시로_돌아간다()
    {
        Assert.Equal(new TowerLampState(false, false, true), IoTowerLampPolicy.Resolve(true, false, false, attention: false, blinkOn: false));
        Assert.Equal(new TowerLampState(false, true, false), IoTowerLampPolicy.Resolve(false, true, false, attention: false, blinkOn: true));
    }
}
