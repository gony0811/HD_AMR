using HD.AMR.App.Communication.Vda5050;
using Xunit;

namespace HD.AMR.Tests;

/// <summary>화면에서 바꾸는 ACS MQTT 브로커 주소 — 입력 검증과 보관 파일.</summary>
public class AcsBrokerStoreTests : IDisposable
{
    private readonly string _saved = AcsBrokerStore.FilePath;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hdamr-broker-" + Guid.NewGuid().ToString("N"));

    public AcsBrokerStoreTests() => AcsBrokerStore.FilePath = Path.Combine(_dir, "acs-broker.json");

    public void Dispose()
    {
        AcsBrokerStore.FilePath = _saved;
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }

    [Theory]
    [InlineData("192.168.100.98", 1883)]
    [InlineData(" 10.0.0.5 ", 1)]
    [InlineData("127.0.0.1", 65535)]
    [InlineData("acs-server", 1883)]
    [InlineData("acs.local", 8883)]
    [InlineData("::1", 1883)]
    public void 올바른_주소는_통과(string host, int port) => Assert.Null(AcsBrokerStore.Validate(host, port));

    [Theory]
    [InlineData("", 1883)]
    [InlineData("   ", 1883)]
    [InlineData(null, 1883)]
    [InlineData("192.168.1", 1883)]            // 자리 수 부족 — 호스트 이름으로 통과시키지 않는다
    [InlineData("300.1.1.1", 1883)]
    [InlineData("192.168.100.98.1", 1883)]
    [InlineData("mqtt://192.168.100.98", 1883)]
    [InlineData("192.168.100.98:1883", 1883)]
    [InlineData("192.168.100.98/x", 1883)]
    [InlineData("192.168 100.98", 1883)]
    [InlineData("192.168.100.98", 0)]
    [InlineData("192.168.100.98", 65536)]
    [InlineData("192.168.100.98", -1)]
    public void 잘못된_주소는_사유를_돌려준다(string? host, int port) => Assert.NotNull(AcsBrokerStore.Validate(host, port));

    [Fact]
    public void 저장한_주소가_설정보다_우선한다()
    {
        Assert.Null(AcsBrokerStore.Load());                     // 파일이 없으면 설정 그대로

        var s = new Vda5050AdapterSettings { BrokerHost = "192.168.100.98", BrokerPort = 1883 };
        AcsBrokerStore.ApplyTo(s);
        Assert.Equal(("192.168.100.98", 1883), (s.BrokerHost, s.BrokerPort));

        AcsBrokerStore.Save(" 10.1.2.3 ", 1884);
        Assert.Equal(("10.1.2.3", 1884), AcsBrokerStore.Load());
        AcsBrokerStore.ApplyTo(s);
        Assert.Equal(("10.1.2.3", 1884), (s.BrokerHost, s.BrokerPort));
    }

    [Theory]
    [InlineData("깨진 내용")]
    [InlineData("{\"host\":\"\",\"port\":1883}")]
    [InlineData("{\"host\":\"10.1.2.3\",\"port\":70000}")]
    public void 읽을_수_없는_파일은_무시한다(string content)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(AcsBrokerStore.FilePath, content);
        Assert.Null(AcsBrokerStore.Load());

        var s = new Vda5050AdapterSettings { BrokerHost = "192.168.100.98", BrokerPort = 1883 };
        AcsBrokerStore.ApplyTo(s);
        Assert.Equal(("192.168.100.98", 1883), (s.BrokerHost, s.BrokerPort));
    }

    [Fact]
    public void 포트가_없는_파일은_기본_포트()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(AcsBrokerStore.FilePath, "{\"host\":\"10.1.2.3\"}");
        Assert.Equal(("10.1.2.3", AcsBrokerStore.DefaultPort), AcsBrokerStore.Load());
    }
}
