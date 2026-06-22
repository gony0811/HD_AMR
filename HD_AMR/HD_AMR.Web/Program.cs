using HD_AMR.Communication;
using HD_AMR.Data;
using HD_AMR.Service;
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

var uploadDirectory = Path.Combine(builder.Environment.ContentRootPath, "UploadedDrawings");
builder.Services.Configure<DrawingStorageOptions>(opt => opt.UploadDirectory = uploadDirectory);

builder.Services.Configure<DwgConversionOptions>(builder.Configuration.GetSection("DwgConversion"));
builder.Services.AddSingleton<IDwgConverter, OdaFileConverter>();

builder.Services.AddDbContext<HdAmrDbContext>(opt =>
    opt.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")
                  ?? "Data Source=hd_amr.db"));

builder.Services.AddScoped<DrawingService>();

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

    // Backward-compatible schema add for TeachingProfiles (preserves existing data).
    db.Database.ExecuteSqlRaw(@"
CREATE TABLE IF NOT EXISTS TeachingProfiles (
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
    MoveHomeFirst INTEGER NOT NULL,
    WaypointsJson TEXT NOT NULL,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    CONSTRAINT FK_TeachingProfiles_Drawings_DrawingId FOREIGN KEY (DrawingId) REFERENCES Drawings (Id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS IX_TeachingProfiles_DrawingId ON TeachingProfiles (DrawingId);
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

// 깊이 영상 hover 프로브 — 정규화 좌표 (u,v)∈[0,1] 위치의 깊이값(mm)을 반환. mm=null 이면 무효/프레임없음.
app.MapGet("/camera/depth/value",
    (CameraService svc, double u, double v) => Results.Json(new { mm = svc.GetLatestDepthMmAt(u, v) }));

// 진단용 상태 엔드포인트 — 브라우저 DevTools 없이 프레임 수신 여부를 한눈에 확인.
// color/depth 가 null 이 아니고 lastFrameMsAgo 가 작게 갱신되면 프레임이 들어오는 중.
app.MapGet("/camera/status", (CameraService svc) => Results.Json(new
{
    svc.IsConnected,
    svc.IsStreaming,
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
}));

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
