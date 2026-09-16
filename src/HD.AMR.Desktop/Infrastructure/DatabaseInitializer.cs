using HD.AMR.App.Data;
using HD.AMR.App.Service;
using Microsoft.Extensions.DependencyInjection;

namespace HD.AMR.Desktop.Infrastructure;

/// <summary>
/// 기동 시 1회 실행하는 DB 초기화. 기존 HD.AMR.Web/Program.cs 의 초기화 블록에 대응한다.
///
/// 데스크톱은 항상 새 스키마(EnsureCreated)를 만들므로, Web 의 후방호환 마이그레이션
/// SQL(ExecuteSqlRaw: 기존 DB 컬럼 추가/이름변경)은 불필요하다. 검사 레시피는 카탈로그를 시드한다.
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

        // 검사 레시피 17종 카탈로그 시드 — 없는 행만 추가(현장 조정값 보존).
        sp.GetRequiredService<InspectionRecipeService>()
            .SeedDefaultsAsync().GetAwaiter().GetResult();
    }
}
