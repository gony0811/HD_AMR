using System.Text;

namespace HD_AMR.Communication.Vision;

public enum SideRole { Client, Server }

/// <summary>
/// 한 쪽(자동화=Client / 비전=Server)의 시뮬레이터. 전송·파서·시퀀스 카운터·5초
/// Heartbeat 타이머·(서버 한정)CAPTURE_REQ 자동 응답을 소유한다. UI 가 폴링할 수 있도록
/// 로그를 내부 버퍼(<see cref="MaxLogs"/> 상한)에 누적하고 <see cref="LogVersion"/> 으로
/// 변경을 알린다. Blazor Server 회로 수명과 무관하게 살아남도록 이벤트 구독이 아닌
/// 폴링 모델을 쓴다.
/// </summary>
public sealed class VisionEngine : IAsyncDisposable
{
    public SideRole Role { get; }
    public DeviceId MyId => Role == SideRole.Client ? DeviceId.Automation : DeviceId.Vision;
    public DeviceId PeerId => Role == SideRole.Client ? DeviceId.Vision : DeviceId.Automation;

    public bool AutoHeartbeat { get; set; } = true;
    public int HeartbeatPeriodMs { get; set; } = 5000;

    public byte NextSeq { get; private set; }
    public bool AutoIncrementSeq { get; set; } = true;

    /// <summary>최근 전송 상태 문자열(연결/리슨/끊김 등). UI 표시용.</summary>
    public string Status { get; private set; } = "Disconnected";

    private IVisionTransport? _transport;
    private readonly FrameParser _parser = new();
    private CancellationTokenSource? _heartbeatCts;
    // Serializes wire writes so the heartbeat timer cannot interleave bytes with a UI-triggered send.
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    // ── CAPTURE_REQ 요청/응답 상관 ──────────────────────────────────
    // Teaching 실행은 한 번에 한 점씩 순차로 await 하므로 단일 보류 슬롯으로 충분하다.
    // (응답 프레임의 SEQ 에코를 가정하지 않고, 직전에 보낸 요청 하나를 매칭한다.)
    private readonly object _captureLock = new();
    private TaskCompletionSource<Frame>? _pendingCapture;

    // ── 로그 버퍼(폴링 모델) ────────────────────────────────────────
    private const int MaxLogs = 1000;
    private readonly object _logLock = new();
    private readonly List<VisionLogEntry> _logs = new();
    /// <summary>로그가 추가/초기화될 때마다 증가. UI 는 이 값 변화로 재렌더 여부를 판단.</summary>
    public long LogVersion { get; private set; }

    public VisionEngine(SideRole role) { Role = role; }

    public bool IsConnected => _transport?.IsConnected ?? false;

    public IReadOnlyList<VisionLogEntry> SnapshotLogs()
    {
        lock (_logLock) return _logs.ToArray();
    }

    public void ClearLog()
    {
        lock (_logLock) { _logs.Clear(); LogVersion++; }
    }

    // ── 연결 ────────────────────────────────────────────────────────
    /// <summary>자동화(Client)로 비전 서버에 접속.</summary>
    public Task ConnectClientAsync(string host, int port, bool autoReconnect)
    {
        var t = new VisionTcpClientTransport(host, port) { AutoReconnect = autoReconnect };
        return StartAsync(t);
    }

