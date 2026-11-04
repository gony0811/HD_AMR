using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.AMR.App.Communication.GasDetector;
using HD.AMR.App.Service;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>측정 결과 표 한 행(표시용 문자열).</summary>
public sealed record GasResultRow(string Seq, string Time, string Label, string Max, string Avg, string Limit,
    string Judgement, bool IsOk, bool IsNg, string Detail);

/// <summary>
/// 헬륨 가스 디텍터(INFICON XL3000flex) 화면 — 기존 Vision Interface 페이지를 대신한다.
/// 통신 경로(RS485 시리얼 / 이더넷 TCP) 선택·적용, 실시간 리크레이트·상태 표시, 수동 측정·ZERO, 측정 결과 목록.
/// 통신·판정은 <see cref="GasDetectorService"/>, 프로토콜은 <see cref="Xl3000AsciiProtocol"/>.
/// </summary>
public sealed partial class GasDetectorViewModel : ViewModelBase
{
    private readonly GasDetectorService _svc;
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _cts = new();
    private int _shownResultCount = -1;

    // ── 편집 중 설정(적용 전) ──
    [ObservableProperty] private bool _isSerial;
    [ObservableProperty] private string? _portName;
    [ObservableProperty] private string _baudRate = "";
    [ObservableProperty] private string _host = "";
    [ObservableProperty] private string _tcpPort = "";
    [ObservableProperty] private string _limitText = "";
    [ObservableProperty] private string _dwellMs = "";
    [ObservableProperty] private string _sampleCount = "";
    [ObservableProperty] private string _sampleIntervalMs = "";

    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isError;

    public ObservableCollection<string> Ports { get; } = new();
    public ObservableCollection<GasResultRow> Results { get; } = new();

    public GasDetectorViewModel(GasDetectorService svc)
    {
        _svc = svc;
        LoadFromService();
        RefreshPorts();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _timer.Tick += (_, _) => RefreshLive();
    }

    public override void OnActivated()
    {
        RefreshLive();
        _timer.Start();
    }

    public override void OnDeactivated() => _timer.Stop();

    public override void Dispose()
    {
        base.Dispose();
        _cts.Cancel();
        _cts.Dispose();
    }

    public bool IsTcp
    {
        get => !IsSerial;
        set => IsSerial = !value;
    }

    partial void OnIsSerialChanged(bool value) => OnPropertyChanged(nameof(IsTcp));

    // ── 상태 표시 ───────────────────────────────────────────────
    public bool IsActive => _svc.IsActive;
    public bool IsInactive => !_svc.IsActive;
    public string ModelText => _svc.Settings.Model;
    public bool IsConnected => _svc.IsConnected;
    public string EndpointText => _svc.Settings.EndpointText;
    public string? LastError => _svc.LastError;
    public string TrafficText => $"TX {_svc.LastTx ?? "—"}   RX {_svc.LastRx ?? "—"}";

    private GasDetectorReading? R => _svc.Latest;

    public string LeakRateText => R?.LeakRate is { } v ? v.ToString("0.00E+0", CultureInfo.InvariantCulture) : "—";
    public string UnitText => R?.Unit ?? "(장비 설정 단위)";
    public string LeakRateMbarText => R?.LeakRateMbarLs is { } v ? $"{v.ToString("0.00E+0", CultureInfo.InvariantCulture)} mbar·l/s" : "—";
    public string ModeText => R is { } r ? Xl3000AsciiProtocol.ModeText(r.Mode) : "—";
    public bool IsMeasuring => R is { Mode: Xl3000Mode.Measuring };
    public string ErrorStatusText => R?.ErrorStatus ?? "—";
    public bool HasDeviceError => R is { HasDeviceError: true } || R is { Mode: Xl3000Mode.Error };

    public bool Trigger1 => R?.Triggers is { } t && t[0];
    public bool Trigger2 => R?.Triggers is { } t && t[1];
    public bool Trigger3 => R?.Triggers is { } t && t[2];
    public bool Trigger4 => R?.Triggers is { } t && t[3];

    /// <summary>현재 값 기준 실시간 판정(참고용) — 시퀀스 판정은 경유점 샘플 최대값으로 한다.</summary>
    public bool IsLiveNg => R?.LeakRateMbarLs is { } v && v >= _svc.Settings.LeakRateLimitMbarLs;
    public bool IsLiveOk => R?.LeakRateMbarLs is { } v && v < _svc.Settings.LeakRateLimitMbarLs;
    public string LiveJudgeText => R?.LeakRateMbarLs is null ? "—"
        : IsLiveNg ? "NG (기준 이상)" : "OK";
    public string LimitDisplay => $"판정 기준 {_svc.Settings.LeakRateLimitMbarLs.ToString("0.0E+0", CultureInfo.InvariantCulture)} mbar·l/s";

