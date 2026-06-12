namespace HD_AMR.Communication.Vision;

public sealed class TransportEvent(string status, bool isConnected)
{
    public string Status { get; } = status;
    public bool IsConnected { get; } = isConnected;
}

public interface IVisionTransport : IAsyncDisposable
{
    bool IsConnected { get; }
    event EventHandler<TransportEvent>? StateChanged;
    event EventHandler<ReadOnlyMemory<byte>>? BytesReceived;
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync();
    Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct = default);
}
