namespace HD_AMR.Communication.Vision;

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

    private static string DescribeCaptureReq(byte[] data)
    {
        if (data.Length < 15) return $"REQ DATA({data.Length}B 부족)";
        var surfaceType = data[0] switch
        {
            0x00 => "Flat",
            0x01 => "Corner",
            0x02 => "Corrugation",
            _    => $"0x{data[0]:X2}",
        };
        ushort surfaceId = (ushort)(data[1] | (data[2] << 8));
        int posX = data[3] | (data[4] << 8) | (data[5] << 16) | (data[6] << 24);
        int posY = data[7] | (data[8] << 8) | (data[9] << 16) | (data[10] << 24);
        return $"Surface={surfaceType}/0x{surfaceId:X2}({SurfaceCatalog.NameOf(surfaceId)}), Pos=({posX},{posY})mm";
    }

    private static string DescribeCaptureRes(byte[] data)
    {
        if (data.Length < 2) return $"RES DATA({data.Length}B 부족)";
        ushort code = (ushort)(data[0] | (data[1] << 8));
        return $"Result=0x{code:X4} {ResultCodeNames.NameOf(code)}";
    }

    private static string DescribeErrorNoti(byte[] data)
    {
        if (data.Length < 2) return $"ERR DATA({data.Length}B 부족)";
        ushort code = (ushort)(data[0] | (data[1] << 8));
        return $"Error=0x{code:X4} {ResultCodeNames.NameOf(code)}";
    }
}
