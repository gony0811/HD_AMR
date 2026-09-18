using HD.AMR.App.Communication;

namespace HD.AMR.Tests;

/// <summary>
/// EBLUM 3채널 홀 동기 컨트롤러 RS232/TTL 프로토콜 검증.
/// 기대값은 사양서 4장 "명령 예시"의 <b>HEX 원문</b>에서 그대로 가져왔다 — 명령 문자열이 바뀌면
/// 실제 장비가 반응하지 않으므로 바이트 단위로 고정한다.
/// </summary>
public class TelescopicProtocolTests
{
    private static string Hex(string command)
        => string.Join(' ', System.Text.Encoding.ASCII
            .GetBytes(command + TelescopicProtocol.Terminator)
            .Select(b => b.ToString("X2")));

    // 사양서 4장 HEX 원문과 1:1. "48 61 6E 64 6C 65 3A" = "Handle:", 끝 "0D 0A" = \r\n
    [Theory]
    [InlineData("Up", "Handle:001", "48 61 6E 64 6C 65 3A 30 30 31 0D 0A")]
    [InlineData("Down", "Handle:002", "48 61 6E 64 6C 65 3A 30 30 32 0D 0A")]
    [InlineData("Stop", "Handle:000", "48 61 6E 64 6C 65 3A 30 30 30 0D 0A")]
    [InlineData("Reset", "Handle:032", "48 61 6E 64 6C 65 3A 30 33 32 0D 0A")]
    [InlineData("Memory1", "Handle:004", "48 61 6E 64 6C 65 3A 30 30 34 0D 0A")]
    [InlineData("Memory2", "Handle:008", "48 61 6E 64 6C 65 3A 30 30 38 0D 0A")]
    [InlineData("Memory3", "Handle:016", "48 61 6E 64 6C 65 3A 30 31 36 0D 0A")]
    public void Commands_MatchSpecHexExactly(string name, string expected, string expectedHex)
    {
        var actual = name switch
        {
            "Up" => TelescopicProtocol.Up(),
            "Down" => TelescopicProtocol.Down(),
            "Stop" => TelescopicProtocol.Stop(),
            "Reset" => TelescopicProtocol.Reset(),
            "Memory1" => TelescopicProtocol.Memory(1),
            "Memory2" => TelescopicProtocol.Memory(2),
            "Memory3" => TelescopicProtocol.Memory(3),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };

        Assert.Equal(expected, actual);
        Assert.Equal(expectedHex, Hex(actual));
    }

    // 제어 명령은 3바이트 10진 문자열이고 컨트롤러가 8비트 값으로 해석한다 — 비트 OR 조합이 성립해야 한다.
    [Fact]
    public void Handle_IsDecimalStringOfBitmask()
    {
        Assert.Equal("Handle:003", TelescopicProtocol.Handle(
            TelescopicProtocol.HandleBits.Up | TelescopicProtocol.HandleBits.Down));
        Assert.Equal("Handle:063", TelescopicProtocol.Handle(
            TelescopicProtocol.HandleBits.Up | TelescopicProtocol.HandleBits.Down |
            TelescopicProtocol.HandleBits.Memory1 | TelescopicProtocol.HandleBits.Memory2 |
            TelescopicProtocol.HandleBits.Memory3 | TelescopicProtocol.HandleBits.Reset));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(-1)]
    public void Memory_RejectsInvalidSlot(int slot)
        => Assert.Throws<ArgumentOutOfRangeException>(() => TelescopicProtocol.Memory(slot));

    [Fact]
    public void Target_UsesSpecifiedDigitCount()
    {
        Assert.Equal("Target:450", TelescopicProtocol.Target(450));
        Assert.Equal("Target:007", TelescopicProtocol.Target(7));
        Assert.Equal("Target:0450", TelescopicProtocol.Target(450, digits: 4));
    }

    // 사양서는 지령 높이를 3바이트로 규정한다 → 1000mm 행정의 최상단은 지령 불가(벤더 확인 대상).
    [Fact]
    public void Target_ThreeDigits_CannotCommandFullStroke()
    {
        Assert.Equal("Target:999", TelescopicProtocol.Target(999));
        Assert.Throws<ArgumentOutOfRangeException>(() => TelescopicProtocol.Target(1000));
        // 자릿수를 넓히면 가능해진다 — 설정으로 열어 둔 이유.
        Assert.Equal("Target:1000", TelescopicProtocol.Target(1000, digits: 4));
    }

    [Fact]
    public void Target_RejectsNegative()
        => Assert.Throws<ArgumentOutOfRangeException>(() => TelescopicProtocol.Target(-1));

