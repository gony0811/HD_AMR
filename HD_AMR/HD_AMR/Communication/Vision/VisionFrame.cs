using System.Buffers.Binary;

namespace HD_AMR.Communication.Vision;

/// <summary>
/// 디코드된 프레임. LENGTH/CHECKSUM 은 수신 그대로 보존하여, 디코더가 잘못된
/// 체크섬/길이와 정상 프레임을 구분할 수 있게 한다.
/// </summary>
public sealed record Frame(
    byte Seq,
    byte Command,
    byte From,
    byte To,
    byte[] Data,
    ushort LengthField,
    byte ChecksumField)
{
    /// <summary>LENGTH 는 COMMAND + FROM + TO + DATA 의 합(사양).</summary>
    public ushort ExpectedLength => (ushort)(3 + Data.Length);

    public byte ExpectedChecksum => FrameCodec.ComputeChecksum(LengthField, Seq, Command, From, To, Data);

    public bool LengthOk   => LengthField == ExpectedLength;
    public bool ChecksumOk => ChecksumField == ExpectedChecksum;
}

public static class FrameCodec
{
    /// <summary>
    /// LENGTH(2) + SEQ + COMMAND + FROM + TO + DATA 의 XOR (사양 시트1: "LENGTH~DATA 전체 XOR").
    /// LENGTH 는 전송 바이트(LE)와 동일하게 XOR 한다.
    /// </summary>
    public static byte ComputeChecksum(ushort length, byte seq, byte command, byte from, byte to, ReadOnlySpan<byte> data)
    {
        byte sum = 0;
        sum ^= (byte)(length & 0xFF);
        sum ^= (byte)((length >> 8) & 0xFF);
        sum ^= seq;
        sum ^= command;
        sum ^= from;
        sum ^= to;
        foreach (var b in data) sum ^= b;
        return sum;
    }

    /// <summary>
    /// 와이어 프레임 생성. <paramref name="lengthOverride"/> / <paramref name="checksumOverride"/>
    /// 로 잘못된 값을 주입하면 음성 테스트(체크섬/길이 오류)를 만들 수 있다.
    /// </summary>
    public static byte[] Encode(
        byte seq,
        byte command,
        byte from,
        byte to,
        ReadOnlySpan<byte> data,
        ushort? lengthOverride = null,
        byte? checksumOverride = null)
    {
        var realLength = (ushort)(3 + data.Length);
        var lengthField = lengthOverride ?? realLength;
        var checksumField = checksumOverride ?? ComputeChecksum(lengthField, seq, command, from, to, data);

        var total = FrameConst.FixedOverhead + data.Length;
        var buf = new byte[total];
        var i = 0;
        buf[i++] = FrameConst.Stx;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(i, 2), lengthField);
        i += 2;
        buf[i++] = seq;
        buf[i++] = command;
        buf[i++] = from;
        buf[i++] = to;
        data.CopyTo(buf.AsSpan(i, data.Length));
        i += data.Length;
        buf[i++] = checksumField;
        buf[i] = FrameConst.Etx;
        return buf;
    }

    public static string ToHex(ReadOnlySpan<byte> bytes, string separator = " ")
    {
        if (bytes.IsEmpty) return string.Empty;
        var sb = new System.Text.StringBuilder(bytes.Length * 3);
        for (var i = 0; i < bytes.Length; i++)
        {
            if (i > 0 && separator.Length > 0) sb.Append(separator);
            sb.Append(bytes[i].ToString("X2"));
        }
        return sb.ToString();
    }

    public static bool TryParseHex(string text, out byte[] bytes)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || c == ',' || c == ':' || c == '-') continue;
            sb.Append(c);
        }
        var s = sb.ToString();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s[2..];
        if (s.Length % 2 != 0) { bytes = Array.Empty<byte>(); return false; }
        var result = new byte[s.Length / 2];
        for (var i = 0; i < result.Length; i++)
        {
            if (!byte.TryParse(s.AsSpan(i * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out result[i]))
            {
                bytes = Array.Empty<byte>();
                return false;
            }
        }
        bytes = result;
        return true;
    }
}
