using System.Net.Sockets;
using Horizon.XmlRpc.Client;
using Horizon.XmlRpc.Core;
using Microsoft.Extensions.Logging;

namespace HD_AMR.Communication;

/// <summary>
/// 페어리노 컨트롤러에 XML-RPC 명령을 보내는 클라이언트. <see cref="ModbusTcpClient"/>와 동일한 형태
/// (SemaphoreSlim 직렬화, 한국어 로그, Connect/Disconnect/Dispose)를 따른다.
/// XML-RPC는 HTTP 기반 stateless이므로 "연결"은 명령 포트(20003)의 TCP 도달성으로 정의한다.
/// (특정 메서드의 응답 역직렬화에 의존하지 않으므로, 명령 실패 원인이 연결 단계에서 가려지지 않는다.)
/// </summary>
public class FairinoRpcClient : IDisposable
{
    private readonly FairinoRpcSettings _settings;
    private readonly ILogger<FairinoRpcClient> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    private IFairinoRpc? _proxy;
    private volatile bool _connected;
    private bool _disposed;

    public FairinoRpcClient(FairinoRpcSettings settings, ILogger<FairinoRpcClient> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public bool IsConnected => _connected;

    public string Name => _settings.Name;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_connected) return;

        await _semaphore.WaitAsync(ct);
        try
        {
            // 1) 명령 포트 TCP 도달성 확인 (소켓 레벨). 메서드 응답 역직렬화에 의존하지 않는다.
            using (var probe = new TcpClient())
            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeoutCts.CancelAfter(_settings.TimeoutMs);
                try
                {
                    await probe.ConnectAsync(_settings.IpAddress, _settings.CommandPort, timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"{_settings.IpAddress}:{_settings.CommandPort} 연결 시간 초과({_settings.TimeoutMs}ms). 로봇 전원/네트워크/RPC 포트를 확인하세요.");
                }
            }

            // 2) XML-RPC 프록시 생성. 경로 "/RPC2" 필수(루트는 404) — 파이썬 ServerProxy 기본 핸들러와 동일.
            var path = string.IsNullOrEmpty(_settings.RpcPath) ? ""
                     : _settings.RpcPath.StartsWith('/') ? _settings.RpcPath
                     : "/" + _settings.RpcPath;
            var proxy = XmlRpcProxyGen.Create<IFairinoRpc>();
            proxy.Url = $"http://{_settings.IpAddress}:{_settings.CommandPort}{path}";
            // 모션 명령(MoveL 등)은 완료까지 블로킹 → 긴 타임아웃 필요.
            proxy.Timeout = _settings.CommandTimeoutMs;
            proxy.KeepAlive = false;

