using HD.AMR.App.Communication;
using HD.AMR.App.Communication.Vision;
using HD.AMR.App.Communication.Weld;
using HD.AMR.App.Models;
using Microsoft.Extensions.Logging;
using OpenCvSharp.Aruco;

namespace HD.AMR.App.Service;

/// <summary>
/// ArUco 기반 눈-손(hand-eye) 캘리브레이션 오케스트레이션 — <c>T_T_C</c>(Tool TCP → 카메라 광학 프레임) 산출.
/// 기존 ArUco 장착 보정(2단계)이 입력으로 요구하는 값이며, 종전에는 CAD·가정값만 넣을 수 있었다.
///
/// 방법: 바닥에 고정한 ArUco 를 <b>AMR·리프트를 세워 둔 채</b> 코봇 자세만 바꿔 촬영한다.
/// <c>T_B_T(k)·X·T_C_Q(k) = 상수</c> 에서 AMR pose 가 소거되므로 <see cref="HandEyeSolver"/> 의
/// AX=XB 로 <c>T_A_B</c> 없이 풀린다.
///
/// 카메라에 컨트롤러 TOOL 이 없으면 <b>tool 0(플랜지)</b> 을 기준으로 잡아도 된다 — 중요한 것은
/// ArUco 장착 보정 화면과 <b>같은 tool 번호</b>를 쓰는 것뿐이다.
/// OpenCV 네이티브가 Windows 전용이므로 검출 경로는 플랫폼 가드가 걸려 있다.
/// </summary>
public class ArucoHandEyeService
{
    private const int SampleDelayMs = 120;
    private const double FrameStaleSeconds = 1.0;

    /// <summary>재투영 오차 상한(px) — 기존 ArUco 장착 보정 화면과 같은 기준.</summary>
    private const double MaxReprojErrPx = 3.0;

    private readonly CameraService _cam;
    private readonly CobotService _cobot;
    private readonly CalibrationService _calib;
    private readonly ILogger<ArucoHandEyeService> _logger;

    public ArucoHandEyeService(CameraService cam, CobotService cobot, CalibrationService calib,
        ILogger<ArucoHandEyeService> logger)
    {
        _cam = cam; _cobot = cobot; _calib = calib; _logger = logger;
    }

    /// <summary>
    /// 현재 코봇 자세에서 표본 1건을 캡처한다 — TCP pose(<c>T_B_T</c>)와 마커 pose(<c>T_C_Q</c>).
    /// 마커 pose 는 <paramref name="frames"/> 장을 평균해 검출 잡음을 줄인다.
    ///
    /// ⚠ <paramref name="tool"/> 은 <b>ArUco 장착 보정 화면과 반드시 같은 번호</b>여야 한다 —
    /// 산출된 T_T_C 는 그 tool 의 TCP 기준이고, 장착 보정이 같은 기준의 <c>T_B_T</c> 와 합성한다.
    /// </summary>
    public async Task<HandEyeSample> CaptureAsync(
        ArucoSettings settings, int tool, int frames = 5, CancellationToken ct = default)
    {
        EnsureDetectable();
        if (settings.SizeMm <= 0)
            throw new InvalidOperationException("ArUco 한 변의 실측 크기(mm)를 입력하세요.");
        if (!_cobot.IsConnected)
            throw new InvalidOperationException("코봇 미연결.");

        var intr = _cam.GetD2CParams();
        if (intr is not { IsValid: true })
            throw new InvalidOperationException("카메라 내부 파라미터를 읽을 수 없습니다.");

        // 캡처 전후 TCP pose 를 비교해 로봇이 움직였으면 표본을 버린다.
        var tcpBefore = await _cobot.Rpc.GetTcpPoseInBaseAsync(tool, ct);

        var poses = new List<double[]>();
        var reproj = new List<double>();
        int markerId = settings.MarkerId ?? 0;
        double? depthVsPnp = null;

        for (var i = 0; i < frames; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (i > 0) await Task.Delay(SampleDelayMs, ct);

            var color = _cam.LatestColor;
            if (color is null || DateTime.UtcNow - _cam.LastFrameAt > TimeSpan.FromSeconds(FrameStaleSeconds))
                throw new InvalidOperationException("카메라 프레임이 오래되었습니다 — 스트리밍 상태를 확인하세요.");

            var r = await Task.Run(() => ArucoPoseEstimator.DetectAndEstimate(
                color, intr, settings.SizeMm, markerId, settings.Dictionary), ct);
            if (r is null) continue;
            if (r.ReprojectionErrorPx > MaxReprojErrPx) continue;

            poses.Add(r.PoseCQ);
            reproj.Add(r.ReprojectionErrorPx);
            depthVsPnp ??= DepthMinusPnpMm(intr, r);
        }

        if (poses.Count < 3)
            throw new InvalidOperationException(
                $"유효 검출이 부족합니다({poses.Count}/{frames}) — 마커가 화면에 크고 선명하게 들어오도록 " +
                $"자세를 조정하세요(재투영 {MaxReprojErrPx:0}px 초과 프레임은 버립니다).");

        var tcpAfter = await _cobot.Rpc.GetTcpPoseInBaseAsync(tool, ct);
        double moved = Math.Sqrt(
            Math.Pow(tcpAfter[0] - tcpBefore[0], 2) +
            Math.Pow(tcpAfter[1] - tcpBefore[1], 2) +
            Math.Pow(tcpAfter[2] - tcpBefore[2], 2));
        if (moved > 1.0)
            throw new InvalidOperationException($"캡처 중 코봇이 움직였습니다(Δ{moved:0.#}mm) — 완전히 정지한 뒤 다시 캡처하세요.");

        // 위치는 산술 평균, 각도는 원형 평균.
        var markerPose = new double[6];
        for (var j = 0; j < 3; j++) markerPose[j] = poses.Average(p => p[j]);
        for (var j = 3; j < 6; j++) markerPose[j] = QrLocalization.CircularMeanDeg(poses.Select(p => p[j]).ToList());

        return new HandEyeSample
        {
            Index = 0,
            MarkerId = markerId,
            Tool = tool,
            TcpPose = tcpBefore,
            MarkerPose = markerPose,
            FramesUsed = poses.Count,
            ReprojErrPx = reproj.Average(),
            DepthMinusPnpMm = depthVsPnp,
            CapturedAtUtc = DateTime.UtcNow,
        };
    }