    // 응답 페이로드 = A B C D 소리 파라미터플래그 파라미터모드 깜빡임 + 에러(2) + 높이(4) = 14자
    [Fact]
    public void ParseStatus_FullSpecLayout()
    {
        //                      A B C D 소리 플래그 모드 깜빡임 에러 높이
        //                      1 2 1 0  1    0    0     0     03  0742
        var s = TelescopicProtocol.ParseStatus("Length:12101000030742");

        Assert.NotNull(s);
        Assert.True(s!.ExactLayout);
        Assert.True(s.Active);
        Assert.Equal(TelescopicProtocol.RunMode.MovingDown, s.Mode);
        Assert.True(s.Unlocked);
        Assert.False(s.Imperial);
        Assert.Equal("mm", s.UnitText);
        Assert.True(s.SoundEnabled);
        Assert.Equal(0, s.ParameterFlag);
        Assert.Equal(3, s.ErrorCode);
        Assert.Equal("저전압 알람", s.ErrorText);
        Assert.False(s.IsHealthy);
        Assert.Equal(742, s.HeightMm);
        Assert.True(s.IsMoving);
    }

    [Fact]
    public void ParseStatus_HealthyStoppedController()
    {
        var s = TelescopicProtocol.ParseStatus("Length:10110000000000");

        Assert.NotNull(s);
        Assert.Equal(TelescopicProtocol.RunMode.Stopped, s!.Mode);
        Assert.Equal("정지", s.ModeText);
        Assert.True(s.IsHealthy);
        Assert.False(s.IsMoving);
        Assert.Equal(0, s.HeightMm);
    }

    // 에러·높이는 뒤에서 센다 — 중간 필드 개수가 달라도 어긋나면 안 된다(펌웨어 변종 내성).
    [Fact]
    public void ParseStatus_ShortPayload_StillReadsErrorAndHeight()
    {
        var s = TelescopicProtocol.ParseStatus("Length:1011090815");

        Assert.NotNull(s);
        Assert.False(s!.ExactLayout);
        Assert.True(s.Active);
        Assert.Equal(TelescopicProtocol.RunMode.Stopped, s.Mode);
        Assert.Equal(9, s.ErrorCode);                 // 뒤에서 6~5번째
        Assert.Equal("홀 A 신호 없음", s.ErrorText);
        Assert.Equal(815, s.HeightMm);                // 마지막 4자
        Assert.Null(s.SoundEnabled);                  // 축약 파싱 — 중간 필드는 신뢰하지 않는다
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Target:0")]
    [InlineData("ClearErr OK")]
    [InlineData("Length:123")]      // 10자 미만 — 앞뒤가 겹쳐 오독 위험이라 거부
    public void ParseStatus_RejectsNonStatus(string? response)
        => Assert.Null(TelescopicProtocol.ParseStatus(response));

    [Fact]
    public void ParseStatus_ToleratesWhitespaceAndCase()
    {
        Assert.NotNull(TelescopicProtocol.ParseStatus("  length:10110000000000\r\n"));
    }

    [Theory]
    [InlineData("Target:0", true)]    // 0 = 실행 가능
    [InlineData("Target:1", false)]   // 1 = 실행 불가(잠김·비활성 등)
    public void ParseTargetAck_ReadsExecutionResult(string response, bool expected)
        => Assert.Equal(expected, TelescopicProtocol.ParseTargetAck(response));

    [Theory]
    [InlineData(null)]
    [InlineData("Length:10110000000000")]
    [InlineData("Target:")]
    [InlineData("Target:X")]
    public void ParseTargetAck_UnknownFormat_IsNull(string? response)
        => Assert.Null(TelescopicProtocol.ParseTargetAck(response));

    [Theory]
    [InlineData("ClearErr OK", true)]
    [InlineData("clearerr ok", true)]
    [InlineData("ClearErr", false)]
    [InlineData(null, false)]
    public void ParseClearErrAck_ChecksOk(string? response, bool expected)
        => Assert.Equal(expected, TelescopicProtocol.ParseClearErrAck(response));

    // 에러 코드는 사양서 3장 16종이 전부 이름을 가져야 한다(운영자가 숫자만 보면 안 된다).
    [Fact]
    public void ErrorName_CoversEveryDocumentedCode()
    {
        for (var code = 0; code <= 16; code++)
            Assert.DoesNotContain("알 수 없는", TelescopicProtocol.ErrorName(code));

        Assert.Contains("알 수 없는", TelescopicProtocol.ErrorName(17));
    }

    [Fact]
    public void ModeName_CoversEveryDocumentedMode()
    {
        foreach (var mode in Enum.GetValues<TelescopicProtocol.RunMode>())
            Assert.DoesNotContain("알 수 없음", TelescopicProtocol.ModeName(mode));
    }

    // 유지 시간은 사양서 값 그대로여야 한다 — 짧으면 메모리 저장이 "이동"으로 오인된다.
    [Fact]
    public void HoldDurations_MatchSpec()
    {
        Assert.Equal(50, TelescopicProtocol.MemoryMoveHoldMs);
        Assert.Equal(2000, TelescopicProtocol.MemorySaveHoldMs);
        Assert.Equal(2000, TelescopicProtocol.ResetHoldMs);
        Assert.Equal(9600, TelescopicProtocol.BaudRate);
        Assert.Equal("\r\n", TelescopicProtocol.Terminator);
    }
}
