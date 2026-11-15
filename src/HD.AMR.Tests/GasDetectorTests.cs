using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using HD.AMR.App.Communication.GasDetector;
using HD.AMR.App.Communication.Vda5050;
using HD.AMR.App.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
    public void Judge_Ok_When_AtOrBelow_Threshold_Ng_When_Above()
    {
        // SAIGE 규격 §2: OK = 임계값 이하, NG = 임계값 초과 — 같은 값은 OK.
        Assert.Equal(GasJudgement.Ok, GasMeasurementResult.Judge(5.0, 5.0));
        Assert.Equal(GasJudgement.Ng, GasMeasurementResult.Judge(5.0001, 5.0));
        Assert.Equal(GasJudgement.Ok, GasMeasurementResult.Judge(new[] { 0.8, 5.0 }, 5.0));
        Assert.Equal(GasJudgement.Ng, GasMeasurementResult.Judge(new[] { 0.8, 7.4 }, 5.0));
        Assert.Equal(GasJudgement.Error, GasMeasurementResult.Judge(Array.Empty<double>(), 5.0));
    }

    [Theory]
    [InlineData("PPM", "ppm", true)]
    [InlineData("MBAR*L/S", "mbar·l/s", true)]
    [InlineData("Pa·m³/s", "PA*M3/S", true)]
    [InlineData("MBAR*L/S", "ppm", false)]
    [InlineData("", "ppm", false)]
    public void SameUnit_Normalizes_Device_And_Display_Notation(string a, string b, bool same)
        => Assert.Equal(same, Xl3000AsciiProtocol.SameUnit(a, b));

    [Theory]
    [InlineData("NO ERROR/WARNING", 0)]
    [InlineData("W45", 45)]
    [InlineData("E 12", 12)]
    [InlineData("102", 102)]
    public void ParseErrorCode_Reads_First_Number(string response, int expected)
        => Assert.Equal(expected, Xl3000AsciiProtocol.ParseErrorCode(response));

    [Fact]
    public void ParseErrorCode_Unknown_Is_Null()
    {
        Assert.Null(Xl3000AsciiProtocol.ParseErrorCode(null));
        Assert.Null(Xl3000AsciiProtocol.ParseErrorCode("WARNING"));
    }

    [Theory]
    [InlineData(Xl3000Mode.Measuring, "MEASURING")]
    [InlineData(Xl3000Mode.Standby, "STANDBY")]
    [InlineData(Xl3000Mode.Calibrating, "CALIBRATING")]
    [InlineData(Xl3000Mode.RunUp, "WARMUP")]
    [InlineData(Xl3000Mode.Error, "ERROR")]
    public void SensorStateCode_Maps_Modes(Xl3000Mode mode, string expected)
        => Assert.Equal(expected, Xl3000AsciiProtocol.SensorStateCode(mode));

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

/// <summary>재측정·포인트/Task 판정·SAIGE 결과 본문(SAIGE 포밍가스 규격 v0.1 §2)·임계값 instantAction 파싱.</summary>
public class GasInspectionReportTests
{
    // SAIGE 사양 예시의 startedAt(1762151520000)과 같은 시각.
    private static readonly DateTime T0 = DateTimeOffset.FromUnixTimeMilliseconds(1762151520000).UtcDateTime;

    private static GasMeasurementResult Scan(int retry, double? value, double threshold = 5.0, int offsetMs = 0)
    {
        var j = value is { } v ? GasMeasurementResult.Judge(v, threshold) : GasJudgement.Error;
        return new GasMeasurementResult(retry + 1, "p", retry, T0, T0.AddMilliseconds(offsetMs), value is null ? 0 : 5,
            value, value, "ppm", threshold, j, value is null ? "응답 없음" : null);
    }

    [Theory]
    [InlineData(GasJudgement.Ok, 0, 2, false)]
    [InlineData(GasJudgement.Ng, 0, 2, true)]
    [InlineData(GasJudgement.Ng, 2, 2, false)]
    [InlineData(GasJudgement.Error, 1, 2, true)]
    [InlineData(GasJudgement.Ng, 0, 0, false)]
    public void NeedsRetry_Only_Until_Ok_Or_Limit(GasJudgement last, int done, int max, bool expected)
        => Assert.Equal(expected, GasPointResult.NeedsRetry(last, done, max));

