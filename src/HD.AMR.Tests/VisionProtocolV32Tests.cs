using System;
using HD.AMR.App.Communication.Vision;

namespace HD.AMR.Tests;

/// <summary>vision_interface_v3.2 CAPTURE_REQ(34B)·CAPTURE_RES(2B) 인코딩/왕복 검증.</summary>
public class VisionProtocolV32Tests
{
    [Fact]
    public void CaptureReq_Length_Is_34()
    {
        var data = CaptureReqPayload.Build(SurfaceType.Corrugation, 3, 1250, 3400, 12,
            Guid.NewGuid(), 1, 3);
        Assert.Equal(34, data.Length);
        Assert.Equal(34, CaptureReqPayload.Length);
    }

    [Fact]
    public void CaptureReq_FieldOffsets_Match_Spec()
    {
        var taskId = Guid.Parse("3a9f2c14-8e51-4d7a-b2c9-1f6e0a5d3b47");
        var data = CaptureReqPayload.Build(SurfaceType.Corrugation, 3, 1250, 3400, 12, taskId, 2, 5);

        Assert.Equal((byte)SurfaceType.Corrugation, data[0]);                 // [0] Surface Type
        Assert.Equal(3, BitConverter.ToUInt16(data, 1));                      // [1-2] Wall ID LE
        Assert.Equal(1250, BitConverter.ToInt32(data, 3));                    // [3-6] PosX (u) LE
        Assert.Equal(3400, BitConverter.ToInt32(data, 7));                    // [7-10] PosY (v) LE
        Assert.Equal(12, BitConverter.ToInt32(data, 11));                     // [11-14] PosZ (h) LE
        Assert.Equal((byte)2, data[31]);                                      // [31] attempt
        Assert.Equal(5, BitConverter.ToUInt16(data, 32));                     // [32-33] captureSeq LE
    }

    [Fact]
    public void CaptureReq_TaskId_Binary_RoundTrips()
    {
        var taskId = Guid.Parse("3a9f2c14-8e51-4d7a-b2c9-1f6e0a5d3b47");
        var data = CaptureReqPayload.Build(SurfaceType.Flat, 1, 0, 0, 0, taskId, 1, 1);

        var f = CaptureReqPayload.TryRead(data);
        Assert.NotNull(f);
        Assert.Equal(taskId, f!.Value.TaskId);
    }

    [Fact]
    public void CaptureReq_TaskId_Is_BigEndian_Rfc4122()
    {
        // 하이픈 문자열을 왼쪽부터 읽은 바이트와 [15..] 16바이트가 일치해야 한다.
        var taskId = Guid.Parse("3a9f2c14-8e51-4d7a-b2c9-1f6e0a5d3b47");
        var data = CaptureReqPayload.Build(SurfaceType.Flat, 1, 0, 0, 0, taskId, 1, 1);
        Assert.Equal(0x3a, data[15]);
        Assert.Equal(0x9f, data[16]);
        Assert.Equal(0x2c, data[17]);
        Assert.Equal(0x14, data[18]);
        Assert.Equal(0x3b, data[29]);
        Assert.Equal(0x47, data[30]);
    }

    [Fact]
    public void CaptureReq_FullRoundTrip()
    {
        var taskId = Guid.NewGuid();
        var data = CaptureReqPayload.Build(SurfaceType.Corner, 10, -5, 42000, -3, taskId, 200, 65000);

        var f = CaptureReqPayload.TryRead(data)!.Value;
        Assert.Equal(SurfaceType.Corner, f.Type);
        Assert.Equal(10, f.WallId);
        Assert.Equal(-5, f.PosX);
        Assert.Equal(42000, f.PosY);
        Assert.Equal(-3, f.PosZ);
        Assert.Equal(taskId, f.TaskId);
        Assert.Equal(200, f.Attempt);
        Assert.Equal(65000, f.CaptureSeq);
    }

    [Fact]
    public void CaptureReq_TryRead_TooShort_ReturnsNull()
    {
        Assert.Null(CaptureReqPayload.TryRead(new byte[15]));   // v2 길이
        Assert.Null(CaptureReqPayload.TryRead(new byte[33]));
    }

    [Fact]
    public void CaptureRes_Is_2Bytes_CodeOnly()
    {
        var data = CaptureResPayload.Build(ResultCode.ErrTimeout);
        Assert.Equal(2, data.Length);
        Assert.True(CaptureResPayload.TryReadCode(data, out var code));
        Assert.Equal((ushort)ResultCode.ErrTimeout, code);
    }

    [Fact]
    public void CaptureRes_TryReadCode_Legacy74B_ReadsOffset72()
    {
        // 레거시 v3(74B): 결과 코드는 [72-73].
        var data = new byte[74];
        data[72] = 0xFF; data[73] = 0x00;   // 0x00FF ERR_UNKNOWN LE
        Assert.True(CaptureResPayload.TryReadCode(data, out var code));
        Assert.Equal((ushort)ResultCode.ErrUnknown, code);
    }

    [Fact]
    public void Frame_Encode_CaptureReq_Total43Bytes()
    {
        var data = CaptureReqPayload.Build(SurfaceType.Flat, 1, 0, 0, 0, Guid.Empty, 1, 1);
        var frame = FrameCodec.Encode(0x10, (byte)CommandCode.CaptureReq,
            (byte)DeviceId.Automation, (byte)DeviceId.Vision, data);
        Assert.Equal(FrameConst.FixedOverhead + 34, frame.Length);   // 43
        Assert.Equal(FrameConst.Stx, frame[0]);
        Assert.Equal(FrameConst.Etx, frame[^1]);
    }
}
