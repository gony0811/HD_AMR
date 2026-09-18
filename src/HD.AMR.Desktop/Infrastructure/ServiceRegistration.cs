using HD.AMR.App.Communication;
using HD.AMR.App.Communication.Weld;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Sequence;
using HD.AMR.App.Service.Sequence.Steps;
using HD.AMR.App.Service.Vision;
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
        // 뷰모델은 작업마다 scope 를 연다. 파일 경로는 저장소 대신 LocalApplicationData 아래 —
        // 웹 앱과 같은 정본 경로를 공유한다(SqliteConnectionResolver).
        var cs = SqliteConnectionResolver.Resolve(config);
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

        // Z축 텔레스코픽(EBLUM 3채널 홀 동기 컨트롤러) — RS232/TTL. 포트가 없으면 조용히 재시도만 한다.
        AddHostedSingleton<TelescopicService>(services);
        services.Configure<TelescopicSerialSettings>(config.GetSection("Telescopic"));

        AddHostedSingleton<VisionInterfaceService>(services);
        services.Configure<HD.AMR.App.Communication.Vision.VisionInterfaceSettings>(config.GetSection("Vision"));

        AddHostedSingleton<LaserDisplacementSensorService>(services);
        services.Configure<LaserDisplacementSensorSettings>(config.GetSection("LaserDisplacementSensor"));
        // 헤드 XY 오프셋 틸트 응답 캘리브레이션 — Laser 페이지에서 실행하는 무상태 루틴.
        services.AddTransient<LaserHeadCalibrationRoutine>();

        // ── 시퀀스 그래프 지원 서비스 ───────────────────────────────
        // 평탄 중심 정렬(무상태 루틴) — 카메라 페이지/FlatSurfaceAlignStep 공유.
        services.AddTransient<FlatSurfaceCenteringService>();

        // 용접라인 추적 — 검출기는 Windows 에서만 실제(OpenCV/ONNX), 그 외 no-op 폴백.
        services.Configure<WeldTrackingSettings>(config.GetSection("WeldTracking"));
        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<IWeldVisionDetector, WeldVisionDetector>();
            services.AddSingleton<IDlWeldVisionDetector, DlWeldVisionDetector>();
        }
        else
        {
            services.AddSingleton<IWeldVisionDetector, NoopWeldVisionDetector>();
            services.AddSingleton<IDlWeldVisionDetector, NoopWeldVisionDetector>();
        }
        services.AddSingleton(sp =>
        {
            var dir = config.GetSection("WeldTracking")["ProfileDirectory"] ?? "RoiProfiles";
            return new RoiProfileStore(Path.IsPathRooted(dir) ? dir : Path.Combine(AppDataDir(), dir));
        });
        services.AddSingleton<WeldTrackingService>();
        // DL 학습 오케스트레이터(학습 프로세스가 페이지와 무관하게 살아야 함) + ONNX 추론(세션 캐시) — 싱글톤.
        services.AddSingleton<WeldTrainingService>();
        services.AddSingleton<OnnxBeadSegmentationService>();

        // 도면 저장/변환(DrawingService 의존성).
        var uploadDir = Path.Combine(AppDataDir(), "UploadedDrawings");
        Directory.CreateDirectory(uploadDir);
        services.Configure<DrawingStorageOptions>(opt => opt.UploadDirectory = uploadDir);
        services.Configure<DwgConversionOptions>(config.GetSection("DwgConversion"));
        services.AddSingleton<IDwgConverter, OdaFileConverter>();

        // ── 시퀀스 전역 실행 잠금(UI/ACS 동시 실행 방지) — 조그 리본이 참조 ──
        services.AddSingleton<HD.AMR.App.Service.Sequence.SequenceRunGate>();

        // ── ACS(VDA 5050) 스택 — 기존 Web 과 동일 ─────────────────────
        // TARS-M v3 REST 클라이언트(VDA5050 order 의 이동 실현 경로).
        services.Configure<AmrRestSettings>(config.GetSection("AmrRest"));
        services.AddSingleton<AmrRestClient>();
        // startWeldInspection 실행 총괄 — 싱글톤, 액션마다 scope 생성해 시퀀스 실행.
        services.AddSingleton<HD.AMR.App.Service.Inspection.IWeldInspectionExecutor,
            HD.AMR.App.Service.Inspection.WeldInspectionOrchestrator>();
        // VDA 5050 어댑터(ACS↔AMR) — connection/state 발행 + order 실행 + instantActions. Enabled=false 면 유휴.
        services.Configure<HD.AMR.App.Communication.Vda5050.Vda5050AdapterSettings>(config.GetSection("Vda5050"));
        services.AddSingleton<Vda5050OrderExecutor>();
        AddHostedSingleton<Vda5050AdapterService>(services);

        // ── DB 백엔드 서비스 (Scoped) ───────────────────────────────
        services.AddScoped<ParameterService>();
        services.AddScoped<InspectionRecipeService>();
        services.AddScoped<CalibrationService>();
        services.AddScoped<ArucoHandEyeService>();
        services.AddScoped<QrLocalizationService>();
        services.AddScoped<DrawingService>();
        services.AddScoped<TeachingService>();
        services.AddScoped<LabelDataService>();

        // ── 시퀀스(스텝 그래프 + 실행/모니터) ───────────────────────
        // SequenceMonitorService 는 별도 모니터 창과 공유하므로 싱글톤.
        services.AddSingleton<SequenceMonitorService>();
        AddSequenceSteps(services);
        // SequenceService 는 스텝/상태를 보유 — 페이지 수명 scope 에서 해석(뷰모델이 scope 개방).
        services.AddScoped<SequenceService>();

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
        services.AddTransient<CalibrationViewModel>();
        services.AddTransient<VisionInterfaceViewModel>();
        services.AddTransient<SequenceViewModel>();
        services.AddTransient<CameraViewModel>();
        services.AddTransient<WeldTrackingViewModel>();
        services.AddTransient<InspectionPointsViewModel>();
        services.AddTransient<InspectionMapViewModel>();
        services.AddTransient<TeachingViewModel>();
        services.AddTransient<LabelEditorViewModel>();
        services.AddTransient<VisionTrainingViewModel>();
        services.AddTransient<LaserViewModel>();
        services.AddTransient<MountCalibrationViewModel>();
        services.AddTransient<HandEyeViewModel>();
        services.AddTransient<ArucoMountCalibrationViewModel>();
        services.AddTransient<LiftViewModel>();

        return services;
    }

    /// <summary>시퀀스 스텝 그래프 등록 — 기존 HD.AMR.Web/Program.cs 와 동일(순서/peakId 포함).
    /// PeakFind/PeakCentering/BeadFind/BeadCentering/WObjPoint 는 peakId 만 다른 동일 클래스라
    /// ActivatorUtilities 로 명시 인자(첫 파라미터)를 넣어 두 번 등록한다.</summary>
    private static void AddSequenceSteps(IServiceCollection services)
    {
        services.AddScoped<ISequenceStep, AmrMoveStep>();
        services.AddScoped<ISequenceStep, CobotInspectionMoveStep>();
        services.AddScoped<ISequenceStep, CameraAlignStep>();
        services.AddScoped<ISequenceStep, FlatSurfaceAlignStep>();
        services.AddScoped<ISequenceStep, LaserWorkingDistanceStep>();
        services.AddScoped<ISequenceStep>(sp => ActivatorUtilities.CreateInstance<PeakFindStep>(sp, 1));
        services.AddScoped<ISequenceStep>(sp => ActivatorUtilities.CreateInstance<PeakCenteringStep>(sp, 1));
        services.AddScoped<ISequenceStep>(sp => ActivatorUtilities.CreateInstance<BeadFindStep>(sp, 1));
        services.AddScoped<ISequenceStep>(sp => ActivatorUtilities.CreateInstance<BeadCenteringStep>(sp, 1));
        services.AddScoped<ISequenceStep>(sp => ActivatorUtilities.CreateInstance<WObjPointStep>(sp, 1));
        services.AddScoped<ISequenceStep, PeakApproachStep>();
        services.AddScoped<ISequenceStep>(sp => ActivatorUtilities.CreateInstance<PeakFindStep>(sp, 2));
        services.AddScoped<ISequenceStep>(sp => ActivatorUtilities.CreateInstance<PeakCenteringStep>(sp, 2));
        services.AddScoped<ISequenceStep>(sp => ActivatorUtilities.CreateInstance<BeadFindStep>(sp, 2));
        services.AddScoped<ISequenceStep>(sp => ActivatorUtilities.CreateInstance<BeadCenteringStep>(sp, 2));
        services.AddScoped<ISequenceStep>(sp => ActivatorUtilities.CreateInstance<WObjPointStep>(sp, 2));
        services.AddScoped<ISequenceStep, WObjRegisterStep>();
        services.AddScoped<ISequenceStep, InspectionRunStep>();
        services.AddScoped<ISequenceStep, CornerInspectionRunStep>();
        services.AddScoped<ISequenceStep, WObjResetStep>();
        services.AddScoped<ISequenceStep, MonitorCloseStep>();
    }

    /// <summary>%LocalAppData%/HD.AMR (macOS: ~/Library/Application Support/HD.AMR).</summary>
    private static string AppDataDir() => SqliteConnectionResolver.AppDataDirectory;

    /// <summary>싱글톤으로 등록하고 동일 인스턴스를 HostedService 로도 노출(주입 공유 + 자동 기동).</summary>
    private static void AddHostedSingleton<T>(IServiceCollection services)
        where T : class, Microsoft.Extensions.Hosting.IHostedService
    {
        services.AddSingleton<T>();
        services.AddHostedService(sp => sp.GetRequiredService<T>());
    }
}
