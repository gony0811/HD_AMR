using HD.AMR.App.Communication.Vision;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service;

/// <summary>
/// 현재 카메라 화면에서 ArUco 마커 중심이 <b>화면 중심(광축)에서 몇 mm 떨어져 있는지</b> 측정한다 —
/// ACS <c>moveToSeamStart</c> 도달 후, 면에 붙인 마커로 seam 시작점 접근의 실제 위치 오차를 기록하는 데 쓴다.
///
/// 마커 설정(사전·기대 ID·한 변 크기)은 ArUco 장착 보정 화면이 저장한 값(<see cref="CalibrationService.GetArucoSettingsAsync"/>)을
/// 쓰고, 기대 ID 가 없으면 가장 큰 마커를 고른다(<see cref="ArucoHandEyeService.CaptureAsync"/> 와 같은 규칙).
/// 편차는 컬러 광학 프레임의 PnP 위치(tvec) — X 오른쪽 +, Y 아래 +, Z 마커까지 거리 — 이고, 여러 프레임을 평균한다.
/// 실패도 예외 없이 <see cref="ArucoCenterOffset.Found"/>=false 와 사유로 돌려준다.
/// </summary>
public sealed class ArucoCenterOffsetMeter
{
    private const int SettleMs = 500;
    private const int Frames = 5;
    private const int FrameDelayMs = 100;
    private const double FrameStaleSeconds = 1.0;
    private const double MaxReprojErrPx = 3.0;

    private readonly CameraService _cam;
    private readonly CalibrationService _calib;
    private readonly ILogger<ArucoCenterOffsetMeter> _logger;

    public ArucoCenterOffsetMeter(CameraService cam, CalibrationService calib, ILogger<ArucoCenterOffsetMeter> logger)
    {
        _cam = cam;
        _calib = calib;
        _logger = logger;
    }

    public async Task<ArucoCenterOffset> MeasureAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return ArucoCenterOffset.Fail("ArUco 검출은 Windows(OpenCV 네이티브)에서만 동작");
        if (!_cam.IsStreaming)
            return ArucoCenterOffset.Fail("카메라 스트리밍 중 아님");
        var intr = _cam.GetD2CParams();
        if (intr is not { IsValid: true })
            return ArucoCenterOffset.Fail("카메라 내부 파라미터 없음");

        var settings = await _calib.GetArucoSettingsAsync();
        if (settings.SizeMm <= 0)
            return ArucoCenterOffset.Fail("ArUco 한 변 크기 미설정(ArUco 장착 보정 화면)");

        await Task.Delay(SettleMs, ct);   // 코봇 정지 직후 진동·자동노출 안정

        int? markerId = settings.MarkerId;
        var seen = new SortedSet<int>();
        var poses = new List<ArucoPoseResult>();
        int rejected = 0, tried = 0;
        double du = 0, dv = 0;

        for (var i = 0; i < Frames; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (i > 0) await Task.Delay(FrameDelayMs, ct);

            var color = _cam.LatestColor;
            if (color is null || DateTime.UtcNow - _cam.LastFrameAt > TimeSpan.FromSeconds(FrameStaleSeconds))
                continue;
            tried++;

            var infos = await Task.Run(() => ArucoPoseEstimator.DetectMarkerInfos(color, settings.Dictionary), ct);
            foreach (var info in infos) seen.Add(info.Id);
            if (markerId is null)
            {
                if (infos.Length == 0) continue;
                markerId = infos.MaxBy(m => m.AreaPx)!.Id;
            }

            var id = markerId.Value;
            var r = await Task.Run(() => ArucoPoseEstimator.DetectAndEstimate(
                color, intr, settings.SizeMm, id, settings.Dictionary), ct);
            if (r is null) continue;
            if (r.ReprojectionErrorPx > MaxReprojErrPx) { rejected++; continue; }

            poses.Add(r);
            // 화면 중심 = 주점(cx, cy) — 프레임 해상도가 보정 해상도와 다르면 비례 환산.
            double sx = intr.ColorW > 0 ? (double)color.Width / intr.ColorW : 1;
            double sy = intr.ColorH > 0 ? (double)color.Height / intr.ColorH : 1;
            du += r.CenterU - intr.ColorCx * sx;
            dv += r.CenterV - intr.ColorCy * sy;
        }

        var seenText = seen.Count > 0 ? string.Join(",", seen) : "없음";
        if (poses.Count == 0)
        {
            var why = tried == 0 ? "유효 카메라 프레임 없음(프레임 지연)"
                : rejected > 0 ? $"재투영 오차 {MaxReprojErrPx}px 초과로 {rejected}프레임 버림"
                : markerId is null ? "마커 미검출"
                : $"마커 ID {markerId} 미검출";
            return ArucoCenterOffset.Fail($"{why} (보인 ID: {seenText}, 사전 {settings.Dictionary})");
        }

        var n = poses.Count;
        var result = new ArucoCenterOffset(true, markerId,
            poses.Average(p => p.PoseCQ[0]), poses.Average(p => p.PoseCQ[1]), poses.Average(p => p.PoseCQ[2]),
            du / n, dv / n, poses.Average(p => p.ReprojectionErrorPx), n, Frames, seenText, null);
        _logger.LogInformation("ArUco 중심 편차: {Summary}", result.Describe());
        return result;
    }
}

/// <summary>ArUco 마커 중심의 화면 중심(광축) 대비 편차 — 컬러 광학 프레임 mm(X 오른쪽+, Y 아래+), Z=거리.</summary>
public sealed record ArucoCenterOffset(
    bool Found, int? MarkerId,
    double DxMm, double DyMm, double ZMm,
    double DuPx, double DvPx, double ReprojPx,
    int FramesUsed, int FramesTried, string SeenIds, string? Note)
{
    public static ArucoCenterOffset Fail(string note) => new(false, null, 0, 0, 0, 0, 0, 0, 0, 0, "", note);

    /// <summary>운영 로그용 상세 문자열.</summary>
    public string Describe() => Found
        ? $"ID {MarkerId} — 화면 중심 대비 X {DxMm:+0.0;-0.0}mm(오른쪽+) / Y {DyMm:+0.0;-0.0}mm(아래+), " +
          $"거리 {ZMm:0}mm, 픽셀 ({DuPx:+0;-0}, {DvPx:+0;-0})px, 재투영 {ReprojPx:0.00}px, 프레임 {FramesUsed}/{FramesTried}"
        : $"미검출 — {Note}";

    /// <summary>ACS resultDescription 용 요약.</summary>
    public string Summary() => Found
        ? $"ArUco#{MarkerId} 중심편차 X{DxMm:+0.0;-0.0}/Y{DyMm:+0.0;-0.0}mm @{ZMm:0}mm"
        : $"ArUco 미검출({Note})";
}
