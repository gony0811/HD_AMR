using HD.AMR.App.Data;
using HD.AMR.App.Service;
using Microsoft.Extensions.DependencyInjection;

namespace HD.AMR.Desktop.Infrastructure;

/// <summary>
/// 기동 시 1회 실행하는 DB 초기화. 기존 HD.AMR.Web/Program.cs 의 초기화 블록에 대응한다.
///
/// 웹에서 만든 레거시 DB 를 가져와 쓸 수 있으므로(LegacyDatabaseImporter), EnsureCreated 만으로는
/// 부족하다 — 비어있지 않은 DB 에서 EnsureCreated 는 no-op 이라 누락 컬럼을 보충하지 않는다.
/// 웹과 공유하는 후방호환 스키마 패치(SqliteCompatMigrations)를 함께 실행한다.
/// TODO(점진 포팅): Teaching/Drawing 등 DB 백엔드 페이지 포팅 시 필요한 시드가 있으면 여기에 추가.
/// </summary>
internal static class DatabaseInitializer
{
    public static void Initialize(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;

        var db = sp.GetRequiredService<HdAmrDbContext>();
        db.Database.EnsureCreated();
        SqliteCompatMigrations.Apply(db);

        // 검사 레시피 17종 카탈로그 시드 — 없는 행만 추가(현장 조정값 보존).
        sp.GetRequiredService<InspectionRecipeService>()
            .SeedDefaultsAsync().GetAwaiter().GetResult();
    }
}
