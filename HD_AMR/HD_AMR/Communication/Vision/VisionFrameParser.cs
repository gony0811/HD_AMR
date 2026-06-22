using System.Buffers.Binary;

namespace HD_AMR.Communication.Vision;

public enum ParseFault
{
    BadLengthRange,
    LengthMismatch,
    ChecksumMismatch,
    MissingEtx,
    Resync,
}

public sealed record RawFrame(byte[] Bytes, Frame? Decoded, ParseFault? Fault = null, string? FaultDetail = null)
{
    public bool IsValid => Decoded is { } d && d.LengthOk && d.ChecksumOk && Fault is null;
}

/// <summary>
/// 바이트 스트림 → 프레임 파서(상태 유지). 수신 바이트를 버퍼링하며 STX 를 찾고,
/// 파싱 실패 시 1바이트 폐기 후 재동기화한다(사양 §6.3).
/// </summary>
public sealed class FrameParser
{
    private readonly List<byte> _buf = new(1024);

    public IReadOnlyList<RawFrame> Feed(ReadOnlySpan<byte> chunk)
    {
        _buf.AddRange(chunk.ToArray());
        var output = new List<RawFrame>();

        while (true)
        {
            var stxIdx = _buf.IndexOf(FrameConst.Stx);
            if (stxIdx < 0) { _buf.Clear(); break; }
            if (stxIdx > 0)
            {
                var skipped = _buf.GetRange(0, stxIdx).ToArray();
                _buf.RemoveRange(0, stxIdx);
                output.Add(new RawFrame(skipped, null, ParseFault.Resync, $"STX 탐색 중 {skipped.Length}바이트 폐기"));
            }

            // Need at least STX + LENGTH(2) to read length.
            if (_buf.Count < 3) break;
            var lengthField = BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(new[] { _buf[1], _buf[2] }));

            if (lengthField < 3 || lengthField > 3 + FrameConst.MaxDataLength)
            {
                var bad = new[] { _buf[0] };
                _buf.RemoveAt(0);
                output.Add(new RawFrame(bad, null, ParseFault.BadLengthRange, $"LENGTH={lengthField} 범위 외 — STX 폐기 후 재탐색"));
                continue;
            }

            var totalLen = FrameConst.FixedOverhead + (lengthField - 3); // = 9 + DATA_len = 6 + lengthField
            if (_buf.Count < totalLen) break;

            // Slice out the candidate frame.
            var frameBytes = new byte[totalLen];
            _buf.CopyTo(0, frameBytes, 0, totalLen);

            var seq      = frameBytes[3];
            var command  = frameBytes[4];
            var from     = frameBytes[5];
            var to       = frameBytes[6];
            var dataLen  = lengthField - 3;
            var data     = new byte[dataLen];
            Array.Copy(frameBytes, 7, data, 0, dataLen);
            var checksum = frameBytes[7 + dataLen];
            var etx      = frameBytes[8 + dataLen];

            var decoded = new Frame(seq, command, from, to, data, lengthField, checksum);

            if (etx != FrameConst.Etx)
            {
                _buf.RemoveAt(0);
                output.Add(new RawFrame(frameBytes, decoded, ParseFault.MissingEtx, $"ETX 위치에 0x{etx:X2} — STX 1바이트 폐기 후 재탐색"));
                continue;
            }

            if (!decoded.ChecksumOk)
            {
                _buf.RemoveRange(0, totalLen);
                output.Add(new RawFrame(frameBytes, decoded, ParseFault.ChecksumMismatch,
                    $"수신 0x{checksum:X2} ≠ 계산 0x{decoded.ExpectedChecksum:X2} — 프레임 폐기 (재전송 없음)"));
                continue;
            }

            _buf.RemoveRange(0, totalLen);
            output.Add(new RawFrame(frameBytes, decoded));
        }

        return output;
    }

    public void Reset() => _buf.Clear();
}
