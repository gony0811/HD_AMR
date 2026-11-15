using System.Text.Json;
using HD.AMR.App.Data;
using HD.AMR.App.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HD.AMR.Tests;

/// <summary>SurfaceType 번호 체계 변경 — 기존 프로파일 WaypointsJson 1회 변환(멱등).</summary>
public class SurfaceTypeMigrationDbTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly HdAmrDbContext _db;

    public SurfaceTypeMigrationDbTests()
    {
        _conn.Open();
        _db = new HdAmrDbContext(new DbContextOptionsBuilder<HdAmrDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    private int AddProfile(params byte[] surfaces)
    {
        var wps = surfaces.Select((s, i) => new InspectionWaypoint(i, 0, 0, false, Surface: s, SurfaceManual: true)).ToList();
        var p = new InspectionProfile
        {
            Name = "p", WaypointsJson = JsonSerializer.Serialize(wps),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _db.InspectionProfiles.Add(p);
        _db.SaveChanges();
        return p.Id;
    }

    private byte[] Surfaces(int id)
    {
        _db.ChangeTracker.Clear();
        var json = _db.InspectionProfiles.AsNoTracking().Single(p => p.Id == id).WaypointsJson;
        return JsonSerializer.Deserialize<List<InspectionWaypoint>>(json)!.Select(w => w.Surface).ToArray();
    }

    [Fact]
    public void 구번호_프로파일을_한번만_변환()
    {
        var id = AddProfile(0, 1, 2);

        SqliteCompatMigrations.Apply(_db);
        Assert.Equal(new byte[] { 0, 0, 1 }, Surfaces(id));
        Assert.True(_db.Parameters.Any(p => p.Name == SqliteCompatMigrations.SurfaceTypeV2Marker));

        // 재실행해도 다시 변환하지 않는다(신 번호 1=Corrugation 이 0 으로 깎이면 안 됨).
        SqliteCompatMigrations.Apply(_db);
        Assert.Equal(new byte[] { 0, 0, 1 }, Surfaces(id));
    }

    [Fact]
    public void 표식_이후_저장된_신번호는_보존()
    {
        SqliteCompatMigrations.Apply(_db);
        var id = AddProfile(2, 5, 8);

        SqliteCompatMigrations.Apply(_db);
        Assert.Equal(new byte[] { 2, 5, 8 }, Surfaces(id));
    }
}