    public string AgeText
    {
        get
        {
            if (R is not { } r) return "측정값 수신 대기 중…";
            var age = (DateTime.UtcNow - r.ReadAtUtc).TotalSeconds;
            return age < 2 ? "마지막 갱신: 방금" : $"마지막 갱신: {age:0}초 전";
        }
    }

    public string ResultSummary
    {
        get
        {
            var rs = _svc.Results;
            if (rs.Count == 0) return "측정 결과 없음";
            int ok = rs.Count(r => r.Judgement == GasJudgement.Ok), ng = rs.Count(r => r.Judgement == GasJudgement.Ng);
            return $"총 {rs.Count}점 — OK {ok} · NG {ng} · 실패 {rs.Count - ok - ng}";
        }
    }

    public bool CanCommand => _svc.IsPortOpen && !Busy;

    private void RefreshLive()
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(EndpointText));
        OnPropertyChanged(nameof(LastError));
        OnPropertyChanged(nameof(TrafficText));
        OnPropertyChanged(nameof(LeakRateText));
        OnPropertyChanged(nameof(UnitText));
        OnPropertyChanged(nameof(LeakRateMbarText));
        OnPropertyChanged(nameof(ModeText));
        OnPropertyChanged(nameof(IsMeasuring));
        OnPropertyChanged(nameof(ErrorStatusText));
        OnPropertyChanged(nameof(HasDeviceError));
        OnPropertyChanged(nameof(Trigger1));
        OnPropertyChanged(nameof(Trigger2));
        OnPropertyChanged(nameof(Trigger3));
        OnPropertyChanged(nameof(Trigger4));
        OnPropertyChanged(nameof(IsLiveNg));
        OnPropertyChanged(nameof(IsLiveOk));
        OnPropertyChanged(nameof(LiveJudgeText));
        OnPropertyChanged(nameof(LimitDisplay));
        OnPropertyChanged(nameof(AgeText));
        OnPropertyChanged(nameof(CanCommand));
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        ZeroCommand.NotifyCanExecuteChanged();
        ClearErrorCommand.NotifyCanExecuteChanged();
        MeasureOnceCommand.NotifyCanExecuteChanged();

        var rs = _svc.Results;
        if (rs.Count != _shownResultCount)
        {
            _shownResultCount = rs.Count;
            Results.Clear();
            foreach (var r in rs.AsEnumerable().Reverse().Take(200))
                Results.Add(ToRow(r));
            OnPropertyChanged(nameof(ResultSummary));
        }
    }

    private static GasResultRow ToRow(GasMeasurementResult r)
    {
        static string E(double? v) => v is { } x ? x.ToString("0.00E+0", CultureInfo.InvariantCulture) : "—";
        var judge = r.Judgement switch { GasJudgement.Ok => "OK", GasJudgement.Ng => "NG", _ => "실패" };
        return new GasResultRow(r.Seq.ToString(CultureInfo.InvariantCulture), r.FinishedUtc.ToLocalTime().ToString("HH:mm:ss"),
            r.Label, E(r.MaxMbarLs), E(r.AvgMbarLs), E(r.LimitMbarLs), judge,
            r.Judgement == GasJudgement.Ok, r.Judgement == GasJudgement.Ng,
            r.Error ?? $"{r.SampleCount}샘플");
    }

    // ── 설정 ───────────────────────────────────────────────────
    private void LoadFromService()
    {
        var s = _svc.Settings;
        var inv = CultureInfo.InvariantCulture;
        IsSerial = s.Transport == GasDetectorTransportKind.Serial;
        PortName = s.PortName;
        BaudRate = s.BaudRate.ToString(inv);
        Host = s.Host;
        TcpPort = s.TcpPort.ToString(inv);
        LimitText = s.LeakRateLimitMbarLs.ToString("0.0E+0", inv);
        DwellMs = s.MeasureDwellMs.ToString(inv);
        SampleCount = s.MeasureSampleCount.ToString(inv);
        SampleIntervalMs = s.MeasureSampleIntervalMs.ToString(inv);
    }

    [RelayCommand]
    private void RefreshPorts()
    {
        var current = PortName;
        Ports.Clear();
        foreach (var p in GasDetectorService.AvailablePorts())
            Ports.Add(p);
        if (current is { Length: > 0 } && !Ports.Contains(current))
            Ports.Add(current);   // 지금은 안 보이는 포트(변환기 미연결)도 설정값은 유지한다.
        PortName = current;
    }

    /// <summary>입력값 검증 후 서비스에 적용(DB 저장, 통신 경로가 바뀌면 재연결).</summary>
    [RelayCommand]
    private async Task Apply()
    {
        var inv = CultureInfo.InvariantCulture;
        var s = _svc.Settings;
        try
        {
            s.Transport = IsSerial ? GasDetectorTransportKind.Serial : GasDetectorTransportKind.Tcp;
            if (IsSerial)
            {
                if (string.IsNullOrWhiteSpace(PortName)) throw new FormatException("시리얼 포트를 선택하세요.");
                s.PortName = PortName.Trim();
                s.BaudRate = ParseInt(BaudRate, "보율", 1200, 115200);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(Host)) throw new FormatException("TCP 호스트(IP)를 입력하세요.");
                s.Host = Host.Trim();
                s.TcpPort = ParseInt(TcpPort, "TCP 포트", 1, 65535);
            }
            if (!double.TryParse(LimitText.Trim(), NumberStyles.Float, inv, out var limit) || limit <= 0)
                throw new FormatException("판정 기준은 양수여야 합니다 (예: 1.0E-5).");
            s.LeakRateLimitMbarLs = limit;
            s.MeasureDwellMs = ParseInt(DwellMs, "측정 전 대기", 0, 60_000);
            s.MeasureSampleCount = ParseInt(SampleCount, "샘플 수", 1, 100);
            s.MeasureSampleIntervalMs = ParseInt(SampleIntervalMs, "샘플 간격", Xl3000AsciiProtocol.MinSampleIntervalMs, 10_000);
        }
        catch (FormatException ex)
        {
            Notify(ex.Message, true);
            return;
        }

        Busy = true;
        try
        {
            await _svc.ApplySettingsAsync(s);
            Notify($"적용했습니다 — {s.EndpointText}. 저장된 설정은 재시작 후에도 유지됩니다.", false);
        }
        finally { Busy = false; }
    }

    private static int ParseInt(string text, string name, int min, int max)
        => int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= min && v <= max
            ? v
            : throw new FormatException($"{name}: {min}~{max} 범위의 정수를 입력하세요.");

    [RelayCommand]
    private void Revert()
    {
        LoadFromService();
        Notify("현재 적용 중인 설정으로 되돌렸습니다.", false);
    }

    [RelayCommand]
    private void Reconnect()
    {
        _svc.RequestReconnect();
        Notify("다시 연결합니다…", false);
    }

    // ── 장비 명령 ───────────────────────────────────────────────
    [RelayCommand(CanExecute = nameof(CanCommand))]
    private Task Start() => SendAsync(Xl3000AsciiProtocol.Start, "측정 시작(*STArt)");

    [RelayCommand(CanExecute = nameof(CanCommand))]
    private Task Stop() => SendAsync(Xl3000AsciiProtocol.Stop, "측정 정지(*STOp)");

    [RelayCommand(CanExecute = nameof(CanCommand))]
    private Task Zero() => SendAsync(Xl3000AsciiProtocol.Zero, "ZERO(배경 보정)");

    [RelayCommand(CanExecute = nameof(CanCommand))]
    private Task ClearError() => SendAsync(Xl3000AsciiProtocol.ClearError, "에러 클리어(*CLS)");

    private async Task SendAsync(string command, string label)
    {
        Busy = true;
        try
        {
            await _svc.SendCommandAsync(command, _cts.Token);
            Notify($"{label} — OK", false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Notify($"{label} 실패: {ex.Message}", true);
        }
        finally { Busy = false; }
    }

    /// <summary>현재 위치에서 경유점과 같은 방식으로 1회 측정(대기 → 샘플 → 판정).</summary>
    [RelayCommand(CanExecute = nameof(CanCommand))]
    private async Task MeasureOnce()
    {
        Busy = true;
        try
        {
            var r = await _svc.MeasureAsync("수동 측정", _cts.Token);
            Notify(r.Judgement switch
            {
                GasJudgement.Ok => $"OK — 최대 {r.MaxMbarLs:0.00E+0} mbar·l/s",
                GasJudgement.Ng => $"NG — 최대 {r.MaxMbarLs:0.00E+0} ≥ 기준 {r.LimitMbarLs:0.00E+0} mbar·l/s",
                _ => $"측정 실패: {r.Error}",
            }, r.Judgement != GasJudgement.Ok);
        }
        catch (OperationCanceledException) { }
        finally { Busy = false; }
        RefreshLive();
    }

    [RelayCommand]
    private void ClearResults()
    {
        _svc.ClearResults();
        RefreshLive();
    }

    partial void OnBusyChanged(bool value) => OnPropertyChanged(nameof(CanCommand));

    private void Notify(string message, bool isError)
    {
        Message = message;
        IsError = isError;
    }
}
