using System.Net.Sockets;

namespace HD_AMR.Communication.Vision;

/// <summary>자동화 S/W(클라이언트) 측 전송. 선택적 자동 재연결(기본 3초 × 최대 10회).</summary>
public sealed class VisionTcpClientTransport : IVisionTransport
{
    private readonly string _host;
    private readonly int _port;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _runLoop;

    public bool AutoReconnect { get; set; }
    public int ReconnectDelayMs { get; set; } = 3000;
    public int MaxReconnectAttempts { get; set; } = 10;

    public VisionTcpClientTransport(string host, int port)
    {
        _host = host;
        _port = port;
    }

    public bool IsConnected => _client?.Connected ?? false;

    public event EventHandler<TransportEvent>? StateChanged;
    public event EventHandler<ReadOnlyMemory<byte>>? BytesReceived;

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_runLoop is not null) throw new InvalidOperationException("이미 실행 중입니다.");
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _runLoop = Task.Run(() => RunAsync(_cts.Token));
        return Task.CompletedTask;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var attempts = 0;
        while (!ct.IsCancellationRequested)
        {
            attempts++;
            Raise($"Connecting → {_host}:{_port} (시도 {attempts})", false);
            try
            {
                _client = new TcpClient();
                await _client.ConnectAsync(_host, _port, ct).ConfigureAwait(false);
                _stream = _client.GetStream();
                attempts = 0;
                Raise($"Connected → {_host}:{_port}", true);
                await ReadLoopAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Raise($"연결 실패: {ex.Message}", false);
            }
            finally
            {
                try { _stream?.Dispose(); } catch { /* ignore */ }
                try { _client?.Close(); } catch { /* ignore */ }
                _stream = null;
                _client = null;
            }

            if (!AutoReconnect || ct.IsCancellationRequested) break;
            if (attempts >= MaxReconnectAttempts)
            {
                Raise($"재연결 최대 {MaxReconnectAttempts}회 초과 — 중단", false);
                break;
            }

            Raise($"{ReconnectDelayMs / 1000}초 후 재연결…", false);
            try { await Task.Delay(ReconnectDelayMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        Raise("Disconnected", false);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[4096];
        while (!ct.IsCancellationRequested && _stream is { } s)
        {
            int n;
            try { n = await s.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (IOException) { break; }
            if (n <= 0) break;
            BytesReceived?.Invoke(this, new ReadOnlyMemory<byte>(buffer, 0, n).ToArray());
        }
    }

    public async Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
    {
        var s = _stream;
        if (s is null) throw new InvalidOperationException("연결되어 있지 않습니다.");
        await s.WriteAsync(bytes, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _stream?.Dispose(); } catch { /* ignore */ }
        try { _client?.Close(); } catch { /* ignore */ }
        if (_runLoop is not null)
        {
            try { await _runLoop.ConfigureAwait(false); } catch { /* ignore */ }
        }
        _stream = null;
        _client = null;
        _runLoop = null;
        _cts?.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private void Raise(string status, bool connected) =>
        StateChanged?.Invoke(this, new TransportEvent(status, connected));
}
