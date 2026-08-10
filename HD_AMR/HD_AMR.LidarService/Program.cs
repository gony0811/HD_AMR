using System.Text.Json;
using System.Text.Json.Serialization;
using HD_AMR.Contracts.Lidar;
using HD_AMR.LidarService;
using HD_AMR.LidarService.Detection;
using HD_AMR.LidarService.Device;
using HD_AMR.LidarService.Preview;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IConfiguration>().GetSection("Lidar:Device").Get<NslDeviceOptions>()
    ?? new NslDeviceOptions());

builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IConfiguration>().GetSection("Lidar:Session").Get<LidarSessionOptions>()
    ?? new LidarSessionOptions());

builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IConfiguration>().GetSection("Lidar:Watchdog").Get<LidarWatchdogOptions>()
    ?? new LidarWatchdogOptions());

builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IConfiguration>().GetSection("Lidar:Replay").Get<ReplayDeviceOptions>()
    ?? new ReplayDeviceOptions());

builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IConfiguration>().GetSection("Lidar:Detector").Get<RidgeDetectorOptions>()
    ?? new RidgeDetectorOptions());

builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IConfiguration>().GetSection("Lidar:Synthetic").Get<SyntheticDeviceOptions>()
    ?? new SyntheticDeviceOptions());

builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IConfiguration>().GetSection("Lidar:Preview").Get<PreviewOptions>()
    ?? new PreviewOptions());

// 센서 구현 선택.
//   Nsl       : 실제 센서 (젯슨 배포)
//   Replay    : 덤프 재생 (실측 데이터로 개발)
//   Synthetic : 정답을 아는 합성 쐐기 (알고리즘 기하 검증)
var deviceKind = builder.Configuration["Lidar:Device:Kind"] ?? "Nsl";