            _proxy = proxy;
            _connected = true;
            _logger.LogInformation("{Name} XML-RPC 연결 완료 ({Url})", _settings.Name, proxy.Url);
        }
        catch (Exception ex)
        {
            _connected = false;
            _proxy = null;
            _logger.LogWarning(ex, "{Name} XML-RPC 연결 실패 ({Ip}:{Port})",
                _settings.Name, _settings.IpAddress, _settings.CommandPort);
            throw;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public void Disconnect()
    {
        _connected = false;
        _proxy = null;
    }

    // ── 좌표계 ─────────────────────────────────────────────────────
    public Task<int> SetToolCoordAsync(int id, double[] coord, int type = 0, int install = 0,
                                       int toolID = 0, int loadNum = 0, CancellationToken ct = default)
        => InvokeAsync("SetToolCoord", p => ToErr(p.SetToolCoord(id, coord, type, install, toolID, loadNum)), ct);

    public Task<int> SetWObjCoordAsync(int id, double[] coord, int refFrame = 0, CancellationToken ct = default)
        => InvokeAsync("SetWObjCoord", p => ToErr(p.SetWObjCoord(id, coord, refFrame)), ct);

    /// <summary>id 공구 좌표계 현재 값 [x,y,z,rx,ry,rz] 읽기. 실패 시 예외(미지원/오류).</summary>
    public Task<double[]> GetToolCoordAsync(int id, CancellationToken ct = default)
        => GetCoordWithIdAsync("GetToolCoordWithID", id, p => p.GetToolCoordWithID(id), "공구", ct);

    /// <summary>id 작업물 좌표계 현재 값 [x,y,z,rx,ry,rz] 읽기. 실패 시 예외(미지원/오류).</summary>
    public Task<double[]> GetWObjCoordAsync(int id, CancellationToken ct = default)
        => GetCoordWithIdAsync("GetWObjCoordWithID", id, p => p.GetWObjCoordWithID(id), "작업물", ct);

    private async Task<double[]> GetCoordWithIdAsync(string op, int id, Func<IFairinoRpc, object> call, string what, CancellationToken ct)
    {
        var raw = await InvokeAsync(op, call, ct);
        if (raw is object[] a && a.Length >= 7 && ToErr(a[0]) == 0)
            return a[1..7].Select(ToDouble).ToArray();
        var err = raw is object[] arr && arr.Length > 0 ? ToErr(arr[0]) : -1;
        throw new InvalidOperationException(
            $"{what} 좌표계 #{id} 읽기 실패 (errcode={err}). 컨트롤러 펌웨어가 미지원이거나 번호가 잘못됐을 수 있습니다.");
    }

    // ── 작업물 좌표계 3점 교시 ─────────────────────────────────────
    /// <summary>3점법 참조점 기록(pointNum 1~3). 로봇이 현재 TCP 위치를 해당 점으로 저장.</summary>
    public Task<int> SetWObjCoordPointAsync(int pointNum, CancellationToken ct = default)
        => InvokeAsync("SetWObjCoordPoint", p => ToErr(p.SetWObjCoordPoint(pointNum)), ct);

    /// <summary>기록된 3점으로 작업물 좌표계 pose 계산. method 0=원점-X축-Z축, 1=원점-X축-XY평면.</summary>
    public async Task<double[]> ComputeWObjCoordAsync(int method, int refFrame = 0, CancellationToken ct = default)
    {
        var raw = await InvokeAsync("ComputeWObjCoord", p => p.ComputeWObjCoord(method, refFrame), ct);
        if (raw is object[] a && a.Length >= 7 && ToErr(a[0]) == 0)
            return a[1..7].Select(ToDouble).ToArray();
        var err = raw is object[] arr && arr.Length > 0 ? ToErr(arr[0]) : -1;
        throw new InvalidOperationException($"작업물 좌표계 계산(ComputeWObjCoord) 실패 (errcode={err}).");
    }

    /// <summary>조그-캡처(SetWObjCoordPoint ×3 선행)한 점들로 좌표계 계산 후 id에 등록. 등록된 pose 반환.</summary>
    public async Task<double[]> RegisterWObjFromTeachingAsync(int id, int method, int refFrame = 0, CancellationToken ct = default)
    {
        var pose = await ComputeWObjCoordAsync(method, refFrame, ct);
        var rc = await SetWObjCoordAsync(id, pose, refFrame, ct);
        if (rc != 0) throw new InvalidOperationException($"작업물 좌표계 등록(SetWObjCoord) 실패 (rc={rc}).");
        return pose;
    }

    /// <summary>수동 입력 3점(베이스 좌표 기준)으로 좌표계를 클라이언트 계산 후 id에 등록. 등록된 pose 반환.</summary>
    public async Task<double[]> RegisterWObjFromPointsAsync(int id, double[] origin, double[] xAxisPt, double[] planePt,
                                                            int method, int refFrame = 0, CancellationToken ct = default)
    {
        var pose = ComputeFramePose(origin, xAxisPt, planePt, method);
        var rc = await SetWObjCoordAsync(id, pose, refFrame, ct);
        if (rc != 0) throw new InvalidOperationException($"작업물 좌표계 등록(SetWObjCoord) 실패 (rc={rc}).");
        return pose;
    }

    /// <summary>역기구학: 직교 자세 → 6축 관절각. 실패 시 예외(작업영역 밖/도달 불가).</summary>
    public async Task<double[]> GetInverseKinAsync(double[] descPose, int type = 0, int config = -1, CancellationToken ct = default)
    {
        var raw = await InvokeAsync("GetInverseKin", p => p.GetInverseKin(type, descPose, config), ct);
        if (raw is object[] a && a.Length >= 7 && ToErr(a[0]) == 0)
            return a[1..7].Select(ToDouble).ToArray();
        var err = raw is object[] arr && arr.Length > 0 ? ToErr(arr[0]) : -1;
        throw new InvalidOperationException(
            $"역기구학(GetInverseKin) 실패 (errcode={err}). 목표 자세가 작업영역 밖이거나 도달 불가일 수 있습니다.");
    }

    // ── 이동 ───────────────────────────────────────────────────────
    /// <summary>
    /// 직교 직선 이동(MoveL). descPose = [x,y,z,rx,ry,rz]. jointPos 미지정 시 역기구학으로 계산한다
    /// (FAIRINO MoveL은 관절각이 필요). 모든 인자를 33요소 단일 배열로 전송한다.
    /// </summary>
    public async Task<int> MoveLAsync(double[] descPose, double[]? jointPos = null, int? tool = null, int? user = null,
                                      double? vel = null, double acc = 0.0, double ovl = 100.0, double blendR = -1.0,
                                      int offsetFlag = 0, double[]? offsetPos = null,
                                      CancellationToken ct = default)
    {
        int t = tool ?? _settings.DefaultToolId;
        int u = user ?? _settings.DefaultUserId;
        double v = vel ?? _settings.DefaultVelPct;
        double[] off = (offsetPos is { Length: >= 6 }) ? offsetPos : new double[6];

        double[] j = jointPos ?? await GetInverseKinAsync(descPose, ct: ct);

        var args = new object[]
        {
            j[0], j[1], j[2], j[3], j[4], j[5],
            descPose[0], descPose[1], descPose[2], descPose[3], descPose[4], descPose[5],
            t, u, v, acc, ovl, blendR,
            0,                                  // blendMode
            0.0, 0.0, 0.0, 0.0,                 // exaxis_pos[4]
            0, offsetFlag,                      // search, offset_flag
            off[0], off[1], off[2], off[3], off[4], off[5], // offset_pos[6]
            100.0,                              // oacc
            0,                                  // velAccParamMode
        };
        return await InvokeAsync("MoveL", p => ToErr(p.MoveL(args)), ct);
    }

    /// <summary>
    /// 작업물 좌표계(user) 기준으로 앵커 포즈에서 offset만큼 이동. offset_flag=1(작업물 좌표계 오프셋) 사용.
    /// anchorPose는 베이스 기준 유효 TCP 포즈(IK 계산용). offset=[dx,dy,dz,drx,dry,drz].
    /// </summary>
    public Task<int> MoveByOffsetAsync(double[] anchorPose, int user, double[] offset, int? tool = null, double? vel = null, CancellationToken ct = default)
        => MoveLAsync(anchorPose, tool: tool, user: user, vel: vel, offsetFlag: 1, offsetPos: offset, ct: ct);

    /// <summary>관절 이동(MoveJ). jointPos = 6축 각도, descPose = 대응 직교 포즈(0이면 컨트롤러가 정기구학 계산).</summary>
    public Task<int> MoveJAsync(double[] jointPos, double[] descPose, int? tool = null, int? user = null,
                                double? vel = null, double acc = 0, double ovl = 100, double blendT = -1,
                                CancellationToken ct = default)
        => InvokeAsync("MoveJ", p => ToErr(p.MoveJ(
                jointPos, descPose,
                tool ?? _settings.DefaultToolId, user ?? _settings.DefaultUserId,
                vel ?? _settings.DefaultVelPct, acc, ovl,
                new double[4], blendT, 0, new double[6])), ct);

    // ── 상태/제어 ──────────────────────────────────────────────────
    public Task<int> RobotEnableAsync(bool enable, CancellationToken ct = default)
        => InvokeAsync("RobotEnable", p => ToErr(p.RobotEnable(enable ? 1 : 0)), ct);

    public Task<int> ModeAsync(int mode, CancellationToken ct = default)
        => InvokeAsync("Mode", p => ToErr(p.Mode(mode)), ct);

    public Task<int> SetSpeedAsync(int velPct, CancellationToken ct = default)
        => InvokeAsync("SetSpeed", p => ToErr(p.SetSpeed(velPct)), ct);

    public Task<int> StopMotionAsync(CancellationToken ct = default)
        => InvokeAsync("StopMotion", p => ToErr(p.StopMotion()), ct);

    /// <summary>현재 TCP 포즈 [x,y,z,rx,ry,rz] 조회 (errcode 헤더 제거 후 반환).</summary>
    public Task<double[]> GetTcpPoseAsync(int flag = 0, CancellationToken ct = default)
        => InvokeAsync("GetActualTCPPose", p => ToPose(p.GetActualTCPPose(flag)), ct);

    // ── 공통 호출 래퍼 ─────────────────────────────────────────────
    private async Task<T> InvokeAsync<T>(string op, Func<IFairinoRpc, T> call, CancellationToken ct)
    {
        EnsureConnected();
        await _semaphore.WaitAsync(ct);
        try
        {
            _logger.LogDebug("{Name} {Op} 호출", _settings.Name, op);
            // XML-RPC 프록시 호출은 동기(blocking HTTP)이므로 스레드풀로 오프로드.
            var proxy = _proxy!;
            var result = await Task.Run(() => call(proxy), ct);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Name} {Op} 실패", _settings.Name, op);
            throw;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private void EnsureConnected()
    {
        if (!_connected || _proxy == null)
            throw new InvalidOperationException($"{_settings.Name} XML-RPC가 연결되지 않았습니다.");
    }

    // ── 반환값 방어적 해석 ─────────────────────────────────────────
    /// <summary>FAIRINO errcode 추출. bare int 또는 [errcode, ...] 배열 모두 처리.</summary>
    private static int ToErr(object? r) => r switch
    {
        null => 0,
        int i => i,
        long l => (int)l,
        bool b => b ? 0 : -1,
        double d => (int)d,
        string s => int.TryParse(s, out var v) ? v : 0,
        object[] a => a.Length > 0 ? ToErr(a[0]) : 0,
        _ => throw new InvalidOperationException($"예상치 못한 RPC 반환 형식: {r.GetType().Name}"),
    };

    /// <summary>TCP 포즈 추출. [errcode,[x..]] / [errcode,x,y,..] / [x,y,..] 모두 처리.</summary>
    private static double[] ToPose(object? r)
    {
        if (r is not object[] a || a.Length == 0) return Array.Empty<double>();
        if (a.Length == 2 && a[1] is object[] inner) return inner.Select(ToDouble).ToArray();
        var flat = a.Select(ToDouble).ToArray();
        return flat.Length >= 7 ? flat[1..7] : flat; // 선두 errcode 제거
    }

    private static double ToDouble(object? o) => o switch
    {
        double d => d,
        int i => i,
        long l => l,
        string s => double.TryParse(s, out var v) ? v : 0,
        _ => 0,
    };

    // ── 3점 → 작업물 좌표계 pose 계산 (수동 입력용) ────────────────
    /// <summary>
    /// 베이스 좌표 3점으로 작업물 좌표계 pose [x,y,z,rx,ry,rz] 계산. 위치는 origin, 회전은 RPY(ZYX, 도).
    /// method 0=원점-X축-Z축(점3=+Z 방향), 1=원점-X축-XY평면(점3=+Y쪽 평면점).
    /// ⚠ FAIRINO 오일러 규약(ZYX 가정)은 조그-캡처 결과와 대조 검증 필요.
    /// </summary>
    public static double[] ComputeFramePose(double[] origin, double[] xAxisPt, double[] planePt, int method)
    {
        var o = new[] { origin[0], origin[1], origin[2] };
        var x = Normalize(Sub(xAxisPt, o), "X축");
        var v = Sub(planePt, o);

        double[] y, z;
        if (method == 0) // 원점-X축-Z축
        {
            var zApprox = Normalize(v, "Z축");
            y = Normalize(Cross(zApprox, x), "Y축(X×Z 직교)");
            z = Cross(x, y);
        }
        else // 원점-X축-XY평면
        {
            z = Normalize(Cross(x, v), "Z축(X×평면 직교)");
            y = Cross(z, x);
        }

        // R = [x | y | z] (열벡터). RPY(ZYX): R = Rz(rz)·Ry(ry)·Rx(rx).
        const double rad2deg = 180.0 / Math.PI;
        double rz = Math.Atan2(x[1], x[0]) * rad2deg;
        double ry = Math.Atan2(-x[2], Math.Sqrt(x[0] * x[0] + x[1] * x[1])) * rad2deg;
        double rx = Math.Atan2(y[2], z[2]) * rad2deg;

        return new[] { o[0], o[1], o[2], rx, ry, rz };
    }

    private static double[] Sub(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };

    private static double[] Cross(double[] a, double[] b) => new[]
    {
        a[1] * b[2] - a[2] * b[1],
        a[2] * b[0] - a[0] * b[2],
        a[0] * b[1] - a[1] * b[0],
    };

    private static double[] Normalize(double[] a, string what)
    {
        double m = Math.Sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]);
        if (m < 1e-9)
            throw new InvalidOperationException($"좌표계 계산 실패: {what} 벡터 크기가 0입니다. 세 점이 서로 충분히 떨어져 있고 일직선이 아닌지 확인하세요.");
        return new[] { a[0] / m, a[1] / m, a[2] / m };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connected = false;
        _proxy = null;
        _semaphore.Dispose();
    }
}
