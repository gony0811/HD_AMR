using System.Buffers.Binary;
using System.Net;
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

    public bool AutoReply { get; set; }              // server-only
    public ushort AutoReplyResultCode { get; set; }  // server-only

    public byte NextSeq { get; private set; }
    public bool AutoIncrementSeq { get; set; } = true;

    /// <summary>최근 전송 상태 문자열(연결/리슨/끊김 등). UI 표시용.</summary>
    public string Status { get; private set; } = "Disconnected";

    private IVisionTransport? _transport;
    private readonly FrameParser _parser = new();
    private CancellationTokenSource? _heartbeatCts;
    // Serializes wire writes so the heartbeat timer cannot interleave bytes with a UI-triggered send.
    private readonly SemaphoreSlim _sendLock = new(1, 1);

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

    /// <summary>비전(Server)으로 지정 IP/포트에서 Listen.</summary>
    public Task ListenServerAsync(string bindIp, int port)
    {
        if (!IPAddress.TryParse(bindIp, out var ip)) ip = IPAddress.Loopback;
        return StartAsync(new VisionTcpServerTransport(ip, port));
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
                MaybeAutoReply(f);
            }
            else
            {
                var summary = r.Decoded is { } d ? FrameDescriber.Summary(d) : "(파싱 실패)";
                var detail = r.FaultDetail ?? r.Fault?.ToString();
                Log(LogDirection.Error, FrameCodec.ToHex(r.Bytes), $"⚠ {r.Fault}: {detail}  | {summary}");
            }
        }
    }

    private void MaybeAutoReply(Frame f)
    {
        if (Role != SideRole.Server || !AutoReply) return;
        if (f.Command != (byte)CommandCode.CaptureReq) return;

        var data = new byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0, 2), AutoReplyResultCode);
        // Reserved 4 bytes already 0x00.

        _ = SendAsync(new SendRequest(
            Command: CommandCode.CaptureRes,
            To: PeerId,
            Data: data,
            SeqOverride: f.Seq,
            LengthOverride: null,
            ChecksumOverride: null,
            Note: "auto-reply"));
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
