using HD_AMR.Communication.Vision;
using HD_AMR.Communication.Weld;
using HD_AMR.Models;
using Microsoft.Extensions.Logging;

namespace HD_AMR.Service;

/// <summary>
/// QR 마커 기반 SLAM 위치 검증 오케스트레이션. 버튼 클릭 시 온디맨드로:
/// 컬러 프레임에서 QR 검출(<see cref="QrPoseEstimator"/>) → 등록 마커 매칭 →
/// 변환 체인 역산(<see cref="QrLocalization"/>) → SLAM pose 와 비교(Δx/Δy/Δθ).
/// N 프레임 평균으로 잡음을 줄이고, 측정 중 AMR 이동을 감지하면 실패 처리한다.
/// 실패는 한국어 메시지의 <see cref="InvalidOperationException"/> 으로 던진다(UI 에서 표시).
/// </summary>
public class QrLocalizationService
{
    private readonly CameraService _cam;
    private readonly CobotService _cobot;
    private readonly AMRService _amr;
    private readonly CalibrationService _calib;
    private readonly ILogger<QrLocalizationService> _logger;

    /// <summary>측정 중 AMR 이동 판정 한계(전/후 SLAM pose 비교).</summary>
    private const double MoveTolMm = 5.0;
    private const double MoveTolDeg = 0.2;
    private const int SampleDelayMs = 200;

    public QrLocalizationService(CameraService cam, CobotService cobot, AMRService amr,
        CalibrationService calib, ILogger<QrLocalizationService> logger)
    {
        _cam = cam; _cobot = cobot; _amr = amr; _calib = calib; _logger = logger;
    }

    /// <summary>현재 컬러 프레임에서 QR 을 한 번 검출해 디코딩 텍스트만 반환(마커 ID 등록 보조). 미검출이면 null.</summary>
    public async Task<string?> ReadQrTextAsync(CancellationToken ct = default)
    {
        var color = _cam.LatestColor ?? throw new InvalidOperationException("카메라 컬러 프레임 없음 — 스트리밍 상태를 확인하세요.");
        var det = await Task.Run(() => QrPoseEstimator.Detect(color), ct);
        return det?.Text;
    }

    /// <summary>측정 1버스트: ~200ms 간격 <paramref name="samples"/>회 검출·역산 후 평균.
    /// 등록 마커 중 검출된 QR(디코딩 텍스트 일치)을 사용한다.</summary>
    public async Task<QrVerificationResult> MeasureAsync(
        IReadOnlyList<QrMarkerReg> markers, MapRegistration reg,
        int samples = 5, CancellationToken ct = default)
    {
        var (d2c, handEye, mount, poseBefore) = await PrepareAsync(markers, ct);

        var xs = new List<double>();
        var ys = new List<double>();
        var ths = new List<double>();
        var zResids = new List<double>();
        var rolls = new List<double>();
        var pitches = new List<double>();
        string markerText = "";
        double? depthVsPnp = null;

        for (int i = 0; i < samples; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (i > 0) await Task.Delay(SampleDelayMs, ct);

            var (marker, pose, tBF) = await DetectOneAsync(markers, d2c, ct);
            markerText = marker.Text;

            var tWQ = QrLocalization.MarkerWorldPose(reg, marker);
            var r = QrLocalization.SolveAmrPose(tWQ, pose.PoseCQ, handEye, tBF, mount);
            xs.Add(r.Xmm); ys.Add(r.Ymm); ths.Add(r.ThetaDeg);
            zResids.Add(r.ZResidMm); rolls.Add(r.RollDeg); pitches.Add(r.PitchDeg);

            depthVsPnp ??= DepthMinusPnpMm(d2c, pose);
        }

        EnsureStationary(poseBefore);

        double mx = xs.Average(), my = ys.Average();
        double mth = QrLocalization.CircularMeanDeg(ths);
        double sx = Std(xs, mx), sy = Std(ys, my);
        double sth = Math.Sqrt(ths.Select(t => QrLocalization.AngleDiffDeg(t, mth)).Average(d => d * d));

        double slamX = poseBefore[0], slamY = poseBefore[1], slamTh = poseBefore[5];
        return new QrVerificationResult(
            markerText, samples,
            mx, my, mth,
            slamX, slamY, slamTh,
            slamX - mx, slamY - my, QrLocalization.AngleDiffDeg(slamTh, mth),
            sx, sy, sth,
            zResids.Average(), rolls.Average(), pitches.Average(),
            depthVsPnp, DateTime.Now);
    }