    public async Task StartAsync(IVisionTransport transport)
    {
        await StopAsync().ConfigureAwait(false);
        _transport = transport;
        _transport.StateChanged += OnState;
        _transport.BytesReceived += OnBytes;
        await _transport.StartAsync().ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        if (_heartbeatCts is { } hbc) { try { hbc.Cancel(); } catch { /* ignore */ } }
        if (_transport is { } t)
        {
            t.StateChanged -= OnState;
            t.BytesReceived -= OnBytes;
            await t.StopAsync().ConfigureAwait(false);
            await t.DisposeAsync().ConfigureAwait(false);
            _transport = null;
        }
        _parser.Reset();
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private void OnState(object? sender, TransportEvent e)
    {
        Status = e.Status;
        Log(LogDirection.Info, "", e.Status);

        if (e.IsConnected && AutoHeartbeat) StartHeartbeat();
        else StopHeartbeat();
    }

    private void OnBytes(object? sender, ReadOnlyMemory<byte> chunk)
    {
        var results = _parser.Feed(chunk.Span);
        foreach (var r in results)
        {
            if (r.Decoded is { } f && r.Fault is null)
            {
                Log(LogDirection.Rx, FrameCodec.ToHex(r.Bytes), FrameDescriber.Summary(f));
                // CAPTURE_RES(성공/실패 코드) 또는 ERROR_NOTI 수신 시 보류 중인 요청을 완료시킨다.
                if (f.Command is (byte)CommandCode.CaptureRes or (byte)CommandCode.ErrorNoti)
                {
                    TaskCompletionSource<Frame>? pending;
                    lock (_captureLock) { pending = _pendingCapture; }
                    pending?.TrySetResult(f);
                }
            }
            else
            {
                var summary = r.Decoded is { } d ? FrameDescriber.Summary(d) : "(파싱 실패)";
                var detail = r.FaultDetail ?? r.Fault?.ToString();
                Log(LogDirection.Error, FrameCodec.ToHex(r.Bytes), $"⚠ {r.Fault}: {detail}  | {summary}");
            }
        }
    }

    public byte ConsumeSeq(byte? explicitSeq)
    {
        if (explicitSeq is { } s) return s;
        var v = NextSeq;
        if (AutoIncrementSeq) NextSeq = (byte)((NextSeq + 1) & 0xFF);
        return v;
    }

    public async Task SendAsync(SendRequest req)
    {
        var t = _transport;
        if (t is null || !t.IsConnected)
        {
            Log(LogDirection.Error, "", "전송 실패: 연결되어 있지 않음");
            return;
        }

        var seq = ConsumeSeq(req.SeqOverride);
        var bytes = FrameCodec.Encode(
            seq,
            (byte)req.Command,
            (byte)MyId,
            (byte)req.To,
            req.Data,
            req.LengthOverride,
            req.ChecksumOverride);

        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await t.SendAsync(bytes).ConfigureAwait(false);
            // Re-decode for the log so we get a faithful summary including any overrides.
            var parsed = new FrameParser().Feed(bytes);
            var summary = parsed.Count > 0 && parsed[0].Decoded is { } d
                ? FrameDescriber.Summary(d)
                : $"{CommandNames.NameOf((byte)req.Command)} SEQ=0x{seq:X2}";
            if (!string.IsNullOrEmpty(req.Note)) summary = $"[{req.Note}] " + summary;
            Log(LogDirection.Tx, FrameCodec.ToHex(bytes), summary);
        }
        catch (Exception ex)
        {
            Log(LogDirection.Error, FrameCodec.ToHex(bytes), $"전송 실패: {ex.Message}");
        }
        finally { _sendLock.Release(); }
    }

