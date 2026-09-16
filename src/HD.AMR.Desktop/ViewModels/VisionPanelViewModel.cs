using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.AMR.App.Communication.Vision;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>
/// 자동화(Client)/비전 프로토콜 프레임 빌더 + HEX 로그 패널. 기존 Shared VisionPanel.razor 이식.
/// 하나의 <see cref="VisionEngine"/> 에 의존. 700ms 폴링으로 연결 상태·로그를 갱신한다.
/// </summary>
public sealed partial class VisionPanelViewModel : ObservableObject
{
    private readonly VisionEngine _engine;
    private readonly DispatcherTimer _timer;
    private long _lastLogVersion = -1;
    private bool _busy;

    public static readonly CommandCode[] Commands = { CommandCode.Heartbeat, CommandCode.CaptureReq, CommandCode.CaptureRes, CommandCode.ErrorNoti };
    public static readonly ushort[] ResultCodes = { 0x0000, 0x0001, 0x0002, 0x0003, 0x0004, 0x0005, 0x00FF };
    public IReadOnlyList<SurfaceInfo> Surfaces => SurfaceCatalog.All;

    public string RoleTitle => "자동화 S/W (Client · ID=0x01)";
    public ObservableCollection<VisionLogRow> Log { get; } = new();

    // 연결
    [ObservableProperty] private string _host = "127.0.0.1";
    [ObservableProperty] private int _port = 5000;
    [ObservableProperty] private bool _autoHeartbeat = true;
    [ObservableProperty] private bool _autoReconnect;

    // 프레임
    [ObservableProperty] private int _tabIndex;           // 0 프레임, 1 Raw HEX
    [ObservableProperty] private int _commandIndex = 1;   // 기본 CaptureReq
    [ObservableProperty] private int _seq;
    [ObservableProperty] private bool _autoSeq = true;
    [ObservableProperty] private int _toIndex = 1;        // 기본 Vision

    [ObservableProperty] private string _hbTimestamp = "";
    [ObservableProperty] private string _hbReserved = "000000";
    [ObservableProperty] private int _surfaceType;
    [ObservableProperty] private int _surfaceIndex;       // SurfaceCatalog.All 인덱스
    [ObservableProperty] private int _posX;
    [ObservableProperty] private int _posY;
    [ObservableProperty] private int _resultIndex;        // ResultCodes 인덱스

    [ObservableProperty] private bool _autoLen = true;
    [ObservableProperty] private int _len;
    [ObservableProperty] private bool _autoChk = true;
    [ObservableProperty] private int _chk;

    [ObservableProperty] private string _rawHex = "";
    [ObservableProperty] private string? _rawError;

    public bool IsFrameTab => TabIndex == 0;
    public bool IsHeartbeat => CommandIndex == 0;
    public bool IsCaptureReq => CommandIndex == 1;
    public bool IsResult => CommandIndex >= 2;
    partial void OnTabIndexChanged(int v) => OnPropertyChanged(nameof(IsFrameTab));
    partial void OnCommandIndexChanged(int v)
    {
        OnPropertyChanged(nameof(IsHeartbeat));
        OnPropertyChanged(nameof(IsCaptureReq));
        OnPropertyChanged(nameof(IsResult));
    }

