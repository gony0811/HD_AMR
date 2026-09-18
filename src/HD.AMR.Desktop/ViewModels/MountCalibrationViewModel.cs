using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.AMR.App.Models;
using HD.AMR.App.Service;
using Microsoft.Extensions.DependencyInjection;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>
/// 코봇 장착 보정(T_A_B — AMR 차체 → 코봇 BASE). 고정 표적 한 점을 서로 다른 AMR 자세에서
/// 코봇으로 터치해 표본을 모으고 <see cref="MapCalibration.SolveMount3D"/> 로 6-DoF 를 산출한다.
///
/// <b>이 페이지는 AMR 도 코봇도 스스로 움직이지 않는다.</b> 하드웨어 접촉은 전부 읽기뿐이다:
/// <see cref="AMRService.LatestStatus"/>(백그라운드 폴링 캐시), <c>GetTcpPoseInBaseAsync</c>
/// (문서상 무동작 — 관절 읽기 + 클라이언트측 FK), 그리고 로컬 SQLite 저장.
/// 코봇 이동은 조그 팝업에서만, AMR 이동은 작업자가 직접 한다.
///
/// tz 는 원리상 관측 불가라 타깃 높이 q_z 입력이 필수다 — <see cref="MountCalibrationResult"/> 주석 참조.
/// </summary>
public sealed partial class MountCalibrationViewModel : ViewModelBase
{
    // 정지 판정 — QrLocalizationService.EnsureStationary 와 같은 기준.
    private const double MoveTolMm = 5.0;
    private const double MoveTolDeg = 0.2;
    private const int StationaryWindowMs = 2500;
    private const int TcpSamplesPerCapture = 5;
    private const double YawGapWarnDeg = 10.0;
    private const double YawGapOkDeg = 20.0;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AMRService _amr;
    private readonly CobotService _cobot;
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _cts = new();

    private readonly List<MountSample> _samples = new();
    private readonly Queue<(DateTime T, double X, double Y, double Yaw)> _poseHistory = new();

    private double[]? _savedMount;
    private double[]? _tcp;
    private DateTime _tcpAt;
    private int _tcpFailStreak;
    private bool _tcpPolling;
    private int _tick;

    /// <summary>T_A_B 편집란 — QR 티칭 화면에서 이관해 온 6필드.</summary>
    public Pose6 Mount { get; } = new();

    public ObservableCollection<MountSampleRow> Rows { get; } = new();

    [ObservableProperty] private int _tool = 1;
    [ObservableProperty] private double _targetQzMm;
    [ObservableProperty] private bool _qzConfirmed;
    [ObservableProperty] private double _cadTzMm;
    [ObservableProperty] private double _telescopicStrokeMm;   // 완전 하강 = 0
    [ObservableProperty] private bool _strokeConfirmed;
    [ObservableProperty] private bool _liveTcp = true;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private MountCalibrationResult? _result;
    [ObservableProperty] private string? _lastSolveText;

    // 수동 표본 입력(하드웨어 없이 전체 경로 검증용)
    [ObservableProperty] private double _manAmrX;
    [ObservableProperty] private double _manAmrY;
    [ObservableProperty] private double _manYaw;
    [ObservableProperty] private double _manBx;
    [ObservableProperty] private double _manBy;
    [ObservableProperty] private double _manBz;

    public MountCalibrationViewModel(IServiceScopeFactory scopeFactory, AMRService amr, CobotService cobot)
    {
        _scopeFactory = scopeFactory;
        _amr = amr;
        _cobot = cobot;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => OnTick();
    }

