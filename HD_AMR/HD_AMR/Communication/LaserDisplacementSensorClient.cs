using HD_AMR.Models;
using Microsoft.Extensions.Logging;
using Sres.Net.EEIP;

namespace HD_AMR.Communication;

/// <summary>
/// 레이저 변위 센서(OMRON ZP-LS300S + ZP-EIP)의 EtherNet/IP 저수준 클라이언트. <see cref="EEIPClient"/>
/// (EEIP.NetStandard)를 감싸 <b>Class1 implicit I/O(tag data link)</b> 로 측정값을 주고받는다.
/// (ZP-EIP 는 Assembly 오브젝트를 explicit 메시지로 노출하지 않으므로 측정값은 implicit 연결로만 읽을 수 있다.)
///
/// 연결 사양(매뉴얼 Z496 §4·Appendix A-2, "Full"/Exclusive Owner, Point-to-Point):
/// <list type="bullet">
///   <item>T→O Input Assembly = 인스턴스 110, 276B (장치→PC): 측정값·상태. EEIP 의 <c>T_O_*</c>/<see cref="EEIPClient.T_O_IOData"/>.</item>
///   <item>O→T Output Assembly = 인스턴스 132, 24B (PC→장치): External Input Request·명령. EEIP 의 <c>O_T_*</c>/<see cref="EEIPClient.O_T_IOData"/>.</item>
/// </list>
/// 측정값은 채널 N(1-based)에 대해 Input Assembly 바이트 <c>48 + (N-1)*4</c> 의 32bit signed(LE).
/// 영점(Zero)은 Output Assembly 바이트 2 의 채널 비트(External Input Request 2 = Zero Reset)로 제어한다.
///
/// EEIP.NetStandard 는 동기 API 라 접속(RegisterSession+ForwardOpen)은 <see cref="Task.Run(Action)"/> +
/// 타임아웃으로 감싼다. <see cref="SemaphoreSlim"/> 로 접속/해제를 직렬화하고 <see cref="IDisposable"/> 구현.
/// </summary>
public sealed class LaserDisplacementSensorClient : IDisposable
{
    // ── Input Assembly(110) 오프셋 ────────────────────────────────────────────
    private const int SensorErrorByte = 2;    // 채널별 에러 비트(byte 2: CH1-8, byte 3: CH9-16)
    private const int SensorWarningByte = 4;   // 채널별 경고 비트
    private const int SensorEnableByte = 8;    // 채널별 Enable(측정범위 내) 비트
    private const int OutputHighByte = 18;     // 판정 HIGH
    private const int OutputLowByte = 20;      // 판정 LOW
    private const int OutputPassByte = 22;     // 판정 PASS
    private const int MeasurementBase = 48;    // Output Data 1(=CH1) 시작. 채널당 4바이트 int32.
    private const int InputAssemblyLength = 276;

    // ── Output Assembly(132) 오프셋 ───────────────────────────────────────────
    private const int ZeroRequestByte = 2;     // External Input Request 2 = Zero Reset (byte 2: CH1-8)
    private const int OutputAssemblyLength = 24;

    private readonly LaserDisplacementSensorSettings _settings;
    private readonly ILogger<LaserDisplacementSensorClient> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Output Assembly 로컬 버퍼. EEIP 가 RPI 마다 재송신하므로 비트를 바꾼 뒤 O_T_IOData 에 대입한다.
    private readonly byte[] _out = new byte[OutputAssemblyLength];
    private readonly object _outLock = new();

    private EEIPClient? _eeip;
    private volatile bool _connected;
    private bool _forwardOpen;
    private bool _disposed;

    public LaserDisplacementSensorClient(
        LaserDisplacementSensorSettings settings,
        ILogger<LaserDisplacementSensorClient> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public bool IsConnected => _connected;
    public string Name => _settings.Name;

    /// <summary>
    /// 세션 등록 후 Class1 implicit 연결(ForwardOpen)을 연다. 이미 접속돼 있으면 아무 것도 하지 않는다(멱등).
    /// 동기 API 이므로 타임아웃 토큰과 함께 <see cref="Task.Run(Action)"/> 로 감싼다.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_connected) return;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_connected) return;

            var client = new EEIPClient();
            var ip = _settings.IpAddress;
            var port = (ushort)_settings.Port;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(_settings.ConnectTimeoutMs));

            _logger.LogInformation("레이저 변위 센서 접속 시도 {Ip}:{Port} (RPI={Rpi}ms)", ip, port, _settings.RpiMs);

            await Task.Run(() =>
            {
                client.RegisterSession(ip, port);
                ConfigureConnection(client);
                client.ForwardOpen();

                // 우리가 소유하는 24B 출력 프레임을 0으로 초기화해 송신 시작. (기존 영점 상태 초기화)
                lock (_outLock)
                {
                    Array.Clear(_out, 0, _out.Length);
                    client.O_T_IOData = (byte[])_out.Clone();
                }
            }, timeoutCts.Token).ConfigureAwait(false);

