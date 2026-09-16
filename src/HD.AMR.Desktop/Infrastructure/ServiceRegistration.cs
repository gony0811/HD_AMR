using HD.AMR.App.Communication;
using HD.AMR.App.Service;
using HD.AMR.App.Data;
using HD.AMR.Desktop.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HD.AMR.Desktop.Infrastructure;

/// <summary>
/// DI 구성. 하드웨어/데이터 서비스는 기존 HD.AMR.Web/Program.cs 의 등록을 그대로 이식한다.
/// 데스크톱은 SignalR 서킷이 없으므로, DB 를 직접 주입받는 서비스(ParameterService 등)는
/// Web 과 동일하게 Scoped 로 두고 뷰모델이 작업마다 <see cref="IServiceScopeFactory"/> 로
/// scope 를 열어 사용한다(DbContext 수명 안전).
///
/// NOTE(점진 포팅): 현재는 Tier 0(셸) + Tier 1(IoModule/AMR/Parameter/Recipe/Home) 에 필요한
/// 서비스만 등록한다. 페이지를 포팅할 때마다 해당 서비스(Cobot 제어/Sequence/Vision/Vda5050 등)를
/// 여기에 추가한다.
/// </summary>
internal static class ServiceRegistration
{
    public static IServiceCollection AddHdAmrServices(this IServiceCollection services, IConfiguration config)
    {
        // ── 데이터 계층 ─────────────────────────────────────────────
        // DB 를 직접 주입받는 서비스(Web 에서 Scoped)를 위해 Scoped DbContext 로 등록하고,
        // 뷰모델은 작업마다 scope 를 연다. 파일 경로는 저장소 대신 LocalApplicationData 아래.
        var cs = ResolveConnectionString(config);
        services.AddDbContext<HdAmrDbContext>(opt => opt.UseSqlite(cs));

        // ── 하드웨어 서비스 (싱글톤 + HostedService) — 기존 Web 과 동일 패턴 ──
        // 기동 시 자동 접속, 실패 시 주기적 재접속(장비 없어도 크래시 없음).
        AddHostedSingleton<AMRService>(services);
        services.Configure<AmrModbusTcpSettings>(config.GetSection("Amr"));

        AddHostedSingleton<CobotService>(services);
        services.Configure<FairinoRpcSettings>(config.GetSection("Cobot"));

        // 카메라(RealSense)는 네이티브 realsense2 가 Windows 전용이라, 비 Windows 에서는
        // 스트리밍 루프(HostedService)를 기동하지 않는다(5초마다 DllNotFound 재시도 로그 방지).
        // 싱글톤 인스턴스는 항상 등록 → Home 의 Camera 카드는 "미연결"로 표시된다.
        services.AddSingleton<CameraService>();
        if (OperatingSystem.IsWindows())
            services.AddHostedService(sp => sp.GetRequiredService<CameraService>());
        services.Configure<RealSenseSettings>(config.GetSection("Camera"));

        AddHostedSingleton<IoModuleService>(services);
        services.Configure<IoModuleModbusTcpSettings>(config.GetSection("IoModule"));

        // ── 시퀀스 전역 실행 잠금(UI/ACS 동시 실행 방지) — 조그 리본이 참조 ──
        services.AddSingleton<HD.AMR.App.Service.Sequence.SequenceRunGate>();

        // ── DB 백엔드 서비스 (Scoped) ───────────────────────────────
        services.AddScoped<ParameterService>();
        services.AddScoped<InspectionRecipeService>();

        // ── UI(네비게이션 + 뷰모델) ─────────────────────────────────
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<MainWindowViewModel>();

        // 페이지 뷰모델 — 진입 시마다 새로 만든다(상태 초기화).
        services.AddTransient<HomeViewModel>();
        services.AddTransient<ComingSoonViewModel>();
        services.AddTransient<IoModuleViewModel>();
        services.AddTransient<AmrViewModel>();
        services.AddTransient<ParametersViewModel>();
        services.AddTransient<InspectionRecipesViewModel>();
        services.AddTransient<CobotViewModel>();

        return services;
    }

    /// <summary>싱글톤으로 등록하고 동일 인스턴스를 HostedService 로도 노출(주입 공유 + 자동 기동).</summary>
    private static void AddHostedSingleton<T>(IServiceCollection services)
        where T : class, Microsoft.Extensions.Hosting.IHostedService
    {
        services.AddSingleton<T>();
        services.AddHostedService(sp => sp.GetRequiredService<T>());
    }

    /// <summary>
    /// 연결 문자열의 상대 SQLite 경로를 %LocalAppData%/HD.AMR (macOS: ~/Library/Application Support)
    /// 아래 절대 경로로 재배치한다. 이미 절대 경로면 그대로 둔다.
    /// </summary>
    private static string ResolveConnectionString(IConfiguration config)
    {
        var raw = config.GetConnectionString("DefaultConnection") ?? "Data Source=hd_amr.db";
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(raw);
        var dataSource = builder.DataSource;

        if (!string.IsNullOrWhiteSpace(dataSource) && !Path.IsPathRooted(dataSource))
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HD.AMR");
            Directory.CreateDirectory(dir);
            builder.DataSource = Path.Combine(dir, dataSource);
        }

        return builder.ToString();
    }
}
