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
builder.Services.AddHostedService<AMRService>();

// 코봇은 RPC 전용. (AMR/IO는 위 Modbus를 계속 사용)
builder.Services.Configure<FairinoRpcSettings>(
    builder.Configuration.GetSection("Cobot"));
// 컴포넌트 주입과 호스티드 서비스가 동일 인스턴스를 공유하도록 싱글톤으로 등록.
builder.Services.AddSingleton<CobotService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CobotService>());

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

app.Run();