    [Fact]
    public void Point_Result_Uses_Last_Valid_Scan()
    {
        Assert.Equal(GasJudgement.Ng, new GasPointResult(1, 0, 0, new[] { Scan(0, 7.4), Scan(1, 6.9), Scan(2, 7.1) }).Result);
        Assert.Equal(GasJudgement.Ok, new GasPointResult(1, 0, 0, new[] { Scan(0, 7.4), Scan(1, 3.0) }).Result);
        Assert.Equal(GasJudgement.Ng, new GasPointResult(1, 0, 0, new[] { Scan(0, 7.4), Scan(1, null) }).Result);
        Assert.Equal(GasJudgement.Error, new GasPointResult(1, 0, 0, new[] { Scan(0, null) }).Result);
    }

    [Fact]
    public void Task_Result_Ng_If_Any_Point_Ng()
    {
        var th = new GasThreshold(5.0, "ppm", GasThresholdSource.Acs, T0);
        var ok = new GasPointResult(1, 1200, 340, new[] { Scan(0, 0.8) });
        var ng = new GasPointResult(2, 1202, 340, new[] { Scan(0, 7.4) });
        var err = new GasPointResult(3, 1204, 340, new[] { Scan(0, null) });
        Assert.Equal(GasJudgement.Ng, new GasTaskResult(T0, T0, th, new[] { ok, ng, err }).Result);
        Assert.Equal(GasJudgement.Error, new GasTaskResult(T0, T0, th, new[] { ok, err }).Result);
        Assert.Equal(GasJudgement.Ok, new GasTaskResult(T0, T0, th, new[] { ok }).Result);
        Assert.Equal(GasJudgement.Error, new GasTaskResult(T0, T0, th, Array.Empty<GasPointResult>()).Result);
    }

    [Fact]
    public void Saige_Report_Matches_Spec_Shape()
    {
        var th = new GasThreshold(5.0, "ppm", GasThresholdSource.Acs, T0);
        var task = new GasTaskResult(T0, T0.AddSeconds(265), th, new[]
        {
            new GasPointResult(1, 1200, 340, new[] { Scan(0, 0.8, offsetMs: 1000) }),
            new GasPointResult(2, 1202, 340, new[] { Scan(0, 7.4), Scan(1, 6.9), Scan(2, 7.1) }),
        });
        var taskId = Guid.Parse("3f2b8c1e-7a4d-4e2b-9c11-5d0a6b7e8f90");

        var json = SaigeGasResultReport.Build(task, "CT1", taskId, 1, "AMR01").ToJson();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("CT1", root.GetProperty("tankId").GetString());
        Assert.Equal(taskId.ToString(), root.GetProperty("taskId").GetString());
        Assert.Equal(1, root.GetProperty("round").GetInt32());
        Assert.Equal("AMR01", root.GetProperty("robotId").GetString());
        Assert.Equal(1762151520000, root.GetProperty("startedAt").GetInt64());
        Assert.Equal(1762151785000, root.GetProperty("finishedAt").GetInt64());
        Assert.Equal(5.0, root.GetProperty("threshold").GetProperty("value").GetDouble());
        Assert.Equal("ppm", root.GetProperty("threshold").GetProperty("unit").GetString());
        Assert.Equal("NG", root.GetProperty("result").GetString());

        var points = root.GetProperty("points");
        Assert.Equal(2, points.GetArrayLength());
        Assert.Equal("OK", points[0].GetProperty("result").GetString());
        var s0 = points[0].GetProperty("scans")[0];
        Assert.Equal(0, s0.GetProperty("retry").GetInt32());
        Assert.Equal(1200, s0.GetProperty("u").GetInt32());
        Assert.Equal(340, s0.GetProperty("v").GetInt32());
        Assert.Equal(0.8, s0.GetProperty("value").GetDouble());
        Assert.Equal(1762151521000, s0.GetProperty("measuredAt").GetInt64());

        var scans = points[1].GetProperty("scans");
        Assert.Equal(3, scans.GetArrayLength());
        Assert.Equal(new[] { 0, 1, 2 }, scans.EnumerateArray().Select(x => x.GetProperty("retry").GetInt32()));
        Assert.All(scans.EnumerateArray(), x => Assert.Equal("NG", x.GetProperty("result").GetString()));
    }