    public override async void OnActivated()
    {
        _timer.Start();
        OnTick();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var calib = scope.ServiceProvider.GetRequiredService<CalibrationService>();

            _savedMount = await calib.GetMountAsync();
            Mount.FromArray(_savedMount);

            var qz = await calib.GetMountTargetZmmAsync();
            if (qz.HasValue) { TargetQzMm = qz.Value; QzConfirmed = true; }

            _samples.Clear();
            _samples.AddRange(await calib.GetMountSamplesAsync());
            RebuildRows();

            var snap = await calib.GetMountSolveAsync();
            LastSolveText = snap is null
                ? "마지막 산출 기록 없음"
                : $"마지막 산출: {snap.SolvedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm} · 표본 {snap.N}개 · " +
                  $"RMS {snap.RmsMm:0.0}mm · 기울기 σ ±{snap.TiltSigmaDeg:0.00}°";
        }
        catch (Exception ex) { Failure($"설정 로드 실패: {ex.Message}"); }
    }

    // 내비게이션이 OnDeactivated → Dispose(→ OnDeactivated) 로 두 번 호출한다. 멱등이어야 한다.
    public override void OnDeactivated() => _timer.Stop();

    public override void Dispose()
    {
        base.Dispose();
        _cts.Cancel();
        _cts.Dispose();
    }

    // ── 폴링 ────────────────────────────────────────────────────────
    private void OnTick()
    {
        if (_amr.LatestStatus is { } st)
        {
            var now = DateTime.UtcNow;
            _poseHistory.Enqueue((now, st.Pose.X * 1000.0, st.Pose.Y * 1000.0, st.Pose.Angle * 180.0 / Math.PI));
            while (_poseHistory.Count > 0 &&
                   (now - _poseHistory.Peek().T).TotalMilliseconds > StationaryWindowMs)
                _poseHistory.Dequeue();
        }
        else _poseHistory.Clear();

        if (++_tick % 2 == 0 && LiveTcp && CobotConnected && !Busy && !_tcpPolling)
            _ = PollTcpAsync();

        OnPropertyChanged(string.Empty);
        CaptureSampleCommand.NotifyCanExecuteChanged();
        SolveCommand.NotifyCanExecuteChanged();
        RefreshTcpNowCommand.NotifyCanExecuteChanged();
        ApplyCadTzCommand.NotifyCanExecuteChanged();
    }

    private async Task PollTcpAsync()
    {
        _tcpPolling = true;
        try
        {
            _tcp = await _cobot.Rpc.GetTcpPoseInBaseAsync(Tool, _cts.Token);
            _tcpAt = DateTime.UtcNow;
            _tcpFailStreak = 0;
        }
        catch (OperationCanceledException) { /* 페이지 이탈 — 정상 */ }
        catch (Exception ex)
        {
            if (++_tcpFailStreak >= 3)
            {
                LiveTcp = false;
                Failure($"코봇 TCP 실시간 읽기를 중단했습니다 — '지금 읽기'로 재시도하세요: {ex.Message}");
            }
        }
        finally { _tcpPolling = false; }
    }

    // ── 상태 표시 ───────────────────────────────────────────────────
    public bool AmrConnected => _amr.IsConnected;
    public bool CobotConnected => _cobot.IsConnected;
    public bool ServoOn => _cobot.IsServoEnabled;
    public bool HasAmrPose => _amr.LatestStatus is not null;

    public double AmrXmm => _amr.LatestStatus is { } s ? s.Pose.X * 1000.0 : 0;
    public double AmrYmm => _amr.LatestStatus is { } s ? s.Pose.Y * 1000.0 : 0;
    public double AmrYawDeg => _amr.LatestStatus is { } s ? s.Pose.Angle * 180.0 / Math.PI : 0;

    public string AmrXText => HasAmrPose ? $"{AmrXmm:0.0} mm" : "-";
    public string AmrYText => HasAmrPose ? $"{AmrYmm:0.0} mm" : "-";
    public string AmrYawText => HasAmrPose ? $"{AmrYawDeg:0.000}°" : "-";

    /// <summary>최근 2.5초 창에서 AMR 이 5mm·0.2° 이내로 머물렀는가. 표본 캡처의 하드 게이트.</summary>
    public bool IsAmrStationary
    {
        get
        {
            if (_poseHistory.Count < 4) return false;
            double minX = double.MaxValue, maxX = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;
            double minA = double.MaxValue, maxA = double.MinValue;
            foreach (var (_, x, y, a) in _poseHistory)
            {
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                minA = Math.Min(minA, a); maxA = Math.Max(maxA, a);
            }
            return maxX - minX < MoveTolMm && maxY - minY < MoveTolMm && maxA - minA < MoveTolDeg;
        }
    }

    public string StationaryText => !HasAmrPose ? "AMR 상태 없음" : IsAmrStationary ? "정지" : "이동 중 (정지 후 캡처하세요)";

    public string TcpXText => _tcp is { } p ? $"{p[0]:0.0} mm" : "-";
    public string TcpYText => _tcp is { } p ? $"{p[1]:0.0} mm" : "-";
    public string TcpZText => _tcp is { } p ? $"{p[2]:0.0} mm" : "-";
    public string TcpAgeText => _tcp is null ? "아직 읽지 않음"
        : $"읽은 시각: {(DateTime.UtcNow - _tcpAt).TotalSeconds:0}초 전";

    public int SampleCount => _samples.Count;
    public string SampleCountText => $"표본 {SampleCount}개";
    public double YawSpanDeg => MapCalibration.CircularSpanDeg(_samples.Select(s => s.AmrYawDeg).ToList());
    public string YawSpanText => SampleCount < 2 ? "Yaw 범위 —"
        : YawSpanDeg >= 90 ? $"Yaw 범위 {YawSpanDeg:0}°" : $"Yaw 범위 {YawSpanDeg:0}° — 부족";
    public bool YawSpanOk => YawSpanDeg >= 90;

    /// <summary>현재 AMR yaw 가 기존 표본들과 얼마나 떨어져 있는가 — 중복 표본 예방용 가이드.</summary>
    public double MinYawGapDeg => _samples.Count == 0 || !HasAmrPose
        ? double.PositiveInfinity
        : _samples.Min(s => Math.Abs(MapCalibration.NormalizeDeg(s.AmrYawDeg - AmrYawDeg)));

    public bool YawGapOk => double.IsInfinity(MinYawGapDeg) || MinYawGapDeg >= YawGapOkDeg;
    public string YawGapText => _samples.Count == 0 ? "첫 표본"
        : !HasAmrPose ? "AMR 상태 없음"
        : YawGapOk ? $"기존 표본과 {MinYawGapDeg:0}° 차이"
        : $"기존 표본과 {MinYawGapDeg:0}° — 너무 가깝습니다";

    public double BzSpreadMm => _samples.Count == 0 ? 0 : _samples.Max(s => s.Bz) - _samples.Min(s => s.Bz);
    public string BzSpreadText => _samples.Count == 0 ? ""
        : BzSpreadMm > 5.0
            ? $"Bz 산포 {BzSpreadMm:0.0} mm — 5 mm 초과: 표적 접촉이 일정하지 않거나 장착 rx/ry 가 큽니다"
            : $"Bz 산포 {BzSpreadMm:0.0} mm";

    public bool IsDirty => _savedMount is null || Mount.ToArray().Zip(_savedMount, (a, b) => Math.Abs(a - b) > 1e-9).Any(v => v);
    public string DirtyText => IsDirty ? "저장값과 다름 — 저장 필요" : "저장값과 동일";

    public bool CanCapture => AmrConnected && CobotConnected && HasAmrPose && IsAmrStationary
                              && QzConfirmed && StrokeConfirmed && !Busy;
    public bool CanSolve => SampleCount >= 3 && QzConfirmed && !Busy;
    public bool CanApply => Result is { Success: true };
    public bool CanRefreshTcp => CobotConnected && !Busy;
    public bool CanApplyCadTz => SampleCount >= 1 && !Busy;

    /// <summary>버튼을 회색으로 두고 끝내지 않고, 무엇이 막고 있는지 말한다.</summary>
    public string SolveBlockedReason =>
        !QzConfirmed ? "표적 높이 q_z 를 입력하고 확인에 체크해야 산출할 수 있습니다."
        : !StrokeConfirmed ? "텔레스코픽 스트로크를 확인에 체크해야 합니다."
        : SampleCount < 3 ? $"표본이 {SampleCount}개입니다 — 최소 3개(권장 5개 이상) 필요합니다."
        : Busy ? "처리 중입니다…"
        : "";

    /// <summary>절차를 막지 않으면서 다음 한 걸음만 알려 준다(고정 마법사 아님).</summary>
    public string NextStepText
    {
        get
        {
            if (!QzConfirmed) return "① 표적 높이 q_z 를 입력하고 확인에 체크하세요.";
            if (!StrokeConfirmed) return "① 텔레스코픽을 완전 하강시키고 스트로크(0) 확인에 체크하세요.";
            if (SampleCount == 0) return "② AMR 을 표적 근처에 정차시키고, 조그로 팁을 표적에 접촉시킨 뒤 '표본 캡처'.";
            if (SampleCount < 3) return $"② 표본 {SampleCount}개 — AMR yaw 를 30° 이상 바꿔 최소 3개(권장 5개)까지 반복하세요.";
            if (Result is null) return "③ '해 계산'을 눌러 T_A_B 를 산출하세요.";
            if (!Result.Success) return "③ 산출 실패 — 사유를 확인하고 표본을 보강하세요.";
            if (IsDirty) return "⑤ 값을 확인한 뒤 'T_A_B 저장'으로 확정하세요.";
            return "④ 결과를 검토한 뒤 '결과 적용'으로 편집란에 채우고, 필요하면 수정하세요.";
        }
    }

    // ── 표본 조작 ───────────────────────────────────────────────────
    [RelayCommand(CanExecute = nameof(CanCapture))]
    private async Task CaptureSample()
    {
        Busy = true;
        Message = null;
        try
        {
            var before = (AmrXmm, AmrYmm, AmrYawDeg);

            // 코봇 pose 를 여러 번 읽어 평균 — 레이저 터치 기록(LaserViewModel.RecordTouch)과 같은 방식.
            double sx = 0, sy = 0, sz = 0, srx = 0, sry = 0, srz = 0;
            for (var i = 0; i < TcpSamplesPerCapture; i++)
            {
                if (i > 0) await Task.Delay(80, _cts.Token);
                var p = await _cobot.Rpc.GetTcpPoseInBaseAsync(Tool, _cts.Token);
                sx += p[0]; sy += p[1]; sz += p[2]; srx += p[3]; sry += p[4]; srz += p[5];
            }
            const double n = TcpSamplesPerCapture;

            // 캡처 중 AMR 이 움직였으면 표본을 버린다 — 조용히 오염되느니 실패가 낫다.
            if (Math.Abs(AmrXmm - before.AmrXmm) > MoveTolMm ||
                Math.Abs(AmrYmm - before.AmrYmm) > MoveTolMm ||
                Math.Abs(MapCalibration.NormalizeDeg(AmrYawDeg - before.AmrYawDeg)) > MoveTolDeg)
            {
                Failure("캡처 중 AMR 이 움직였습니다 — 표본을 버렸습니다. 완전히 정지한 뒤 다시 캡처하세요.");
                return;
            }

            double gap = MinYawGapDeg;
            _samples.Add(new MountSample
            {
                Index = _samples.Count,
                AmrXmm = before.AmrXmm, AmrYmm = before.AmrYmm, AmrYawDeg = before.AmrYawDeg,
                Bx = sx / n, By = sy / n, Bz = sz / n,
                Brx = srx / n, Bry = sry / n, Brz = srz / n,
                Tool = Tool, CapturedAtUtc = DateTime.UtcNow,
                TelescopicStrokeMm = TelescopicStrokeMm,
            });

            await PersistSamplesAsync();
            Resolve();

            // yaw 가 가까워도 막지는 않는다 — 재현성 확인 목적의 반복 표본은 정당하다.
            if (!double.IsInfinity(gap) && gap < YawGapWarnDeg)
                Notify($"표본 #{_samples.Count} 기록 — 기존 표본과 yaw 차이가 {gap:0}°뿐입니다. " +
                       "해에 거의 기여하지 않으니 yaw 를 30° 이상 바꿔 추가하세요.", error: false);
            else
                Success($"표본 #{_samples.Count} 기록됨 (yaw {before.AmrYawDeg:0.0}°).");
        }
        catch (OperationCanceledException) { /* 페이지 이탈 */ }
        catch (Exception ex) { Failure($"표본 캡처 실패: {ex.Message}"); }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task RemoveSample(MountSampleRow row)
    {
        if (row.Index < 0 || row.Index >= _samples.Count) return;
        _samples.RemoveAt(row.Index);
        Renumber();
        await PersistSamplesAsync();
        Resolve();
        Success($"표본 #{row.No} 삭제됨.");
    }

    [RelayCommand]
    private async Task ClearSamples()
    {
        _samples.Clear();
        Result = null;
        await PersistSamplesAsync();
        RebuildRows();
        Success("표본을 전부 삭제했습니다.");
    }

    /// <summary>하드웨어 없이 산출 경로 전체를 점검할 수 있게 하는 수동 입력.</summary>
    [RelayCommand]
    private async Task AddManualSample()
    {
        _samples.Add(new MountSample
        {
            Index = _samples.Count,
            AmrXmm = ManAmrX, AmrYmm = ManAmrY, AmrYawDeg = ManYaw,
            Bx = ManBx, By = ManBy, Bz = ManBz,
            Tool = Tool, CapturedAtUtc = DateTime.UtcNow,
            TelescopicStrokeMm = TelescopicStrokeMm,
        });
        await PersistSamplesAsync();
        Resolve();
        Success($"수동 표본 #{_samples.Count} 추가됨.");
    }

    [RelayCommand(CanExecute = nameof(CanRefreshTcp))]
    private async Task RefreshTcpNow()
    {
        try
        {
            _tcp = await _cobot.Rpc.GetTcpPoseInBaseAsync(Tool, _cts.Token);
            _tcpAt = DateTime.UtcNow;
            _tcpFailStreak = 0;
            Success("코봇 TCP 를 읽었습니다.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Failure($"코봇 TCP 읽기 실패: {ex.Message}"); }
    }

    /// <summary>CAD 의 tz 로부터 q_z 를 역산(q_z = tz + 평균 Bz). rx≈ry≈0 가정이라 초기 추정용이다.</summary>
    [RelayCommand(CanExecute = nameof(CanApplyCadTz))]
    private void ApplyCadTz()
    {
        TargetQzMm = CadTzMm + _samples.Average(s => s.Bz);
        QzConfirmed = false;
        Notify($"CAD tz {CadTzMm:0.0}mm 기준으로 q_z ≈ {TargetQzMm:0.0}mm 로 채웠습니다 — " +
               "rx/ry≈0 가정이므로 실측값으로 확인한 뒤 체크하세요.", error: false);
    }

    // ── 산출 · 적용 · 저장 ──────────────────────────────────────────
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
                await calib.SaveMountSolveAsync(r);
                await calib.SaveMountTargetZmmAsync(TargetQzMm);
                LastSolveText = $"마지막 산출: {DateTime.Now:yyyy-MM-dd HH:mm} · 표본 {r.N}개 · " +
                                $"RMS {r.RmsMm:0.0}mm · 기울기 σ ±{r.TiltSigmaDeg:0.00}°";
                Success($"산출 완료 — RMS {r.RmsMm:0.0}mm, 경고 {r.Warnings.Count}건. " +
                        "값을 검토한 뒤 '결과 적용'을 누르세요.");
            }
            else Failure($"산출 실패: {Result?.Error}");
        }
        catch (Exception ex) { Failure($"산출 실패: {ex.Message}"); }
        finally { Busy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void ApplyResult()
    {
        Mount.FromArray(Result!.MountPose);
        Success("산출값을 편집란에 채웠습니다 — 필요하면 수정한 뒤 저장하세요. (아직 저장되지 않았습니다.)");
    }

    [RelayCommand]
    private async Task SaveMount()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var calib = scope.ServiceProvider.GetRequiredService<CalibrationService>();
            var pose = Mount.ToArray();
            await calib.SaveMountAsync(pose);
            _savedMount = pose;
            Success($"T_A_B 를 저장했습니다 — tz 는 텔레스코픽 스트로크 {TelescopicStrokeMm:0} mm 기준입니다. " +
                    "(이 앱의 설정 DB에만 기록 — 컨트롤러·HD_ACS 에는 쓰지 않습니다.)");
        }
        catch (Exception ex) { Failure($"T_A_B 저장 실패: {ex.Message}"); }
    }

    [RelayCommand]
    private async Task ReloadMount()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var calib = scope.ServiceProvider.GetRequiredService<CalibrationService>();
            _savedMount = await calib.GetMountAsync();
            Mount.FromArray(_savedMount);
            Success("저장된 T_A_B 로 되돌렸습니다.");
        }
        catch (Exception ex) { Failure($"되돌리기 실패: {ex.Message}"); }
    }

    // ── 내부 ────────────────────────────────────────────────────────
    /// <summary>표본이 바뀔 때마다 즉시 재산출 — 잔차 열이 항상 현재 표본을 반영하게 한다.</summary>
    private void Resolve()
    {
        if (_samples.Count < 3) { Result = null; RebuildRows(); return; }
        using var scope = _scopeFactory.CreateScope();
        var calib = scope.ServiceProvider.GetRequiredService<CalibrationService>();
        Result = calib.SolveMount3D(_samples, QzConfirmed ? TargetQzMm : null, _savedMount);
        RebuildRows();
    }

    private void Renumber()
    {
        for (var i = 0; i < _samples.Count; i++) _samples[i].Index = i;
    }

    private void RebuildRows()
    {
        Rows.Clear();
        var res = Result is { Success: true } r ? r.ResidualsMm : null;
        double rms = Result is { Success: true } rr ? rr.RmsMm : 0;
        for (var i = 0; i < _samples.Count; i++)
        {
            var s = _samples[i];
            double? residual = res is not null && i < res.Count ? res[i] : null;
            Rows.Add(new MountSampleRow(i, s.AmrXmm, s.AmrYmm, s.AmrYawDeg, s.Bx, s.By, s.Bz, residual)
            {
                IsOutlier = residual is { } v && v > Math.Max(5.0, 3.0 * rms),
            });
        }
    }

    private async Task PersistSamplesAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var calib = scope.ServiceProvider.GetRequiredService<CalibrationService>();
            await calib.SaveMountSamplesAsync(_samples);
        }
        catch (Exception ex) { Failure($"표본 저장 실패: {ex.Message}"); }
    }

    private void Success(string msg) => Notify(msg, error: false);
    private void Failure(string msg) => Notify(msg, error: true);
    private void Notify(string msg, bool error) { Message = msg; IsError = error; }
}

/// <summary>표본 표의 한 행. 표본 변경·재산출 때마다 통째로 다시 만든다(잔차가 산출에서 오므로).</summary>
public sealed record MountSampleRow(
    int Index, double AmrXmm, double AmrYmm, double AmrYawDeg,
    double Bx, double By, double Bz, double? ResidualMm)
{
    public int No => Index + 1;
    public string ResidualText => ResidualMm is { } r ? r.ToString("F2") : "—";
    public bool IsOutlier { get; init; }
}
