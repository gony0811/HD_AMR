using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.AMR.App.Communication;
using HD.AMR.App.Models;
using HD.AMR.App.Service;
using Microsoft.Extensions.DependencyInjection;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>
/// 레이저 변위 센서(EtherNet/IP · 3채널). 기존 LaserDisplacementSensor.razor 이식 —
/// 연결/측정값·영점, 삼각형 평면 중심 pose, 평행 기준(장착 틸트 보정), 툴 자세 보정(Rx/Ry 적용),
/// 헤드 위치 캘리브레이션(틸트 응답 자동측정), 로봇 TCP 캘리브레이션(레이저 보조 터치), Input Assembly hex 진단.
/// </summary>
public sealed partial class LaserViewModel : ViewModelBase
{
    private readonly LaserDisplacementSensorService _svc;
    private readonly CobotService _cobot;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DispatcherTimer _timer;
    private const string HeadPlaneParamKey = "Laser.TouchTcp.HeadPlaneZmm";

    public ObservableCollection<LaserChannelRow> Readings { get; } = new();
    public ObservableCollection<string> CalLog { get; } = new();
    public ObservableCollection<TouchRow> Touches { get; } = new();

    private PlanePose _pose = PlanePose.Invalid("연결 대기 중.");
    private PlanePose _rawPose = PlanePose.Invalid("연결 대기 중.");

    // 평행 기준
    [ObservableProperty] private bool _capturingRef;
    [ObservableProperty] private string? _refMsg;
    [ObservableProperty] private bool _refOk;
    // 툴 자세 보정
    [ObservableProperty] private double _velPct = 5;
    [ObservableProperty] private bool _applying;
    [ObservableProperty] private string? _cobotMsg;
    // 헤드 캘리브레이션
    [ObservableProperty] private double _calTiltDeg = 2.0;
    [ObservableProperty] private int _calSamples = 5;
    [ObservableProperty] private double _calVelPct = 5;
    [ObservableProperty] private bool _calSignCheck = true, _calAutoLevel = true;
    [ObservableProperty] private bool _calibrating;
    [ObservableProperty] private LaserHeadCalibrationResult? _calResult;
    private CancellationTokenSource? _calCts;
    // TCP 캘리브레이션
    private readonly TcpTouchCalibrator _tcpCal = new();
    [ObservableProperty] private int _tcpModeIndex;          // 0 기준Z 고정, 1 h 고정
    [ObservableProperty] private double? _tcpRefZ, _tcpFixedH;
    [ObservableProperty] private bool _tcpRecording, _tcpCapturingH, _tcpWriting, _tcpConfirm, _tcpAllowTool1;
    [ObservableProperty] private int _tcpWriteId = 3;
    [ObservableProperty] private TcpTouchCalibrationResult? _tcpResult;
    [ObservableProperty] private string? _tcpMsg;
    [ObservableProperty] private bool _tcpMsgOk;
    // 진단
    [ObservableProperty] private string _hexDump = "(프레임 없음)";

