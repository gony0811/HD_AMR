using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.AMR.App.Communication.Vision;
using HD.AMR.App.Models;
using HD.AMR.App.Service;
using Microsoft.Extensions.DependencyInjection;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>바닥 ArUco의 q_z만 알고 T_A_B와 T_W_Q를 동시에 구하는 읽기 전용 캡처 페이지.</summary>
public sealed partial class ArucoMountCalibrationViewModel : ViewModelBase
{
    private readonly IServiceScopeFactory _scopes;
    private readonly AMRService _amr;
    private readonly CobotService _cobot;
    private readonly CameraService _camera;
    private readonly List<ArucoMountSample> _samples = new();
    private double[] _savedMount = new double[6];

    public Pose6 HandEye { get; } = new();
    public Pose6 Mount { get; } = new();
    public ObservableCollection<ArucoMountSampleRow> Rows { get; } = new();

    [ObservableProperty] private int _tool = 2;
    [ObservableProperty] private int _markerId;
    [ObservableProperty] private double _markerSizeMm = 120;
    [ObservableProperty] private double _markerQzMm;
    [ObservableProperty] private bool _qzConfirmed;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private ArucoMountCalibrationResult? _result;

    public ArucoMountCalibrationViewModel(IServiceScopeFactory scopes, AMRService amr, CobotService cobot, CameraService camera)
    { _scopes = scopes; _amr = amr; _cobot = cobot; _camera = camera; }

    public override void OnActivated() => _ = ReloadAsync();