    public VisionPanelViewModel(VisionEngine engine, string host, int port, bool autoReconnectDefault = false)
    {
        _engine = engine;
        _host = host;
        _port = port;
        _autoReconnect = autoReconnectDefault;
        _hbTimestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
        _rawHex = "02 00 17 00 01 01 02 " + string.Concat(Enumerable.Repeat("30 ", 14)) + "30 30 30 30 30 30 00 03";

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _timer.Tick += (_, _) =>
        {
            if (_engine.LogVersion != _lastLogVersion) RefreshLog();
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(StatusText));
        };
    }

    public void Start() { RefreshLog(); _timer.Start(); }
    public void Stop() => _timer.Stop();

    public bool IsConnected => _engine.IsConnected;
    public string StatusText => _engine.Status;

    private void RefreshLog()
    {
        var all = _engine.SnapshotLogs();
        Log.Clear();
        foreach (var e in all.Reverse().Take(300))
            Log.Add(new VisionLogRow(e));
        _lastLogVersion = _engine.LogVersion;
    }

    [RelayCommand]
    private void StampNow() => HbTimestamp = DateTime.Now.ToString("yyyyMMddHHmmss");

    [RelayCommand]
    private void ApplyHeartbeat() => _engine.AutoHeartbeat = AutoHeartbeat;

    [RelayCommand]
    private async Task Connect()
    {
        if (_busy || _engine.IsConnected) return;
        _busy = true;
        try
        {
            _engine.AutoHeartbeat = AutoHeartbeat;
            _engine.AutoIncrementSeq = AutoSeq;
            await _engine.ConnectClientAsync(Host.Trim(), Port, AutoReconnect);
        }
        catch (Exception ex) { RawError = ex.Message; }
        finally { _busy = false; RefreshLog(); }
    }

    [RelayCommand]
    private async Task Disconnect()
    {
        if (_busy) return;
        _busy = true;
        try { await _engine.StopAsync(); }
        finally { _busy = false; RefreshLog(); }
    }

    [RelayCommand]
    private async Task Send()
    {
        if (_busy || !_engine.IsConnected) return;
        _busy = true;
        try
        {
            var cmd = Commands[CommandIndex];
            byte[] data = cmd switch
            {
                CommandCode.Heartbeat => BuildHeartbeat(),
                CommandCode.CaptureReq => CaptureReqPayload.Build((SurfaceType)SurfaceType,
                    Surfaces.Count > 0 ? Surfaces[Math.Clamp(SurfaceIndex, 0, Surfaces.Count - 1)].Id : (ushort)0, PosX, PosY),
                _ => CaptureResPayload.Build(Guid.Empty, Guid.Empty, (ResultCode)ResultCodes[Math.Clamp(ResultIndex, 0, ResultCodes.Length - 1)]),
            };
            _engine.AutoIncrementSeq = AutoSeq;
            await _engine.SendAsync(new SendRequest(
                Command: cmd,
                To: ToIndex == 0 ? DeviceId.Automation : DeviceId.Vision,
                Data: data,
                SeqOverride: AutoSeq ? null : (byte)Seq,
                LengthOverride: AutoLen ? null : (ushort)Len,
                ChecksumOverride: AutoChk ? null : (byte)Chk));
            if (AutoSeq) Seq = _engine.NextSeq;
        }
        catch (Exception ex) { RawError = ex.Message; }
        finally { _busy = false; RefreshLog(); }
    }

    [RelayCommand]
    private async Task SendRaw()
    {
        if (_busy || !_engine.IsConnected) return;
        _busy = true;
        RawError = null;
        try
        {
            if (!FrameCodec.TryParseHex(RawHex, out var bytes) || bytes.Length == 0)
            {
                RawError = "HEX 파싱 실패 — 짝수 길이 16진수만 입력하세요.";
                return;
            }
            await _engine.SendRawAsync(bytes);
        }
        finally { _busy = false; RefreshLog(); }
    }

    [RelayCommand]
    private void ClearLog() { _engine.ClearLog(); RefreshLog(); }

    private byte[] BuildHeartbeat()
    {
        var ts = (HbTimestamp ?? "").PadRight(14)[..14];
        var rs = (HbReserved ?? "").PadRight(6)[..6];
        var data = new byte[20];
        System.Text.Encoding.ASCII.GetBytes(ts, 0, 14, data, 0);
        System.Text.Encoding.ASCII.GetBytes(rs, 0, 6, data, 14);
        return data;
    }
}

/// <summary>로그 한 줄(표시용).</summary>
public sealed class VisionLogRow
{
    public string Time { get; }
    public string Direction { get; }
    public string Hex { get; }
    public string Summary { get; }
    public LogDirection Kind { get; }

    public VisionLogRow(VisionLogEntry e)
    {
        Time = e.At.ToString("HH:mm:ss.fff");
        Direction = e.Direction.ToString();
        Hex = e.Hex;
        Summary = e.Summary;
        Kind = e.Direction;
    }
}
