using HD.AMR.App.Data;
using HD.AMR.App.Data.Entities;
using HD.AMR.App.Models;
using HD.AMR.App.Service;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HD.AMR.Tests;

/// <summary>리프트 모드(SHORT/LONG)별 티칭 세트 — 기존 DB 티칭은 SHORT, LONG 은 별도 슬롯.</summary>
public class TeachingLiftModeTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly HdAmrDbContext _db;

    public TeachingLiftModeTests()
    {
        _conn.Open();
        _db = new HdAmrDbContext(new DbContextOptionsBuilder<HdAmrDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    private TeachingService Svc() => new(_db, new ParameterService(_db));

    [Fact]
    public async Task 구_스키마_티칭은_SHORT_로_이관되고_LONG_은_빈_슬롯()
    {
        // 구 스키마 재현: LiftMode 컬럼·복합 인덱스 없이 Key 단독 unique.
        _db.Database.ExecuteSqlRaw(@"
DROP TABLE TeachingPositions;
CREATE TABLE TeachingPositions (
    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, ""Key"" TEXT NOT NULL, Name TEXT NOT NULL,
    SortOrder INTEGER NOT NULL DEFAULT 0,
    X REAL NULL, Y REAL NULL, Z REAL NULL, Rx REAL NULL, Ry REAL NULL, Rz REAL NULL,
    J1 REAL NULL, J2 REAL NULL, J3 REAL NULL, J4 REAL NULL, J5 REAL NULL, J6 REAL NULL,
    Tool INTEGER NOT NULL DEFAULT 1, CapturedAt TEXT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL);
CREATE UNIQUE INDEX IX_TeachingPositions_Key ON TeachingPositions (""Key"");
INSERT INTO TeachingPositions (""Key"", Name, X, CreatedAt, UpdatedAt) VALUES ('home', '홈 위치', 123.0, '2026-01-01', '2026-01-01');
");
        SqliteCompatMigrations.Apply(_db);
        var svc = Svc();

        var shortList = await svc.ListAsync(TeachingLiftMode.Short);
        var longList = await svc.ListAsync(TeachingLiftMode.Long);

        Assert.Equal(123.0, shortList.Single(p => p.Key == "home").X);
        Assert.Null(longList.Single(p => p.Key == "home").X);
        Assert.Equal(TeachingService.Slots.Length, longList.Count);
        Assert.All(longList, p => Assert.Equal(TeachingLiftMode.Long, p.LiftMode));
    }

    [Fact]
    public async Task 활성_모드_전환이_기본_조회와_추가에_적용된다()
    {
        var svc = Svc();
        Assert.Equal(TeachingLiftMode.Short, await svc.GetActiveLiftModeAsync());

        await svc.SetActiveLiftModeAsync(TeachingLiftMode.Long);
        var added = await svc.AddAsync("롱 전용", 0x20);
        var longHome = (await svc.ListAsync()).Single(p => p.Key == "home");
        await svc.SaveCaptureAsync(longHome.Id, [1, 2, 3, 4, 5, 6], [1, 2, 3, 4, 5, 6], 1);

        Assert.Equal(TeachingLiftMode.Long, added.LiftMode);
        Assert.Contains(await svc.ListAsync(), p => p.Id == added.Id);
        Assert.DoesNotContain(await svc.ListAsync(TeachingLiftMode.Short), p => p.Id == added.Id);
        Assert.Null((await svc.ListAsync(TeachingLiftMode.Short)).Single(p => p.Key == "home").X);
        Assert.Equal(1, (await svc.ListAsync()).Single(p => p.Key == "home").X);
    }
}
