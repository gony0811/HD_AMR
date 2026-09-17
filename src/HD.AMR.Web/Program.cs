using System.Runtime.InteropServices;
using HD.AMR.App.Communication;
using HD.AMR.App.Communication.Vision;
using HD.AMR.App.Communication.Weld;
using HD.AMR.App.Data;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Sequence;
using HD.AMR.App.Service.Sequence.Steps;
using HD.AMR.Web.Components;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// 종료 시 호스티드 서비스(AMR/Cobot)의 정리가 길어져도 프로세스가 매달리지 않도록
// 호스트 종료 제한 시간을 짧게 둔다(기본 30초 → 5초). 이 시간을 넘기면 강제 종료한다.
builder.Services.Configure<HostOptions>(opt =>
    opt.ShutdownTimeout = TimeSpan.FromSeconds(5));

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.Configure<AmrModbusTcpSettings>(
    builder.Configuration.GetSection("Amr"));
// 컴포넌트 주입과 호스티드 서비스가 동일 인스턴스를 공유하도록 싱글톤으로 등록.
builder.Services.AddSingleton<AMRService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AMRService>());

// 코봇은 RPC 전용. (AMR/IO는 위 Modbus를 계속 사용)
builder.Services.Configure<FairinoRpcSettings>(
    builder.Configuration.GetSection("Cobot"));
// 컴포넌트 주입과 호스티드 서비스가 동일 인스턴스를 공유하도록 싱글톤으로 등록.
builder.Services.AddSingleton<CobotService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CobotService>());

// Intel RealSense D435 깊이 카메라. CobotService 와 동일 패턴(싱글톤 + 호스티드).
builder.Services.Configure<RealSenseSettings>(
    builder.Configuration.GetSection("Camera"));
builder.Services.AddSingleton<CameraService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CameraService>());

// 비전 인터페이스(자동화↔비전 TCP 프로토콜). 싱글톤 + 호스티드.
// AMR/Cobot 과 동일하게 기동 시 상시 자동 접속 — 실패 시 5초마다 재시도.
builder.Services.Configure<VisionInterfaceSettings>(
    builder.Configuration.GetSection("Vision"));
builder.Services.AddSingleton<VisionInterfaceService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<VisionInterfaceService>());

// 레이저 변위 센서(EtherNet/IP). AMR/Cobot/Camera 와 동일 패턴(싱글톤 + 호스티드) — 기동 시 상시 자동 접속, 실패 시 재시도.
builder.Services.Configure<LaserDisplacementSensorSettings>(
    builder.Configuration.GetSection("LaserDisplacementSensor"));
builder.Services.AddSingleton<LaserDisplacementSensorService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LaserDisplacementSensorService>());
// 헤드 XY 오프셋 틸트 응답 캘리브레이션 — /laser 페이지에서 실행하는 무상태 루틴.
builder.Services.AddTransient<LaserHeadCalibrationRoutine>();

// 평탄 중심 정렬 — 카메라 페이지와 FlatSurfaceAlignStep 이 공유하는 무상태 루틴.
builder.Services.AddTransient<FlatSurfaceCenteringService>();

// 노드↔AMR Job/Task 인덱스 로컬 매핑 저장소(JSON). /amr-job-mapping 편집 화면 + (향후)어댑터 조회.
builder.Services.AddSingleton<AmrJobMappingStore>();

// TARS-M v3 REST 클라이언트 — VDA5050 order 의 이동 실현 경로(/robot/go·state·status).
builder.Services.Configure<AmrRestSettings>(
    builder.Configuration.GetSection("AmrRest"));
builder.Services.AddSingleton<AmrRestClient>();

// VDA 5050 어댑터(ACS↔AMR) — connection/state 발행 + order 실행(REST 이동) + instantActions.
// AMRService(Modbus 상태)를 참조해 state를 매핑. Enabled=false 면 유휴. AMR/Cobot 과 동일 패턴.
builder.Services.Configure<HD.AMR.App.Communication.Vda5050.Vda5050AdapterSettings>(
    builder.Configuration.GetSection("Vda5050"));
