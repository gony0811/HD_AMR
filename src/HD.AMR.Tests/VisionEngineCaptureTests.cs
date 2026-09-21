using System;
using System.Threading;
using System.Threading.Tasks;
using HD.AMR.App.Communication.Vision;

namespace HD.AMR.Tests;

/// <summary>RequestCaptureAsync 응답 해석 검증 — CAPTURE_RES 뿐 아니라 ERROR_NOTI 의
/// 결과 코드도 보존해야 한다(과거에는 ERROR_NOTI 를 무조건 ERR_UNKNOWN 으로 뭉갰음).</summary>
public class VisionEngineCaptureTests
{
    /// <summary>CAPTURE_REQ 수신 즉시 지정된 프레임으로 응답하는 인메모리 트랜스포트.</summary>
    private sealed class FakeTransport : IVisionTransport
    {
        private readonly byte[]? _reply;
        public FakeTransport(byte[]? reply) => _reply = reply;

        public bool IsConnected => true;
        public event EventHandler<TransportEvent>? StateChanged;
        public event EventHandler<ReadOnlyMemory<byte>>? BytesReceived;

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
        {
            if (_reply is { } r) BytesReceived?.Invoke(this, r);
            return Task.CompletedTask;
        }

        // 이벤트 미사용 경고(CS0067) 억제용 — 인터페이스 구현상 필요.
        public void RaiseState(string status, bool connected) => StateChanged?.Invoke(this, new TransportEvent(status, connected));
    }

    private static async Task<CaptureOutcome> RunCaptureAsync(byte[]? reply)
    {
        var engine = new VisionEngine(SideRole.Client) { AutoHeartbeat = false };
        await using var _ = engine;
        await engine.StartAsync(new FakeTransport(reply));
        var req = CaptureReqPayload.Build(SurfaceType.Flat, 1, 0, 0, 400, Guid.Empty, 1, 1);
        return await engine.RequestCaptureAsync(req, TimeSpan.FromSeconds(2));
    }

    private static byte[] VisionFrame(CommandCode cmd, byte[] data) =>
        FrameCodec.Encode(0x01, (byte)cmd, (byte)DeviceId.Vision, (byte)DeviceId.Automation, data);

    [Fact]
    public async Task ErrorNoti_Preserves_Result_Code()
    {
        var reply = VisionFrame(CommandCode.ErrorNoti, CaptureResPayload.Build(ResultCode.ErrSurface));
        var outcome = await RunCaptureAsync(reply);

        Assert.True(outcome.Sent);
        Assert.True(outcome.Responded);
        Assert.False(outcome.Success);
        Assert.Equal(ResultCode.ErrSurface, outcome.Code);
    }

    [Fact]
    public async Task CaptureRes_Success_Unchanged()
    {
        var reply = VisionFrame(CommandCode.CaptureRes, CaptureResPayload.Build(ResultCode.Success));
        var outcome = await RunCaptureAsync(reply);

        Assert.True(outcome.Success);
        Assert.Equal(ResultCode.Success, outcome.Code);
    }

    [Fact]
    public async Task ErrorNoti_Without_Code_Falls_Back_To_Unknown()
    {
        var reply = VisionFrame(CommandCode.ErrorNoti, Array.Empty<byte>());
        var outcome = await RunCaptureAsync(reply);

        Assert.True(outcome.Responded);
        Assert.False(outcome.Success);
        Assert.Equal(ResultCode.ErrUnknown, outcome.Code);
    }
}