if (string.Equals(deviceKind, "Replay", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddSingleton<ILidarDevice, ReplayLidarDevice>();
else if (string.Equals(deviceKind, "Synthetic", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddSingleton<ILidarDevice, SyntheticLidarDevice>();
else
    builder.Services.AddSingleton<ILidarDevice, NslLidarDevice>();

// 능선 검출 방식 선택.
//   Arc      : 평판 위 반원 비드의 정점선 (실제 측정 대상의 형상)
//   TwoPlane : 두 경사면의 교선 (뾰족한 마루용. 이 대상에는 맞지 않는다)
//
// 실물은 평판에 성형된 반경 35mm 반원 리브라, 교차하는 두 평면이 존재하지 않는다.
// TwoPlane 을 실물에 돌리면 판 안에서 두 번째 평면을 못 찾고 배경을 집어 오검출을 낸다.
var detectorKind = builder.Configuration["Lidar:Detector:Kind"] ?? "Arc";

if (string.Equals(detectorKind, "TwoPlane", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddSingleton<IRidgeDetector, RidgeDetector>();
else
    builder.Services.AddSingleton<IRidgeDetector, ArcRidgeDetector>();

builder.Services.AddSingleton<LidarSession>();

// 연결을 지속적으로 유지한다. 기동 시 첫 연결도 이 워치독이 담당하므로, 센서가 아직
// 안 붙었거나 케이블이 빠진 상태로 서비스가 떠도 나중에 알아서 붙는다. 센서가 없다는
// 이유로 서비스가 죽으면 원격 진단이 불가능해진다.
builder.Services.AddHostedService<LidarWatchdog>();

// 모니터링 화면용 프레임 생성기. 엔드포인트가 인스턴스를 직접 참조해야 하므로
// 싱글턴으로 먼저 등록하고 같은 인스턴스를 호스티드 서비스로 넘긴다.
builder.Services.AddSingleton<LivePreviewService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LivePreviewService>());

var app = builder.Build();

// 기동 시 실제로 바인딩된 초기 설정을 남긴다. "설정 파일을 고쳤는데 반영이 안 된다"는
// 상황에서 파일을 아무리 들여다봐도 답이 안 나오는데, 저널에 이 한 줄이 있으면 즉시 갈린다.
// 특히 ROI 는 값이 없으면 전체 화면으로 도는데 그 상태가 겉으로는 정상처럼 보인다.
{
    var deviceOptions = app.Services.GetRequiredService<NslDeviceOptions>();
    var roi = deviceOptions.InitialConfig?.Roi;

    app.Logger.LogInformation(
        "초기 설정: device={Kind}, detector={Detector}, 노출={Exposure}us, minAmp={MinAmp}, ROI={Roi}",
        deviceKind, detectorKind,
        deviceOptions.InitialConfig?.IntegrationTime3D,
        deviceOptions.InitialConfig?.MinAmplitude,
        roi is null ? "전체 화면" : $"({roi.XMin},{roi.YMin})-({roi.XMax},{roi.YMax})");
}

var session = app.Services.GetRequiredService<LidarSession>();
app.Lifetime.ApplicationStopping.Register(() => session.DisposeAsync().AsTask().GetAwaiter().GetResult());

// ── 엔드포인트 ────────────────────────────────────────────────────────────
// 경로는 HD_AMR.Contracts 의 LidarApiRoutes 상수를 쓴다. 클라이언트(HD_AMR)와 같은
// 상수를 참조하므로 경로 오타가 컴파일 시점에 걸린다.

app.MapGet(LidarApiRoutes.Health, () => Results.Ok(new { status = "ok" }));

app.MapGet(LidarApiRoutes.Status, async (LidarSession s, CancellationToken ct) =>
    Results.Ok(await s.GetStatusAsync(ct)));

app.MapPost(LidarApiRoutes.Measure, async (MeasureRequest? request, LidarSession s, CancellationToken ct) =>
    Results.Ok(await s.MeasureAsync(request ?? new MeasureRequest(), ct)));

app.MapGet(LidarApiRoutes.Config, async (LidarSession s, CancellationToken ct) =>
    Results.Ok(await s.GetConfigAsync(ct)));

app.MapMethods(LidarApiRoutes.ConfigPatch, ["PATCH"],
    async (LidarConfigPatch patch, LidarSession s, CancellationToken ct) =>
        Results.Ok(await s.PatchConfigAsync(patch, ct)));

app.MapPost(LidarApiRoutes.ConfigPersist, async (LidarSession s, CancellationToken ct) =>
{
    await s.PersistConfigAsync(ct);
    return Results.Ok(new { persisted = true });
});

// ── 모니터링 미리보기 (Jetson 프론트엔드 전용) ──────────────────────────────
// 이미지와 메타데이터는 백그라운드 루프가 한 프레임에서 함께 만들어 게시한 것을 읽기만
// 한다. 요청이 센서를 직접 건드리지 않으므로 브라우저를 몇 개 열든 센서 부하는 같다.
//
// 모든 미리보기 엔드포인트가 Touch() 를 호출한다. 이게 "보는 사람이 있다"는 유일한
// 신호이고, 없으면 루프가 잠들어 CPU 를 놓는다.

app.MapGet(LidarApiRoutes.Preview, (LivePreviewService preview) =>
{
    preview.Touch();
    var frame = preview.Latest;
    return frame is null
        ? Results.Json(new { waiting = true }, statusCode: StatusCodes.Status503ServiceUnavailable)
        : Results.Ok(frame.Info);
});

app.MapGet(LidarApiRoutes.PreviewDistance, (LivePreviewService preview) =>
{
    preview.Touch();
    return Png(preview.Latest?.DistancePng);
});

app.MapGet(LidarApiRoutes.PreviewAmplitude, (LivePreviewService preview) =>
{
    preview.Touch();
    return Png(preview.Latest?.AmplitudePng);
});

app.MapGet(LidarApiRoutes.PreviewOverlay, (LivePreviewService preview) =>
{
    preview.Touch();
    return Png(preview.Latest?.OverlayPng);
});

app.MapGet(LidarApiRoutes.PreviewOptions, (PreviewOptions options) => Results.Ok(options));

app.MapMethods(LidarApiRoutes.PreviewOptions, ["PATCH"],
    (PreviewOptionsPatch patch, PreviewOptions options) =>
    {
        // 주기 하한을 두는 이유: 0 을 넣으면 루프가 워커 스레드를 쉬지 않고 두드려
        // 실제 측정 요청이 큐 뒤에서 밀린다.
        if (patch.IntervalMs is { } interval) options.IntervalMs = Math.Clamp(interval, 100, 10000);
        if (patch.DetectionEnabled is { } detect) options.DetectionEnabled = detect;
        if (patch.MinRangeMm is { } min) options.MinRangeMm = min;
        if (patch.MaxRangeMm is { } max) options.MaxRangeMm = max;
        if (patch.AmplitudeMax is { } amp) options.AmplitudeMax = Math.Max(1, amp);
        if (patch.RidgeBandMm is { } band) options.RidgeBandMm = Math.Max(1, band);
        if (patch.FollowDetectorBand is { } follow) options.FollowDetectorBand = follow;

        return Results.Ok(options);
    });

// ── 검출기 파라미터 (현장 튜닝, 휘발성) ─────────────────────────────────────
// 재시작하면 appsettings.json 값으로 돌아간다. 튜닝으로 확정된 값은 설정 파일에
// 옮겨 적어야 살아남는다 — 화면에서 맞춘 값이 슬그머니 영구화되면, 나중에 왜 이렇게
// 동작하는지 설정 파일만 보고는 알 수 없게 된다.

app.MapGet(LidarApiRoutes.Detector, (RidgeDetectorOptions options) => DetectorView(options));

app.MapMethods(LidarApiRoutes.Detector, ["PATCH"],
    (DetectorOptionsPatch patch, RidgeDetectorOptions options) =>
    {
        if (patch.InlierThresholdMm is { } t) options.InlierThresholdMm = Math.Max(0.1, t);
        if (patch.MinDistanceMm is { } minD) options.MinDistanceMm = Math.Max(0, minD);
        if (patch.MaxDistanceMm is { } maxD) options.MaxDistanceMm = Math.Max(0, maxD);
        if (patch.RansacIterations is { } iter) options.RansacIterations = Math.Clamp(iter, 10, 20000);
        if (patch.MinPlaneInliers is { } inl) options.MinPlaneInliers = Math.Max(3, inl);
        if (patch.MinPlaneAngleDeg is { } ang) options.MinPlaneAngleDeg = Math.Clamp(ang, 0, 89);
        if (patch.MaxRmsMm is { } rms) options.MaxRmsMm = Math.Max(0.1, rms);
        if (patch.MinRidgeLengthMm is { } len) options.MinRidgeLengthMm = Math.Max(0, len);
        if (patch.MaxRidgeLengthMm is { } maxLen) options.MaxRidgeLengthMm = Math.Max(0, maxLen);
        if (patch.MinSampleFraction is { } frac) options.MinSampleFraction = Math.Clamp(frac, 0, 1);

        // 반원 코러게이션 검출 전용
        if (patch.PlaneInlierThresholdMm is { } pt) options.PlaneInlierThresholdMm = Math.Max(0.1, pt);
        if (patch.CorrugationMinHeightMm is { } cmin) options.CorrugationMinHeightMm = Math.Max(0, cmin);
        if (patch.CorrugationMaxHeightMm is { } cmax) options.CorrugationMaxHeightMm = Math.Max(1, cmax);
        if (patch.CorrugationWidthMm is { } cw) options.CorrugationWidthMm = Math.Max(1, cw);
        if (patch.CorrugationBandMarginMm is { } cb) options.CorrugationBandMarginMm = Math.Max(0, cb);
        if (patch.MinCorrugationPoints is { } cp) options.MinCorrugationPoints = Math.Max(3, cp);
        if (patch.MinArcPoints is { } ap) options.MinArcPoints = Math.Max(3, ap);
        if (patch.PeakRelativeThreshold is { } prt) options.PeakRelativeThreshold = Math.Clamp(prt, 0.01, 1);
        if (patch.MaxCandidates is { } mc) options.MaxCandidates = Math.Clamp(mc, 1, 20);
        if (patch.ArcInlierThresholdMm is { } at) options.ArcInlierThresholdMm = Math.Max(0.1, at);
        if (patch.ExpectedRadiusMm is { } er) options.ExpectedRadiusMm = Math.Max(0, er);
        if (patch.RadiusTolerance is { } rt) options.RadiusTolerance = Math.Clamp(rt, 0.01, 1);

        return DetectorView(options);
    });

// Jetson 자체 모니터링 프론트엔드. wwwroot 의 정적 파일로 서비스한다.
app.UseDefaultFiles();
app.UseStaticFiles();

app.Run();

// 프레임이 아직 없을 때 빈 이미지를 만들어 내보내면 화면이 "정상인데 새까만" 상태로
// 보인다. 503 으로 명시해서 UI 가 "대기 중"을 표시하게 한다.
static IResult Png(byte[]? bytes) =>
    bytes is null
        ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
        : Results.Bytes(bytes, "image/png");

// 검출기 설정 응답에 어느 방식으로 도는지(kind)를 함께 싣는다. 옵션 값만으로는 Arc 인지
// TwoPlane 인지 구분할 수 없어서, 파라미터를 아무리 맞춰도 반영이 안 되는 상황에서
// 원인을 좁힐 수 없다.
IResult DetectorView(RidgeDetectorOptions options) =>
    Results.Ok(new { kind = detectorKind, options });
