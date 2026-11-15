using HD.AMR.App.Data;
using HD.AMR.App.Data.Entities;
using HD.AMR.App.Models;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Sequence;
using HD.AMR.App.Service.Sequence.Steps;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HD.AMR.Tests;

/// <summary>레이저 WD 면별 파라미터(…WorkingDistanceMm.{wall_code}) 조회와 진단 로그 한 줄.</summary>
public class LaserWorkingDistanceSettingTests : IDisposable
{
    private const string Common = LaserWorkingDistanceStep.WorkingDistanceKey;

    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly HdAmrDbContext _db;
    private readonly ParameterService _param;

    public LaserWorkingDistanceSettingTests()
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

    [Fact]
    public async Task 면별_값이_있으면_그_값_없으면_공통_값()
    {
        Add(Common, "323");
        Add(Common + ".T", "318");
        Add(Common + ".PU", "326.5");

        Assert.Equal((318, Common + ".T"), await LaserWorkingDistanceSetting.ResolveAsync(_param, "T"));
        Assert.Equal((326.5, Common + ".PU"), await LaserWorkingDistanceSetting.ResolveAsync(_param, "PU"));
        Assert.Equal((323, Common), await LaserWorkingDistanceSetting.ResolveAsync(_param, "SU"));   // 면별 행 없음
        Assert.Equal((323, Common), await LaserWorkingDistanceSetting.ResolveAsync(_param, null));   // 면 모름
    }

    [Fact]
    public async Task 소문자_면_코드도_같은_키를_찾고_모르는_면은_공통_값()
    {
        Add(Common, "323");
        Add(Common + ".PU", "326");
        Add(Common + ".XX", "111");   // 10면이 아닌 이름의 행은 읽지 않는다

        Assert.Equal((326, Common + ".PU"), await LaserWorkingDistanceSetting.ResolveAsync(_param, " pu "));
        Assert.Equal((323, Common), await LaserWorkingDistanceSetting.ResolveAsync(_param, "XX"));
        Assert.Null(LaserWorkingDistanceSetting.KeyFor("XX"));
    }

    [Fact]
    public async Task 면별_값이_숫자가_아니면_공통_값_둘_다_없으면_기본값()
    {
        Add(Common + ".T", "abc");
        Assert.Equal((LaserWorkingDistanceStep.DefaultWorkingDistanceMm, "기본값"),
            await LaserWorkingDistanceSetting.ResolveAsync(_param, "T"));

        Add(Common, "323");
        Assert.Equal((323, Common), await LaserWorkingDistanceSetting.ResolveAsync(_param, "T"));
    }

    [Fact]
    public void 시퀀스_면은_wall_code_우선_없으면_Surface_ID()
    {
        Assert.Equal("SU", LaserWorkingDistanceSetting.WallOf(new SequenceContext { WallCode = "SU", InspectionSurfaceId = 0x02 }));
        Assert.Equal("T", LaserWorkingDistanceSetting.WallOf(new SequenceContext { InspectionSurfaceId = 0x02 }));
        Assert.Equal("PU", LaserWorkingDistanceSetting.WallOf(new SequenceContext { InspectionSurfaceId = 0x09 }));
    }

    private static LaserChannelReading Ch(int n, double mm, bool enabled = true)
        => new(n, 0, mm, enabled, false, false, false, false, false, false);

    private static PlanePose Plane(double z, double rx, double ry)
        => new(0, 0, z, rx, ry, 0, new double[] { 0, 0, 1 }, true, null);

    [Fact]
    public void 진단_줄에_면_목표_출처_채널별_거리_광축_앙각이_들어간다()
    {
        // 툴 z 가 BASE +z 를 보는 자세(천장) — 앙각 +90°, 중력은 툴 −z.
        var line = LaserWorkingDistanceSetting.Diagnostics(
            "완료(횡복귀 후)", "T", InspectionMoveDirection.Vertical, 318, Common + ".T",
            new[] { Ch(1, 317.20), Ch(2, 318.90), Ch(3, 318.10) },
            raw: Plane(318.07, 0.412, -0.250), corrected: Plane(318.07, 0.012, -0.050),
            jointsDeg: new[] { 0.2, 59.5, -140.9, 80.8, 90.0, -179.7 },
            tcpBase: new[] { -1000.0, -150, 900, 0, 0, 0 }, ToolAxisDir.PlusZ);

        Assert.StartsWith("[WD 진단] 단계=완료(횡복귀 후) 면=T 방향=Vertical", line);
        Assert.Contains($"목표=318mm({Common}.T)", line);
        Assert.Contains("CH1=317.20 CH2=318.90 CH3=318.10 최대−최소=1.70", line);
        Assert.Contains("z=318.07 잔차=+0.07", line);
        Assert.Contains("Rx=+0.412/+0.012 Ry=-0.250/-0.050", line);
        Assert.Contains("광축 앙각=+90.0°", line);
        Assert.Contains("중력(툴)=(+0.000,+0.000,-1.000)", line);
        Assert.Contains("관절=[0.2, 59.5, -140.9, 80.8, 90.0, -179.7]", line);
    }

    [Fact]
    public void 진단_줄_광축이_수평이면_앙각_0_이고_중력은_툴_가로축에_실린다()
    {
        // ry = 90° — 툴 z 가 BASE +x(수평), 툴 x 가 BASE −z(아래).
        var line = LaserWorkingDistanceSetting.Diagnostics(
            "이동 전", "PM", InspectionMoveDirection.Horizontal, 323, Common,
            new[] { Ch(1, 330), Ch(2, 331), Ch(3, 0, enabled: false) },
            raw: PlanePose.Invalid("3채널 모두 측정중이어야 합니다."), corrected: PlanePose.Invalid("3채널 모두 측정중이어야 합니다."),
            jointsDeg: null, tcpBase: new[] { 0.0, 0, 0, 0, 90, 0 }, ToolAxisDir.PlusZ);

        Assert.Contains("CH3=0.00(범위 밖) 최대−최소=1.00", line);
        Assert.Contains("z=- (3채널 모두 측정중이어야 합니다.)", line);
        Assert.Contains("광축 앙각=+0.0°", line);
        Assert.Contains("중력(툴)=(+1.000,+0.000,+0.000)", line);
        Assert.Contains("관절=-", line);
    }
}
