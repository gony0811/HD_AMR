using HD.AMR.App.Communication;
using HD.AMR.App.Enums;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HD.AMR.App.Service;

/// <summary>
/// LS산전 IO Module ModbusTCP 서비스. AMR/Cobot 과 동일하게 싱글톤 + 호스티드로 등록되어
/// 기동 시 자동 접속하고 실패 시 5초마다 재접속한다. 입력/출력 이미지를 주기적으로 폴링해
/// <see cref="IoModuleState"/> 스냅샷으로 캐싱하고, 페이지에서 임의 주소를 읽고/쓸 수 있도록
/// 범용 pass-through 메서드도 노출한다.
/// 접점 ↔ 설비 신호 대응은 <see cref="IoPointMap"/> 이 단일 원천이다(docs/IO_MODULE_POINTS.md).
/// </summary>
public class IoModuleService : BackgroundService
{
    private readonly IoModuleModbusTcpSettings _settings;
    private readonly ModbusTcpClient _client;
    private readonly ILogger<IoModuleService> _logger;
    private readonly AMRService _amr;
    private readonly IoAmrModeControl _modeControl = new();
    private readonly IoStartStopLampControl _lampControl = new();
    private readonly SemaphoreSlim _outputWriteLock = new(1, 1);

    private readonly object _stateLock = new();
    private IoModuleState? _state;
    private bool _initialStopStateApplied;

    public IoModuleService(IOptions<IoModuleModbusTcpSettings> options, ILoggerFactory loggerFactory, AMRService amr)
    {
        _settings = options.Value;
        _client = new ModbusTcpClient(_settings, loggerFactory.CreateLogger<ModbusTcpClient>());
        _logger = loggerFactory.CreateLogger<IoModuleService>();
        _amr = amr;
    }

    public bool IsConnected => _client.IsConnected;

    public ModbusTcpClient Client => _client;

    public IoModuleModbusTcpSettings Settings => _settings;

    /// <summary>마지막 연결/통신 오류 메시지 (정상 시 null)</summary>
    public string? LastError { get; private set; }

    /// <summary>입력(FC02 @InputStart) 폴링 오류 — 정상 시 null.</summary>
    public string? InputError { get; private set; }

    /// <summary>출력 echo(FC02 @OutputEchoStart) 폴링 오류 — 정상 시 null.</summary>
    public string? OutputError { get; private set; }

    /// <summary>입력을 마지막으로 성공적으로 읽은 시각(UTC). 한 번도 못 읽었으면 null.</summary>
    public DateTime? LastInputsOkUtc { get; private set; }

    /// <summary>출력 echo 를 마지막으로 성공적으로 읽은 시각(UTC).</summary>
    public DateTime? LastOutputsOkUtc { get; private set; }

    private readonly System.Collections.Concurrent.ConcurrentQueue<DateTime> _inputChangeTimes = new();

    /// <summary>최근 60초간 입력 이미지 변화 횟수 — 백플레인 불안정(비트 시프트) 식별용 참고 지표.
    /// 정지 상태에서 이 값이 계속 올라가면 어댑터↔모듈 데이터 교환 불량(H/W 점검 대상).</summary>
    public int InputChangesLastMinute
    {
        get
        {
            while (_inputChangeTimes.TryPeek(out var t) && (DateTime.UtcNow - t).TotalSeconds > 60)
                _inputChangeTimes.TryDequeue(out _);
            return _inputChangeTimes.Count;
        }
    }

    /// <summary>가장 최근 캐싱된 입출력 상태 스냅샷 (아직 한 번도 못 읽었으면 null)</summary>
    public IoModuleState? GetState()
    {
        lock (_stateLock)
        {
            return _state;
        }
    }

    // 연결 실패 warn을 끊김당 1회만 남기기 위한 플래그(재연결 성공 시 리셋).
    private bool _retryWarned;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("IoModuleService 시작 ({Ip}:{Port}, SlaveId={SlaveId})",
            _settings.IpAddress, _settings.Port, _settings.SlaveId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_client.IsConnected)
                {
                    _logger.LogDebug("IO Module Modbus TCP 연결 시도");
                    await _client.ConnectAsync(stoppingToken);
                    LastError = null;
                    _retryWarned = false;
                    _logger.LogInformation("IO Module Modbus TCP 연결 완료");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                if (!_retryWarned)
                {
                    _logger.LogWarning("IO Module Modbus TCP 연결 실패, 이후 자동 재시도 — {Err}", ex.Message);
                    _retryWarned = true;
                }
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }

