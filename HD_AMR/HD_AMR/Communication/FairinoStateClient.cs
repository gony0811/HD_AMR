using System.Buffers.Binary;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace HD_AMR.Communication;

/// <summary>
/// 페어리노 상태 피드백 포트(기본 20004)에 TCP로 붙어 고정 길이 ROBOT_STATE_PKG 패킷을 수신/파싱한다.
/// 프레임: [0x5A 0x5A][frame_cnt:1][data_len:2(LE)][...payload...][checksum:2(LE)].
/// checksum = frame_head부터 payload 끝까지 바이트 합(하위 16비트).
///
/// ⚠ payload 내부 필드 오프셋은 펌웨어/SDK 버전마다 다르다. 아래 Offsets 상수는 1차 추정이며,
///   실물 캡처 + C++ SDK 헤더(ROBOT_STATE_PKG)로 확정한 뒤 보정할 것. 오프셋이 패킷을 벗어나면
///   해당 필드는 기본값으로 둔다(연결/수신 루프는 영향 없음).
/// </summary>
public class FairinoStateClient
{
    private const byte FrameHead = 0x5A;

    // payload(프레임 헤더 5바이트 이후) 기준 오프셋 — ⚠ 캡처로 확정 필요.
    private static class Offsets
    {
        public const int ProgramState = 0;   // uint8
        public const int RobotMode = 1;       // uint8
        public const int ErrorCode = 2;       // int16 (LE)
        public const int JointPos = 16;       // double[6]
        public const int TcpPose = 64;        // double[6]
    }

    private readonly FairinoRpcSettings _settings;
    private readonly ILogger<FairinoStateClient> _logger;

    private volatile FairinoState? _latest;
    private volatile bool _connected;

    public FairinoStateClient(FairinoRpcSettings settings, ILogger<FairinoStateClient> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public bool IsConnected => _connected;
    public FairinoState? Latest => _latest;

    /// <summary>취소될 때까지 연결→수신→재연결을 반복한다. CobotService가 백그라운드로 구동.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(_settings.IpAddress, _settings.StatePort, ct);
                _connected = true;
                _logger.LogInformation("{Name} 상태 소켓 연결 ({Ip}:{Port})", _settings.Name, _settings.IpAddress, _settings.StatePort);

                await ReadLoopAsync(tcp.GetStream(), ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{Name} 상태 소켓 오류 — 5초 후 재연결", _settings.Name);
            }
            finally
            {
                _connected = false;
            }

            if (!ct.IsCancellationRequested)
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }

    private async Task ReadLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var acc = new List<byte>(16384);

        while (!ct.IsCancellationRequested)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(), ct);
            if (read <= 0) throw new IOException("상태 소켓이 닫혔습니다.");

            acc.AddRange(buffer.AsSpan(0, read).ToArray());
            DrainFrames(acc);
        }
    }

    /// <summary>누적 버퍼에서 완전한 프레임을 모두 추출/파싱하고 소비한 바이트를 제거한다.</summary>
    private void DrainFrames(List<byte> acc)
    {
        int i = 0;
        while (true)
        {
            // 프레임 헤더(0x5A 0x5A) 동기화.
            while (i + 1 < acc.Count && !(acc[i] == FrameHead && acc[i + 1] == FrameHead))
                i++;

            // 헤더(2) + cnt(1) + len(2) 최소 5바이트 필요.
            if (i + 5 > acc.Count) break;

            int dataLen = acc[i + 3] | (acc[i + 4] << 8);
            int frameLen = 5 + dataLen + 2; // header+cnt+len + payload + checksum
            if (i + frameLen > acc.Count) break; // 아직 전체 프레임 미수신.

            var frame = acc.GetRange(i, frameLen).ToArray();
            if (VerifyChecksum(frame))
                TryParse(frame, dataLen);
            else
                _logger.LogDebug("{Name} 상태 프레임 체크섬 불일치(len={Len})", _settings.Name, frameLen);

            i += frameLen;
        }

        if (i > 0) acc.RemoveRange(0, i);
    }

    private static bool VerifyChecksum(byte[] frame)
    {
        int sum = 0;
        for (int k = 0; k < frame.Length - 2; k++) sum += frame[k];
        int expected = frame[^2] | (frame[^1] << 8);
        return (sum & 0xFFFF) == expected;
    }

    private void TryParse(byte[] frame, int dataLen)
    {
        var payload = frame.AsSpan(5, dataLen);

        var state = new FairinoState
        {
            ProgramState = ReadU8(payload, Offsets.ProgramState),
            RobotMode = ReadU8(payload, Offsets.RobotMode),
            ErrorCode = ReadI16(payload, Offsets.ErrorCode),
            JointPos = ReadDoubles(payload, Offsets.JointPos, 6),
            TcpPose = ReadDoubles(payload, Offsets.TcpPose, 6),
            UpdatedAt = DateTime.UtcNow,
        };
        _latest = state;
    }

    private static int ReadU8(ReadOnlySpan<byte> p, int off)
        => off >= 0 && off < p.Length ? p[off] : 0;

    private static int ReadI16(ReadOnlySpan<byte> p, int off)
        => off >= 0 && off + 2 <= p.Length ? BinaryPrimitives.ReadInt16LittleEndian(p.Slice(off, 2)) : 0;

    private static double[] ReadDoubles(ReadOnlySpan<byte> p, int off, int count)
    {
        var result = new double[count];
        for (int k = 0; k < count; k++)
        {
            int o = off + k * 8;
            if (o + 8 <= p.Length)
                result[k] = BinaryPrimitives.ReadDoubleLittleEndian(p.Slice(o, 8));
        }
        return result;
    }
}
