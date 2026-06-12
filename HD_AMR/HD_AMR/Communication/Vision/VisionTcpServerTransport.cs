using System.Net;
using System.Net.Sockets;

namespace HD_AMR.Communication.Vision;

/// <summary>비전 S/W(서버) 측 전송. 한 번에 하나의 클라이언트만 수용한다.</summary>
public sealed class VisionTcpServerTransport : IVisionTransport
{
    private readonly IPAddress _bind;
    private readonly int _port;
    private TcpListener? _listener;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public VisionTcpServerTransport(IPAddress bind, int port)
    {
        _bind = bind;
        _port = port;
    }

    public bool IsConnected => _client?.Connected ?? false;

    public event EventHandler<TransportEvent>? StateChanged;
    public event EventHandler<ReadOnlyMemory<byte>>? BytesReceived;

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_listener is not null) throw new InvalidOperationException("이미 Listen 중입니다.");
        _listener = new TcpListener(_bind, _port);
        _listener.Start();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Raise($"Listening {_bind}:{_port}", false);
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient incoming;
                try
                {
                    incoming = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }

                if (_client is { Connected: true })
                {
                    // Already serving a client — reject the new one.
                    incoming.Close();
                    continue;
                }

                _client = incoming;
                _stream = _client.GetStream();
                var remote = _client.Client.RemoteEndPoint?.ToString() ?? "?";
                Raise($"Connected ← {remote}", true);
                await ReadLoopAsync(ct).ConfigureAwait(false);
                Raise($"Listening {_bind}:{_port}", false);
            }
        }
        catch (Exception ex)
        {
            Raise($"Listen 오류: {ex.Message}", false);
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[4096];
        try
        {
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
        finally
        {
            try { _stream?.Dispose(); } catch { /* ignore */ }
            try { _client?.Close(); } catch { /* ignore */ }
            _stream = null;
            _client = null;
        }
    }

    public async Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
    {
        var s = _stream;
        if (s is null) throw new InvalidOperationException("연결된 클라이언트가 없습니다.");
        await s.WriteAsync(bytes, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _listener?.Stop(); } catch { /* ignore */ }
        try { _stream?.Dispose(); } catch { /* ignore */ }
        try { _client?.Close(); } catch { /* ignore */ }
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch { /* ignore */ }
        }
        _stream = null;
        _client = null;
        _listener = null;
        _acceptLoop = null;
        _cts?.Dispose();
        _cts = null;
        Raise("Stopped", false);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private void Raise(string status, bool connected) =>
        StateChanged?.Invoke(this, new TransportEvent(status, connected));
}