    /// <summary>저장된 T_A_B/T_T_C 를 다시 읽는다 — 부모가 ② 탭 게이트 평가 전에 await 할 수 있게 분리.</summary>
    public async Task ReloadAsync()
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var c = scope.ServiceProvider.GetRequiredService<CalibrationService>();
            _savedMount = await c.GetMountAsync(); Mount.FromArray(_savedMount); HandEye.FromArray(await c.GetHandEyeAsync());
        }
        catch (Exception ex) { Fail($"설정 로드 실패: {ex.Message}"); }
        NotifyState();
    }

    /// <summary>① 단계에서 측정한 T_T_C 표시 — 이 화면에서는 편집하지 않는다(가정값 주입 방지).</summary>
    public string HandEyeText => HandEye.ToArray().All(v => v == 0)
        ? "미설정 — ① 핸드아이 측정 단계를 먼저 완료하세요."
        : $"X={HandEye.V0:0.00}  Y={HandEye.V1:0.00}  Z={HandEye.V2:0.00} mm   ·   " +
          $"Rx={HandEye.V3:0.000}  Ry={HandEye.V4:0.000}  Rz={HandEye.V5:0.000}°";

    public bool AmrConnected => _amr.IsConnected;
    public bool CobotConnected => _cobot.IsConnected;
    public bool CameraReady => _camera.LatestColor is not null && _camera.GetD2CParams() is not null;
    public string AmrPoseText => _amr.LatestStatus is { } s
        ? $"X {s.Pose.X * 1000:0.0} mm · Y {s.Pose.Y * 1000:0.0} mm · Yaw {s.Pose.Angle * 180 / Math.PI:0.00}°" : "AMR pose 없음";
    public string SampleSummary => $"{_samples.Count}개 · Yaw 범위 {CircularSpan():0}° · 위치 범위 {PositionSpan():0} mm";
    public bool CanCapture => !Busy && AmrConnected && CobotConnected && CameraReady && QzConfirmed && MarkerSizeMm > 0;
    public bool CanSolve => !Busy && _samples.Count >= 8 && QzConfirmed;
    public bool CanApply => Result?.Success == true && !Busy;

    [RelayCommand(CanExecute = nameof(CanCapture))]
    private async Task Capture()
    {
        Busy = true;
        try
        {
            var first = _amr.LatestStatus ?? throw new InvalidOperationException("AMR pose가 없습니다.");
            await Task.Delay(1200);
            var status = _amr.LatestStatus ?? throw new InvalidOperationException("AMR pose가 없습니다.");
            double moveMm = Math.Sqrt(Math.Pow((status.Pose.X - first.Pose.X) * 1000, 2) + Math.Pow((status.Pose.Y - first.Pose.Y) * 1000, 2));
            double moveDeg = Math.Abs((status.Pose.Angle - first.Pose.Angle) * 180 / Math.PI);
            if (moveMm > 5 || moveDeg > .2) throw new InvalidOperationException($"AMR이 정지하지 않았습니다(1.2초 동안 {moveMm:0.0} mm, {moveDeg:0.00}° 변화).");
            var frame = _camera.LatestColor ?? throw new InvalidOperationException("RGB 프레임이 없습니다.");
            if ((DateTime.UtcNow - frame.CapturedAt).TotalSeconds > 1) throw new InvalidOperationException("RGB 프레임이 오래되었습니다.");
            var intr = _camera.GetD2CParams() ?? throw new InvalidOperationException("RGB 내부 파라미터가 없습니다.");
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("ArUco 검출은 D435가 연결된 Windows 장비에서 실행하세요.");
            var aruco = await Task.Run(() => ArucoPoseEstimator.DetectAndEstimate(frame, intr, MarkerSizeMm, MarkerId));
            if (aruco is null) throw new InvalidOperationException($"DICT_4X4_50의 ID {MarkerId} 마커를 찾지 못했습니다.");
            if (aruco.ReprojectionErrorPx > 3) throw new InvalidOperationException($"재투영 오차 {aruco.ReprojectionErrorPx:0.00}px가 너무 큽니다. 초점·거리·마커 평탄도를 확인하세요.");
            var bt = await _cobot.Rpc.GetTcpPoseInBaseAsync(Tool);
            var wa = new[] { status.Pose.X * 1000, status.Pose.Y * 1000, 0.0, 0.0, 0.0, status.Pose.Angle * 180 / Math.PI };
            _samples.Add(new(_samples.Count, DateTime.UtcNow, wa, bt, aruco.PoseCQ, aruco.ReprojectionErrorPx));
            Rebuild(); Success($"표본 #{_samples.Count} 캡처 — 재투영 오차 {aruco.ReprojectionErrorPx:0.00}px. AMR 위치/yaw 또는 코봇 시점을 바꿔 반복하세요.");
        }
        catch (Exception ex) { Fail($"캡처 실패: {ex.Message}"); }
        finally { Busy = false; NotifyState(); }
    }

    [RelayCommand(CanExecute = nameof(CanSolve))]
    private void Solve()
    {
        Result = ArucoMountCalibration.Solve(_samples, HandEye.ToArray(), MarkerQzMm, _savedMount);
        if (Result.Success) Success($"산출 완료 — 위치 RMS {Result.TranslationRmsMm:0.0} mm, 회전 RMS {Result.RotationRmsDeg:0.00}°. 검토 후 결과 적용/저장하세요.");
        else Fail($"산출 실패: {Result.Error}");
        NotifyState();
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply() { Mount.FromArray(Result!.MountPoseAB); Success("산출 T_A_B를 편집란에 적용했습니다. 아직 저장되지 않았습니다."); }

    [RelayCommand]
    private async Task SaveMount()
    {
        try { using var s = _scopes.CreateScope(); await s.ServiceProvider.GetRequiredService<CalibrationService>().SaveMountAsync(Mount.ToArray()); _savedMount = Mount.ToArray(); Success("T_A_B를 설정 DB에 저장했습니다."); }
        catch (Exception ex) { Fail($"저장 실패: {ex.Message}"); }
    }

    [RelayCommand]
    private async Task SaveHandEye()
    {
        try { using var s = _scopes.CreateScope(); await s.ServiceProvider.GetRequiredService<CalibrationService>().SaveHandEyeAsync(HandEye.ToArray()); Success("T_T_C를 저장했습니다."); }
        catch (Exception ex) { Fail($"저장 실패: {ex.Message}"); }
    }

    [RelayCommand]
    private void Clear() { _samples.Clear(); Result = null; Rebuild(); Success("이 페이지의 표본을 모두 지웠습니다."); }

    [RelayCommand]
    private void Remove(int index) { var s = _samples.FirstOrDefault(x => x.Index == index); if (s is null) return; _samples.Remove(s); for (int i = 0; i < _samples.Count; i++) _samples[i] = _samples[i] with { Index = i }; Result = null; Rebuild(); }

    private void Rebuild()
    {
        Rows.Clear(); foreach (var s in _samples) Rows.Add(new(s.Index, s.AmrPoseWA[0], s.AmrPoseWA[1], s.AmrPoseWA[5], s.ReprojectionErrorPx));
        NotifyState();
    }
    private double PositionSpan() { double m = 0; for (int i = 0; i < _samples.Count; i++) for (int j = i + 1; j < _samples.Count; j++) { double dx = _samples[i].AmrPoseWA[0] - _samples[j].AmrPoseWA[0], dy = _samples[i].AmrPoseWA[1] - _samples[j].AmrPoseWA[1]; m = Math.Max(m, Math.Sqrt(dx * dx + dy * dy)); } return m; }
    private double CircularSpan() { var a = _samples.Select(s => (s.AmrPoseWA[5] % 360 + 360) % 360).Order().ToArray(); if (a.Length < 2) return 0; double g = a[0] + 360 - a[^1]; for (int i = 1; i < a.Length; i++) g = Math.Max(g, a[i] - a[i - 1]); return 360 - g; }
    private void NotifyState() { OnPropertyChanged(string.Empty); OnPropertyChanged(nameof(HandEyeText)); CaptureCommand.NotifyCanExecuteChanged(); SolveCommand.NotifyCanExecuteChanged(); ApplyCommand.NotifyCanExecuteChanged(); }
    private void Success(string s) { Message = s; IsError = false; }
    private void Fail(string s) { Message = s; IsError = true; }
}

public sealed record ArucoMountSampleRow(int Index, double X, double Y, double Yaw, double ErrorPx);