builder.Services.AddSingleton<Vda5050OrderExecutor>();
builder.Services.AddSingleton<Vda5050AdapterService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Vda5050AdapterService>());
// startWeldInspection 실행 총괄(2차 검사 연동) — singleton, 액션마다 scope 생성해 시퀀스 실행.
builder.Services.AddSingleton<HD.AMR.App.Service.Inspection.IWeldInspectionExecutor,
    HD.AMR.App.Service.Inspection.WeldInspectionOrchestrator>();

// LS산전 IO Module(ModbusTCP). AMR/Cobot 과 동일 패턴(싱글톤 + 호스티드) — 기동 시 상시 자동 접속, 실패 시 5초마다 재시도.
builder.Services.Configure<IoModuleModbusTcpSettings>(
    builder.Configuration.GetSection("IoModule"));
builder.Services.AddSingleton<IoModuleService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IoModuleService>());

// 용접라인 추적(명세서 v2). 검출은 OpenCvSharp(Windows) — 그 외 플랫폼은 no-op 폴백.
// ROI 프로파일은 JSON 파일로 저장. 싱글톤(운영자 1인, 상태 유지).
builder.Services.Configure<WeldTrackingSettings>(
    builder.Configuration.GetSection("WeldTracking"));
// 고전 CV(파라미터) 검출기 + DL(YOLOv8-seg) 검출기를 함께 등록 → WeldTrackingService 가 런타임 토글.
if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    builder.Services.AddSingleton<IWeldVisionDetector, WeldVisionDetector>();
    builder.Services.AddSingleton<IDlWeldVisionDetector, HD.AMR.App.Service.Vision.DlWeldVisionDetector>();
}
else
{
    builder.Services.AddSingleton<IWeldVisionDetector, NoopWeldVisionDetector>();
    builder.Services.AddSingleton<IDlWeldVisionDetector, NoopWeldVisionDetector>();
}
builder.Services.AddSingleton(sp =>
{
    var dir = builder.Configuration.GetSection("WeldTracking")["ProfileDirectory"] ?? "RoiProfiles";
    return new RoiProfileStore(Path.Combine(builder.Environment.ContentRootPath, dir));
});
builder.Services.AddSingleton<WeldTrackingService>();

var uploadDirectory = Path.Combine(builder.Environment.ContentRootPath, "UploadedDrawings");
builder.Services.Configure<DrawingStorageOptions>(opt => opt.UploadDirectory = uploadDirectory);

builder.Services.Configure<DwgConversionOptions>(builder.Configuration.GetSection("DwgConversion"));
builder.Services.AddSingleton<IDwgConverter, OdaFileConverter>();

// 상대 Data Source 를 %LocalAppData%/HD.AMR 아래 절대 경로로 통일 — 데스크톱 앱과 같은 DB 파일 공유.
builder.Services.AddDbContext<HdAmrDbContext>(opt =>
    opt.UseSqlite(SqliteConnectionResolver.Resolve(builder.Configuration)));

builder.Services.AddScoped<DrawingService>();
builder.Services.AddScoped<TeachingService>();
builder.Services.AddScoped<ParameterService>();
// 검사 타입 17종 레시피(사양 §8.5.1) CRUD + 기동 시드 — ACS startWeldInspection 매핑 대상.
builder.Services.AddScoped<InspectionRecipeService>();
// QR 정차 pose 티칭에 필요한 T_A_B, T_T_C, 목표 T_A_Q 및 기존 정합값 저장.
builder.Services.AddScoped<CalibrationService>();
// 바닥 QR 기준 목표 AMR SLAM 정차 pose 계산. 온디맨드 측정 — 호스티드 불필요.
builder.Services.AddScoped<QrLocalizationService>();