    /// <summary>핸드아이 부트스트랩: 현 지점의 SLAM 을 참으로 놓고 T_F_C 를 N 회 평균 역산.
    /// 결과는 저장하지 않는다 — UI 입력칸에 채우고 사용자가 저장으로 확정.</summary>
    public async Task<double[]> BootstrapHandEyeAsync(
        IReadOnlyList<QrMarkerReg> markers, MapRegistration reg,
        int samples = 5, CancellationToken ct = default)
    {
        var (d2c, _, mount, poseBefore) = await PrepareAsync(markers, ct, requireHandEye: false);

        var comp = new List<double[]>();
        for (int i = 0; i < samples; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (i > 0) await Task.Delay(SampleDelayMs, ct);

            var (marker, pose, tBF) = await DetectOneAsync(markers, d2c, ct);
            var tWQ = QrLocalization.MarkerWorldPose(reg, marker);
            var st = _amr.LatestStatus ?? throw new InvalidOperationException("AMR 상태 없음.");
            var tWA = MapCalibration.AmrPoseToMmDeg(st.Pose.X, st.Pose.Y, st.Pose.Angle);
            comp.Add(QrLocalization.SolveHandEye(tWQ, pose.PoseCQ, tBF, mount, tWA));
        }

        EnsureStationary(poseBefore);

        var result = new double[6];
        for (int j = 0; j < 3; j++) result[j] = comp.Average(p => p[j]);
        for (int j = 3; j < 6; j++) result[j] = QrLocalization.CircularMeanDeg(comp.Select(p => p[j]).ToList());
        return result;
    }

    /// <summary>공통 전제 검사 후 (D2C 파라미터, T_F_C, T_A_B, 시작 시점 SLAM pose[mm/deg])를 반환.</summary>
    private async Task<(CameraD2CParams d2c, double[] handEye, double[] mount, double[] poseBefore)>
        PrepareAsync(IReadOnlyList<QrMarkerReg> markers, CancellationToken ct, bool requireHandEye = true)
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("QR 검출은 Windows(OpenCV 네이티브)에서만 동작합니다.");
        if (markers.Count == 0)
            throw new InvalidOperationException("등록된 QR 마커가 없습니다 — 마커를 먼저 등록하세요.");
        if (!_cam.IsStreaming)
            throw new InvalidOperationException("카메라 스트리밍 중이 아닙니다.");
        if (!_cobot.IsConnected)
            throw new InvalidOperationException("코봇 미연결.");

        var d2c = _cam.GetD2CParams();
        if (d2c is not { IsValid: true })
            throw new InvalidOperationException("카메라 내부 파라미터를 읽을 수 없습니다.");

        var handEye = await _calib.GetHandEyeAsync();
        if (requireHandEye && handEye.All(v => v == 0))
            throw new InvalidOperationException("핸드아이 T_F_C 가 미설정입니다 — 값을 입력하거나 역산을 먼저 실행하세요.");

