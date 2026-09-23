using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.AMR.App.Communication;
using HD.AMR.App.Service;
using Microsoft.Extensions.DependencyInjection;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>
/// ACS 월드 용접선의 예상 위치를 T_W_A·T_A_B·T_B_T·T_T_C 체인으로 컬러 영상에 투영하고,
/// 예상점 주변 물리 ROI의 Depth 유효성을 확인하는 개발용 초안. 모션 명령은 의도적으로 보내지 않는다.
/// </summary>
public sealed partial class SeamLocalizationTestViewModel : ViewModelBase
{
    private readonly CameraService _camera;
    private readonly AMRService _amr;
    private readonly CobotService _cobot;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _cts = new();
    private bool _polling;

    [ObservableProperty] private Bitmap? _colorImage;
    [ObservableProperty] private double _startX = 12.510;
    [ObservableProperty] private double _startY = 5.980;
    [ObservableProperty] private double _startZ = 1.420;
    [ObservableProperty] private double _endX = 13.310;
    [ObservableProperty] private double _endY = 5.980;
    [ObservableProperty] private double _endZ = 1.420;
    [ObservableProperty] private double _roiHalfWidthMm = 200;
    [ObservableProperty] private int _tool = 1;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _statusText = "분석 대기";
    [ObservableProperty] private string _chainText = "—";
    [ObservableProperty] private string _cameraPointText = "—";
    [ObservableProperty] private string _roiStatsText = "—";
    [ObservableProperty] private bool _hasProjection;
    [ObservableProperty] private double _roiX;
    [ObservableProperty] private double _roiY;
    [ObservableProperty] private double _roiW;
    [ObservableProperty] private double _roiH;
    [ObservableProperty] private double _targetU;
    [ObservableProperty] private double _targetV;
    [ObservableProperty] private int _markerId;
    [ObservableProperty] private double _markerSizeMm = 120;
    [ObservableProperty] private double _referenceGxMm;
    [ObservableProperty] private double _referenceGyMm;
    [ObservableProperty] private double _referenceGzMm;
    [ObservableProperty] private double _referenceGrxDeg;
    [ObservableProperty] private double _referenceGryDeg;
    [ObservableProperty] private double _referenceGrzDeg;
    [ObservableProperty] private string _arucoStatusText = "캡처 대기";
    [ObservableProperty] private string _arucoMeasuredPoseText = "—";
    [ObservableProperty] private string _arucoErrorText = "—";
    [ObservableProperty] private string _arucoQualityText = "—";

    public bool IsCameraStreaming => _camera.IsStreaming;
    public bool IsAmrConnected => _amr.IsConnected;
    public bool IsCobotConnected => _cobot.IsConnected;

    public SeamLocalizationTestViewModel(CameraService camera, AMRService amr, CobotService cobot,
        IServiceScopeFactory scopeFactory)
    {
        _camera = camera;
        _amr = amr;
        _cobot = cobot;
        _scopeFactory = scopeFactory;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += async (_, _) => await PollFrameAsync();
    }

    public override void OnActivated()
    {
        _timer.Start();
        NotifyHardware();
        _ = LoadArucoSettingsAsync();
    }

