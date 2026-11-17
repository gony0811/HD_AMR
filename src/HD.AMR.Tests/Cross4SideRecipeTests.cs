using HD.AMR.App.Data;
using HD.AMR.App.Data.Entities;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Inspection;
using HD.AMR.App.Service.Sequence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace HD.AMR.Tests;

/// <summary>
/// 십자 4갈래를 수직 용접선 좌/우 두 타입(CROSS4_L/CROSS4_R)으로 나눈 사양 — ACS seamType 을 그대로 따르고,
/// 면별로 다른 프로필을 지정할 수 있으며, 구 CROSS4 레시피의 현장 설정은 양쪽으로 옮겨진다.
/// </summary>
public class Cross4SideRecipeTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly HdAmrDbContext _db;

    public Cross4SideRecipeTests()
    {
        _conn.Open();
        _db = new HdAmrDbContext(new DbContextOptionsBuilder<HdAmrDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    private InspectionRecipeService Service() => new(_db, NullLogger<InspectionRecipeService>.Instance);

    private int AddProfile(string name, string seamType = "CROSS", int waypoints = 2)
    {
        var p = new InspectionProfile
        {
            Name = name, SeamType = seamType,
            WaypointsJson = "[" + string.Join(",", Enumerable.Repeat("{\"X\":0,\"Y\":0,\"Theta\":0}", waypoints)) + "]",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _db.InspectionProfiles.Add(p);
        _db.SaveChanges();
        return p.Id;
    }

    // ── 레시피 선택: ACS 가 보낸 좌/우를 그대로 따른다 ────────────────

    [Theory]
    [InlineData(SeamTypeKind.Cross4L, "CROSS4_L")]
    [InlineData(SeamTypeKind.Cross4R, "CROSS4_R")]
    [InlineData(SeamTypeKind.Cross, "CROSS4_L")]   // 좌/우 미지정 "CROSS4" — 면별 기본값 설정이 없으면 좌측
    public void 좌우_유형은_면과_무관하게_각자의_레시피로_간다(SeamTypeKind seam, string expected)
    {
        foreach (var wall in HD.AMR.App.Models.WallCodes.All)
            Assert.Equal(expected, InspectionRecipeResolver.ResolveRecipeId(seam, wall.Code));
    }

    [Fact]
    public void 좌우_미지정_CROSS4_는_면별_기본값으로_고르고_좌우를_밝힌_지령은_그대로_따른다()
    {
        var recipes = new[]
        {
            new InspectionRecipe { Id = RecipeIds.Cross4L },
            new InspectionRecipe { Id = RecipeIds.Cross4R, UnspecifiedSideWalls = "SM, SL,XX" },   // 정본 코드가 아닌 XX 는 버린다
        };
        var right = InspectionRecipeResolver.Cross4RightWalls(recipes);
        Assert.Equal(new[] { "SL", "SM" }, right.OrderBy(w => w));

        Assert.Equal("CROSS4_R", InspectionRecipeResolver.ResolveRecipeId(SeamTypeKind.Cross, "SM", right));
        Assert.Equal("CROSS4_R", InspectionRecipeResolver.ResolveRecipeId(SeamTypeKind.Cross, "SL", right));
        Assert.Equal("CROSS4_L", InspectionRecipeResolver.ResolveRecipeId(SeamTypeKind.Cross, "PM", right));
        // ACS 가 좌/우를 밝혔으면 면별 기본값과 반대여도 그대로
        Assert.Equal("CROSS4_L", InspectionRecipeResolver.ResolveRecipeId(SeamTypeKind.Cross4L, "SM", right));
        Assert.Equal("CROSS4_R", InspectionRecipeResolver.ResolveRecipeId(SeamTypeKind.Cross4R, "PM", right));
        Assert.Null(InspectionRecipeResolver.ResolveRecipeId(SeamTypeKind.Cross, "W03", right));

        Assert.Equal("SM,SL", InspectionRecipeResolver.SerializeWallList(new[] { "SL", "SM" }));   // 정본 표 순
        Assert.Null(InspectionRecipeResolver.SerializeWallList(Array.Empty<string>()));
        Assert.Empty(InspectionRecipeResolver.Cross4RightWalls(Array.Empty<InspectionRecipe>()));
    }

    // ── 프로필 선택: 면별 지정이 있으면 그것, 없으면 기본 ─────────────

    [Fact]
    public void 면별로_지정한_프로필이_실행_경로에서_선택된다()
    {
        var left = new InspectionRecipe { Id = RecipeIds.Cross4L, InspectionProfileId = 9, WallProfilesJson = "{\"SM\":12,\"T\":13}" };
        var right = new InspectionRecipe { Id = RecipeIds.Cross4R, InspectionProfileId = 21, WallProfilesJson = "{\"SM\":22}" };

        int? Pick(InspectionRecipe r, string wall, InspectionMoveDirection dir = InspectionMoveDirection.Horizontal)
            => InspectionRecipeResolver.SelectProfileId(r, wall, dir).ProfileId;

        Assert.Equal(12, Pick(left, "SM"));
        Assert.Equal(13, Pick(left, "T"));
        Assert.Equal(9, Pick(left, "PM"));                                    // 지정 없는 면은 기본 프로필
        Assert.Equal(12, Pick(left, "SM", InspectionMoveDirection.Vertical)); // 방향과 무관
        Assert.Equal(22, Pick(right, "SM"));                                  // 같은 면이라도 좌/우 레시피가 따로
        Assert.Equal(21, Pick(right, "T"));
    }

    // 벽면용과 챔퍼용을 따로 — 기본 프로필을 벽면용으로 두고 챔퍼 면(SL/PL/SU/PU)에 챔퍼용을 지정한다.
    // LINE 의 챔퍼 수직 전용 프로필과 달리 방향(H/V)과 무관하게 면으로만 고른다.
    [Fact]
    public void 벽면용과_챔퍼용_프로필을_따로_지정하면_면에_따라_선택되고_지정표에도_실린다()
    {
        const int wallProfile = 9, chamferProfile = 30;
        var chamferWalls = "{\"SL\":30,\"PL\":30,\"SU\":30,\"PU\":30}";
        var left = new InspectionRecipe { Id = RecipeIds.Cross4L, InspectionProfileId = wallProfile, WallProfilesJson = chamferWalls };
        var right = new InspectionRecipe { Id = RecipeIds.Cross4R, InspectionProfileId = 21, WallProfilesJson = "{\"SL\":31,\"PL\":31,\"SU\":31,\"PU\":31}" };

        foreach (var dir in new[] { InspectionMoveDirection.Horizontal, InspectionMoveDirection.Vertical })
        {
            foreach (var wall in new[] { "SL", "PL", "SU", "PU" })
            {
                Assert.Equal((chamferProfile, false), InspectionRecipeResolver.SelectProfileId(left, wall, dir));
                Assert.Equal(31, InspectionRecipeResolver.SelectProfileId(right, wall, dir).ProfileId);
            }
            foreach (var wall in new[] { "SM", "PM", "F", "A", "B", "T" })
            {
                Assert.Equal(wallProfile, InspectionRecipeResolver.SelectProfileId(left, wall, dir).ProfileId);
                Assert.Equal(21, InspectionRecipeResolver.SelectProfileId(right, wall, dir).ProfileId);
            }
        }

        var table = AcsProfilePayload.BuildAssignments(new[] { left, right });
        Assert.All(table.Where(a => a.SeamType == "CROSS4_L"),
            a => Assert.Equal(a.WallCode is "SL" or "PL" or "SU" or "PU" ? chamferProfile : wallProfile, a.ProfileId));
        Assert.All(table.Where(a => a.SeamType == "CROSS4_R"),
            a => Assert.Equal(a.WallCode is "SL" or "PL" or "SU" or "PU" ? 31 : 21, a.ProfileId));
    }

    [Fact]
    public void 면별_지정은_십자_레시피에서만_읽는다()
    {
        var line = new InspectionRecipe { Id = RecipeIds.Line, InspectionProfileId = 18, WallProfilesJson = "{\"SM\":12}" };
        Assert.Equal(18, InspectionRecipeResolver.SelectProfileId(line, "SM", InspectionMoveDirection.Horizontal).ProfileId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"XX\":5,\"SM\":\"12\"}")]   // 정본 10코드가 아닌 면·숫자가 아닌 값은 버린다
    public void 면별_지정이_비었거나_깨졌으면_기본_프로필을_쓴다(string? json)
    {
        Assert.Empty(InspectionRecipeResolver.ParseWallProfiles(json));
        var recipe = new InspectionRecipe { Id = RecipeIds.Cross4L, InspectionProfileId = 9, WallProfilesJson = json };
        Assert.Equal(9, InspectionRecipeResolver.SelectProfileId(recipe, "SM", InspectionMoveDirection.Horizontal).ProfileId);
    }

    [Fact]
    public void 면별_지정_저장값은_정본_면_순서이고_비면_null()
    {
        var json = InspectionRecipeResolver.SerializeWallProfiles(new Dictionary<string, int> { ["SM"] = 12, ["B"] = 7 });
        Assert.Equal(new Dictionary<string, int> { ["B"] = 7, ["SM"] = 12 }, InspectionRecipeResolver.ParseWallProfiles(json));
        Assert.True(json!.IndexOf("\"B\"", StringComparison.Ordinal) < json.IndexOf("\"SM\"", StringComparison.Ordinal));
        Assert.Null(InspectionRecipeResolver.SerializeWallProfiles(new Dictionary<string, int>()));
    }

    // ── ACS 지정표 ────────────────────────────────────────────────

    [Fact]
    public void ACS_지정표에_좌우_유형과_면별_프로필이_실린다()
    {
        var table = AcsProfilePayload.BuildAssignments(new[]
        {
            new InspectionRecipe { Id = RecipeIds.Cross4L, InspectionProfileId = 9, WallProfilesJson = "{\"SM\":12}" },
            new InspectionRecipe { Id = RecipeIds.Cross4R, InspectionProfileId = 21, UnspecifiedSideWalls = "F" },
        });
        int? Of(string type, string wall, string dir) =>
            table.SingleOrDefault(a => a.SeamType == type && a.WallCode == wall && a.Direction == dir)?.ProfileId;

        Assert.Equal(9, Of("CROSS4_L", "PM", "H"));
        Assert.Equal(12, Of("CROSS4_L", "SM", "H"));
        Assert.Equal(12, Of("CROSS4_L", "SM", "V"));
        Assert.Equal(21, Of("CROSS4_R", "SM", "H"));
        // 좌/우 미지정 "CROSS4" 는 실행 때와 같은 면별 기본값으로 실린다 — F 면만 우측, 나머지는 좌측
        Assert.Equal(9, Of("CROSS4", "PM", "H"));
        Assert.Equal(12, Of("CROSS4", "SM", "H"));
        Assert.Equal(21, Of("CROSS4", "F", "H"));
        Assert.Equal(3 * 20, table.Count);
    }

    // ── 시드·마이그레이션 ─────────────────────────────────────────

    [Fact]
    public async Task 새_DB_는_좌우_두_레시피로_시드되고_구_CROSS4_는_없다()
    {
        SqliteCompatMigrations.Apply(_db);
        await Service().SeedDefaultsAsync();

        var ids = _db.InspectionRecipes.AsNoTracking().Select(r => r.Id).ToList();
        Assert.Contains("CROSS4_L", ids);
        Assert.Contains("CROSS4_R", ids);
        Assert.DoesNotContain("CROSS4", ids);
        Assert.Equal(RecipeIds.All.OrderBy(i => i), ids.OrderBy(i => i));
        Assert.Equal(SeamTypeKind.Cross4R, _db.InspectionRecipes.AsNoTracking().Single(r => r.Id == "CROSS4_R").SeamType);
    }

    [Fact]
    public async Task 구_CROSS4_의_현장_설정은_좌우_양쪽으로_옮겨진다()
    {
        // 운영 DB 상태: CROSS4 에 프로필 9 지정·Enabled·속도 조정
        var profileId = AddProfile("CROSS-4-V1");
        _db.InspectionRecipes.Add(new InspectionRecipe
        {
            Id = "CROSS4", DisplayName = "4점 십자", SeamType = SeamTypeKind.Cross, Enabled = true,
            InspectionProfileId = profileId, MoveVelPercent = 30, InspectVelPercent = 10, CameraTargetDistanceMm = 380,
            StepKeysJson = "[\"a\",\"b\"]", VisionFailRatioMax = 0.5,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();

        SqliteCompatMigrations.Apply(_db);
        SqliteCompatMigrations.Apply(_db);            // 멱등
        await Service().SeedDefaultsAsync();          // 시드는 옮겨진 행을 건드리지 않는다
        _db.ChangeTracker.Clear();

        Assert.False(_db.InspectionRecipes.Any(r => r.Id == "CROSS4"));
        foreach (var (id, seam) in new[] { ("CROSS4_L", SeamTypeKind.Cross4L), ("CROSS4_R", SeamTypeKind.Cross4R) })
        {
            var r = _db.InspectionRecipes.AsNoTracking().Single(x => x.Id == id);
            Assert.Equal(seam, r.SeamType);
            Assert.True(r.Enabled);
            Assert.Equal(profileId, r.InspectionProfileId);
            Assert.Equal(30, r.MoveVelPercent);
            Assert.Equal(10, r.InspectVelPercent);
            Assert.Equal(380, r.CameraTargetDistanceMm);
            Assert.Equal("[\"a\",\"b\"]", r.StepKeysJson);
            Assert.Equal(0.5, r.VisionFailRatioMax);
            Assert.Null(r.WallProfilesJson);
            Assert.Null(r.UnspecifiedSideWalls);
        }
    }

    [Fact]
    public void 이미_나뉜_뒤의_현장_수정값은_다시_덮어쓰지_않는다()
    {
        SqliteCompatMigrations.Apply(_db);
        _db.InspectionRecipes.Add(new InspectionRecipe
        {
            Id = "CROSS4_R", DisplayName = "우", SeamType = SeamTypeKind.Cross4R, InspectionProfileId = 77,
            WallProfilesJson = "{\"SM\":78}", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();

        SqliteCompatMigrations.Apply(_db);
        _db.ChangeTracker.Clear();

        var r = _db.InspectionRecipes.AsNoTracking().Single(x => x.Id == "CROSS4_R");
        Assert.Equal(77, r.InspectionProfileId);
        Assert.Equal("{\"SM\":78}", r.WallProfilesJson);
    }

    // ── 저장 검증·기본값 재적용·프로필 삭제 ───────────────────────

    [Fact]
    public async Task 면별_프로필도_타입과_경유점_수를_검사한다()
    {
        var cross = AddProfile("cross-ok");
        var line = AddProfile("line", "LINE");
        var onePoint = AddProfile("cross-1pt", waypoints: 1);
        var svc = Service();

        InspectionRecipe R(string id, string? walls) => new() { Id = id, InspectionProfileId = cross, WallProfilesJson = walls };

        Assert.Null(await svc.ValidateProfileAssignmentAsync(R(RecipeIds.Cross4R, $"{{\"SM\":{cross}}}")));
        Assert.Contains("SM 면용", await svc.ValidateProfileAssignmentAsync(R(RecipeIds.Cross4R, $"{{\"SM\":{line}}}")));
        Assert.Contains("2점 미만", await svc.ValidateProfileAssignmentAsync(R(RecipeIds.Cross4L, $"{{\"T\":{onePoint}}}")));
        Assert.Contains("찾을 수 없습니다", await svc.ValidateProfileAssignmentAsync(R(RecipeIds.Cross4L, "{\"T\":9999}")));
        Assert.Contains("CROSS4_R 레시피에만",
            await svc.ValidateProfileAssignmentAsync(new InspectionRecipe { Id = RecipeIds.Cross4L, InspectionProfileId = cross, UnspecifiedSideWalls = "SM" }));
        Assert.Null(await svc.ValidateProfileAssignmentAsync(new InspectionRecipe { Id = RecipeIds.Cross4R, InspectionProfileId = cross, UnspecifiedSideWalls = "SM" }));
        Assert.Contains("CROSS4_L/CROSS4_R 레시피에만",
            await svc.ValidateProfileAssignmentAsync(new InspectionRecipe { Id = RecipeIds.Cross3R0, WallProfilesJson = $"{{\"SM\":{cross}}}" }));
    }

    [Fact]
    public async Task 기본값_재적용은_면별_지정을_보존한다()
    {
        SqliteCompatMigrations.Apply(_db);
        var svc = Service();
        await svc.SeedDefaultsAsync();
        var seeded = (await svc.GetAsync(RecipeIds.Cross4R))!;
        seeded.Enabled = true;
        seeded.InspectionProfileId = 21;
        seeded.WallProfilesJson = "{\"SM\":22}";
        seeded.UnspecifiedSideWalls = "SM,SL";
        await svc.SaveAsync(seeded);
        _db.ChangeTracker.Clear();

        Assert.True(await svc.ResetToDefaultAsync(RecipeIds.Cross4R));
        _db.ChangeTracker.Clear();

        var r = (await svc.GetAsync(RecipeIds.Cross4R))!;
        Assert.False(r.Enabled);                       // 코드 기본값으로
        Assert.Equal(21, r.InspectionProfileId);       // 현장 지정은 그대로
        Assert.Equal("{\"SM\":22}", r.WallProfilesJson);
        Assert.Equal("SM,SL", r.UnspecifiedSideWalls);
    }
}