// 시퀀스 단계 등록 (ISequenceStep). 새 단계 추가 시 여기에 한 줄만 추가.
builder.Services.AddScoped<ISequenceStep, AmrMoveStep>();
builder.Services.AddScoped<ISequenceStep, CobotInspectionMoveStep>();
builder.Services.AddScoped<ISequenceStep, CameraAlignStep>();
builder.Services.AddScoped<ISequenceStep, FlatSurfaceAlignStep>();
builder.Services.AddScoped<ISequenceStep, LaserWorkingDistanceStep>();   // 450: ④ 직후 레이저 WD 거리 조정
// ⑤~⑫ Peak/Bead 측정 — ⑤⑨·⑥⑩·⑦⑪은 peakId 만 다른 동일 동작이라 한 클래스를 두 번 등록한다.
// ActivatorUtilities.CreateInstance 는 명시 인자를 생성자 앞쪽부터 매칭하므로 peakId 가 첫 파라미터여야 한다.
builder.Services.AddScoped<ISequenceStep>(sp => ActivatorUtilities.CreateInstance<PeakFindStep>(sp, 1));
builder.Services.AddScoped<ISequenceStep>(sp => ActivatorUtilities.CreateInstance<PeakCenteringStep>(sp, 1));
builder.Services.AddScoped<ISequenceStep>(sp => ActivatorUtilities.CreateInstance<BeadFindStep>(sp, 1));
builder.Services.AddScoped<ISequenceStep>(sp => ActivatorUtilities.CreateInstance<BeadCenteringStep>(sp, 1));   // 750
builder.Services.AddScoped<ISequenceStep>(sp => ActivatorUtilities.CreateInstance<WObjPointStep>(sp, 1));   // 760: 작업물 좌표계 점1(원점)
builder.Services.AddScoped<ISequenceStep, PeakApproachStep>();
builder.Services.AddScoped<ISequenceStep>(sp => ActivatorUtilities.CreateInstance<PeakFindStep>(sp, 2));
builder.Services.AddScoped<ISequenceStep>(sp => ActivatorUtilities.CreateInstance<PeakCenteringStep>(sp, 2));
builder.Services.AddScoped<ISequenceStep>(sp => ActivatorUtilities.CreateInstance<BeadFindStep>(sp, 2));
builder.Services.AddScoped<ISequenceStep>(sp => ActivatorUtilities.CreateInstance<BeadCenteringStep>(sp, 2));   // 1150
builder.Services.AddScoped<ISequenceStep>(sp => ActivatorUtilities.CreateInstance<WObjPointStep>(sp, 2));   // 1160: 작업물 좌표계 점2(X방향)
builder.Services.AddScoped<ISequenceStep, WObjRegisterStep>();   // 1170: 가상 점3(툴Z+50mm) + 좌표계 등록
builder.Services.AddScoped<ISequenceStep, InspectionRunStep>();
builder.Services.AddScoped<ISequenceStep, CornerInspectionRunStep>();   // 1250: ⑱ᶜ CORNER3 티칭 슬롯 순회
builder.Services.AddScoped<ISequenceStep, WObjResetStep>();   // 1300: 활성 작업물 좌표계 0 복귀
builder.Services.AddScoped<ISequenceStep, MonitorCloseStep>();   // 1400: 모니터링 창 닫기 (최종)
// 시퀀스 모니터링 허브 — 별도 브라우저 창(/sequence-monitor, 다른 서킷)이 구독하므로 싱글톤.
builder.Services.AddSingleton<SequenceMonitorService>();  // 1200: ⑱ 검사 수행(도면 경유점 순회 + 비전 캡처)
// 시퀀스 전역 실행 잠금 — SequenceService 는 서킷별 scoped 라 UI/ACS 동시 실행을 막으려면 전역 게이트가 필요.
builder.Services.AddSingleton<SequenceRunGate>();
builder.Services.AddScoped<SequenceService>();
builder.Services.AddScoped<HD.AMR.App.Service.Vision.LabelDataService>();
// DL 학습 오케스트레이터 — 학습 프로세스가 페이지 이동/서킷과 무관하게 살아 있어야 하므로 싱글톤.
builder.Services.AddSingleton<HD.AMR.App.Service.Vision.WeldTrainingService>();
// DL 비드 세그멘테이션 추론(ONNX CPU) — 세션 캐시 유지 위해 싱글톤.
builder.Services.AddSingleton<HD.AMR.App.Service.Vision.OnnxBeadSegmentationService>();

