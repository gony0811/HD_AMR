using System.Net;
using System.Net.Sockets;
using System.Text;
using HD.AMR.App.Communication.GasDetector;
using Xunit;

namespace HD.AMR.Tests;

public class GasDetectorProtocolTests
{
    [Theory]
    [InlineData("2.876E-7", 2.876E-7)]
    [InlineData("1.0E-9\r", 1.0E-9)]
    [InlineData("4.5-7", 4.5E-7)]
    [InlineData("-1.2+3", -1.2E3)]
    [InlineData("15.6", 15.6)]
    public void TryParseNumber_Accepts_Spec_Formats(string text, double expected)
    {
        Assert.True(Xl3000AsciiProtocol.TryParseNumber(text, out var v));
        Assert.Equal(expected, v, 12);
    }

    [Theory]
    [InlineData("OK")]
    [InlineData("MEAS")]
    [InlineData("")]
    public void TryParseNumber_Rejects_NonNumbers(string text)
        => Assert.False(Xl3000AsciiProtocol.TryParseNumber(text, out _));

    [Theory]
    [InlineData("MEAS", Xl3000Mode.Measuring)]
    [InlineData("STBY", Xl3000Mode.Standby)]
    [InlineData("ERROR", Xl3000Mode.Error)]
    [InlineData("CAL", Xl3000Mode.Calibrating)]
    [InlineData("ACCL", Xl3000Mode.RunUp)]
    [InlineData("EMI OFF", Xl3000Mode.EmissionOff)]
    [InlineData("???", Xl3000Mode.Unknown)]
    public void ParseMode(string text, Xl3000Mode expected)
        => Assert.Equal(expected, Xl3000AsciiProtocol.ParseMode(text));

    [Fact]
    public void ParseTriggers_Reads_Four_States()
    {
        Assert.Equal(new[] { true, false, false, true }, Xl3000AsciiProtocol.ParseTriggers("ON,OFF,OFF,ON"));
        Assert.Equal(new[] { false, true, false, false }, Xl3000AsciiProtocol.ParseTriggers("OFF, ON, OFF, OFF"));
        Assert.Null(Xl3000AsciiProtocol.ParseTriggers("ON,OFF"));
    }

    [Fact]
    public void TryGetError_Reads_Exx()
    {
        Assert.True(Xl3000AsciiProtocol.TryGetError("E06", out var code));
        Assert.Equal(6, code);
        Assert.Contains("RS232", Xl3000AsciiProtocol.ErrorMeaning(code));
        Assert.False(Xl3000AsciiProtocol.TryGetError("2.0E-9", out _));
    }

    [Fact]
    public void Frame_Ends_With_CR()
        => Assert.Equal("*READ?\r", Xl3000AsciiProtocol.Frame(Xl3000AsciiProtocol.ReadLeakRate));

    [Fact]
    public void Judge_Uses_Max_Against_Limit()
    {
        Assert.Equal(GasJudgement.Ok, GasMeasurementResult.Judge(new[] { 1e-7, 5e-6 }, 1e-5));
        Assert.Equal(GasJudgement.Ng, GasMeasurementResult.Judge(new[] { 1e-7, 1e-5 }, 1e-5));
        Assert.Equal(GasJudgement.Error, GasMeasurementResult.Judge(Array.Empty<double>(), 1e-5));
    }

    [Fact]
    public void EndpointText_Describes_Transport()
    {
        var s = new GasDetectorSettings { PortName = "COM7" };
        Assert.Equal("RS485 COM7 @ 19200 8N1", s.EndpointText);
        s.Transport = GasDetectorTransportKind.Tcp;
        s.Host = "10.0.0.5";
        s.TcpPort = 4001;
        Assert.Equal("TCP 10.0.0.5:4001", s.EndpointText);
    }
}

/// <summary>TCP 전송 + 클라이언트를 가짜 XL3000flex(로컬 TcpListener)와 왕복시킨다.</summary>
public class GasDetectorTcpClientTests
{
    private static async Task RunFakeDeviceAsync(TcpListener listener, Func<string, string?> reply, CancellationToken ct)
    {
        using var socket = await listener.AcceptTcpClientAsync(ct);
        var stream = socket.GetStream();
        var buf = new byte[256];
        var pending = new StringBuilder();
        while (!ct.IsCancellationRequested)
        {
            var n = await stream.ReadAsync(buf, ct);
            if (n == 0) return;
            pending.Append(Encoding.ASCII.GetString(buf, 0, n));
            int cr;
            while ((cr = pending.ToString().IndexOf('\r')) >= 0)
            {
                var cmd = pending.ToString(0, cr);
                pending.Remove(0, cr + 1);
                // 응답을 두 조각으로 나눠 보내 분할 수신도 확인한다.
                if (reply(cmd) is { } r)
                {
                    var bytes = Encoding.ASCII.GetBytes(r + "\r");
                    await stream.WriteAsync(bytes.AsMemory(0, 1), ct);
                    await stream.WriteAsync(bytes.AsMemory(1), ct);
                }
            }
        }
    }

    private static string? FakeReply(string cmd) => cmd.ToUpperInvariant() switch
    {
        "*READ?" => "2.5E-7",
        "*READ:MBAR*L/S?" => "3.0E-6",
        "*STATUS?" => "MEAS",
        "*STARTX" => "E03",
        "*ZERO" => "OK",
        "*SILENT?" => null,
        _ => "E10",
    };

    [Fact]
    public async Task Tcp_RoundTrip_Reads_Values_And_Errors()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var device = RunFakeDeviceAsync(listener, FakeReply, cts.Token);

        using var client = new Xl3000FlexClient(new TcpGasDetectorTransport("127.0.0.1", port, 2000), responseTimeoutMs: 500);
        await client.OpenAsync(cts.Token);

        Assert.Equal(2.5E-7, await client.ReadLeakRateAsync(cts.Token), 12);
        Assert.Equal(3.0E-6, await client.ReadLeakRateMbarLsAsync(cts.Token), 12);
        Assert.Equal("MEAS", await client.QueryAsync(Xl3000AsciiProtocol.ReadStatus, cts.Token));
        await client.CommandAsync(Xl3000AsciiProtocol.Zero, cts.Token);

        var ex = await Assert.ThrowsAsync<GasDetectorException>(() => client.QueryAsync("*STARTX", cts.Token));
        Assert.Contains("E03", ex.Message);

        await Assert.ThrowsAsync<TimeoutException>(() => client.QueryAsync("*SILENT?", cts.Token));
        // 무응답 뒤에도 다음 문답은 정상이어야 한다.
        Assert.Equal(2.5E-7, await client.ReadLeakRateAsync(cts.Token), 12);

        client.Close();
        cts.Cancel();
        listener.Stop();
        try { await device; } catch (OperationCanceledException) { } catch (IOException) { } catch (SocketException) { }
    }
}
