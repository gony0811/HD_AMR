using HD.AMR.App.Data;
using HD.AMR.App.Data.Entities;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Inspection;
using HD.AMR.App.Service.Sequence.Steps;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HD.AMR.Tests;

/// <summary>
/// 작업물 좌표계 점1 기준 높이(Sequence.WObj.Point1HeightMm) — 점1 을 평탄부 초점 높이(레이저 WD)에서 기록하면 0 이어야
/// 순회 높이가 맞는다(33.8 이면 전체가 면 쪽으로 33.8mm 들어간다, 실기 2026-11-11 T 면).
/// </summary>
public class WObjPoint1HeightTests : IDisposable
{
    private const string Key = WObjRegisterStep.Point1HeightKey;

    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly HdAmrDbContext _db;
    private readonly ParameterService _param;

    public WObjPoint1HeightTests()
    {
        _conn.Open();
        _db = new HdAmrDbContext(new DbContextOptionsBuilder<HdAmrDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
        _param = new ParameterService(_db);
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    private void Add(string name, string value)
    {
        _db.Parameters.Add(new Parameter { Name = name, Value = value, Description = "d", UpdatedAt = DateTime.UtcNow });
        _db.SaveChanges();
    }

    // 천장을 보는 자세 — 툴 +Z 가 BASE −z(면에서 멀어지는 쪽). 실기 T 면 점1(z 1217.3)·점2 와 같은 꼴.
    private static readonly double[] P1 = { -832.5, 1233.9, 1217.3, 180, 0, 90 };
    private static readonly double[] P2 = { -832.5, 876.0, 1217.3, 180, 0, 90 };

    private static double TcpZ(double[] frame, double profileZ)
        => InternalWorkpieceFrame.ToBasePose(new[] { 0.0, 0, profileZ, 0, 0, 0 }, frame)[2];

    [Fact]
    public void 기본_33_8_이면_평탄_구간이_점1_보다_33_8mm_면_쪽이다_종전_동작()
    {
        var frame = InternalWorkpieceFrame.Compute(P1, P2);

        Assert.Equal(1217.3 + 33.8, frame[2], 6);          // 원점이 천장 쪽으로 33.8
        Assert.Equal(1217.3, TcpZ(frame, 33.8), 6);        // 꼭대기 경유점 = 점1 높이
        Assert.Equal(1217.3 + 33.8, TcpZ(frame, 0), 6);    // 평탄 경유점 = 점1 보다 33.8 안쪽
    }

    [Fact]
    public void 값이_0_이면_평탄_구간은_점1_높이_꼭대기는_33_8mm_물러난다()
    {
        var frame = InternalWorkpieceFrame.Compute(P1, P2, 0);

        Assert.Equal(1217.3, frame[2], 6);                 // 원점 = 점1(WD 로 맞춘 평탄부 초점 높이)
        Assert.Equal(1217.3, TcpZ(frame, 0), 6);           // 평탄 경유점 = WD 높이
        Assert.Equal(1217.3 - 33.8, TcpZ(frame, 33.8), 6); // 꼭대기 경유점 = 면에서 33.8 멀어짐(Z+)
    }

    [Fact]
    public async Task 면별_값_공통_값_기본값_순으로_찾는다()
    {
        Assert.Equal((33.8, "기본값"), await WObjRegisterStep.ResolvePoint1HeightAsync(_param, "T"));

        Add(Key, "33.8");
        Add(Key + ".T", "0");
        Assert.Equal((0, Key + ".T"), await WObjRegisterStep.ResolvePoint1HeightAsync(_param, "T"));
        Assert.Equal((0, Key + ".T"), await WObjRegisterStep.ResolvePoint1HeightAsync(_param, " t "));
        Assert.Equal((33.8, Key), await WObjRegisterStep.ResolvePoint1HeightAsync(_param, "PU"));
        Assert.Equal((33.8, Key), await WObjRegisterStep.ResolvePoint1HeightAsync(_param, null));
    }

    [Fact]
    public async Task 기동_시드는_종전_값_33_8_을_넣어_배포만으로는_동작이_바뀌지_않는다()
    {
        await _param.SeedDefaultsAsync();

        Assert.Equal(33.8, await _param.GetDoubleAsync(Key));
        Assert.Equal((33.8, Key), await WObjRegisterStep.ResolvePoint1HeightAsync(_param, "T"));
    }
}
