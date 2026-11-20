using HD.AMR.App.Data.Entities;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Sequence;
using static HD.AMR.App.Service.Sequence.Steps.SequenceEntry;

namespace HD.AMR.Tests;

/// <summary>홈 모드 복귀의 검사 준비 직선 경유(실기 2026-11-21 04:34 M2 SM: 벽 앞 → 작업 준비 위치 MoveJ 가 벽 쪽으로 휨) —
/// 옆벽처럼 홈 모드인 면은 그 면의 검사 준비가 티칭돼 있을 때만 경유한다.</summary>
public class HomeViaWallSlotTests
{
    private static TeachingPosition Taught(string key, params double[] j) => new()
    {
        Key = key, Name = key,
        X = 0, Y = 0, Z = 0, Rx = 0, Ry = 0, Rz = 0,
        J1 = j[0], J2 = j[1], J3 = j[2], J4 = j[3], J5 = j[4], J6 = j[5],
    };

    private static SequenceContext Ctx(string? wall, params TeachingPosition[] ps) => new()
    {
        WallCode = wall,
        Positions = ps.ToDictionary(p => p.Key, p => p),
    };

    private static readonly TeachingPosition Home = Taught("home", 0, -90, 90, 0, 90, 0);
    private static readonly TeachingPosition Ready = Taught("ready", 0, -80, 100, -20, 90, 0);
    private static readonly TeachingPosition SlotSM = Taught(TeachingService.WallSlotKey("SM"), 30, -60, 100, -40, 90, 0);
    private static readonly TeachingPosition SlotPM = Taught(TeachingService.WallSlotKey("PM"), -30, -60, 100, -40, 90, 0);
    private static readonly TeachingPosition SlotT = Taught(TeachingService.WallSlotKey("T"), 60, -90, -60, 30, 90, 0);

    [Fact]
    public void 옆벽은_그_면의_검사_준비를_경유()
        => Assert.Equal(("wall.SM", "SM"), HomeViaSlot(Ctx("SM", Home, Ready, SlotSM, SlotPM)));

    [Fact]
    public void 면_정보가_없으면_마지막_진입_면()
        => Assert.Equal("SM", HomeViaSlot(Ctx(null, Home, Ready, SlotSM, SlotPM), lastWall: "SM")?.Code);

    [Fact]
    public void 미티칭이면_다른_면_슬롯을_빌리지_않고_종전_경로()
        => Assert.Null(HomeViaSlot(Ctx("SM", Home, Ready, SlotPM)));

    [Fact]
    public void 홈_모드가_아닌_면은_해당_없음()
    {
        Assert.Null(HomeViaSlot(Ctx("T", Home, Ready, SlotT)));
        Assert.Null(HomeViaSlot(Ctx(null, Home, Ready, SlotT), lastWall: "T"));
        Assert.Null(HomeViaSlot(Ctx(null, Home, Ready, SlotSM)));   // 면을 모르면 경유 없음
    }
}
