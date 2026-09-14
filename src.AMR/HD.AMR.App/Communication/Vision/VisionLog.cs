using System.Buffers.Binary;

namespace HD.AMR.App.Communication.Vision;

public enum LogDirection { Tx, Rx, Info, Error }

public sealed record VisionLogEntry(
    DateTime At,
    LogDirection Direction,
    string Hex,
    string Summary,
    string? Detail = null);

/// <summary>디코드된 프레임을 사람이 읽을 수 있는 한 줄 요약으로 변환.</summary>
public static class FrameDescriber
{
    public static string Summary(Frame f)
    {
        var cmd  = CommandNames.NameOf(f.Command);
        var from = DeviceIdNames.NameOf(f.From);
        var to   = DeviceIdNames.NameOf(f.To);
        var head = $"{cmd}  SEQ=0x{f.Seq:X2}  {from}→{to}";

        var body = f.Command switch
        {
            (byte)CommandCode.Heartbeat  => DescribeHeartbeat(f.Data),
            (byte)CommandCode.CaptureReq => DescribeCaptureReq(f.Data),
            (byte)CommandCode.CaptureRes => DescribeCaptureRes(f.Data),
            (byte)CommandCode.ErrorNoti  => DescribeErrorNoti(f.Data),
            _ => $"DATA({f.Data.Length}B)",
        };

        var notes = new List<string>();
        if (!f.LengthOk)   notes.Add($"LENGTH={f.LengthField} (예상 {f.ExpectedLength})");
        if (!f.ChecksumOk) notes.Add($"CHKSUM=0x{f.ChecksumField:X2} (예상 0x{f.ExpectedChecksum:X2})");
        var note = notes.Count == 0 ? "" : "  ⚠ " + string.Join(", ", notes);

        return $"{head}  {body}{note}";
    }

    private static string DescribeHeartbeat(byte[] data)
    {
        if (data.Length < 14) return $"HB DATA({data.Length}B 부족)";
        var ts = System.Text.Encoding.ASCII.GetString(data, 0, 14);
        return $"ts={ts}";
    }

    private static string SurfaceTypeName(byte t) => t switch
    {
        0x00 => "Flat",
        0x01 => "Corner",
        0x02 => "Corrugation",
        _    => $"0x{t:X2}",
    };

    /// <summary>GUID 앞 8자리만 요약(0 GUID 는 '-').</summary>
    private static string ShortId(Guid id) =>
        id == Guid.Empty ? "-" : id.ToString("D").Substring(0, 8);

    private static string DescribeCaptureReq(byte[] data)
    {
        // v3: 115B (Run/Task/Job + 면·위치)
        if (data.Length >= CaptureReqPayload.Length)
        {
            var runId  = GuidAscii.Read(data.AsSpan(CaptureReqPayload.RunIdOff, 36));
            var taskId = GuidAscii.Read(data.AsSpan(CaptureReqPayload.TaskIdOff, 36));
            var st  = data[CaptureReqPayload.SurfaceTypeOff];
            var sid = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(CaptureReqPayload.SurfaceIdOff, 2));
            var px  = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(CaptureReqPayload.PosXOff, 4));
            var py  = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(CaptureReqPayload.PosYOff, 4));
            return $"Run={ShortId(runId)} Task={ShortId(taskId)} " +
                   $"Surface={SurfaceTypeName(st)}/0x{sid:X2}({SurfaceCatalog.NameOf(sid)}), Pos=({px},{py})mm";
        }
        // v2 레거시: 15B (면·위치)
        if (data.Length >= 15)
        {
            var sid = (ushort)(data[1] | (data[2] << 8));
            var px = data[3] | (data[4] << 8) | (data[5] << 16) | (data[6] << 24);
            var py = data[7] | (data[8] << 8) | (data[9] << 16) | (data[10] << 24);
            return $"[v2] Surface={SurfaceTypeName(data[0])}/0x{sid:X2}({SurfaceCatalog.NameOf(sid)}), Pos=({px},{py})mm";
        }
        return $"REQ DATA({data.Length}B 부족)";
    }

    private static string DescribeCaptureRes(byte[] data)
    {
        if (!CaptureResPayload.TryReadCode(data, out var code)) return $"RES DATA({data.Length}B 부족)";
        var idPart = "";
        if (data.Length >= CaptureResPayload.Length)
        {
            var (r, t) = CaptureResPayload.ReadIds(data);
            idPart = $"Run={ShortId(r)} Task={ShortId(t)} ";
        }
        return $"{idPart}Result=0x{code:X4} {ResultCodeNames.NameOf(code)}";
    }

    private static string DescribeErrorNoti(byte[] data)
    {
        if (!CaptureResPayload.TryReadCode(data, out var code)) return $"ERR DATA({data.Length}B 부족)";
        var idPart = "";
        if (data.Length >= CaptureResPayload.Length)
        {
            var (r, t) = CaptureResPayload.ReadIds(data);
            idPart = $"Run={ShortId(r)} Task={ShortId(t)} ";
        }
        return $"{idPart}Error=0x{code:X4} {ResultCodeNames.NameOf(code)}";
    }
}