    [Fact]
    public void Saige_Report_Omits_Scans_Without_Value_And_Null_Round()
    {
        var th = new GasThreshold(5.0, "ppm", GasThresholdSource.Local, T0);
        var task = new GasTaskResult(T0, T0, th, new[] { new GasPointResult(1, 0, 0, new[] { Scan(0, null) }) });
        var json = SaigeGasResultReport.Build(task, "CT1", null, null, null).ToJson();
        Assert.DoesNotContain("\"round\"", json);
        Assert.DoesNotContain("\"robotId\"", json);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(0, doc.RootElement.GetProperty("points")[0].GetProperty("scans").GetArrayLength());
        Assert.Equal("ERROR", doc.RootElement.GetProperty("result").GetString());
    }

    [Fact]
    public void ParseGasThreshold_Reads_Saige_Fields()
    {
        var action = JsonSerializer.Deserialize<VdaAction>("""
        { "actionType": "setGasThreshold", "actionId": "th-1", "blockingType": "NONE",
          "actionParameters": [
            { "key": "tankId", "value": "CT1" }, { "key": "value", "value": 4.5 }, { "key": "unit", "value": "ppm" },
            { "key": "requestedBy", "value": "operator01" }, { "key": "requestedAt", "value": 1762152000000 } ] }
        """)!;
        var (tank, value, unit, by, at) = Vda5050AdapterService.ParseGasThreshold(action);
        Assert.Equal("CT1", tank);
        Assert.Equal(4.5, value);
        Assert.Equal("ppm", unit);
        Assert.Equal("operator01", by);
        Assert.Equal(1762152000000, at);
    }
}

/// <summary>가짜 XL3000flex(로컬 TcpListener) — 명령별 응답 함수를 받는다. 응답을 두 조각으로 나눠 보내 분할 수신도 확인한다.</summary>
internal static class FakeXl3000
{
    public static async Task RunAsync(TcpListener listener, Func<string, string?> reply, CancellationToken ct)
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
                if (reply(cmd) is { } r)
                {
                    var bytes = Encoding.ASCII.GetBytes(r + "\r");
                    await stream.WriteAsync(bytes.AsMemory(0, 1), ct);
                    await stream.WriteAsync(bytes.AsMemory(1), ct);
                }
            }
        }
    }

    public static async Task StopAsync(TcpListener listener, CancellationTokenSource cts, Task device)
    {
        cts.Cancel();
        listener.Stop();
        try { await device; } catch (OperationCanceledException) { } catch (IOException) { } catch (SocketException) { }
    }
}

/// <summary>TCP 전송 + 클라이언트를 가짜 XL3000flex 와 왕복시킨다.</summary>
public class GasDetectorTcpClientTests
{
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
        var device = FakeXl3000.RunAsync(listener, FakeReply, cts.Token);

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
        await FakeXl3000.StopAsync(listener, cts, device);
    }
}

/// <summary>GasDetectorService 전체(폴링·단위 확인·재측정)를 가짜 장비와 돌린다.</summary>
public class GasDetectorServiceTests
{
    private sealed class FakeDevice
    {
        public volatile string Value = "0.8";
        public volatile string Unit = "PPM";

        public string? Reply(string cmd) => cmd.ToUpperInvariant() switch
        {
            "*READ?" => Value,
            "*READ:MBAR*L/S?" => "1.0E-7",
            "*STATUS?" => "MEAS",
            "*STATUS:TRIGGER?" => "OFF,OFF,OFF,OFF",
            "*STATUS:ERROR?" => "NO ERROR/WARNING",
            "*CONFIG:UNIT:LRSNIFF?" => Unit,
            _ => "OK",
        };
    }

    private static async Task<(GasDetectorService Svc, FakeDevice Dev, Func<Task> Stop)> StartAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var dev = new FakeDevice();
        var device = FakeXl3000.RunAsync(listener, dev.Reply, cts.Token);

