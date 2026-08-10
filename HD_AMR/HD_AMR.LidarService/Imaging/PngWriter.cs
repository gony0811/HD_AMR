using System.Buffers.Binary;
using System.IO.Compression;

namespace HD_AMR.LidarService.Imaging;

/// <summary>
/// 최소 기능 PNG 인코더(8bit RGB / RGBA, 무압축 필터는 Sub 고정).
///
/// <b>왜 직접 쓰는가.</b> .NET 8 에는 크로스플랫폼 이미지 인코더가 없어서 보통
/// ImageSharp 같은 패키지를 넣지만, 여기서는 두 가지 이유로 직접 썼다.
///
/// 첫째, <b>진단 영상에 손실 압축은 해롭다.</b> 이 화면의 목적은 픽셀 분류(포화/ADC 오버플로/
/// 진폭 부족)를 눈으로 읽는 것이다. JPEG 는 의사색상 경계에 링잉을 만들어 없던 포화 영역이
/// 있는 것처럼 보이게 한다. PNG 는 무손실이라 화면에서 읽은 색이 곧 원본 코드값이다.
///
/// 둘째, 오버레이에 알파가 필요하고, PNG 는 zlib 만 있으면 인코딩이 100줄 남짓이다.
/// <c>System.IO.Compression.ZLibStream</c> 이 zlib 컨테이너(헤더 + Adler-32)까지 처리해 주므로
/// 남는 일은 청크 포장과 CRC-32 뿐이다. 젯슨 arm64 배포에 패키지를 하나 덜 얹는다.
///
/// 대역폭은 320x240 기준 프레임당 수십 KB 수준이라 LAN 에서 문제되지 않는다.
/// </summary>
internal static class PngWriter
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>
    /// 인터리브 픽셀 배열을 PNG 로 인코딩한다.
    /// </summary>
    /// <param name="pixels">RGB 또는 RGBA 인터리브, row-major. 길이는 width*height*channels.</param>
    /// <param name="channels">3(RGB) 또는 4(RGBA).</param>
    public static byte[] Encode(ReadOnlySpan<byte> pixels, int width, int height, int channels)
    {
        if (channels is not (3 or 4))
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "3(RGB) 또는 4(RGBA)만 지원한다.");

        var stride = width * channels;
        if (pixels.Length != stride * height)
            throw new ArgumentException($"픽셀 길이가 {pixels.Length}다. {stride * height} 기대.", nameof(pixels));

        // 스캔라인마다 필터 바이트 1개가 앞에 붙는다. Sub 필터(1)는 왼쪽 픽셀과의 차분이라
        // 계산이 거의 공짜인데, 거리 의사색상처럼 가로로 완만한 그라데이션에서 압축률이
        // 눈에 띄게 좋아진다.
        var raw = new byte[(stride + 1) * height];

        for (int y = 0; y < height; y++)
        {
            var src = y * stride;
            var dst = y * (stride + 1);
            raw[dst] = 1;

            for (int i = 0; i < stride; i++)
            {
                var left = i >= channels ? pixels[src + i - channels] : (byte)0;
                raw[dst + 1 + i] = (byte)(pixels[src + i] - left);
            }
        }

        byte[] compressed;
        using (var buffer = new MemoryStream(raw.Length / 4))
        {
            // 미리보기는 초당 수 회 인코딩되므로 압축률보다 속도를 택한다.
            using (var zlib = new ZLibStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
                zlib.Write(raw, 0, raw.Length);

            compressed = buffer.ToArray();
        }

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;                                  // bit depth
        ihdr[9] = (byte)(channels == 4 ? 6 : 2);      // color type: 6=RGBA, 2=RGB
        ihdr[10] = 0;                                 // compression: deflate
        ihdr[11] = 0;                                 // filter method: adaptive
        ihdr[12] = 0;                                 // interlace: none

        using var output = new MemoryStream(compressed.Length + 64);
        output.Write(Signature);
        WriteChunk(output, "IHDR"u8, ihdr);
        WriteChunk(output, "IDAT"u8, compressed);
        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);

        output.Write(type);
        output.Write(data);

        // CRC 는 타입과 데이터를 이어서 계산한다.
        var crc = Crc32(type, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static uint Crc32(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var c = 0xFFFFFFFFu;
        foreach (var v in a) c = CrcTable[(c ^ v) & 0xFF] ^ (c >> 8);
        foreach (var v in b) c = CrcTable[(c ^ v) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
