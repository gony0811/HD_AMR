using System.IO.Ports;
using System.Text;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Communication;

/// <summary>
/// 텔레스코픽 컨트롤러 시리얼 I/O. 프로토콜이 <b>엄격한 문답식</b>이라 모든 송수신을
/// <see cref="SemaphoreSlim"/> 로 직렬화한다 — 동시 호출이 겹치면 응답이 서로 섞인다.
///
/// 명령 문자열 생성·응답 파싱은 전부 <see cref="TelescopicProtocol"/>(순수 계층)에 있고,
/// 이 클래스는 포트 열기/쓰기/한 줄 읽기만 한다.
/// </summary>
public sealed class TelescopicClient : IDisposable
{
    private readonly TelescopicSerialSettings _settings;
    private readonly ILogger<TelescopicClient> _logger;
    private readonly SemaphoreSlim _io = new(1, 1);
    private SerialPort? _port;

    public TelescopicClient(TelescopicSerialSettings settings, ILogger<TelescopicClient> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public bool IsOpen => _port is { IsOpen: true };

    /// <summary>마지막 송신 명령(진단 표시용).</summary>
    public string? LastTx { get; private set; }

    /// <summary>마지막 수신 응답(진단 표시용).</summary>
    public string? LastRx { get; private set; }

    /// <summary>이 환경에서 보이는 시리얼 포트 목록.</summary>
    public static string[] AvailablePorts()
    {
        try { return SerialPort.GetPortNames(); }
        catch { return Array.Empty<string>(); }
    }

    public void Open()
    {
        if (IsOpen) return;

        // 사양서 1장: 9600 8N1, 패리티/흐름제어 없음.
        var port = new SerialPort(_settings.PortName, _settings.BaudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            NewLine = TelescopicProtocol.Terminator,
            ReadTimeout = _settings.ReadTimeoutMs,
            WriteTimeout = _settings.WriteTimeoutMs,
            Encoding = Encoding.ASCII,
        };
        port.Open();
        port.DiscardInBuffer();
        port.DiscardOutBuffer();
        _port = port;
        _logger.LogInformation("텔레스코픽 포트 열림 ({Port} @ {Baud} 8N1)", _settings.PortName, _settings.BaudRate);
    }

    public void Close()
    {
        var port = _port;
        _port = null;
        if (port is null) return;
        try { if (port.IsOpen) port.Close(); }
        catch (Exception ex) { _logger.LogDebug(ex, "텔레스코픽 포트 닫기 중 예외 — 무시"); }
        port.Dispose();
    }

    /// <summary>
    /// 명령 한 건을 보내고 한 줄 응답을 받는다. 타임아웃이면 null.
    /// 종결자(<c>\r\n</c>)는 <see cref="SerialPort.WriteLine"/> 가 붙인다.
    /// </summary>
    public async Task<string?> SendAsync(string command, CancellationToken ct = default)
    {
        await _io.WaitAsync(ct);
        try
        {
            var port = _port ?? throw new InvalidOperationException("텔레스코픽 포트가 열려 있지 않습니다.");

            port.DiscardInBuffer();
            port.WriteLine(command);
            LastTx = command;

            try
            {
                var response = port.ReadLine().TrimEnd('\r', '\n');
                LastRx = response;
                return response;
            }
            catch (TimeoutException)
            {
                LastRx = null;
                _logger.LogDebug("텔레스코픽 응답 타임아웃 (TX={Tx})", command);
                return null;
            }
        }
        finally { _io.Release(); }
    }

    /// <summary>응답을 기다리지 않는 송신 — 조그 유지 명령처럼 왕복 지연이 아까운 경우에만 쓴다.</summary>
    public async Task SendNoWaitAsync(string command, CancellationToken ct = default)
    {
        await _io.WaitAsync(ct);
        try
        {
            var port = _port ?? throw new InvalidOperationException("텔레스코픽 포트가 열려 있지 않습니다.");
            port.WriteLine(command);
            LastTx = command;
            // 응답은 다음 SendAsync 의 DiscardInBuffer 에서 버려진다.
        }
        finally { _io.Release(); }
    }

    public void Dispose()
    {
        Close();
        _io.Dispose();
    }
}