        var mount = await _calib.GetMountAsync();
        var st = _amr.LatestStatus ?? throw new InvalidOperationException("AMR 상태 없음 — 맵 pose를 읽을 수 없습니다.");
        var poseBefore = MapCalibration.AmrPoseToMmDeg(st.Pose.X, st.Pose.Y, st.Pose.Angle);
        return (d2c, handEye, mount, poseBefore);
    }

    /// <summary>프레임 1장 검출 + 등록 마커 매칭 + 플랜지 pose 조회.</summary>
    private async Task<(QrMarkerReg marker, QrPoseResult pose, double[] tBF)> DetectOneAsync(
        IReadOnlyList<QrMarkerReg> markers, CameraD2CParams d2c, CancellationToken ct)
    {
        var color = _cam.LatestColor;
        if (color is null || DateTime.UtcNow - _cam.LastFrameAt > TimeSpan.FromSeconds(1))
            throw new InvalidOperationException("카메라 프레임이 오래되었습니다 — 스트리밍 상태를 확인하세요.");

        var det = await Task.Run(() => QrPoseEstimator.Detect(color), ct)
                  ?? throw new InvalidOperationException("QR 미검출 — 코드가 화면 중앙에 크게 보이도록 코봇 자세를 조정하세요.");
        var marker = markers.FirstOrDefault(m => m.Text == det.Text)
                     ?? throw new InvalidOperationException($"미등록 QR 검출: \"{det.Text}\" — 마커 목록에 등록하세요.");
        if (marker.SizeMm <= 0)
            throw new InvalidOperationException($"마커 \"{marker.Text}\" 의 크기(mm)가 유효하지 않습니다.");

        var pose = QrPoseEstimator.EstimatePose(det, color.Width, color.Height, d2c, marker.SizeMm);
        var tBF = await _cobot.Rpc.GetTcpPoseInBaseAsync(0, ct);   // tool 0 = 플랜지
        return (marker, pose, tBF);
    }

    /// <summary>버스트 종료 시점 SLAM pose 가 시작 시점 대비 이동했으면 측정 무효.</summary>
    private void EnsureStationary(double[] poseBefore)
    {
        var st = _amr.LatestStatus ?? throw new InvalidOperationException("AMR 상태 없음.");
        var after = MapCalibration.AmrPoseToMmDeg(st.Pose.X, st.Pose.Y, st.Pose.Angle);
        double d = Math.Sqrt(Math.Pow(after[0] - poseBefore[0], 2) + Math.Pow(after[1] - poseBefore[1], 2));
        double dth = Math.Abs(QrLocalization.AngleDiffDeg(after[5], poseBefore[5]));
        if (d > MoveTolMm || dth > MoveTolDeg)
            throw new InvalidOperationException(
                $"측정 중 AMR 이동 감지(Δ{d:0.#}mm/{dth:0.##}°) — 정차 후 재시도하세요.");
    }

    /// <summary>QR 중심의 깊이 실측(mm) − PnP 거리(tvec.Z). 깊이 무효면 null. 큰 차이는 크기 오등록/검출 오류 신호.</summary>
    private double? DepthMinusPnpMm(CameraD2CParams d2c, QrPoseResult pose)
    {
        var color = _cam.LatestColor;
        var depth = _cam.LatestDepth;
        if (color is null || depth is null) return null;
        var mapper = new DepthColorMapper(d2c, depth.Width, depth.Height, color.Width, color.Height);
        var (du, dv) = mapper.ColorToDepthApprox(pose.CenterU, pose.CenterV);
        var mm = _cam.GetLatestDepthMmAt(du / depth.Width, dv / depth.Height);
        return mm is null ? null : mm.Value - pose.PoseCQ[2];
    }

    private static double Std(IReadOnlyList<double> v, double mean)
        => Math.Sqrt(v.Average(x => (x - mean) * (x - mean)));
}

/// <summary>QR 검증 결과. Δ = SLAM − QR 측정. σ = 버스트 내 표본 산포(코봇 진동/검출 잡음 지표).
/// 면외 잔차(z/roll/pitch)는 0 근처가 정상 — 크면 T_F_C/장착 오프셋 점검 필요.</summary>
public sealed record QrVerificationResult(
    string MarkerText, int SamplesUsed,
    double MeasXmm, double MeasYmm, double MeasThetaDeg,
    double SlamXmm, double SlamYmm, double SlamThetaDeg,
    double DXmm, double DYmm, double DThetaDeg,
    double StdXmm, double StdYmm, double StdThetaDeg,
    double ZResidMm, double RollDeg, double PitchDeg,
    double? DepthVsPnpMm, DateTime MeasuredAt);