            _eeip = client;
            _forwardOpen = true;
            _connected = true;
            _logger.LogInformation("레이저 변위 센서 연결 완료(ForwardOpen) {Ip}:{Port}", ip, port);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>ZP-EIP "Full"(Exclusive Owner) 연결 파라미터 설정. 매뉴얼 Z496 기준.</summary>
    private void ConfigureConnection(EEIPClient client)
    {
        client.AssemblyObjectClass = 0x04;

        // O→T (PC→장치): Output Assembly 132, 24B
        client.O_T_InstanceID = 132;
        client.O_T_Length = OutputAssemblyLength;
        client.O_T_RealTimeFormat = RealTimeFormat.Header32Bit;   // Run/Idle 헤더
        client.O_T_ConnectionType = ConnectionType.Point_to_Point;
        client.O_T_Priority = Priority.Scheduled;
        client.O_T_OwnerRedundant = false;                        // Exclusive Owner
        client.O_T_VariableLength = false;                        // 고정 길이 어셈블리

        // T→O (장치→PC): Input Assembly 110, 276B
        client.T_O_InstanceID = 110;
        client.T_O_Length = InputAssemblyLength;
        client.T_O_RealTimeFormat = RealTimeFormat.Modeless;      // 헤더 없음
        client.T_O_ConnectionType = ConnectionType.Point_to_Point; // 단일 스캐너 → P2P (기본 Multicast 에서 변경)
        client.T_O_Priority = Priority.Scheduled;
        client.T_O_OwnerRedundant = false;
        client.T_O_VariableLength = false;

        client.ConfigurationAssemblyInstanceID = (byte)_settings.ConfigAssemblyInstanceId;

        var rpiUs = (uint)Math.Clamp(_settings.RpiMs, 1, 10000) * 1000u;
        client.RequestedPacketRate_O_T = rpiUs;
        client.RequestedPacketRate_T_O = rpiUs;
    }

    /// <summary>연결을 해제한다. 비동기 종료 경로용(세마포어로 진행 중 작업과 직렬화).</summary>
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            CloseSession();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>연결을 해제한다. 호스트 종료 등 동기 경로용.</summary>
    public void Disconnect()
    {
        // 종료 경로에서는 세마포어 대기 없이 곧바로 정리한다.
        CloseSession();
    }

    private void CloseSession()
    {
        var client = _eeip;
        _eeip = null;
        _connected = false;
        if (client is null) return;

        try
        {
            if (_forwardOpen)
            {
                client.ForwardClose();
                _forwardOpen = false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "레이저 변위 센서 ForwardClose 중 오류(무시)");
        }

        try
        {
            client.UnRegisterSession();
            _logger.LogInformation("레이저 변위 센서 세션 해제");
        }
        catch (Exception ex)
        {
            // 이미 끊긴 소켓 등 — 해제 실패는 무시(로그만).
            _logger.LogWarning(ex, "레이저 변위 센서 세션 해제 중 오류(무시)");
        }
    }

    /// <summary>
    /// 최신 Input Assembly 프레임을 스냅샷하여 채널 1..<paramref name="count"/> 의 측정값을 파싱한다.
    /// 미연결이거나 아직 프레임이 없으면 <see cref="LaserChannelReading.Enabled"/>=false 인 항목을 돌려준다.
    /// </summary>
    public LaserChannelReading[] ReadChannels(int count)
    {
        count = Math.Max(0, count);
        var result = new LaserChannelReading[count];

        var client = _eeip;
        var data = _connected ? client?.T_O_IOData : null;
        // 프레임 수신 전이거나 측정값 영역에 못 미치면 데이터 없음으로 처리.
        bool hasFrame = data is not null && data.Length >= MeasurementBase + count * 4;

        for (int i = 0; i < count; i++)
        {
            int ch = i + 1;
            if (!hasFrame)
            {
                result[i] = new LaserChannelReading(ch, 0, 0, false, false, false, false, false, false, IsZeroed(ch));
                continue;
            }

            int raw = BitConverter.ToInt32(data!, MeasurementBase + i * 4);   // CIP LE = 머신 LE(x64/arm64)
            double value = raw * _settings.MeasurementScale;

            result[i] = new LaserChannelReading(
                ch,
                raw,
                value,
                Enabled: Bit(data!, SensorEnableByte, i),
                Error: Bit(data!, SensorErrorByte, i),
                Warning: Bit(data!, SensorWarningByte, i),
                High: Bit(data!, OutputHighByte, i),
                Low: Bit(data!, OutputLowByte, i),
                Pass: Bit(data!, OutputPassByte, i),
                Zeroed: IsZeroed(ch));
        }

        return result;
    }

    /// <summary>채널별 비트 필드(byte baseOffset: CH1-8, baseOffset+1: CH9-16)에서 채널 인덱스 비트 읽기.</summary>
    private static bool Bit(byte[] data, int baseOffset, int chIndex)
    {
        int off = baseOffset + chIndex / 8;
        if ((uint)off >= (uint)data.Length) return false;
        return (data[off] & (1 << (chIndex % 8))) != 0;
    }

    /// <summary>
    /// 채널의 영점(Zero) 요청 비트를 설정/해제한다. Output Assembly 바이트 2 의 채널 비트(Zero Reset).
    /// ON = 현재값을 0으로(영점 설정), OFF = 실제값 복원(영점 해제). EEIP 가 RPI 마다 재송신한다.
    /// </summary>
    public void SetZero(int ch, bool on)
    {
        if (ch < 1) return;
        int idx = ch - 1;
        int byteIdx = ZeroRequestByte + idx / 8;
        if ((uint)byteIdx >= (uint)_out.Length) return;

        var client = _eeip;
        lock (_outLock)
        {
            if (on) _out[byteIdx] |= (byte)(1 << (idx % 8));
            else _out[byteIdx] &= (byte)~(1 << (idx % 8));

            if (client is not null && _connected)
                client.O_T_IOData = (byte[])_out.Clone();
        }
    }

    private bool IsZeroed(int ch)
    {
        if (ch < 1) return false;
        int idx = ch - 1;
        int byteIdx = ZeroRequestByte + idx / 8;
        if ((uint)byteIdx >= (uint)_out.Length) return false;
        lock (_outLock)
            return (_out[byteIdx] & (1 << (idx % 8))) != 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
        _gate.Dispose();
    }
}