    private async Task LoadArucoSettingsAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var saved = await scope.ServiceProvider.GetRequiredService<CalibrationService>().GetArucoSettingsAsync();
            MarkerId = saved.MarkerId ?? 0;
            MarkerSizeMm = saved.SizeMm;
        }
        catch
        {
            // 공통 설정을 읽지 못해도 화면 기본값으로 시험할 수 있다.
        }
    }

    public override void OnDeactivated()
    {
        _timer.Stop();
        ColorImage = null;
    }

    public override void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        base.Dispose();
    }

    private void NotifyHardware()
    {
        OnPropertyChanged(nameof(IsCameraStreaming));
        OnPropertyChanged(nameof(IsAmrConnected));
        OnPropertyChanged(nameof(IsCobotConnected));
    }

    private async Task PollFrameAsync()
    {
        NotifyHardware();
        if (_polling || !_camera.IsStreaming) return;
        _polling = true;
        try
        {
            var bytes = await _camera.GetLatestColorJpegAsync(_camera.Settings.JpegQuality, _cts.Token);
            if (bytes is { Length: > 0 })
            {
                using var ms = new MemoryStream(bytes);
                ColorImage = new Bitmap(ms);
            }
        }
        catch { /* 다음 프레임에서 재시도 */ }
        finally { _polling = false; }
    }

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        Busy = true;
        HasProjection = false;
        try
        {
            if (_amr.LatestStatus is not { } amrStatus)
                throw new InvalidOperationException("AMR SLAM 자세가 없습니다.");
            if (!_cobot.IsConnected)
                throw new InvalidOperationException("코봇 RPC가 연결되지 않았습니다.");
            if (!_camera.IsStreaming || _camera.LatestColor is not { } color)
                throw new InvalidOperationException("컬러/Depth 카메라 스트림을 먼저 시작하세요.");
            var intr = _camera.GetD2CParams();
            if (intr is not { IsValid: true })
                throw new InvalidOperationException("카메라 내참수/Depth↔Color 보정값이 없습니다.");

            using var scope = _scopeFactory.CreateScope();
            var calibration = scope.ServiceProvider.GetRequiredService<CalibrationService>();
            var tABPose = await calibration.GetMountAsync();
            var tTCPose = await calibration.GetHandEyeAsync();
            var calibratedTool = await calibration.GetHandEyeToolAsync();
            if (tABPose.All(v => v == 0)) throw new InvalidOperationException("T_A_B가 미설정입니다.");
            if (tTCPose.All(v => v == 0)) throw new InvalidOperationException("T_T_C가 미설정입니다.");
            if (calibratedTool is { } savedTool && (int)Math.Round(savedTool) != Tool)
                throw new InvalidOperationException($"T_T_C 기준 Tool #{savedTool:0}과 선택 Tool #{Tool}이 다릅니다.");

            var tWAPose = MapCalibration.AmrPoseToMmDeg(
                amrStatus.Pose.X, amrStatus.Pose.Y, amrStatus.Pose.Angle);
            var tBTPose = await _cobot.Rpc.GetTcpPoseInBaseAsync(Tool, _cts.Token);
            var tWC = FrameMath.Multiply(FrameMath.PoseToMatrix(tWAPose), FrameMath.PoseToMatrix(tABPose));
            tWC = FrameMath.Multiply(tWC, FrameMath.PoseToMatrix(tBTPose));
            tWC = FrameMath.Multiply(tWC, FrameMath.PoseToMatrix(tTCPose));

            // VDA 계약의 seamStartW/endW 단위는 m. 중점을 mm로 바꿔 컬러 광학 프레임으로 변환한다.
            var pW = new[] { (StartX + EndX) * 500.0, (StartY + EndY) * 500.0, (StartZ + EndZ) * 500.0 };
            var pC = TransformPoint(FrameMath.Invert(tWC), pW);
            if (pC[2] <= 1) throw new InvalidOperationException($"예상점이 카메라 뒤에 있습니다(Zc={pC[2]:0.0}mm).");

            double sx = intr.ColorW > 0 ? (double)color.Width / intr.ColorW : 1;
            double sy = intr.ColorH > 0 ? (double)color.Height / intr.ColorH : 1;
            double fx = intr.ColorFx * sx, fy = intr.ColorFy * sy;
            double cx = intr.ColorCx * sx, cy = intr.ColorCy * sy;
            double px = fx * pC[0] / pC[2] + cx;
            double py = fy * pC[1] / pC[2] + cy;
            if (px < 0 || px >= color.Width || py < 0 || py >= color.Height)
                throw new InvalidOperationException($"예상점이 영상 밖입니다(u={px:0.0}, v={py:0.0}).");

            double halfPxX = fx * RoiHalfWidthMm / pC[2];
            double halfPxY = fy * RoiHalfWidthMm / pC[2];
            double x0 = Math.Clamp((px - halfPxX) / color.Width, 0, 1);
            double y0 = Math.Clamp((py - halfPxY) / color.Height, 0, 1);
            double x1 = Math.Clamp((px + halfPxX) / color.Width, 0, 1);
            double y1 = Math.Clamp((py + halfPxY) / color.Height, 0, 1);

            TargetU = px / color.Width; TargetV = py / color.Height;
            RoiX = x0; RoiY = y0; RoiW = x1 - x0; RoiH = y1 - y0;
            HasProjection = RoiW > 0 && RoiH > 0;
            var stats = _camera.ComputeDepthRoiStatsForColorRoi(RoiX, RoiY, RoiW, RoiH);

            ChainText = $"T_W_A [{tWAPose[0]:0},{tWAPose[1]:0},{tWAPose[5]:0.0}°] · " +
                        $"Tool #{Tool} · T_T_C [{tTCPose[0]:0.0},{tTCPose[1]:0.0},{tTCPose[2]:0.0}] mm";
            CameraPointText = $"예상 seam 중심 C = [{pC[0]:0.0}, {pC[1]:0.0}, {pC[2]:0.0}] mm · pixel=({px:0.0},{py:0.0})";
            RoiStatsText = stats is null
                ? "Depth 통계 없음"
                : $"깊이 min/avg/max {stats.MinMm}/{stats.AvgMm:0}/{stats.MaxMm} mm · 유효 {stats.ValidRatio:P0} ({stats.ValidCount}/{stats.TotalCount})";
            StatusText = stats is { ValidRatio: >= 0.5 }
                ? "ROI 투영 성공 — 다음 단계에서 이 영역에 용접선 검출기를 연결할 수 있습니다."
                : "ROI는 투영됐지만 유효 Depth가 부족합니다.";
        }
        catch (Exception ex)
        {
            StatusText = $"분석 실패: {ex.Message}";
            RoiStatsText = "—";
        }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task ReadArucoAsync()
    {
        Busy = true;
        try
        {
            if (_amr.LatestStatus is not { } amrStatus)
                throw new InvalidOperationException("AMR SLAM 자세가 없습니다.");
            if (!_camera.IsStreaming)
                throw new InvalidOperationException("컬러/Depth 카메라 스트림을 먼저 시작하세요.");
            if (!_cobot.IsConnected)
                throw new InvalidOperationException("코봇 RPC가 연결되지 않았습니다.");
            if (MarkerSizeMm <= 0)
                throw new InvalidOperationException("마커 검은 사각형 한 변의 실측 크기를 입력하세요.");

            ArucoStatusText = "ArUco 5프레임을 캡처하고 있습니다…";
            ArucoMeasuredPoseText = ArucoErrorText = ArucoQualityText = "—";

            using var scope = _scopeFactory.CreateScope();
            var calibration = scope.ServiceProvider.GetRequiredService<CalibrationService>();
            var capture = scope.ServiceProvider.GetRequiredService<ArucoHandEyeService>();
            var tABPose = await calibration.GetMountAsync();
            var tTCPose = await calibration.GetHandEyeAsync();
            var calibratedTool = await calibration.GetHandEyeToolAsync();
            var registration = await calibration.GetRegistrationAsync();
            var commonAruco = await calibration.GetArucoSettingsAsync();

            if (tABPose.All(v => v == 0)) throw new InvalidOperationException("T_A_B가 미설정입니다.");
            if (tTCPose.All(v => v == 0)) throw new InvalidOperationException("T_T_C가 미설정입니다.");
            if (registration is null) throw new InvalidOperationException("도면↔SLAM 정합 T_W_G가 미설정입니다.");
            if (calibratedTool is { } savedTool && (int)Math.Round(savedTool) != Tool)
                throw new InvalidOperationException($"T_T_C 기준 Tool #{savedTool:0}과 선택 Tool #{Tool}이 다릅니다.");

            var settings = new ArucoSettings
            {
                Dictionary = commonAruco.Dictionary,
                MarkerId = MarkerId,
                SizeMm = MarkerSizeMm,
            };
            var sample = await capture.CaptureAsync(settings, Tool, frames: 5, _cts.Token);

            var tWAPose = MapCalibration.AmrPoseToMmDeg(
                amrStatus.Pose.X, amrStatus.Pose.Y, amrStatus.Pose.Angle);
            var tWQ = FrameMath.Multiply(FrameMath.PoseToMatrix(tWAPose), FrameMath.PoseToMatrix(tABPose));
            tWQ = FrameMath.Multiply(tWQ, FrameMath.PoseToMatrix(sample.TcpPose));
            tWQ = FrameMath.Multiply(tWQ, FrameMath.PoseToMatrix(tTCPose));
            tWQ = FrameMath.Multiply(tWQ, FrameMath.PoseToMatrix(sample.MarkerPose));

            var tWGPose = new[] { registration.Tx, registration.Ty, 0.0, 0.0, 0.0, registration.ThetaDeg };
            var tGQ = FrameMath.Multiply(FrameMath.Invert(FrameMath.PoseToMatrix(tWGPose)), tWQ);
            var measured = FrameMath.MatrixToPose(tGQ);

            var reference = new[]
            {
                ReferenceGxMm, ReferenceGyMm, ReferenceGzMm,
                ReferenceGrxDeg, ReferenceGryDeg, ReferenceGrzDeg,
            };
            var dx = measured[0] - reference[0];
            var dy = measured[1] - reference[1];
            var dz = measured[2] - reference[2];
            var posError = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            var drx = QrLocalization.AngleDiffDeg(measured[3], reference[3]);
            var dry = QrLocalization.AngleDiffDeg(measured[4], reference[4]);
            var drz = QrLocalization.AngleDiffDeg(measured[5], reference[5]);

            ArucoMeasuredPoseText = $"역산 T_G_Q = [{measured[0]:0.0}, {measured[1]:0.0}, {measured[2]:0.0}] mm / " +
                                    $"[{measured[3]:0.00}, {measured[4]:0.00}, {measured[5]:0.00}]°";
            ArucoErrorText = $"기준 대비 Δ = [{dx:+0.0;-0.0;0.0}, {dy:+0.0;-0.0;0.0}, {dz:+0.0;-0.0;0.0}] mm " +
                             $"(|Δp|={posError:0.0} mm) / ΔR=[{drx:+0.00;-0.00;0.00}, {dry:+0.00;-0.00;0.00}, {drz:+0.00;-0.00;0.00}]°";
            ArucoQualityText = $"ID {sample.MarkerId} · 유효 {sample.FramesUsed}/5 프레임 · 재투영 RMS {sample.ReprojErrPx:0.00}px" +
                               (sample.DepthMinusPnpMm is { } d ? $" · Depth−PnP {d:+0.0;-0.0;0.0} mm" : string.Empty) +
                               $" · T_W_G 정합 RMS {registration.RmsMm:0.0} mm";
            ArucoStatusText = "마커의 도면 좌표 역산 완료 — 아래 기준 좌표는 정합에 사용하지 않은 독립 실측값과 비교하세요.";
        }
        catch (Exception ex)
        {
            ArucoStatusText = $"역산 실패: {ex.Message}";
            ArucoMeasuredPoseText = ArucoErrorText = ArucoQualityText = "—";
        }
        finally { Busy = false; }
    }

    private static double[] TransformPoint(double[,] t, double[] p) =>
    [
        t[0, 0] * p[0] + t[0, 1] * p[1] + t[0, 2] * p[2] + t[0, 3],
        t[1, 0] * p[0] + t[1, 1] * p[1] + t[1, 2] * p[2] + t[1, 3],
        t[2, 0] * p[0] + t[2, 1] * p[1] + t[2, 2] * p[2] + t[2, 3],
    ];
}
