using HD.AMR.App.Service;

namespace HD.AMR.Tests;

public class TeachingHomeSlotTests
{
    // 홈 1.5·2 는 홈 바로 아래, 작업 준비 위치 위에 온다(화면 표시 순서 = Slots 순서).
    [Fact]
    public void HomeSlots_FollowHomeInOrder()
    {
        var keys = TeachingService.Slots.Select(s => s.Key).Take(4).ToArray();
        Assert.Equal(new[] { "home", "home15", "home2", "ready" }, keys);
        Assert.Equal("홈 위치_1.5", TeachingService.Slots[1].Name);
        Assert.Equal("홈 위치_2", TeachingService.Slots[2].Name);
        Assert.True(TeachingService.IsSeedSlot("home15"));
        Assert.True(TeachingService.IsSeedSlot("home2"));
    }

    // 현장에서 "항목 추가"로 만든 이름은 띄어쓰기와 무관하게 이관 대상으로 잡힌다.
    [Theory]
    [InlineData("홈위치_1.5", "home15")]
    [InlineData("홈 위치_1.5", "home15")]
    [InlineData("홈위치_2", "home2")]
    [InlineData(" 홈 위치_2 ", "home2")]
    [InlineData("홈위치_3", null)]
    [InlineData("홈 위치", null)]
    public void UserHomeSlotKey_MatchesByName(string name, string? expected)
        => Assert.Equal(expected, TeachingService.UserHomeSlotKey(name));
}
