using HD.AMR.App.Data;
using HD.AMR.App.Data.Entities;
using HD.AMR.App.Service;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HD.AMR.Tests;

/// <summary>이름이 바뀐 파라미터 키(Weld.Dl.* → Weld.Yolo.*) 기동 이전.</summary>
public class ParameterRenameTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly HdAmrDbContext _db;

    public ParameterRenameTests()
    {
        _conn.Open();
        _db = new HdAmrDbContext(new DbContextOptionsBuilder<HdAmrDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    private void Add(string name, string value)
    {
        _db.Parameters.Add(new Parameter { Name = name, Value = value, Description = "d", UpdatedAt = DateTime.UtcNow });
        _db.SaveChanges();
    }

    [Fact]
    public async Task 옛_이름은_값을_유지한_채_새_이름으로()
    {
        Add("Weld.Dl.ModelPath", "/m/weld_yolo_rgb_area_261108_0447.onnx");
        Add("Weld.Dl.Confidence", "0.15");
        var svc = new ParameterService(_db);

        var done = await svc.RenameLegacyAsync();

        Assert.Equal(2, done.Count);
        Assert.Equal("/m/weld_yolo_rgb_area_261108_0447.onnx", await svc.GetAsync("Weld.Yolo.ModelPath"));
        Assert.Equal(0.15, await svc.GetDoubleAsync("Weld.Yolo.Confidence"));
        Assert.Null(await svc.GetAsync("Weld.Dl.ModelPath"));
        Assert.Null(await svc.GetAsync("Weld.Dl.Confidence"));
    }

    [Fact]
    public async Task 새_이름이_이미_있으면_새_값을_두고_옛_행만_지운다()
    {
        Add("Weld.Dl.MaskThreshold", "0.4");
        Add("Weld.Yolo.MaskThreshold", "0.6");
        var svc = new ParameterService(_db);

        await svc.RenameLegacyAsync();

        Assert.Equal(0.6, await svc.GetDoubleAsync("Weld.Yolo.MaskThreshold"));
        Assert.Null(await svc.GetAsync("Weld.Dl.MaskThreshold"));
    }

    [Fact]
    public async Task 옛_이름이_없으면_아무것도_안_한다()
    {
        var svc = new ParameterService(_db);
        Assert.Empty(await svc.RenameLegacyAsync());
        Assert.Empty(await svc.RenameLegacyAsync());   // 멱등
    }
}
