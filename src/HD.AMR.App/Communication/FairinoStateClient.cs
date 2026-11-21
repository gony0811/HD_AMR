using System.Buffers.Binary;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Communication;

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

    // payload(프레임 헤더 5바이트 이후) 기준 오프셋 — 공식 FAIRINO RobotStatePkg(_pack_=1) 필드 순서.
    // program_state,robot_state,main_code(i32),sub_code(i32),robot_mode,jt_cur_pos[6],tl_cur_pos[6],
    // flange[6],actual_qd[6],actual_qdd[6],target_TCP_CmpSpeed[2],target_TCP_Speed[6],
    // actual_TCP_CmpSpeed[2],actual_TCP_Speed[6],jt_cur_tor[6],tool(i32),user(i32),...
    // ⚠ 펌웨어 버전차로 tool/user 오프셋이 어긋나면 배지 검증으로 판정 후 보정(SeedActiveFrames 범위검증이 차단).
    private static class Offsets
    {
        public const int ProgramState = 0;   // uint8
        public const int RobotState = 1;      // uint8
        public const int MainCode = 2;        // int32 (LE) — 주 결함 코드(ErrorCode로 노출)
        public const int SubCode = 6;         // int32 (LE)
        public const int RobotMode = 10;      // uint8 (0=자동,1=수동)
        public const int JointPos = 11;       // double[6]
        public const int TcpPose = 59;        // double[6]
        public const int Tool = 427;          // int32 (LE) — 활성 공구 좌표계 번호
        public const int User = 431;          // int32 (LE) — 활성 작업물 좌표계 번호
        // 전역 속도 % — 공식 C# SDK(FAIR-INNOVATION/fairino-csharp-sdk RobotTypes.cs ROBOT_STATE_PKG, Pack=1)의 필드 크기를
        // 차례로 더한 위치. 같은 계산이 tool/user(427/431)와 맞아떨어진다. 펌웨어 패킷이 이보다 짧으면 필드가 없는 것.
        public const int SpeedScaleManual = 1571;   // double
        public const int SpeedScaleAuto = 1579;     // double
    }

    // 전역 속도 확인 로그 — 첫 패킷과 값이 바뀔 때만 남긴다.
    private bool _speedLogged;
    private double? _lastSpeedAuto, _lastSpeedManual;

    /// <summary>전역 속도 % 판독 — 패킷 안이고 0 초과 100 이하일 때만 값, 아니면 null.</summary>
    internal static double? ReadSpeedScale(ReadOnlySpan<byte> payload, int offset)
    {
        if (offset < 0 || offset + 8 > payload.Length) return null;
        var v = BinaryPrimitives.ReadDoubleLittleEndian(payload.Slice(offset, 8));
        return double.IsFinite(v) && v > 0 && v <= 100 ? v : null;
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
            RobotState = ReadU8(payload, Offsets.RobotState),
            RobotMode = ReadU8(payload, Offsets.RobotMode),
            ErrorCode = ReadI32(payload, Offsets.MainCode),
            SubCode = ReadI32(payload, Offsets.SubCode),
            JointPos = ReadDoubles(payload, Offsets.JointPos, 6),
            TcpPose = ReadDoubles(payload, Offsets.TcpPose, 6),
            Tool = ReadI32(payload, Offsets.Tool),
            User = ReadI32(payload, Offsets.User),
            SpeedScaleManual = ReadSpeedScale(payload, Offsets.SpeedScaleManual),
            SpeedScaleAuto = ReadSpeedScale(payload, Offsets.SpeedScaleAuto),
            UpdatedAt = DateTime.UtcNow,
        };
        LogFaultTransition(_latest, state);
        LogSpeedScale(state, dataLen);
        if (dataLen < Offsets.SpeedScaleAuto + 8)
            DiagnoseSpeedCandidates(payload);
        _latest = state;
    }

    // ── 전역 속도 위치 진단 — SDK 위치가 없는(짧은) 펌웨어 패킷에서 전역 속도 후보 자리를 찾는다 ──
    // 처음 한 번 1~100 사이 정수값(double·float·int32)인 자리를 모두 남기고, 이후 그 자리 값이 다른 1~100 정수로
    // 바뀌면 남긴다 — 펜던트 전역 속도를 바꿔(예: 40→41) 같이 바뀌는 자리가 전역 속도다.
    private Dictionary<string, double>? _speedCandidates;
    private int _speedCandidateChangeLogs;
    private const int MaxSpeedCandidateChangeLogs = 40;

    /// <summary>1~100 정수값 자리(형식@위치 → 값) — double·float·int32.</summary>
    internal static Dictionary<string, double> SpeedCandidates(ReadOnlySpan<byte> p)
    {
        static bool Ok(double v) => double.IsFinite(v) && v >= 1 && v <= 100 && Math.Abs(v - Math.Round(v)) < 1e-6;
        // 비율 형태(0.41 = 41%) — 0.01~0.99 이고 소수 둘째 자리까지인 값. 기록은 % 로(×100) 한다.
        static bool Ratio(double v, double eps) => double.IsFinite(v) && v >= 0.01 && v < 1
            && Math.Abs(v * 100 - Math.Round(v * 100)) < eps;
        var map = new Dictionary<string, double>();
        for (int o = 0; o + 8 <= p.Length; o++)
        {
            var d = BinaryPrimitives.ReadDoubleLittleEndian(p.Slice(o, 8));
            if (Ok(d)) map[$"d@{o}"] = d;
            else if (Ratio(d, 1e-6)) map[$"dr@{o}"] = Math.Round(d * 100);
        }
        for (int o = 0; o + 4 <= p.Length; o++)
        {
            var f = BinaryPrimitives.ReadSingleLittleEndian(p.Slice(o, 4));
            if (Ok(f) && f != 1f) map[$"f@{o}"] = f;      // float 1.0 은 흔해서 뺀다
            else if (Ratio(f, 1e-3)) map[$"fr@{o}"] = Math.Round(f * 100);
            var n = BinaryPrimitives.ReadInt32LittleEndian(p.Slice(o, 4));
            if (n >= 2 && n <= 100) map[$"i@{o}"] = n;     // int 1 도 흔해서 뺀다
        }
        return map;
    }

    // 처음 30초는 학습 — 그 사이 값이 바뀐 자리(관절·시각·카운터 등 계속 변하는 값)는 후보에서 뺀다.
    private DateTime _speedCandidateStartUtc;
    private readonly HashSet<string> _speedCandidateNoisy = new();
    private const double SpeedCandidateLearnSec = 30;

    private void DiagnoseSpeedCandidates(ReadOnlySpan<byte> payload)
    {
        if (_speedCandidateChangeLogs >= MaxSpeedCandidateChangeLogs) return;
        var now = SpeedCandidates(payload);
        if (_speedCandidates is not null && (DateTime.UtcNow - _speedCandidateStartUtc).TotalSeconds < SpeedCandidateLearnSec)
        {
            foreach (var (key, v) in now)
                if (!_speedCandidates.TryGetValue(key, out var o) || Math.Abs(o - v) >= 0.5) _speedCandidateNoisy.Add(key);
            foreach (var key in _speedCandidates.Keys)
                if (!now.ContainsKey(key)) _speedCandidateNoisy.Add(key);
            _speedCandidates = now;
            return;
        }
        if (_speedCandidates is not null && _speedCandidateStartUtc != DateTime.MinValue)
        {
            var stable = _speedCandidates.Where(kv => !_speedCandidateNoisy.Contains(kv.Key))
                .GroupBy(kv => kv.Value).OrderBy(g => g.Key)
                .Select(g => $"{g.Key:0}: {string.Join(" ", g.Select(kv => kv.Key).Take(12))}{(g.Count() > 12 ? " …" : "")}");
            _logger.LogInformation("{Name} [전역 속도 진단] 30초 동안 바뀌지 않은 후보 — {List}. 이제 펜던트 전역 속도를 바꾸면 같이 바뀌는 자리를 남깁니다",
                _settings.Name, string.Join(" | ", stable));
            _speedCandidateStartUtc = DateTime.MinValue;   // 한 번만
        }
        if (_speedCandidates is null)
        {
            _speedCandidateStartUtc = DateTime.UtcNow;
            _speedCandidates = now;
            // 관절·위치값과 섞여 많으니 값별로 묶어 남긴다.
            var byValue = now.GroupBy(kv => kv.Value).OrderBy(g => g.Key)
                .Select(g => $"{g.Key:0}: {string.Join(" ", g.Select(kv => kv.Key).Take(12))}{(g.Count() > 12 ? " …" : "")}");
            _logger.LogInformation("{Name} [전역 속도 진단] 상태 패킷 {Len}바이트의 1~100 정수값 자리 {N}곳 — {List}",
                _settings.Name, payload.Length, now.Count, string.Join(" | ", byValue));
            return;
        }
        foreach (var (key, v) in now)
        {
            if (_speedCandidateNoisy.Contains(key)) continue;
            if (_speedCandidates.TryGetValue(key, out var old) && Math.Abs(old - v) >= 0.5)
            {
                _logger.LogInformation("{Name} [전역 속도 진단] {Key} 값 변화 {Old:0} → {New:0}", _settings.Name, key, old, v);
                if (++_speedCandidateChangeLogs >= MaxSpeedCandidateChangeLogs)
                {
                    _logger.LogInformation("{Name} [전역 속도 진단] 변화 로그 {Max}건 — 더 남기지 않습니다", _settings.Name, MaxSpeedCandidateChangeLogs);
                    break;
                }
            }
        }
        _speedCandidates = now;
    }

    private void LogSpeedScale(FairinoState s, int dataLen)
    {
        static double? R(double? v) => v is { } x ? Math.Round(x, 1) : null;
        if (_speedLogged && R(s.SpeedScaleAuto) == R(_lastSpeedAuto) && R(s.SpeedScaleManual) == R(_lastSpeedManual)) return;
        _speedLogged = true;
        _lastSpeedAuto = s.SpeedScaleAuto;
        _lastSpeedManual = s.SpeedScaleManual;
        if (dataLen < Offsets.SpeedScaleAuto + 8)
            _logger.LogWarning("{Name} 상태 패킷 {Len}바이트 — 전역 속도 필드(위치 {Off})가 없는 펌웨어입니다", _settings.Name, dataLen, Offsets.SpeedScaleAuto);
        else
            _logger.LogInformation("{Name} 전역 속도: 자동 {Auto}% / 수동 {Manual}% (모드 {Mode}, 상태 패킷 {Len}바이트)",
                _settings.Name, s.SpeedScaleAuto?.ToString("0.#") ?? "판독 불가", s.SpeedScaleManual?.ToString("0.#") ?? "판독 불가",
                s.RobotMode == 1 ? "수동" : "자동", dataLen);
    }

    /// <summary>결함 코드(main/sub)가 바뀔 때만 남긴다 — 타워램프 적색은 이 값을 따르는데, 순간 결함은
    /// 다른 로그에 흔적이 없어 원인을 추적할 수 없었다(2026-10-30 11:07~11:11 적색 깜빡임 240회).</summary>
    private void LogFaultTransition(FairinoState? prev, FairinoState cur)
    {
        var (prevMain, prevSub) = prev is null ? (0, 0) : (prev.ErrorCode, prev.SubCode);
        if (cur.ErrorCode == prevMain && cur.SubCode == prevSub) return;

        var joints = string.Join(", ", cur.JointPos.Select(v => v.ToString("0.##")));
        if (cur.ErrorCode != 0 || cur.SubCode != 0)
            _logger.LogWarning(
                "{Name} 결함 코드 발생: main={Main}, sub={Sub} (이전 main={PrevMain}, sub={PrevSub}) — robot_state={State}, program_state={Prog}, j=[{Joints}]",
                _settings.Name, cur.ErrorCode, cur.SubCode, prevMain, prevSub, cur.RobotState, cur.ProgramState, joints);
        else
            _logger.LogInformation(
                "{Name} 결함 코드 해제 (이전 main={PrevMain}, sub={PrevSub}) — robot_state={State}, program_state={Prog}, j=[{Joints}]",
                _settings.Name, prevMain, prevSub, cur.RobotState, cur.ProgramState, joints);
    }

    private static int ReadU8(ReadOnlySpan<byte> p, int off)
        => off >= 0 && off < p.Length ? p[off] : 0;

    private static int ReadI32(ReadOnlySpan<byte> p, int off)
        => off >= 0 && off + 4 <= p.Length ? BinaryPrimitives.ReadInt32LittleEndian(p.Slice(off, 4)) : 0;

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
