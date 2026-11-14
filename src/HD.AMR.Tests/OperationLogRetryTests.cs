using HD.AMR.App.Service;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HD.AMR.Tests;

/// <summary>운영 로그 DB 쓰기 — 잠김(database is locked)이면 버리지 않고 다시 시도한다.</summary>
public class OperationLogRetryTests
{
    [Theory]
    [InlineData(5, true)]    // SQLITE_BUSY — 현장 2026-11-14 'database is locked'
    [InlineData(6, true)]    // SQLITE_LOCKED
    [InlineData(19, false)]  // 제약 위반 — 다시 써도 같다
    [InlineData(1, false)]
    public void IsDbLocked_OnlyBusyOrLocked(int code, bool expected)
    {
        var sqlite = new SqliteException("x", code);
        Assert.Equal(expected, OperationLogService.IsDbLocked(sqlite));
        // EF 가 감싼 형태(DbUpdateException → SqliteException)도 같게 본다
        Assert.Equal(expected, OperationLogService.IsDbLocked(new DbUpdateException("save", sqlite)));
    }

    [Fact]
    public void IsDbLocked_OtherExceptions_False()
    {
        Assert.False(OperationLogService.IsDbLocked(new InvalidOperationException("x")));
        Assert.False(OperationLogService.IsDbLocked(null));
    }

    [Fact]
    public void RetryDelay_DoublesAndStaysBounded()
    {
        Assert.Equal(new[] { 200, 400, 800, 1600, 3200 },
            Enumerable.Range(1, 5).Select(OperationLogService.RetryDelayMs).ToArray());
        Assert.Equal(3200, OperationLogService.RetryDelayMs(50));
        Assert.True(Enumerable.Range(1, OperationLogService.MaxWriteAttempts - 1).Sum(OperationLogService.RetryDelayMs) <= 7000);
    }
}