    /// <summary>표본으로 <c>T_T_C</c> 산출. 예외를 던지지 않고 결과에 성공/실패를 담는다.</summary>
    public HandEyeResult Solve(IEnumerable<HandEyeSample> samples)
    {
        var list = samples.ToList();
        var ids = list.Select(s => s.MarkerId).Distinct().ToList();
        if (ids.Count > 1)
            return HandEyeResult.Fail(
                $"표본에 서로 다른 마커 ID 가 섞여 있습니다({string.Join(", ", ids)}) — " +
                "한 마커만 고정해 두고 재촬영하세요.");

        // tool 이 섞이면 TCP 기준이 달라져 해가 무의미해진다 — 잔차로는 드러나지 않는다.
        var tools = list.Where(s => s.Tool.HasValue).Select(s => s.Tool!.Value).Distinct().ToList();
        if (tools.Count > 1)
            return HandEyeResult.Fail(
                $"표본 간 Tool 번호가 다릅니다({string.Join(", ", tools)}) — " +
                "T_T_C 는 특정 tool 의 TCP 기준이므로 한 번호로 통일해 재촬영하세요.");

        return HandEyeSolver.Solve(
            list.Select(s => s.TcpPose).ToList(),
            list.Select(s => s.MarkerPose).ToList());
    }

    private static void EnsureDetectable()
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("ArUco 검출은 Windows(OpenCV 네이티브)에서만 동작합니다.");
    }

    /// <summary>마커 중심의 깊이 실측(mm) − PnP 거리. 평면 PnP 의 가장 약한 축(깊이)을 교차 검증한다.</summary>
    private double? DepthMinusPnpMm(CameraD2CParams d2c, ArucoPoseResult pose)
    {
        var color = _cam.LatestColor;
        var depth = _cam.LatestDepth;
        if (color is null || depth is null) return null;
        try
        {
            var mapper = new DepthColorMapper(d2c, depth.Width, depth.Height, color.Width, color.Height);
            var (du, dv) = mapper.ColorToDepthApprox(pose.CenterU, pose.CenterV);
            var mm = _cam.GetLatestDepthMmAt(du / depth.Width, dv / depth.Height);
            return mm is null ? null : mm.Value - pose.PoseCQ[2];
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "깊이 교차검증 실패 — 무시");
            return null;
        }
    }
}

/// <summary>ArUco 마커 설정 — 사전 종류, 기대 ID, 실측 한 변 길이(mm).</summary>
public class ArucoSettings
{
    /// <summary>OpenCV 사전 종류. 기본 4×4_50 (프로젝트 제공 120 mm 마커와 일치).</summary>
    public PredefinedDictionaryName Dictionary { get; set; } = PredefinedDictionaryName.Dict4X4_50;

    /// <summary>기대 마커 ID. null 이면 화면에서 가장 큰 마커를 채택한다.</summary>
    public int? MarkerId { get; set; }

    /// <summary>검은 사각형 한 변의 실측 길이(mm). 이 값이 틀리면 거리가 비례로 틀어진다.</summary>
    public double SizeMm { get; set; } = 120;
}

/// <summary>
/// 눈-손 캘리브레이션 표본 1건 — 플랜지 pose(<c>T_B_F</c>)와 그때 본 마커 pose(<c>T_C_Q</c>).
/// JSON(<c>Calib.DepthHandEye.SamplesJson</c>)으로 보관한다.
/// </summary>
public class HandEyeSample
{
    public int Index { get; set; }

    /// <summary>검출된 마커 ID — 표본 간 혼용을 막기 위해 기록한다.</summary>
    public int MarkerId { get; set; }

    /// <summary>
    /// <c>T_B_T</c> — 해당 tool 의 TCP pose(BASE 기준, mm/도).
    /// 카메라에 컨트롤러 TOOL 이 없으면 <b>tool 0(플랜지)</b> 을 써도 된다 — 중요한 것은
    /// 장착 보정 화면과 <b>같은 tool 번호</b>를 쓰는 것뿐이다.
    /// </summary>
    public double[] TcpPose { get; set; } = new double[6];

    /// <summary>기준 tool 번호 — 표본 간 혼용을 막기 위해 기록한다.</summary>
    public int? Tool { get; set; }

    /// <summary>T_C_Q — 카메라 기준 마커 pose(mm/도).</summary>
    public double[] MarkerPose { get; set; } = new double[6];

    /// <summary>평균에 사용한 유효 프레임 수.</summary>
    public int FramesUsed { get; set; }

    /// <summary>재투영 RMS(px) — 검출 품질.</summary>
    public double ReprojErrPx { get; set; }

    /// <summary>깊이 실측 − PnP 거리(mm). 평면 PnP 의 약축(깊이) 교차검증. 깊이 무효면 null.</summary>
    public double? DepthMinusPnpMm { get; set; }

    public DateTime? CapturedAtUtc { get; set; }
}
