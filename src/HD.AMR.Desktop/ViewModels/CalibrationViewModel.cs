using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.AMR.App.Service;
using Microsoft.Extensions.DependencyInjection;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>
/// QR 기준 AMR 정차 Pose 티칭. 기존 Calibration.razor 이식.
/// T_T_C · QR 정차 기준 저장/로드는 완전 동작(DB). <b>T_A_B 편집은 이 화면에서 제거됐다</b> —
/// 측정·수정은 SETTINGS ▸ 장착 보정 (T_A_B) 에서 하고 여기서는 읽기 전용으로 확인만 한다. QR 촬영·측정은
/// 카메라/OpenCV 하드웨어가 필요 — 장비 없는 환경(macOS)에서는 실패 메시지로 폴백한다.
/// 라이브 카메라 영상은 Tier 3(CameraView)에서 공용 컨트롤로 배선 예정(현재는 스트리밍 상태만 표시).
/// </summary>
public sealed partial class CalibrationViewModel : ViewModelBase
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AMRService _amr;
    private readonly CobotService _cobot;
    private readonly CameraService _camera;
    private readonly INavigationService _nav;
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _cts = new();

    public Pose6 HandEye { get; } = new();    // T_T_C

    // T_A_B 는 읽기 전용 표시 — 값 자체는 QR 측정 체인이 쓰므로 계속 로드한다.
    [ObservableProperty] private string _mountText = "(로드 중)";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(Ready))] private bool _mountIsZero = true;
    [ObservableProperty] private int _tool = 1;

    // QR 정차 기준
    [ObservableProperty] private string _qrText = "STOP-QR";
    [ObservableProperty] private double _qrSizeMm = 150;
    [ObservableProperty] private double _targetXmm;
    [ObservableProperty] private double _targetYmm;
    [ObservableProperty] private double _targetZmm;
    [ObservableProperty] private double _targetYawDeg;
    [ObservableProperty] private double _positionToleranceMm = 10;
    [ObservableProperty] private double _yawToleranceDeg = 0.5;

    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private MeasurementVm? _measurement;

    public CalibrationViewModel(IServiceScopeFactory scopeFactory,
        AMRService amr, CobotService cobot, CameraService camera, INavigationService nav)
    {
        _scopeFactory = scopeFactory;
        _amr = amr; _cobot = cobot; _camera = camera; _nav = nav;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RefreshStatus();
    }

    public override async void OnActivated()
    {
        _timer.Start();
        RefreshStatus();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var calib = scope.ServiceProvider.GetRequiredService<CalibrationService>();
            var mount = await calib.GetMountAsync();
            MountIsZero = mount.All(v => v == 0);
            MountText = MountIsZero
                ? "미설정 — 장착 보정 페이지에서 먼저 T_A_B 를 구하세요."
                : $"X={mount[0]:0.0}  Y={mount[1]:0.0}  Z={mount[2]:0.0} mm   ·   " +
                  $"Rx={mount[3]:0.000}  Ry={mount[4]:0.000}  Rz={mount[5]:0.000}°";
            HandEye.FromArray(await calib.GetHandEyeAsync());
            var r = await calib.GetQrStopReferenceAsync();
            QrText = r.Text; QrSizeMm = r.SizeMm;
            TargetXmm = r.TargetXmm; TargetYmm = r.TargetYmm; TargetZmm = r.TargetZmm; TargetYawDeg = r.TargetYawDeg;
            PositionToleranceMm = r.PositionToleranceMm; YawToleranceDeg = r.YawToleranceDeg;
        }
        catch (Exception ex) { Failure($"설정 로드 실패: {ex.Message}"); }
    }

    public override void OnDeactivated() { _timer.Stop(); }

    private void RefreshStatus()
    {
        OnPropertyChanged(nameof(AmrConnected));
        OnPropertyChanged(nameof(CobotConnected));
        OnPropertyChanged(nameof(CameraStreaming));
        OnPropertyChanged(nameof(Ready));
        OnPropertyChanged(nameof(SlamXText));
        OnPropertyChanged(nameof(SlamYText));
        OnPropertyChanged(nameof(SlamYawText));
    }

    public bool AmrConnected => _amr.IsConnected;
    public bool CobotConnected => _cobot.IsConnected;
    public bool CameraStreaming => _camera.IsStreaming;
    public bool Ready => _amr.IsConnected && _cobot.IsConnected && _camera.IsStreaming
                         && !HandEye.ToArray().All(v => v == 0) && !MountIsZero;

    public string SlamXText => _amr.LatestStatus is { } s ? $"{s.Pose.X * 1000:0.0} mm" : "-";
    public string SlamYText => _amr.LatestStatus is { } s ? $"{s.Pose.Y * 1000:0.0} mm" : "-";
    public string SlamYawText => _amr.LatestStatus is { } s ? $"{s.Pose.Angle * 180 / Math.PI:0.000}°" : "-";

    private QrStopReference BuildReference() => new()
    {
        Text = QrText, SizeMm = QrSizeMm,
        TargetXmm = TargetXmm, TargetYmm = TargetYmm, TargetZmm = TargetZmm, TargetYawDeg = TargetYawDeg,
        PositionToleranceMm = PositionToleranceMm, YawToleranceDeg = YawToleranceDeg,
    };

    private void ValidateReference()
    {
        if (string.IsNullOrWhiteSpace(QrText)) throw new InvalidOperationException("QR ID를 입력하세요.");
        if (QrSizeMm <= 0) throw new InvalidOperationException("QR 실측 크기는 0보다 커야 합니다.");
        if (PositionToleranceMm < 0 || YawToleranceDeg < 0) throw new InvalidOperationException("허용오차는 음수일 수 없습니다.");
    }

    [RelayCommand]
    private void OpenMountCalibration() => _nav.NavigateTo<MountCalibrationViewModel>();

    [RelayCommand]
    private async Task SaveHandEye()
    {
        try { await WithCalib(c => c.SaveHandEyeAsync(HandEye.ToArray())); Success("T_T_C를 저장했습니다."); }
        catch (Exception ex) { Failure($"T_T_C 저장 실패: {ex.Message}"); }
    }

    [RelayCommand]
    private async Task SaveReference()
    {
        try { ValidateReference(); await WithCalib(c => c.SaveQrStopReferenceAsync(BuildReference())); Measurement = null; Success("QR 정차 기준을 저장했습니다."); }
        catch (Exception ex) { Failure($"기준 저장 실패: {ex.Message}"); }
    }

    [RelayCommand]
    private async Task ReadQrId()
    {
        Busy = true;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var qr = scope.ServiceProvider.GetRequiredService<QrLocalizationService>();
            var text = await qr.ReadQrTextAsync(_cts.Token);
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("QR을 검출하지 못했습니다.");
            QrText = text; Success($"QR ID를 읽었습니다: {text}");
        }
        catch (Exception ex) { Failure($"QR ID 읽기 실패: {ex.Message}"); }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task Measure()
    {
        Busy = true;
        try
        {
            ValidateReference();
            using var scope = _scopeFactory.CreateScope();
            var qr = scope.ServiceProvider.GetRequiredService<QrLocalizationService>();
            var m = await qr.MeasureStopPoseAsync(BuildReference(), Tool, 5, _cts.Token);
            Measurement = new MeasurementVm(m, BuildReference());
            Success($"목표 SLAM Pose 계산 완료: X={m.TargetSlamXmm:0.0}mm, Y={m.TargetSlamYmm:0.0}mm, Yaw={m.TargetSlamYawDeg:0.000}°");
        }
        catch (Exception ex) { Failure($"QR 측정 실패: {ex.Message}"); }
        finally { Busy = false; }
    }

    private async Task WithCalib(Func<CalibrationService, Task> op)
    {
        using var scope = _scopeFactory.CreateScope();
        await op(scope.ServiceProvider.GetRequiredService<CalibrationService>());
    }

    private void Success(string t) { Message = t; IsError = false; }
    private void Failure(string t) { Message = t; IsError = true; }

    public override void Dispose() { base.Dispose(); _cts.Cancel(); _cts.Dispose(); }
}