            // 입력(FC02 ×16)·출력(FC01 ×8)을 읽어 스냅샷 캐싱. 읽기 실패는 LastError에 기록하고 계속(연결 유지 로직은 위에서 처리).
            try
            {
                var inputs = await PollStateAsync(stoppingToken);
                if (inputs is not null)
                {
                    if (!_initialStopStateApplied)
                    {
                        try
                        {
                            await _amr.SetDrivingModeAsync(DrivingMode.Cart, stoppingToken);
                            await _lampControl.ApplyAsync(
                                DrivingMode.Cart, SetStartStopLampsAsync, stoppingToken);
                            _initialStopStateApplied = true;
                            _logger.LogInformation("시스템 초기 상태 동기화 완료: AMR Cart, START 램프 OFF, STOP 램프 ON");
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "시스템 초기 STOP 상태 동기화 실패 — 다음 입력 폴링에서 재시도");
                        }

                        // 초기 STOP 상태를 먼저 확정한 다음 주기부터 실제 버튼 입력을 처리한다.
                        await Task.Delay(TimeSpan.FromMilliseconds(200), stoppingToken);
                        continue;
                    }

                    try
                    {
                        await _modeControl.ApplyAsync(inputs, async (mode, ct) =>
                        {
                            await _amr.SetDrivingModeAsync(mode, ct);
                            _logger.LogInformation("IO 버튼 입력으로 AMR 모드 전환: {Mode}", mode);
                        }, stoppingToken);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "IO 버튼 입력에 따른 AMR 모드 전환 실패 — 다음 입력 폴링에서 재시도");
                    }