var app = builder.Build();

// 과거 상대경로 시절의 레거시 DB(프로젝트 폴더의 hd_amr.db)를 정본 경로로 1회 가져오기.
LegacyDatabaseImporter.ImportIfNeeded(
    builder.Configuration, builder.Environment.ContentRootPath,
    app.Services.GetRequiredService<ILogger<Program>>());

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<HdAmrDbContext>();
    db.Database.EnsureCreated();

    // 후방호환 스키마 패치(멱등) — 데스크톱과 공유 (SqliteCompatMigrations 로 이동).
    SqliteCompatMigrations.Apply(db);

    // 레시피 카탈로그 시드 — 없는 행만 추가(현장 조정값 보존). LINE-* 5종만 Enabled.
    scope.ServiceProvider.GetRequiredService<InspectionRecipeService>()
        .SeedDefaultsAsync().GetAwaiter().GetResult();

    var converter = scope.ServiceProvider.GetRequiredService<IDwgConverter>();
    if (!converter.IsAvailable)
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        logger.LogWarning(
            "DWG converter not configured or not found at {Path}. .dwg files will be stored without conversion.",
            converter.ConfiguredPath ?? "(unset)");
    }
}
Directory.CreateDirectory(uploadDirectory);

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// MJPEG 라이브 스트림 엔드포인트. 브라우저의 <img src="/camera/color.mjpeg"> 로 직접 표시 가능.
// multipart/x-mixed-replace 응답으로 JPEG 프레임을 연속 송출.
app.MapGet("/camera/color.mjpeg",
    (CameraService svc, HttpContext http, CancellationToken ct) =>
        StreamMjpegAsync(http, ct, svc.Settings.MjpegFps,
            () => svc.GetLatestColorJpegAsync(svc.Settings.JpegQuality, ct)));

app.MapGet("/camera/depth.mjpeg",
    (CameraService svc, HttpContext http, CancellationToken ct) =>
        StreamMjpegAsync(http, ct, svc.Settings.MjpegFps,
            () => svc.GetLatestDepthJpegAsync(svc.Settings.JpegQuality, ct)));

app.MapGet("/camera/ir.mjpeg",
    (CameraService svc, HttpContext http, CancellationToken ct) =>
        StreamMjpegAsync(http, ct, svc.Settings.MjpegFps,
            () => svc.GetLatestIrJpegAsync(svc.Settings.JpegQuality, ct)));

// 깊이 영상 hover 프로브 — 정규화 좌표 (u,v)∈[0,1] 위치의 깊이값(mm)을 반환. mm=null 이면 무효/프레임없음.
app.MapGet("/camera/depth/value",
    (CameraService svc, double u, double v) => Results.Json(new { mm = svc.GetLatestDepthMmAt(u, v) }));

// 깊이 ROI 통계 — 정규화 사각형(x,y,w,h)∈[0,1] 안의 최소/평균/최대(mm)·유효율. 프레임/영역 없으면 null.
app.MapGet("/camera/depth/roi-stats",
    (CameraService svc, double x, double y, double w, double h) =>
        Results.Json(svc.ComputeDepthRoiStats(x, y, w, h)));

// 진단용 상태 엔드포인트 — 브라우저 DevTools 없이 프레임 수신 여부를 한눈에 확인.
// color/depth 가 null 이 아니고 lastFrameMsAgo 가 작게 갱신되면 프레임이 들어오는 중.
app.MapGet("/camera/status", (CameraService svc) => Results.Json(new
{
    svc.IsConnected,
    svc.IsStreaming,
    svc.IsIrActive,
    svc.ConnectionType,
    lastFrameMsAgo = (DateTime.UtcNow - svc.LastFrameAt).TotalMilliseconds,
    color = svc.LatestColor is null ? null : (object)new
    {
        svc.LatestColor.Width, svc.LatestColor.Height, svc.LatestColor.PixelFormat, len = svc.LatestColor.Pixels.Length
    },
    depth = svc.LatestDepth is null ? null : (object)new
    {
        svc.LatestDepth.Width, svc.LatestDepth.Height, len = svc.LatestDepth.Pixels.Length
    },
    ir = svc.LatestIr is null ? null : (object)new
    {
        svc.LatestIr.Width, svc.LatestIr.Height, svc.LatestIr.PixelFormat, len = svc.LatestIr.Pixels.Length
    },
}));