/// <summary>측정 결과 표시용 래퍼.</summary>
public sealed class MeasurementVm
{
    private readonly QrStopPoseMeasurement _m;
    private readonly QrStopReference _r;
    public MeasurementVm(QrStopPoseMeasurement m, QrStopReference r) { _m = m; _r = r; }

    public bool IsCentered =>
        Math.Sqrt(_m.ErrorXmm * _m.ErrorXmm + _m.ErrorYmm * _m.ErrorYmm) <= _r.PositionToleranceMm
        && Math.Abs(_m.ErrorYawDeg) <= _r.YawToleranceDeg;

    public string Headline => IsCentered
        ? "현재 위치가 QR 정차 허용오차 이내입니다."
        : "현재 위치에서 계산한 HD_ACS 등록용 목표 SLAM Pose입니다.";

    public string MeasuredX => _m.MeasuredQrXmm.ToString("0.0");
    public string MeasuredY => _m.MeasuredQrYmm.ToString("0.0");
    public string MeasuredYaw => _m.MeasuredQrYawDeg.ToString("0.000");
    public string TargetX => _r.TargetXmm.ToString("0.0");
    public string TargetY => _r.TargetYmm.ToString("0.0");
    public string TargetYaw => _r.TargetYawDeg.ToString("0.000");
    public string ErrX => _m.ErrorXmm.ToString("+0.0;-0.0;0.0");
    public string ErrY => _m.ErrorYmm.ToString("+0.0;-0.0;0.0");
    public string ErrYaw => _m.ErrorYawDeg.ToString("+0.000;-0.000;0.000");
    public string SlamX => _m.TargetSlamXmm.ToString("0.000");
    public string SlamY => _m.TargetSlamYmm.ToString("0.000");
    public string SlamYaw => _m.TargetSlamYawDeg.ToString("0.000000");
    public string Stats => $"표본 {_m.SamplesUsed} · σ={_m.StdTargetXmm:0.00}/{_m.StdTargetYmm:0.00} mm, {_m.StdTargetYawDeg:0.000}° · 재투영 RMS={_m.ReprojectionRmsPx:0.00} px"
        + (_m.DepthVsPnpMm is { } dz ? $" · Depth−PnP={dz:0.0} mm" : "");
}
