using System.IO.Ports;
using System.Net.Sockets;
using System.Text;

namespace HD.AMR.App.Communication.GasDetector;

/// <summary>
/// 가스 디텍터 전송 계층 — 한 줄(CR 종결) 명령을 보내고 한 줄 응답을 받는 문답식 교환만 한다.
/// 직렬화(동시 호출 방지)는 상위 <see cref="Xl3000FlexClient"/> 가 맡는다.
/// </summary>
public interface IGasDetectorTransport : IDisposable
{
    bool IsOpen { get; }
    Task OpenAsync(CancellationToken ct);
    void Close();

    /// <summary>수신 버퍼를 비우고 <paramref name="frame"/> 을 보낸 뒤 CR 까지 한 줄을 받는다. 타임아웃이면 null.</summary>
    Task<string?> ExchangeAsync(string frame, int timeoutMs, CancellationToken ct);
}

public static class GasDetectorTransportFactory
{
    public static IGasDetectorTransport Create(GasDetectorSettings s) => s.Transport switch
    {
        GasDetectorTransportKind.Tcp => new TcpGasDetectorTransport(s.Host, s.TcpPort, s.ConnectTimeoutMs),
        _ => new SerialGasDetectorTransport(s.PortName, s.BaudRate, s.Parity, s.DataBits, s.StopBits),
    };
}

/// <summary>
/// 시리얼(RS485/RS232) 전송. RS485 는 USB-RS485 변환기(자동 송수신 전환형)의 COM 포트로 연다 —
/// 2선식 반이중이라도 문답식이므로 별도 방향 제어가 필요 없다.
/// </summary>
public sealed class SerialGasDetectorTransport(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits)
    : IGasDetectorTransport
{
    private SerialPort? _port;

    public bool IsOpen => _port is { IsOpen: true };

    public static string[] AvailablePorts()
    {
        try { return SerialPort.GetPortNames().OrderBy(p => p).ToArray(); }
        catch { return Array.Empty<string>(); }
    }

    public Task OpenAsync(CancellationToken ct)
    {
        if (IsOpen) return Task.CompletedTask;
        // SerialPort.Open 은 동기 블로킹 — 호출 스레드를 붙잡지 않도록 스레드풀에서 연다.
        return Task.Run(() =>
        {
            var port = new SerialPort(portName, baudRate, parity, dataBits, stopBits)
            {
                Handshake = Handshake.None,
                NewLine = Xl3000AsciiProtocol.Terminator.ToString(),
                Encoding = Encoding.ASCII,
                WriteTimeout = 1000,
            };
            port.Open();
            port.DiscardInBuffer();
            port.DiscardOutBuffer();
            _port = port;
        }, ct);
    }

    public void Close()
    {
        var port = _port;
        _port = null;
        if (port is null) return;
        try { if (port.IsOpen) port.Close(); } catch { /* 닫기 실패는 무시 */ }
        port.Dispose();
    }

    public Task<string?> ExchangeAsync(string frame, int timeoutMs, CancellationToken ct)
    {
        var port = _port ?? throw new InvalidOperationException("시리얼 포트가 열려 있지 않습니다.");
        return Task.Run<string?>(() =>
        {
            port.DiscardInBuffer();
            port.ReadTimeout = timeoutMs;
            port.Write(frame);
            try { return port.ReadLine().Trim('\r', '\n', ' '); }
            catch (TimeoutException) { return null; }
        }, ct);
    }

    public void Dispose() => Close();
}

/// <summary>
/// 이더넷(TCP) 전송 — 시리얼-이더넷 디바이스 서버(TCP Server/Raw 모드)에 접속해 같은 ASCII 프레임을 주고받는다.
/// </summary>
public sealed class TcpGasDetectorTransport(string host, int port, int connectTimeoutMs) : IGasDetectorTransport
{
    private TcpClient? _client;
    private NetworkStream? _stream;
    private readonly StringBuilder _pending = new();
    private readonly byte[] _buf = new byte[512];

    public bool IsOpen => _client is { Connected: true } && _stream is not null;

    public async Task OpenAsync(CancellationToken ct)
    {
        if (IsOpen) return;
        var client = new TcpClient { NoDelay = true };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(connectTimeoutMs);
        try
        {
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException($"TCP 접속 타임아웃 ({host}:{port}, {connectTimeoutMs} ms)");
        }
        catch
        {
            client.Dispose();
            throw;
        }
        _client = client;
        _stream = client.GetStream();
        _pending.Clear();
    }

    public void Close()
    {
        _stream?.Dispose();
        _stream = null;
        _client?.Dispose();
        _client = null;
        _pending.Clear();
    }

    public async Task<string?> ExchangeAsync(string frame, int timeoutMs, CancellationToken ct)
    {
        var stream = _stream ?? throw new InvalidOperationException("TCP 연결이 없습니다.");

        // 이전 명령의 늦은 응답이 섞이지 않게 남은 수신분을 버린다.
        _pending.Clear();
        while (stream.DataAvailable)
            _ = await stream.ReadAsync(_buf, ct).ConfigureAwait(false);

        await stream.WriteAsync(Encoding.ASCII.GetBytes(frame), ct).ConfigureAwait(false);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            while (true)
            {
                var text = _pending.ToString();
                var cr = text.IndexOf(Xl3000AsciiProtocol.Terminator);
                if (cr >= 0)
                {
                    _pending.Remove(0, cr + 1);
                    return text[..cr].Trim('\r', '\n', ' ');
                }
                var n = await stream.ReadAsync(_buf, cts.Token).ConfigureAwait(false);
                if (n == 0)
                    throw new IOException("가스 디텍터 TCP 연결이 끊어졌습니다.");
                _pending.Append(Encoding.ASCII.GetString(_buf, 0, n));
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    public void Dispose() => Close();
}