// 용접라인 검출 overlay(주석 이미지) — 수동 트리거라 단일 JPEG 으로 제공. 프레임 없으면 204.
// UI 는 <img src="/camera/weld/overlay.jpg?k=캐시버스터"> 로 검출 후 갱신.
app.MapGet("/camera/weld/overlay.jpg", (WeldTrackingService w) => JpegOrNoContent(w.LastOverlay));
app.MapGet("/camera/weld/peak1.jpg", (WeldTrackingService w) => JpegOrNoContent(w.Peak1Overlay));
app.MapGet("/camera/weld/peak2.jpg", (WeldTrackingService w) => JpegOrNoContent(w.Peak2Overlay));

static IResult JpegOrNoContent(byte[]? jpeg)
    => jpeg is { Length: > 0 } ? Results.File(jpeg, "image/jpeg") : Results.NoContent();

// DL 라벨 에디터용 — 캡처 폴더의 이미지/마스크 파일을 이름으로 서빙(폴더는 서버 설정값, 경로 탈출 차단).
// UI 는 <img src="/camera/label/image?name=STEM_rgb.png"> 로 로드. 캐시 방지 위해 no-store.
app.MapGet("/camera/label/image", async (string name, HD.AMR.App.Service.Vision.LabelDataService lbl) =>
{
    var bytes = await lbl.ReadAsync(name);
    return bytes is null ? Results.NotFound() : Results.File(bytes, "image/png");
});

// DL 추론 결과 오버레이(비드 마스크) — 수동 트리거라 단일 JPEG. 없으면 204.
app.MapGet("/vision/infer/overlay.jpg",
    (HD.AMR.App.Service.Vision.OnnxBeadSegmentationService seg) => JpegOrNoContent(seg.LastOverlay));

static async Task StreamMjpegAsync(HttpContext http, CancellationToken ct, int fps,
    Func<Task<byte[]?>> getJpeg)
{
    const string boundary = "frame";
    http.Response.ContentType = $"multipart/x-mixed-replace; boundary={boundary}";
    http.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
    http.Response.Headers.Pragma = "no-cache";
    http.Response.Headers["Connection"] = "close";

    var delayMs = Math.Max(33, 1000 / Math.Max(1, fps));
    var crlf = "\r\n"u8.ToArray();
    var body = http.Response.Body;

    // 헤더를 즉시 흘려보내 클라이언트(브라우저/curl)가 200 응답을 받고 첫 프레임을 기다릴 수 있게 한다.
    // 프레임이 없는 동안(예: 카메라 미연결)에도 연결이 살아 있어야 자동 복구 후 표시가 시작됨.
    await http.Response.StartAsync(ct);
    await body.FlushAsync(ct);

    while (!ct.IsCancellationRequested)
    {
        var jpeg = await getJpeg();
        if (jpeg is not null && jpeg.Length > 0)
        {
            var header = System.Text.Encoding.ASCII.GetBytes(
                $"--{boundary}\r\nContent-Type: image/jpeg\r\nContent-Length: {jpeg.Length}\r\n\r\n");
            try
            {
                await body.WriteAsync(header, ct);
                await body.WriteAsync(jpeg, ct);
                await body.WriteAsync(crlf, ct);
                await body.FlushAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception) { break; } // 클라이언트 단절 — 루프 종료.
        }
        try { await Task.Delay(delayMs, ct); }
        catch (OperationCanceledException) { break; }
    }
}

app.Run();