                    var indicatorMode = _amr.IndicatorDrivingMode;
                    if (indicatorMode is not null)
                    {
                        try
                        {
                            await _lampControl.ApplyAsync(
                                indicatorMode.Value, SetStartStopLampsAsync, stoppingToken);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "AMR 주행 모드에 따른 START/STOP 램프 동기화 실패 — 다음 입력 폴링에서 재시도");
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _logger.LogDebug(ex, "IO Module 상태 폴링 실패");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), stoppingToken);
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _client.Disconnect();
        _logger.LogInformation("IoModuleService 종료");
        return base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _client.Dispose();
        base.Dispose();
    }

    #region XBE-DC16A(입력) / XBE-TN16A(출력) typed 접근

    /// <summary>입력 접점(XBE-DC16A, 16점) 읽기 — Discrete Input(FC02 @InputStart = 0x2020,
    /// LED 헤더 4바이트 뒤. 매뉴얼 부록 A.2 + 실측 확정)</summary>
    public Task<bool[]> ReadInputsAsync(CancellationToken ct = default)
        => RunAsync(() => _client.ReadDiscreteInputsAsync(_settings.InputStart, _settings.InputCount, ct));

    /// <summary>출력 명령값(XBE-TN16A, 16점) 읽기 — Holding(FC03 @OutputWriteAddress x1워드) 비트 분해.
    /// TN16A 는 입력 리프레시에 echo 를 싣지 않으므로 홀딩 되읽기가 유일한 상태 소스다.</summary>
    public async Task<bool[]> ReadOutputsAsync(CancellationToken ct = default)
    {
        var words = await RunAsync(() => _client.ReadHoldingRegistersAsync(_settings.OutputWriteAddress, 1, ct));
        return WordToBits(words[0], _settings.OutputCount);
    }

    /// <summary>출력 접점(XBE-TN16A) 한 점을 ON/OFF — Holding 워드 read-modify-write(FC03→FC16,
    /// 매뉴얼 §3 Step.8: 출력 리프레시 = Holding 0x200~). 쓰기 후 되읽어 반영 여부를 반환한다 —
    /// false 면 어댑터가 쓰기를 반영하지 않는 상태(§3 Step.3: XG5000 으로 드라이버 "RAPIEnet v2"
    /// → Disable 설정 필요 — 공장 초기값이 RAPIEnet 대기라 Modbus 출력이 무시됨).</summary>
    public async Task<bool> WriteOutputAsync(int index, bool value, CancellationToken ct = default)
    {
        if (index < 0 || index >= _settings.OutputCount)
            throw new ArgumentOutOfRangeException(nameof(index),
                $"출력 인덱스는 0~{_settings.OutputCount - 1} 범위여야 합니다.");

        await _outputWriteLock.WaitAsync(ct);
        try
        {
            var word = await ReadOutputWordForUpdateAsync(ct);
            word = value ? (ushort)(word | (1 << index)) : (ushort)(word & ~(1 << index));
            await WriteOutputWordAsync(word, ct);
        }
        finally
        {
            _outputWriteLock.Release();
        }

        // 반영 확인: 되읽어 스냅샷 갱신 + 해당 비트 일치 여부 판정.
        await PollStateAsync(ct);
        var state = GetState();
        return state is not null && index < state.Outputs.Length && state.Outputs[index] == value;
    }

    private async Task SetStartStopLampsAsync(bool startSelected, CancellationToken ct)
    {
        await _outputWriteLock.WaitAsync(ct);
        try
        {
            var word = await ReadOutputWordForUpdateAsync(ct);
            var startMask = (ushort)(1 << IoPointMap.Out.StartButtonLamp);
            var stopMask = (ushort)(1 << IoPointMap.Out.StopButtonLamp);

            word = startSelected
                ? (ushort)((word | startMask) & ~stopMask)
                : (ushort)((word | stopMask) & ~startMask);

            await WriteOutputWordAsync(word, ct);
            _logger.LogInformation("AMR {Mode} 모드 표시: START 램프 {Start}, STOP 램프 {Stop}",
                startSelected ? "Drive" : "Cart",
                startSelected ? "ON" : "OFF",
                startSelected ? "OFF" : "ON");
        }
        finally
        {
            _outputWriteLock.Release();
        }
    }

    private async Task<ushort> ReadOutputWordForUpdateAsync(CancellationToken ct)
    {
        try
        {
            return (await _client.ReadHoldingRegistersAsync(_settings.OutputWriteAddress, 1, ct))[0];
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return _lastCommandedOutputs;
        }
    }

    private async Task WriteOutputWordAsync(ushort word, CancellationToken ct)
    {
        await RunAsync(() => _client.WriteMultipleRegistersAsync(
            _settings.OutputWriteAddress, new[] { word }, ct));
        _lastCommandedOutputs = word;
    }

    /// <summary>마지막으로 명령한 출력 워드 — 홀딩 되읽기 실패 시 RMW 기준값.</summary>
    private ushort _lastCommandedOutputs;

    private static bool[] WordToBits(ushort word, int count)
    {
        var bits = new bool[count];
        for (var i = 0; i < count && i < 16; i++)
            bits[i] = (word & (1 << i)) != 0;
        return bits;
    }

    // 같은 오류 메시지의 반복 Warning 을 억제하기 위한 마지막 로깅 오류(복구 시 Information 1회).
    private string? _lastPollErrorLogged;

    /// <summary>입력·출력 echo 를 <b>개별로</b> 읽어 스냅샷 캐싱 — 한쪽 실패가 다른 쪽 상태 표시를
    /// 막지 않도록 부분 갱신한다. 읽기 오류는 파트별 프로퍼티(<see cref="InputError"/>/<see cref="OutputError"/>)
    /// 와 <see cref="LastError"/> 에 남기고, 오류 내용이 바뀔 때만 Warning 1회(복구 시 Information).</summary>
    private async Task<bool[]?> PollStateAsync(CancellationToken ct)
    {
        bool[]? inputs = null, outputs = null;
        string? inErr = null, outErr = null;

        try
        {
            inputs = await _client.ReadDiscreteInputsAsync(_settings.InputStart, _settings.InputCount, ct);
            LastInputsOkUtc = DateTime.UtcNow;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { inErr = ex.Message; }

        try
        {
            var words = await _client.ReadHoldingRegistersAsync(_settings.OutputWriteAddress, 1, ct);
            outputs = WordToBits(words[0], _settings.OutputCount);
            LastOutputsOkUtc = DateTime.UtcNow;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { outErr = ex.Message; }

        // 어댑터 LED 헤더(FC04 2워드, 부록 A.2.2) — 진단 표시용. 실패해도 폴링 오류로 승격하지 않음.
        try
        {
            var led = await _client.ReadInputRegistersAsync(_settings.LedHeaderAddress, 2, ct);
            AdapterLedSummary = DecodeLedHeader(led[0], led[1]);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { AdapterLedSummary = null; }

        lock (_stateLock)
        {
            var prev = _state;
            // 입력 이미지 변화 추적 — 어댑터↔모듈 백플레인 불안정(1비트 시프트 프레임, 2026-09-16 실측)을
            // 화면에서 식별할 수 있게 최근 60초 변화 횟수를 집계한다. 정상 운전 중 실제 입력 변화도
            // 집계되므로 '오류'가 아니라 참고 지표다.
            if (inputs is not null && prev is not null && !inputs.SequenceEqual(prev.Inputs))
            {
                _inputChangeTimes.Enqueue(DateTime.UtcNow);
                while (_inputChangeTimes.TryPeek(out var t) && (DateTime.UtcNow - t).TotalSeconds > 60)
                    _inputChangeTimes.TryDequeue(out _);
            }
            if (inputs is not null || outputs is not null)
                _state = new IoModuleState(
                    inputs ?? prev?.Inputs ?? new bool[_settings.InputCount],
                    outputs ?? prev?.Outputs ?? new bool[_settings.OutputCount],
                    DateTime.UtcNow);
        }

        InputError = inErr;
        OutputError = outErr;
        var err = inErr is not null && outErr is not null ? $"입력: {inErr} / 출력: {outErr}"
                : inErr is not null ? $"입력: {inErr}"
                : outErr is not null ? $"출력: {outErr}"
                : null;
        LastError = err;

        if (err != _lastPollErrorLogged)
        {
            if (err is not null)
                _logger.LogWarning("IO Module 폴링 실패 — {Err} (입력 FC02@0x{In:X4}, 출력 Holding@0x{Out:X4})",
                    err, _settings.InputStart, _settings.OutputWriteAddress);
            else if (_lastPollErrorLogged is not null)
                _logger.LogInformation("IO Module 폴링 복구");
            _lastPollErrorLogged = err;
        }
        return inputs;
    }

    /// <summary>어댑터 LED 상태 요약 — 입력 리프레시 헤더 4바이트(부록 A.2.2, 2비트/LED: 0=Off,1=On,2=Blink).
    /// null 이면 읽기 실패. "RNS 적색 On/점멸" = RAPIEnet 대기 상태(출력 무시 — Step.3 Disable 필요 신호).</summary>
    public string? AdapterLedSummary { get; private set; }

    private static string DecodeLedHeader(ushort w0, ushort w1)
    {
        // 32비트 헤더: w0 = bit0~15, w1 = bit16~31.
        static string S(int v) => v switch { 0 => "─", 1 => "켜짐", 2 => "점멸", _ => "?" };
        static string Pair(string name, int green, int red) =>
            red != 0 ? $"{name} 적{S(red)}" : green != 0 ? $"{name} 녹{S(green)}" : $"{name} ─";

        int runG = w0 & 3, runR = (w0 >> 2) & 3;
        int rmsG = (w0 >> 4) & 3, rmsR = (w0 >> 6) & 3;
        int rnsG = (w0 >> 8) & 3, rnsR = (w0 >> 10) & 3;
        int relay = (w0 >> 12) & 3;
        int l1G = w1 & 3, l1Y = (w1 >> 2) & 3;
        int l2G = (w1 >> 4) & 3, l2Y = (w1 >> 6) & 3;

        return $"{Pair("RUN", runG, runR)} · {Pair("RMS", rmsG, rmsR)} · {Pair("RNS", rnsG, rnsR)}" +
               $" · RELAY {S(relay)} · LINK1 {(l1Y != 0 ? "황" + S(l1Y) : S(l1G))} · LINK2 {(l2Y != 0 ? "황" + S(l2Y) : S(l2G))}";
    }

    #endregion

    #region 범용 Modbus 읽기/쓰기 (IO list 확정 전 테스트용)

    /// <summary>Coil(출력, FC01) 읽기</summary>
    public Task<bool[]> ReadCoilsAsync(ushort startAddress, ushort count, CancellationToken ct = default)
        => RunAsync(() => _client.ReadCoilsAsync(startAddress, count, ct));

    /// <summary>Discrete Input(입력, FC02) 읽기</summary>
    public Task<bool[]> ReadDiscreteInputsAsync(ushort startAddress, ushort count, CancellationToken ct = default)
        => RunAsync(() => _client.ReadDiscreteInputsAsync(startAddress, count, ct));

    /// <summary>Holding Register(FC03) 읽기</summary>
    public Task<ushort[]> ReadHoldingRegistersAsync(ushort startAddress, ushort count, CancellationToken ct = default)
        => RunAsync(() => _client.ReadHoldingRegistersAsync(startAddress, count, ct));

    /// <summary>Input Register(FC04) 읽기</summary>
    public Task<ushort[]> ReadInputRegistersAsync(ushort startAddress, ushort count, CancellationToken ct = default)
        => RunAsync(() => _client.ReadInputRegistersAsync(startAddress, count, ct));

    /// <summary>Coil(출력, FC05) 단일 쓰기</summary>
    public Task WriteSingleCoilAsync(ushort address, bool value, CancellationToken ct = default)
        => RunAsync(() => _client.WriteSingleCoilAsync(address, value, ct));

    /// <summary>Holding Register(FC06) 단일 쓰기</summary>
    public Task WriteSingleRegisterAsync(ushort address, ushort value, CancellationToken ct = default)
        => RunAsync(() => _client.WriteSingleRegisterAsync(address, value, ct));

    /// <summary>오류를 LastError 로 기록하며 실행 (값 반환용)</summary>
    private async Task<T> RunAsync<T>(Func<Task<T>> op)
    {
        try
        {
            var result = await op();
            LastError = null;
            return result;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            throw;
        }
    }

    /// <summary>오류를 LastError 로 기록하며 실행 (반환값 없음)</summary>
    private async Task RunAsync(Func<Task> op)
    {
        try
        {
            await op();
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            throw;
        }
    }

    #endregion
}