        var settings = new GasDetectorSettings
        {
            Transport = GasDetectorTransportKind.Tcp, Host = "127.0.0.1", TcpPort = port,
            ResponseTimeoutMs = 1000, PollIntervalMs = 100,
            MeasureDwellMs = 0, MeasureSampleCount = 2, MeasureSampleIntervalMs = 100,
            ThresholdValue = 5.0, ThresholdUnit = "ppm", MaxRetries = 2,
        };
        // ParameterService 미등록 — 저장값 읽기/쓰기는 실패해도 경고만 남기고 진행한다.
        var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var svc = new GasDetectorService(Options.Create(settings),
            Options.Create(new InspectionSensorSettings { Mode = InspectionSensorMode.GasDetector }),
            scopes, NullLogger<GasDetectorService>.Instance);
        await svc.StartAsync(CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!svc.IsConnected && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.True(svc.IsConnected, svc.LastError);

        return (svc, dev, async () =>
        {
            await svc.StopAsync(CancellationToken.None);
            svc.Dispose();
            await FakeXl3000.StopAsync(listener, cts, device);
            cts.Dispose();
        });
    }

    [Fact]
    public async Task MeasurePoint_Ok_Takes_One_Scan()
    {
        var (svc, dev, stop) = await StartAsync();
        try
        {
            dev.Value = "0.8";
            var scans = await svc.MeasurePointAsync("p1", svc.CurrentThreshold);
            var s = Assert.Single(scans);
            Assert.Equal(GasJudgement.Ok, s.Judgement);
            Assert.Equal(0.8, s.Value);
            Assert.Equal("ppm", s.Unit);
            Assert.Equal(0, s.Retry);
        }
        finally { await stop(); }
    }

    [Fact]
    public async Task MeasurePoint_Ng_Retries_Twice()
    {
        var (svc, dev, stop) = await StartAsync();
        try
        {
            dev.Value = "7.4";
            var scans = await svc.MeasurePointAsync("p2", svc.CurrentThreshold);
            Assert.Equal(new[] { 0, 1, 2 }, scans.Select(x => x.Retry));
            Assert.All(scans, x => Assert.Equal(GasJudgement.Ng, x.Judgement));
            Assert.Equal(GasJudgement.Ng, new GasPointResult(2, 0, 0, scans).Result);
        }
        finally { await stop(); }
    }

    [Fact]
    public async Task Measure_Unit_Mismatch_Is_Error_And_Threshold_From_Acs_Rejected()
    {
        var (svc, dev, stop) = await StartAsync();
        try
        {
            dev.Unit = "MBAR*L/S";
            var r = await svc.MeasureAsync("p3", svc.CurrentThreshold);
            Assert.Equal(GasJudgement.Error, r.Judgement);
            Assert.Contains("단위 불일치", r.Error);

            // 폴링이 장비 단위(mbar·l/s)를 읽은 뒤라야 거부된다 — 단위 조회는 10회 폴링마다 한 번.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (svc.Latest?.Unit != "mbar·l/s" && DateTime.UtcNow < deadline)
                await Task.Delay(50);
            Assert.NotNull(await svc.SetThresholdFromAcsAsync("CT1", 4.5, "ppm", "op", 1));
            Assert.Equal(5.0, svc.CurrentThreshold.Value);
        }
        finally { await stop(); }
    }

    [Fact]
    public async Task Threshold_From_Acs_Applies_And_Reports_Status()
    {
        var (svc, _, stop) = await StartAsync();
        try
        {
            GasThreshold? seen = null;
            svc.ThresholdChanged += t => seen = t;
            Assert.Null(await svc.SetThresholdFromAcsAsync("CT1", 4.5, "ppm", "operator01", 1762152000000));
            Assert.Equal(4.5, svc.CurrentThreshold.Value);
            Assert.Equal(GasThresholdSource.Acs, svc.CurrentThreshold.Source);
            Assert.Equal("operator01", seen?.RequestedBy);

            Assert.NotNull(await svc.SetThresholdFromAcsAsync("CT1", -1, "ppm", null, null));
            Assert.Equal(4.5, svc.CurrentThreshold.Value);

            var status = svc.GetSensorStatus();
            Assert.Equal("MEASURING", status?.State);
            Assert.Equal(0, status?.ErrorCode);
        }
        finally { await stop(); }
    }
}
