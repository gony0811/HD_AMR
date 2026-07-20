using System.Runtime.InteropServices;
using HD_AMR.Communication;
using HD_AMR.Communication.Vision;
using HD_AMR.Communication.Weld;
using HD_AMR.Data;
using HD_AMR.Service;
using HD_AMR.Service.Sequence;
using HD_AMR.Service.Sequence.Steps;
using HD_AMR.Web.Components;
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

// Orbbec Gemini 2 깊이 카메라. CobotService 와 동일 패턴(싱글톤 + 호스티드).
builder.Services.Configure<OrbbecGeminiSettings>(
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
    builder.Services.AddSingleton<IDlWeldVisionDetector, HD_AMR.Web.Services.DlWeldVisionDetector>();
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

builder.Services.AddDbContext<HdAmrDbContext>(opt =>
    opt.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")
                  ?? "Data Source=hd_amr.db"));

builder.Services.AddScoped<DrawingService>();
builder.Services.AddScoped<TeachingService>();
builder.Services.AddScoped<ParameterService>();

// 시퀀스 단계 등록 (ISequenceStep). 새 단계 추가 시 여기에 한 줄만 추가.
builder.Services.AddScoped<ISequenceStep, AmrMoveStep>();
builder.Services.AddScoped<ISequenceStep, CobotInspectionMoveStep>();
builder.Services.AddScoped<ISequenceStep, CameraAlignStep>();
builder.Services.AddScoped<ISequenceStep, FlatSurfaceAlignStep>();
builder.Services.AddScoped<SequenceService>();
builder.Services.AddScoped<HD_AMR.Web.Services.LabelDataService>();
// DL 학습 오케스트레이터 — 학습 프로세스가 페이지 이동/서킷과 무관하게 살아 있어야 하므로 싱글톤.
builder.Services.AddSingleton<HD_AMR.Web.Services.WeldTrainingService>();
// DL 비드 세그멘테이션 추론(ONNX CPU) — 세션 캐시 유지 위해 싱글톤.
builder.Services.AddSingleton<HD_AMR.Web.Services.OnnxBeadSegmentationService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<HdAmrDbContext>();
    db.Database.EnsureCreated();

    // Backward-compatible schema add for ExcludedRegions (preserves existing data).
    db.Database.ExecuteSqlRaw(@"
CREATE TABLE IF NOT EXISTS ExcludedRegions (
    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    DrawingId INTEGER NOT NULL,
    MinX REAL NOT NULL,
    MinY REAL NOT NULL,
    MaxX REAL NOT NULL,
    MaxY REAL NOT NULL,
    CreatedAt TEXT NOT NULL,
    CONSTRAINT FK_ExcludedRegions_Drawings_DrawingId FOREIGN KEY (DrawingId) REFERENCES Drawings (Id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS IX_ExcludedRegions_DrawingId ON ExcludedRegions (DrawingId);
");

    // 과거 TeachingProfiles 테이블을 InspectionProfiles 로 이름 변경(기존 데이터 보존).
    // 구 테이블이 있고 신 테이블이 없을 때만 RENAME 한다(SQLite는 RENAME IF EXISTS 미지원).
    var hasOldTeachingProfiles = db.Database
        .SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = 'TeachingProfiles'")
        .AsEnumerable().First() > 0;
    var hasInspectionProfiles = db.Database
        .SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = 'InspectionProfiles'")
        .AsEnumerable().First() > 0;
    if (hasOldTeachingProfiles && !hasInspectionProfiles)
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE TeachingProfiles RENAME TO InspectionProfiles;");
    }

    // Backward-compatible schema add for InspectionProfiles (preserves existing data).
    db.Database.ExecuteSqlRaw(@"
CREATE TABLE IF NOT EXISTS InspectionProfiles (
    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    DrawingId INTEGER NOT NULL,
    Name TEXT NOT NULL,
    SpacingMm REAL NOT NULL,
    CorrugThresholdDeg REAL NOT NULL,
    CorrugStepDeg REAL NOT NULL,
    RunTool INTEGER NOT NULL,
    RunUser INTEGER NOT NULL,
    RunVel INTEGER NOT NULL,
    DelaySec REAL NOT NULL,
    ThMax REAL NOT NULL,
    SettleDelaySec REAL NOT NULL DEFAULT 0,
    MoveHomeFirst INTEGER NOT NULL,
    WaypointsJson TEXT NOT NULL,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    CONSTRAINT FK_InspectionProfiles_Drawings_DrawingId FOREIGN KEY (DrawingId) REFERENCES Drawings (Id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS IX_InspectionProfiles_DrawingId ON InspectionProfiles (DrawingId);
");

    // 기존 DB에 SettleDelaySec 컬럼이 없으면 추가(엔티티가 나중에 추가된 칼럼). SQLite는
    // ADD COLUMN IF NOT EXISTS 가 없으므로 pragma 로 존재 여부를 확인한 뒤에만 ALTER 한다.
    var hasSettleDelaySec = db.Database
        .SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM pragma_table_info('InspectionProfiles') WHERE name = 'SettleDelaySec'")
        .AsEnumerable().First() > 0;
    if (!hasSettleDelaySec)
    {
        db.Database.ExecuteSqlRaw(
            "ALTER TABLE InspectionProfiles ADD COLUMN SettleDelaySec REAL NOT NULL DEFAULT 0;");
    }

    // Backward-compatible schema add for TeachingPositions (고정 슬롯형 티칭 위치; 기존 데이터 보존).
    db.Database.ExecuteSqlRaw(@"
CREATE TABLE IF NOT EXISTS TeachingPositions (
    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    ""Key"" TEXT NOT NULL,
    Name TEXT NOT NULL,
    SortOrder INTEGER NOT NULL DEFAULT 0,
    X REAL NULL, Y REAL NULL, Z REAL NULL,
    Rx REAL NULL, Ry REAL NULL, Rz REAL NULL,
    J1 REAL NULL, J2 REAL NULL, J3 REAL NULL, J4 REAL NULL, J5 REAL NULL, J6 REAL NULL,
    Tool INTEGER NOT NULL DEFAULT 1,
    CapturedAt TEXT NULL,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    UserFrame INTEGER NULL,
    RelX REAL NULL, RelY REAL NULL, RelZ REAL NULL,
    RelRx REAL NULL, RelRy REAL NULL, RelRz REAL NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS IX_TeachingPositions_Key ON TeachingPositions (""Key"");
");

    // 기존 TeachingPositions 에 작업물 좌표계(추종 절대위치) 컬럼이 없으면 추가(기존 데이터 보존).
    // SQLite는 ADD COLUMN IF NOT EXISTS 미지원 → pragma 로 확인 후에만 ALTER.
    foreach (var col in new[] { "UserFrame INTEGER", "RelX REAL", "RelY REAL", "RelZ REAL", "RelRx REAL", "RelRy REAL", "RelRz REAL" })
    {
        var name = col.Split(' ')[0];
        var exists = db.Database
            .SqlQueryRaw<long>($"SELECT COUNT(*) AS Value FROM pragma_table_info('TeachingPositions') WHERE name = '{name}'")
            .AsEnumerable().First() > 0;
        if (!exists)
            db.Database.ExecuteSqlRaw($"ALTER TABLE TeachingPositions ADD COLUMN {col} NULL;");
    }

    // Backward-compatible schema add for Parameters (범용 key/value 설정 저장소; 기존 데이터 보존).
    db.Database.ExecuteSqlRaw(@"
CREATE TABLE IF NOT EXISTS Parameters (
    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    Name TEXT NOT NULL,
    Value TEXT NOT NULL,
    Description TEXT NULL,
    UpdatedAt TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS IX_Parameters_Name ON Parameters (Name);
");

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
app.MapGet("/camera/label/image", async (string name, HD_AMR.Web.Services.LabelDataService lbl) =>
{
    var bytes = await lbl.ReadAsync(name);
    return bytes is null ? Results.NotFound() : Results.File(bytes, "image/png");
});

// DL 추론 결과 오버레이(비드 마스크) — 수동 트리거라 단일 JPEG. 없으면 204.
app.MapGet("/vision/infer/overlay.jpg",
    (HD_AMR.Web.Services.OnnxBeadSegmentationService seg) => JpegOrNoContent(seg.LastOverlay));

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
