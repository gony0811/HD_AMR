using HD_AMR.Communication;
using HD_AMR.Data;
using HD_AMR.Service;
using HD_AMR.Web.Components;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.Configure<AmrModbusTcpSettings>(
    builder.Configuration.GetSection("Amr"));
builder.Services.AddHostedService<AMRService>();

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