    /// <summary>
    /// CAPTURE_REQ 를 전송하고 비전의 응답(CAPTURE_RES/ERROR_NOTI)을 <paramref name="timeout"/> 까지 대기한다.
    /// 미연결이면 전송하지 않고 즉시 반환(Sent=false). 응답이 없으면 Responded=false(타임아웃).
    /// </summary>
    public async Task<CaptureOutcome> RequestCaptureAsync(byte[] data, TimeSpan timeout, CancellationToken ct = default)
    {
        var t = _transport;
        if (t is null || !t.IsConnected)
        {
            Log(LogDirection.Error, "", "CAPTURE_REQ 전송 실패: 연결되어 있지 않음");
            return new CaptureOutcome(false, false, null);
        }

        var tcs = new TaskCompletionSource<Frame>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_captureLock) { _pendingCapture = tcs; }
        try
        {
            await SendAsync(new SendRequest(
                Command: CommandCode.CaptureReq,
                To: PeerId,
                Data: data,
                Note: "capture")).ConfigureAwait(false);

            var winner = await Task.WhenAny(tcs.Task, Task.Delay(timeout, ct)).ConfigureAwait(false);
            if (winner == tcs.Task)
            {
                var f = await tcs.Task.ConfigureAwait(false);
                if (f.Command == (byte)CommandCode.CaptureRes && f.Data.Length >= 2)
                {
                    var code = (ushort)(f.Data[0] | (f.Data[1] << 8));
                    return new CaptureOutcome(true, true, (ResultCode)code);
                }
                // ERROR_NOTI 등: 응답은 왔으나 성공 코드가 아님.
                return new CaptureOutcome(true, true, ResultCode.ErrUnknown);
            }

            return new CaptureOutcome(true, false, null); // 타임아웃/취소
        }
        finally
        {
            lock (_captureLock) { if (ReferenceEquals(_pendingCapture, tcs)) _pendingCapture = null; }
        }
    }

    public async Task SendRawAsync(byte[] bytes, string note = "raw")
    {
        var t = _transport;
        if (t is null || !t.IsConnected)
        {
            Log(LogDirection.Error, FrameCodec.ToHex(bytes), "raw 전송 실패: 연결되어 있지 않음");
            return;
        }
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await t.SendAsync(bytes).ConfigureAwait(false);
            Log(LogDirection.Tx, FrameCodec.ToHex(bytes), $"[{note}] {bytes.Length}바이트 원시 전송");
        }
        catch (Exception ex)
        {
            Log(LogDirection.Error, FrameCodec.ToHex(bytes), $"raw 전송 실패: {ex.Message}");
        }
        finally { _sendLock.Release(); }
    }

    private void StartHeartbeat()
    {
        StopHeartbeat();
        if (!AutoHeartbeat) return;
        var cts = new CancellationTokenSource();
        _heartbeatCts = cts;
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try { await Task.Delay(HeartbeatPeriodMs, cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                if (cts.IsCancellationRequested) break;
                if (!IsConnected) break;

                var ts = DateTime.Now.ToString("yyyyMMddHHmmss");
                var data = new byte[20];
                Encoding.ASCII.GetBytes(ts, 0, 14, data, 0);
                Encoding.ASCII.GetBytes("000000", 0, 6, data, 14);
                await SendAsync(new SendRequest(
                    Command: CommandCode.Heartbeat,
                    To: PeerId,
                    Data: data,
                    Note: "heartbeat")).ConfigureAwait(false);
            }
        });
    }

    private void StopHeartbeat()
    {
        if (_heartbeatCts is { } cts)
        {
            try { cts.Cancel(); } catch { /* ignore */ }
            _heartbeatCts = null;
        }
    }

    private void Log(LogDirection dir, string hex, string summary, string? detail = null)
    {
        lock (_logLock)
        {
            _logs.Add(new VisionLogEntry(DateTime.Now, dir, hex, summary, detail));
            if (_logs.Count > MaxLogs) _logs.RemoveRange(0, _logs.Count - MaxLogs);
            LogVersion++;
        }
    }
}

public sealed record SendRequest(
    CommandCode Command,
    DeviceId To,
    byte[] Data,
    byte? SeqOverride = null,
    ushort? LengthOverride = null,
    byte? ChecksumOverride = null,
    string? Note = null);

/// <summary>
/// <see cref="VisionEngine.RequestCaptureAsync"/> 결과. Sent=전송 여부(미연결이면 false),
/// Responded=응답 수신 여부(타임아웃이면 false), Code=수신한 결과 코드(있을 때).
/// </summary>
public sealed record CaptureOutcome(bool Sent, bool Responded, ResultCode? Code)
{
    public bool Success => Responded && Code == ResultCode.Success;
}