    public LaserViewModel(LaserDisplacementSensorService svc, CobotService cobot, IServiceScopeFactory scopeFactory)
    {
        _svc = svc; _cobot = cobot; _scopeFactory = scopeFactory;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(50, svc.Settings.UiRefreshMs)) };
        _timer.Tick += (_, _) => Refresh();
    }

    public override async void OnActivated()
    {
        _timer.Start(); Refresh();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            TcpFixedH = await scope.ServiceProvider.GetRequiredService<ParameterService>().GetDoubleAsync(HeadPlaneParamKey);
        }
        catch { /* h 고정 모드 사용 시 직접 입력 */ }
    }
    public override void OnDeactivated() => _timer.Stop();

    // ── 상태(폴링) ──
    public bool IsConnected => _svc.IsConnected;
    public string Target => $"대상: {_svc.Settings.IpAddress}:{_svc.Settings.Port}";
    public string? LastError => _svc.LastError;
    public string Unit => _svc.Settings.MeasurementUnit;
    public bool PoseValid => _pose.Valid;
    public string PoseNote => _pose.Note ?? "";
    public string PX => _pose.X.ToString("F3"); public string PY => _pose.Y.ToString("F3"); public string PZ => _pose.Z.ToString("F3");
    public string PRx => _pose.Rx.ToString("F3"); public string PRy => _pose.Ry.ToString("F3");
    public string NormalText => $"법선(툴): ({_pose.Normal[0]:F4}, {_pose.Normal[1]:F4}, {_pose.Normal[2]:F4})";
    public bool HasTiltRef => _svc.TiltRef is not null;
    public string RawRefText => _svc.TiltRef is { } tr && _rawPose.Valid
        ? $"원시(보정 전): Rx={_rawPose.Rx:F3}° Ry={_rawPose.Ry:F3}° · 기준 Rx={tr.RxDeg:F3}° Ry={tr.RyDeg:F3}° ({tr.CapturedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm})" : "";
    public bool CobotConnected => _cobot.IsConnected;
    public bool ServoOn => _cobot.IsServoEnabled;
    // 실장비 검증 부호: +Rx 회전 → 측정 Rx −(gain≈−1) → offset=+Rx; +Ry 회전 → 측정 Ry +(gain≈+1) → offset=−Ry.
    public double AppliedRx => _pose.Rx;
    public double AppliedRy => -_pose.Ry;
    public string AppliedRxText => AppliedRx.ToString("F3"); public string AppliedRyText => AppliedRy.ToString("F3");
    public bool CanCaptureRef => IsConnected && _rawPose.Valid && !CapturingRef && !Applying && !Calibrating;
    public bool CanApply => CobotConnected && ServoOn && PoseValid && !Applying && !Calibrating;
    public bool CanCalibrate => IsConnected && CobotConnected && ServoOn && PoseValid && !Calibrating && !Applying;
    public bool TcpFixH => TcpModeIndex == 1;
    public int TouchCount => _tcpCal.Touches.Count;
    public bool CanRecordTouch => IsConnected && CobotConnected && PoseValid && !TcpRecording;
    public bool CanCaptureH => IsConnected && PoseValid && !TcpRecording && !TcpCapturingH;
    public bool CanWriteTcp => CobotConnected && TcpConfirm && !TcpWriting && TcpResult is { Success: true } && (TcpWriteId != 1 || TcpAllowTool1);
    public string TouchMeanText => _tcpCal.Touches.Count > 0 ? _tcpCal.Touches.Average(t => t.MeanDistanceMm).ToString("F0") : "—";
    public string TipZLabel => TcpFixH ? "Tip Z (산출)" : "Tip Z (기준값)";
    public string HLabel => TcpFixH ? "h (고정값)" : "h (추정)";

    private void Refresh()
    {
        if (_svc.IsConnected)
        {
            var list = _svc.GetReadings();
            for (var i = 0; i < list.Count; i++)
            {
                if (i < Readings.Count) Readings[i].Update(list[i]); else Readings.Add(new LaserChannelRow(list[i]));
            }
            while (Readings.Count > list.Count) Readings.RemoveAt(Readings.Count - 1);
            _pose = _svc.GetPlanePose();
            _rawPose = _svc.GetRawPlanePose();
            HexDump = FormatHex(_svc.SnapshotInputAssembly());
        }
        OnPropertyChanged(string.Empty);
        ApplyCorrectionCommand.NotifyCanExecuteChanged(); StartCalibrationCommand.NotifyCanExecuteChanged();
        CaptureReferenceCommand.NotifyCanExecuteChanged(); RecordTouchCommand.NotifyCanExecuteChanged();
        CaptureHeadPlaneCommand.NotifyCanExecuteChanged(); WriteTcpToolCommand.NotifyCanExecuteChanged();
    }

    private static string FormatHex(byte[]? data)
    {
        if (data is null || data.Length == 0) return "(프레임 없음)";
        var sb = new StringBuilder();
        for (var i = 0; i < data.Length; i += 16)
        {
            sb.Append(i.ToString("D3")).Append(": ");
            for (var j = i; j < i + 16 && j < data.Length; j++) sb.Append(data[j].ToString("X2")).Append(' ');
            sb.Append('\n');
        }
        return sb.ToString();
    }

    // ── 영점 ──
    [RelayCommand] private void ZeroSet(LaserChannelRow r) { _svc.ZeroSet(r.Channel); Refresh(); }
    [RelayCommand] private void ZeroReset(LaserChannelRow r) { _svc.ZeroReset(r.Channel); Refresh(); }
    [RelayCommand] private void ZeroSetAll() { _svc.ZeroSetAll(); Refresh(); }
    [RelayCommand] private void ZeroResetAll() { _svc.ZeroResetAll(); Refresh(); }

    // ── 평행 기준 ──
    [RelayCommand(CanExecute = nameof(CanCaptureRef))]
    private async Task CaptureReference()
    {
        CapturingRef = true; RefMsg = null;
        try
        {
            var (ok, msg, warn) = await _svc.CaptureTiltReferenceAsync();
            RefOk = ok; RefMsg = string.IsNullOrEmpty(warn) ? msg : $"{msg}\n⚠ {warn}";
        }
        catch (Exception ex) { RefOk = false; RefMsg = $"기준 저장 오류: {ex.Message}"; }
        finally { CapturingRef = false; }
    }

    [RelayCommand]
    private async Task ClearReference()
    {
        try { await _svc.ClearTiltReferenceAsync(); RefOk = true; RefMsg = "평행 기준을 해제했습니다."; }
        catch (Exception ex) { RefOk = false; RefMsg = $"기준 해제 오류: {ex.Message}"; }
    }

    // ── 툴 자세 보정: 측정 평면 기울기를 tool=1 회전 오프셋으로 적용(위치 고정, Rz 미적용) ──
    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyCorrection()
    {
        Applying = true; CobotMsg = null;
        try
        {
            var offset = new[] { 0.0, 0.0, 0.0, AppliedRx, AppliedRy, 0.0 };
            var anchor = await _cobot.Rpc.GetTcpPoseInBaseAsync(1);
            var rc = await _cobot.Rpc.MoveByToolOffsetAsync(anchor, user: 0, offset, tool: 1, vel: Math.Clamp(VelPct, 1, 30));
            CobotMsg = rc == 0 ? $"보정 적용 완료 (Rx={AppliedRx:0.###}°, Ry={AppliedRy:0.###}°)." : $"보정 이동 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.";
        }
        catch (Exception ex) { CobotMsg = $"보정 적용 오류: {ex.Message}"; }
        finally { Applying = false; }
    }

    // ── 헤드 위치 캘리브레이션(로봇이 움직임) ──
    [RelayCommand(CanExecute = nameof(CanCalibrate))]
    private async Task StartCalibration()
    {
        Calibrating = true; CalResult = null; CalLog.Clear();
        _calCts = new CancellationTokenSource();
        try
        {
            var opts = new LaserHeadCalibrationOptions
            {
                TiltDeg = CalTiltDeg, SamplesPerPoint = CalSamples, VelPct = CalVelPct,
                VerifyReadingSign = CalSignCheck, AutoLevel = CalAutoLevel,
            };
            using var scope = _scopeFactory.CreateScope();
            var routine = scope.ServiceProvider.GetRequiredService<LaserHeadCalibrationRoutine>();
            CalResult = await routine.RunAsync(opts, msg => Dispatcher.UIThread.Post(() => CalLog.Add(msg)), _calCts.Token);
        }
        catch (Exception ex) { CalResult = LaserHeadCalibrationResult.Fail($"예기치 못한 오류: {ex.Message}"); }
        finally { Calibrating = false; _calCts.Dispose(); _calCts = null; }
    }
    [RelayCommand] private void AbortCalibration() => _calCts?.Cancel();

    // ── TCP 캘리브레이션(레이저 보조 터치, 로봇은 수동 조그) ──
    partial void OnTcpModeIndexChanged(int value) { OnPropertyChanged(nameof(TcpFixH)); ResolveTcp(); }
    partial void OnTcpRefZChanged(double? value) => ResolveTcp();
    partial void OnTcpFixedHChanged(double? value) => ResolveTcp();

    [RelayCommand(CanExecute = nameof(CanRecordTouch))]
    private async Task RecordTouch()
    {
        TcpRecording = true; TcpMsg = null;
        try
        {
            double sx = 0, sy = 0, sz = 0, srx = 0, sry = 0; int valid = 0; string? note = null;
            for (var i = 0; i < 5; i++)
            {
                if (i > 0) await Task.Delay(100);
                var p = _svc.GetPlanePose();
                if (p.Valid) { sx += p.X; sy += p.Y; sz += p.Z; srx += p.Rx; sry += p.Ry; valid++; } else note = p.Note;
            }
            if (valid < 3) { TcpMsgOk = false; TcpMsg = $"유효 샘플 부족({valid}/5) — {note ?? "센서 상태를 확인하세요."}"; return; }
            var t1 = await _cobot.Rpc.GetToolCoordAsync(1);
            TcpRefZ ??= t1[2];
            var rec = TcpTouchCalibrator.BuildRecord(sx / valid, sy / valid, sz / valid, srx / valid, sry / valid, t1, DateTime.UtcNow);
            _tcpCal.Add(rec);
            ResolveTcp();
            TcpMsgOk = true; TcpMsg = $"터치 #{_tcpCal.Touches.Count} 기록됨 (Rx={rec.RxDeg:F2}°, Ry={rec.RyDeg:F2}°, 거리 {rec.MeanDistanceMm:F1}mm)";
        }
        catch (Exception ex) { TcpMsgOk = false; TcpMsg = $"터치 기록 오류: {ex.Message}"; }
        finally { TcpRecording = false; }
    }

    [RelayCommand] private void RemoveTouch(TouchRow row) { _tcpCal.RemoveAt(row.Index); ResolveTcp(); }
    [RelayCommand] private void ClearTouches() { _tcpCal.Clear(); TcpResult = null; TcpMsg = null; TcpConfirm = false; RebuildTouches(); }

    private void ResolveTcp()
    {
        var t1 = _tcpCal.Touches.Count > 0 ? _tcpCal.Touches[0].ToolCoord1 : null;
        TcpResult = (_tcpCal.Touches.Count, TcpFixH, TcpRefZ, TcpFixedH) switch
        {
            ( >= 3, false, { } refZ, _) => _tcpCal.Solve(refZ, t1),
            ( >= 3, true, _, { } fixH) => _tcpCal.SolveWithFixedHeadPlane(fixH, t1),
            _ => null,
        };
        TcpConfirm = false;
        RebuildTouches();
    }

    private void RebuildTouches()
    {
        Touches.Clear();
        for (var i = 0; i < _tcpCal.Touches.Count; i++)
        {
            var t = _tcpCal.Touches[i];
            double? res = TcpResult is { Success: true } rr && i < rr.ResidualsMm.Count ? rr.ResidualsMm[i] : null;
            Touches.Add(new TouchRow(i, t.RxDeg, t.RyDeg, t.MeanDistanceMm, res));
        }
        OnPropertyChanged(nameof(TouchCount)); OnPropertyChanged(nameof(TouchMeanText));
        OnPropertyChanged(nameof(TipZLabel)); OnPropertyChanged(nameof(HLabel));
    }

    // 수평+팁 접촉 상태에서 측정거리 = h — 그 순간 평균 거리를 h 앵커로 저장.
    [RelayCommand(CanExecute = nameof(CanCaptureH))]
    private async Task CaptureHeadPlane()
    {
        TcpCapturingH = true; TcpMsg = null;
        try
        {
            double sz = 0, srx = 0, sry = 0; int valid = 0; string? note = null;
            for (var i = 0; i < 5; i++)
            {
                if (i > 0) await Task.Delay(100);
                var p = _svc.GetPlanePose();
                if (p.Valid) { sz += p.Z; srx += p.Rx; sry += p.Ry; valid++; } else note = p.Note;
            }
            if (valid < 3) { TcpMsgOk = false; TcpMsg = $"유효 샘플 부족({valid}/5) — {note ?? "센서 상태를 확인하세요."}"; return; }
            double h = sz / valid, maxTilt = Math.Max(Math.Abs(srx / valid), Math.Abs(sry / valid));
            await SaveParam(HeadPlaneParamKey, h, "터치 TCP — 출사면 높이 h(수평+팁 접촉 캡처)");
            TcpFixedH = h;
            TcpMsgOk = maxTilt < 0.5;
            TcpMsg = $"출사면 h={h:F1}mm 저장됨 (잔여 틸트 {maxTilt:F2}°).";
            if (maxTilt >= 0.5) TcpMsg += $" ⚠ 잔여 틸트가 큽니다 — h 오차 ≈ {138.0 * Math.Tan(maxTilt * Math.PI / 180.0):F1}mm 가능, '보정 적용' 후 재캡처 권장.";
        }
        catch (Exception ex) { TcpMsgOk = false; TcpMsg = $"h 캡처 오류: {ex.Message}"; }
        finally { TcpCapturingH = false; }
    }

    [RelayCommand]
    private async Task SaveHeadPlane()
    {
        if (TcpResult is not { Success: true } r) return;
        try
        {
            await SaveParam(HeadPlaneParamKey, r.HeadPlaneZmm, "터치 TCP — 출사면 높이 h(경험적 앵커, 기준 Z 모드 산출)");
            TcpFixedH = r.HeadPlaneZmm; TcpMsgOk = true; TcpMsg = $"출사면 h={r.HeadPlaneZmm:F1}mm 저장됨 — 'h 고정' 모드에서 자동 사용됩니다.";
        }
        catch (Exception ex) { TcpMsgOk = false; TcpMsg = $"h 저장 실패: {ex.Message}"; }
    }

    // 산출된 팁 위치를 컨트롤러 공구 좌표계에 쓴다 — 위치는 산출값, 회전은 tool-1 정의 복사.
    [RelayCommand(CanExecute = nameof(CanWriteTcp))]
    private async Task WriteTcpTool()
    {
        if (TcpResult is not { Success: true } r) return;
        TcpWriting = true; TcpMsg = null;
        try
        {
            var t1 = await _cobot.Rpc.GetToolCoordAsync(1);
            var rc = await _cobot.Rpc.SetToolCoordAsync(TcpWriteId, new[] { r.TipX, r.TipY, r.TipZ, t1[3], t1[4], t1[5] });
            TcpMsgOk = rc == 0;
            TcpMsg = rc == 0 ? $"tool {TcpWriteId} 설정 완료: ({r.TipX:F2}, {r.TipY:F2}, {r.TipZ:F2}) + tool-1 회전." : $"tool {TcpWriteId} 설정 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.";
        }
        catch (Exception ex) { TcpMsgOk = false; TcpMsg = $"공구 설정 오류: {ex.Message}"; }
        finally { TcpWriting = false; }
    }

    private async Task SaveParam(string key, double v, string desc)
    {
        using var scope = _scopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ParameterService>().SetDoubleAsync(key, v, desc);
    }

    public override void Dispose() { base.Dispose(); _calCts?.Cancel(); }
}

/// <summary>채널 측정 행(제자리 갱신).</summary>
public sealed partial class LaserChannelRow : ObservableObject
{
    public int Channel { get; }
    [ObservableProperty] private string _valueText = "";
    [ObservableProperty] private bool _enabled, _error, _warning, _high, _low, _pass, _zeroed;
    public LaserChannelRow(LaserChannelReading r) { Channel = r.Channel; Update(r); }
    public void Update(LaserChannelReading r)
    {
        ValueText = r.Value.ToString("F3"); Enabled = r.Enabled; Error = r.Error; Warning = r.Warning;
        High = r.High; Low = r.Low; Pass = r.Pass; Zeroed = r.Zeroed;
    }
}

public sealed record TouchRow(int Index, double RxDeg, double RyDeg, double MeanDistanceMm, double? ResidualMm)
{
    public int No => Index + 1;
    public string ResidualText => ResidualMm is { } r ? r.ToString("F3") : "—";
}
