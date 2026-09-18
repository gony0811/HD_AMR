using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.AMR.App.Models;
using HD.AMR.App.Service;
using Microsoft.Extensions.DependencyInjection;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>
/// 핸드아이 캘리브레이션(<c>T_T_C</c>: Tool TCP → 카메라 광학 프레임) — AX=XB 측정.
///
/// 종전에는 <c>ArUco 장착 보정</c> 화면에 T_T_C 를 CAD·가정값으로 <b>입력</b>할 수밖에 없었다
/// (docs/ARUCO_TAB_CALIBRATION.md §3). 이 화면은 그 값을 <b>측정</b>해 같은 키에 저장한다.
///
/// 바닥에 고정한 ArUco 를 <b>AMR·리프트를 세워 둔 채</b> 코봇 자세만 바꿔 촬영하면
/// AMR pose 가 식에서 소거되어 <c>T_A_B</c> 없이 AX=XB 로 풀린다(순환 의존 없음).
/// 카메라에 컨트롤러 TOOL 이 없으면 tool 0(플랜지)을 써도 된다 — 중요한 것은 장착 보정 화면과
/// <b>같은 tool 번호</b>를 쓰는 것뿐이다.
///
/// <b>이 화면은 코봇을 스스로 움직이지 않는다</b> — 자세 변경은 조그 팝업에서 작업자가 한다.
/// 핵심 성패 요인은 표본 수가 아니라 <b>회전축 다양성</b>이며, 부족하면 산출을 거부한다.
/// </summary>
public sealed partial class HandEyeViewModel : ViewModelBase
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CameraService _camera;
    private readonly CobotService _cobot;
    private readonly TelescopicService _lift;
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _cts = new();

    private readonly List<HandEyeSample> _samples = new();
    private double[]? _savedTtc;
    private double? _liftHeightAtStart;

    /// <summary>T_T_C 편집란 — 산출값을 적용한 뒤 필요하면 손으로 보정한다.</summary>
    public Pose6 HandEye { get; } = new();

    public ObservableCollection<HandEyeRow> Rows { get; } = new();

    [ObservableProperty] private double _markerSizeMm = 100;
    /// <summary>ArUco 장착 보정 화면과 반드시 같은 번호여야 한다(그 화면 기본값 2).</summary>
    [ObservableProperty] private int _tool = 2;
    [ObservableProperty] private int _markerId;
    [ObservableProperty] private bool _matchMarkerId = true;
    [ObservableProperty] private bool _stationaryConfirmed;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private HandEyeResult? _result;
    [ObservableProperty] private string? _lastSolveText;

    public HandEyeViewModel(IServiceScopeFactory scopeFactory, CameraService camera,
        CobotService cobot, TelescopicService lift)
    {
        _scopeFactory = scopeFactory;
        _camera = camera;
        _cobot = cobot;
        _lift = lift;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) =>
        {
            OnPropertyChanged(string.Empty);
            CaptureCommand.NotifyCanExecuteChanged();
            SolveCommand.NotifyCanExecuteChanged();
        };
    }

    public override async void OnActivated()
    {
        _timer.Start();
        _liftHeightAtStart = _lift.Latest?.HeightMm;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var calib = scope.ServiceProvider.GetRequiredService<CalibrationService>();

            _savedTtc = await calib.GetHandEyeAsync();
            HandEye.FromArray(_savedTtc);

            var aruco = await calib.GetArucoSettingsAsync();
            MarkerSizeMm = aruco.SizeMm;
            MatchMarkerId = aruco.MarkerId.HasValue;
            MarkerId = aruco.MarkerId ?? 0;

            _samples.Clear();
            _samples.AddRange(await calib.GetHandEyeSamplesAsync());
            RebuildRows();

            var snap = await calib.GetHandEyeSolveAsync();
            LastSolveText = snap is null
                ? "마지막 산출 기록 없음"
                : $"마지막 산출: {snap.SolvedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm} · 자세 {snap.N}개 · " +
                  $"회전 잔차 {snap.RotationRmsDeg:0.00}° · 병진 잔차 {snap.TranslationRmsMm:0.0}mm · " +
                  $"축 다양성 {snap.AxisSpreadDeg:0.0}°";
        }
        catch (Exception ex) { Notify($"설정 로드 실패: {ex.Message}", true); }
    }

    // 내비게이션이 OnDeactivated → Dispose(→ OnDeactivated) 로 두 번 호출한다. 멱등이어야 한다.
    public override void OnDeactivated() => _timer.Stop();

    public override void Dispose()
    {
        base.Dispose();
        _cts.Cancel();
        _cts.Dispose();
    }

    // ── 상태 ────────────────────────────────────────────────────────
    public bool CameraStreaming => _camera.IsStreaming;
    public bool CobotConnected => _cobot.IsConnected;
    public bool DetectorAvailable => OperatingSystem.IsWindows();

    /// <summary>1단계 중 리프트가 움직였는가 — 움직이면 마커가 베이스 기준으로 이동해 전제가 깨진다.</summary>
    public bool LiftMoved =>
        _liftHeightAtStart is { } h0 && _lift.Latest is { HeightMm: >= 0 } s && Math.Abs(s.HeightMm - h0) > 1.0;

    public string LiftText => _lift.Latest is { HeightMm: >= 0 } s
        ? LiftMoved
            ? $"⚠ 리프트가 움직였습니다 ({_liftHeightAtStart:0} → {s.HeightMm} mm) — 표본을 버리고 다시 시작하세요."
            : $"리프트 {s.HeightMm} mm (고정 유지 중)"
        : "리프트 상태 미수신 — 움직이지 않도록 주의하세요.";

    public int SampleCount => _samples.Count;
    public string SampleCountText => $"자세 {SampleCount}개";

    public string AxisSpreadText => Result is { Success: true } r
        ? $"회전축 다양성 {r.AxisSpreadDeg:0.0}° (유효 쌍 {r.PairCount}개)"
        : SampleCount < 3 ? "회전축 다양성 — 자세 3개 이상부터 평가" : "산출을 누르면 평가됩니다";

    public bool CanCapture => DetectorAvailable && CameraStreaming && CobotConnected
                              && StationaryConfirmed && !LiftMoved && !Busy;
    public bool CanSolve => SampleCount >= 3 && !Busy;
    public bool CanApply => Result is { Success: true };

    public string BlockedReason =>
        !DetectorAvailable ? "ArUco 검출은 Windows(OpenCV 네이티브)에서만 동작합니다."
        : !CameraStreaming ? "카메라 스트리밍이 필요합니다."
        : !CobotConnected ? "코봇 미연결."
        : !StationaryConfirmed ? "'AMR·리프트 정지 확인'에 체크해야 캡처할 수 있습니다."
        : LiftMoved ? "리프트가 움직였습니다 — 전체 삭제 후 다시 시작하세요."
        : Busy ? "처리 중입니다…"
        : "";

    public string NextStepText =>
        !StationaryConfirmed ? "① AMR 을 정차시키고 리프트를 고정한 뒤 확인에 체크하세요. 이후 둘 다 절대 건드리지 마세요."
        : SampleCount == 0 ? "② 조그로 코봇 자세를 잡아 마커가 화면에 크게 들어오게 하고 '자세 캡처'."
        : SampleCount < 8 ? $"② 자세 {SampleCount}/8 — 손목을 서로 평행하지 않은 축으로 ±20~30° 돌려가며 반복하세요."
        : Result is null ? "③ '산출'을 눌러 T_T_C 를 계산하세요."
        : !Result.Success ? "③ 산출 실패 — 사유를 확인하고 자세를 보강하세요."
        : "④ 결과를 검토한 뒤 '결과 적용' → 'T_T_C 저장'. 이후 ArUco 장착 보정으로 넘어갑니다.";

    public bool IsDirty => _savedTtc is null
        || HandEye.ToArray().Zip(_savedTtc, (a, b) => Math.Abs(a - b) > 1e-9).Any(v => v);
    public string DirtyText => IsDirty ? "저장값과 다름 — 저장 필요" : "저장값과 동일";

    private ArucoSettings BuildSettings() => new()
    {
        SizeMm = MarkerSizeMm,
        MarkerId = MatchMarkerId ? MarkerId : null,
    };

    // ── 명령 ────────────────────────────────────────────────────────
    [RelayCommand(CanExecute = nameof(CanCapture))]
    private async Task Capture()
    {
        Busy = true;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<ArucoHandEyeService>();
            var calib = scope.ServiceProvider.GetRequiredService<CalibrationService>();

            var sample = await svc.CaptureAsync(BuildSettings(), Tool, 5, _cts.Token);
            sample.Index = _samples.Count;
            _samples.Add(sample);

            await calib.SaveHandEyeSamplesAsync(_samples);
            await calib.SaveArucoSettingsAsync(BuildSettings());
            Resolve();

            Notify($"자세 #{_samples.Count} 캡처 (마커 ID {sample.MarkerId}, tool {sample.Tool}, " +
                   $"재투영 {sample.ReprojErrPx:0.0}px).", false);
        }
        catch (Exception ex) { Notify($"캡처 실패: {ex.Message}", true); }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task RemoveSample(HandEyeRow row)
    {
        if (row.Index < 0 || row.Index >= _samples.Count) return;
        _samples.RemoveAt(row.Index);
        for (var i = 0; i < _samples.Count; i++) _samples[i].Index = i;
        await PersistAsync();
        Resolve();
        Notify($"자세 #{row.No} 삭제됨.", false);
    }

    [RelayCommand]
    private async Task ClearSamples()
    {
        _samples.Clear();
        Result = null;
        _liftHeightAtStart = _lift.Latest?.HeightMm;
        await PersistAsync();
        RebuildRows();
        Notify("표본을 전부 삭제했습니다.", false);
    }

    [RelayCommand(CanExecute = nameof(CanSolve))]
    private async Task Solve()
    {
        Busy = true;
        try
        {
            Resolve();
            if (Result is { Success: true } r)
            {
                using var scope = _scopeFactory.CreateScope();
                var calib = scope.ServiceProvider.GetRequiredService<CalibrationService>();
                await calib.SaveHandEyeSolveAsync(r, Tool);
                LastSolveText = $"마지막 산출: {DateTime.Now:yyyy-MM-dd HH:mm} · 자세 {r.N}개 · " +
                                $"회전 잔차 {r.RotationRmsDeg:0.00}° · 병진 잔차 {r.TranslationRmsMm:0.0}mm · " +
                                $"축 다양성 {r.AxisSpreadDeg:0.0}°";
                Notify($"산출 완료 — 회전 잔차 {r.RotationRmsDeg:0.00}°, 병진 잔차 {r.TranslationRmsMm:0.0}mm, " +
                       $"경고 {r.Warnings.Count}건.", false);
            }
            else Notify($"산출 실패: {Result?.Error}", true);
        }
        catch (Exception ex) { Notify($"산출 실패: {ex.Message}", true); }
        finally { Busy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void ApplyResult()
    {
        HandEye.FromArray(Result!.PoseFC);
        Notify("산출값을 편집란에 채웠습니다 — 필요하면 수정한 뒤 저장하세요. (아직 저장되지 않았습니다.)", false);
    }

    [RelayCommand]
    private async Task SaveHandEyePose()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var calib = scope.ServiceProvider.GetRequiredService<CalibrationService>();
            var pose = HandEye.ToArray();
            await calib.SaveHandEyeAsync(pose);
            _savedTtc = pose;
            Notify($"T_T_C 를 저장했습니다(tool {Tool} 기준) — ArUco 장착 보정 화면에서 " +
                   "같은 tool 번호를 쓰는지 확인하세요.", false);
        }
        catch (Exception ex) { Notify($"저장 실패: {ex.Message}", true); }
    }

    [RelayCommand]
    private async Task ReloadHandEyePose()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var calib = scope.ServiceProvider.GetRequiredService<CalibrationService>();
            _savedTtc = await calib.GetHandEyeAsync();
            HandEye.FromArray(_savedTtc);
            Notify("저장된 T_T_C 로 되돌렸습니다.", false);
        }
        catch (Exception ex) { Notify($"되돌리기 실패: {ex.Message}", true); }
    }

    // ── 내부 ────────────────────────────────────────────────────────
    private void Resolve()
    {
        if (_samples.Count < 3) { Result = null; RebuildRows(); return; }
        using var scope = _scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ArucoHandEyeService>();
        Result = svc.Solve(_samples);
        RebuildRows();
    }

    private void RebuildRows()
    {
        Rows.Clear();
        foreach (var s in _samples)
            Rows.Add(new HandEyeRow(s.Index, s.MarkerId, s.Tool ?? -1, s.TcpPose,
                s.ReprojErrPx, s.DepthMinusPnpMm));
    }

    private async Task PersistAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var calib = scope.ServiceProvider.GetRequiredService<CalibrationService>();
            await calib.SaveHandEyeSamplesAsync(_samples);
        }
        catch (Exception ex) { Notify($"표본 저장 실패: {ex.Message}", true); }
    }

    private void Notify(string msg, bool error) { Message = msg; IsError = error; }
}

/// <summary>핸드아이 표본 표의 한 행.</summary>
public sealed record HandEyeRow(
    int Index, int MarkerId, int Tool, double[] TcpPose,
    double ReprojErrPx, double? DepthMinusPnpMm)
{
    public int No => Index + 1;
    public string TcpText => $"{TcpPose[0]:F0}, {TcpPose[1]:F0}, {TcpPose[2]:F0}";
    public string OrientText => $"{TcpPose[3]:F0}, {TcpPose[4]:F0}, {TcpPose[5]:F0}";
    public string ReprojText => $"{ReprojErrPx:F1}px";
    public string DepthText => DepthMinusPnpMm is { } d ? $"{d:+0.0;-0.0}mm" : "—";
}
