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
    // 긴급 정지 전용 프록시. 진행 중인 블로킹 이동이 _proxy 를 점유해도, 별도 연결로
    // StopMotion 을 즉시 보낼 수 있도록 _semaphore 와 무관하게 사용한다.
    private IFairinoRpc? _stopProxy;
    private volatile bool _connected;
    private bool _disposed;

    // 컨트롤러의 현재 활성 공구 번호 추적값. MoveL/MoveJ의 tool 인자가 활성 공구를 바꾸므로 성공 시 갱신한다.
    // GetActualTCPNum 실측을 못 읽는 펌웨어에서 GetTcpPoseInBaseAsync의 재프레임에 쓸 활성 공구의
    // 폴백 소스로 쓰인다(ResolveActiveToolAsync). -1 = 미상(연결 직후). 연결/해제 시 재설정된다.
    private int _activeTool = -1;

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

            // 긴급 정지 전용 프록시(같은 URL, 짧은 타임아웃). KeepAlive=false 라 호출마다 새 TCP 연결이므로
            // 이동 프록시가 블로킹 중이어도 별도 연결로 StopMotion 을 동시에 보낼 수 있다.
            var stopProxy = XmlRpcProxyGen.Create<IFairinoRpc>();
            stopProxy.Url = proxy.Url;
            stopProxy.Timeout = _settings.TimeoutMs;
            stopProxy.KeepAlive = false;

            _proxy = proxy;
            _stopProxy = stopProxy;
            _activeTool = -1;   // 새 연결: 활성 공구 추적값 초기화(다음 Ensure 호출이 강제 동기화).
            _connected = true;
            _logger.LogInformation("{Name} XML-RPC 연결 완료 ({Url})", _settings.Name, proxy.Url);
        }
        catch (Exception ex)
        {
            _connected = false;
            _proxy = null;
            _stopProxy = null;
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
        _stopProxy = null;
        _activeTool = -1;
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

    /// <summary>현재 관절각(도) [j0..j5] 조회. flag 0=블로킹,1=논블로킹. 실패 시 예외.
    /// 블로킹(flag=0)이 막히는 펌웨어 대비 실패 시 논블로킹(flag=1, 캐시)으로 1회 폴백하고,
    /// 최종 실패 메시지에는 실제 컨트롤러 오류코드(main/sub)를 best-effort로 덧붙인다.</summary>
    public async Task<double[]> GetActualJointPosAsync(int flag = 0, CancellationToken ct = default)
    {
        var raw = await InvokeAsync("GetActualJointPosDegree", p => p.GetActualJointPosDegree(flag), ct);
        if (raw is object[] a && a.Length >= 7 && ToErr(a[0]) == 0)
            return a[1..7].Select(ToDouble).ToArray();

        if (flag == 0)   // 블로킹 실패 시 논블로킹(캐시)으로 1회 폴백
        {
            var raw2 = await InvokeAsync("GetActualJointPosDegree", p => p.GetActualJointPosDegree(1), ct);
            if (raw2 is object[] b && b.Length >= 7 && ToErr(b[0]) == 0)
                return b[1..7].Select(ToDouble).ToArray();
            raw = raw2;
        }
        var err = raw is object[] arr && arr.Length > 0 ? ToErr(arr[0]) : -1;
        var detail = await TryDescribeRobotErrorAsync(ct);
        throw new InvalidOperationException($"관절각 조회(GetActualJointPosDegree) 실패 (errcode={err}).{detail}");
    }

    /// <summary>실제 컨트롤러 오류코드(main/sub)를 best-effort로 읽어 진단 문자열 반환(실패 시 빈 문자열).</summary>
    private async Task<string> TryDescribeRobotErrorAsync(CancellationToken ct)
    {
        try
        {
            var (m, s) = await GetRobotErrorCodeAsync(ct);
            return m == 0 && s == 0
                ? " 컨트롤러 보고 오류 없음(main=0,sub=0) — 모드/통신 상태 확인 필요."
                : $" 컨트롤러 오류 main={m}, sub={s}.";
        }
        catch { return ""; }
    }

    /// <summary>현재 활성 공구(Tool) 좌표계 번호 조회. flag 0=블로킹,1=논블로킹.
    /// 성공 시 번호, 실패(펌웨어 미지원·형식 불일치·예외) 시 null을 반환한다(예외를 던지지 않고 추적 캐시로 폴백하게 함).
    /// 일부 컨트롤러 펌웨어는 GetActualTCPNum이 기대 형식 [errcode, toolNum]으로 응답하지 않는다.</summary>
    public async Task<int?> TryGetActualToolNumAsync(int flag = 0, CancellationToken ct = default)
    {
        try
        {
            var raw = await InvokeAsync("GetActualTCPNum", p => p.GetActualTCPNum(flag), ct);
            if (raw is object[] a && a.Length >= 2 && ToErr(a[0]) == 0)
                return (int)ToDouble(a[1]);
            var err = raw is object[] arr && arr.Length > 0 ? ToErr(arr[0]) : -1;
            _logger.LogWarning("{Name} 활성 공구 번호 조회(GetActualTCPNum) 실패 (errcode={Err}) — 추적 캐시로 폴백", _settings.Name, err);
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Name} 활성 공구 번호 조회(GetActualTCPNum) 예외 — 추적 캐시로 폴백", _settings.Name);
            return null;
        }
    }

    /// <summary>정기구학: 관절각 → 베이스 기준 TCP 직교 포즈 [x,y,z,rx,ry,rz](현재 활성 공구 적용). 실패 시 예외.</summary>
    public async Task<double[]> GetForwardKinAsync(double[] jointPos, CancellationToken ct = default)
    {
        var raw = await InvokeAsync("GetForwardKin", p => p.GetForwardKin(jointPos), ct);
        if (raw is object[] a && a.Length >= 7 && ToErr(a[0]) == 0)
            return a[1..7].Select(ToDouble).ToArray();
        var err = raw is object[] arr && arr.Length > 0 ? ToErr(arr[0]) : -1;
        throw new InvalidOperationException($"정기구학(GetForwardKin) 실패 (errcode={err}). 컨트롤러 펌웨어 미지원일 수 있습니다.");
    }

    /// <summary>
    /// 베이스 좌표계 기준, 이동 공구 <paramref name="tool"/> 프레임의 현재 TCP 포즈 [x,y,z,rx,ry,rz].
    /// GetActualTCPPose는 활성 작업물 좌표계 기준이므로, 현재 관절각을 읽어 정기구학(GetForwardKin)으로
    /// BASE 기준 포즈를 구한다.
    /// ⚠ FK는 <b>현재 활성 공구</b> 프레임 기준이다. 과거에는 무변위 MoveJ로 활성 공구를 tool로 바꿔
    /// 프레임을 맞췄으나(실물에서 rc=154로 거부), 이제는 <b>모션을 전혀 보내지 않고</b> 공구 오프셋으로
    /// 클라이언트에서 재프레임한다: P_T = P_active ∘ inv(offset_active) ∘ offset_T.
    /// 이렇게 하면 포즈 조회가 로봇을 움직이지 않고 enable/자동 모드도 요구하지 않는다. 후속 이동
    /// (MoveL/MoveByOffset)이 같은 tool을 쓰므로 앵커와 이동 공구 프레임은 항상 일치한다.
    /// </summary>
    public async Task<double[]> GetTcpPoseInBaseAsync(int tool, CancellationToken ct = default)
    {
        var joints = await GetActualJointPosAsync(ct: ct);
        var pActive = await GetForwardKinAsync(joints, ct);   // BASE, 현재 활성 공구 프레임
        int active = await ResolveActiveToolAsync(ct);
        if (active == tool) return pActive;                    // 재프레임 불필요 → 공구 좌표 조회 생략.

        var offAct = await GetToolOffsetAsync(active, ct);     // 0 → identity(flange)
        var offT = await GetToolOffsetAsync(tool, ct);         // 0 → identity(flange)
        var tFlange = PoseMath.Multiply(PoseMath.FromPose(pActive),
                                        PoseMath.Inverse(PoseMath.FromPose(offAct)));
        var tT = PoseMath.Multiply(tFlange, PoseMath.FromPose(offT));
        return PoseMath.ToPose(tT);
    }

    /// <summary>공구 <paramref name="id"/>의 flange→TCP 오프셋 [x,y,z,rx,ry,rz]. 공구 0 = flange(identity).
    /// 그 외는 GetToolCoordAsync(실패 시 throw)로 읽는다.</summary>
    private async Task<double[]> GetToolOffsetAsync(int id, CancellationToken ct)
        => id == 0 ? new double[6] : await GetToolCoordAsync(id, ct);

    /// <summary>현재 활성 공구 번호를 확정한다: GetActualTCPNum 실측(read-only) 우선 → 추적 캐시
    /// <see cref="_activeTool"/> → 기본값(DefaultToolId). 실측이 되면 캐시도 갱신한다.</summary>
    private async Task<int> ResolveActiveToolAsync(CancellationToken ct)
    {
        var actual = await TryGetActualToolNumAsync(ct: ct);   // 미지원 시 null
        if (actual is int a) { _activeTool = a; return a; }
        if (_activeTool >= 0) return _activeTool;
        _logger.LogWarning("{Name} 활성 공구 미상 — 기본 공구 #{Def} 가정(실제와 다르면 앵커가 틀어질 수 있음).",
            _settings.Name, _settings.DefaultToolId);
        return _settings.DefaultToolId;
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
        var rc = await InvokeAsync("MoveL", p => ToErr(p.MoveL(args)), ct, faultRecovery: false);
        if (rc == 0) _activeTool = t;   // MoveL의 tool 인자가 컨트롤러 활성 공구를 바꾸므로 추적값 동기.
        return rc;
    }

    /// <summary>
    /// 작업물/베이스 좌표계(user) 기준으로 앵커 포즈에서 offset만큼 이동. offset_flag=1(base/작업물 좌표계 오프셋) 사용.
    /// ⚠ FAIRINO offset_flag: 0=오프셋 없음(무시), 1=base/작업물 좌표 오프셋, 2=tool 좌표 오프셋.
    /// user=0이면 BASE축, user=N이면 작업물 N축 기준으로 offset이 적용된다.
    /// anchorPose는 베이스 기준 유효 TCP 포즈(IK 계산용). offset=[dx,dy,dz,drx,dry,drz].
    /// </summary>
    public Task<int> MoveByOffsetAsync(double[] anchorPose, int user, double[] offset, int? tool = null, double? vel = null, CancellationToken ct = default)
        => MoveLAsync(anchorPose, tool: tool, user: user, vel: vel, offsetFlag: 1, offsetPos: offset, ct: ct);

    /// <summary>관절 이동(MoveJ). jointPos = 6축 각도, descPose = 대응 직교 포즈(0이면 컨트롤러가 정기구학 계산).</summary>
    public async Task<int> MoveJAsync(double[] jointPos, double[] descPose, int? tool = null, int? user = null,
                                      double? vel = null, double acc = 0, double ovl = 100, double blendT = -1,
                                      CancellationToken ct = default)
    {
        int t = tool ?? _settings.DefaultToolId;
        var rc = await InvokeAsync("MoveJ", p => ToErr(p.MoveJ(
                jointPos, descPose,
                t, user ?? _settings.DefaultUserId,
                vel ?? _settings.DefaultVelPct, acc, ovl,
                new double[4], blendT, 0, new double[6])), ct, faultRecovery: false);
        if (rc == 0) _activeTool = t;   // MoveJ의 tool 인자가 컨트롤러 활성 공구를 바꾸므로 추적값 동기.
        return rc;
    }

    // ── 점동(JOG) ──────────────────────────────────────────────────
    /// <summary>
    /// 점동(JOG) 시작. IK 없이 <paramref name="axis"/>축(1~6)을 <paramref name="dir"/>(0=음,1=양) 방향으로
    /// 직접 이동시킨다 — MoveL+IK 방식의 특이점 rc=38을 회피한다. <b>논블로킹</b>: 즉시 반환하고
    /// <see cref="StopJogAsync"/>/<see cref="ImmStopJogImmediateAsync"/> 전까지(또는 <paramref name="maxDis"/>까지)
    /// 계속 이동한다. <paramref name="maxDis"/>는 오버런 방어용 안전 상한(mm 또는 °). 모션이므로 faultRecovery=false.
    /// </summary>
    public Task<int> StartJogAsync(JogFrame frame, int axis, int dir, double maxDis, double vel,
                                   double acc = 100.0, CancellationToken ct = default)
        => InvokeAsync("StartJOG", p => ToErr(p.StartJOG(frame.StartRef(), axis, dir, maxDis, vel, acc)),
                       ct, faultRecovery: false);

    /// <summary>점동 감속 정지(버튼 떼기). <see cref="StopMotionImmediateAsync"/>와 동일하게 <see cref="_semaphore"/>를
    /// <b>우회</b>(_stopProxy)해, 진행 중 조그가 세마포어를 물고 있어도 정지가 막히지 않게 한다.</summary>
    public async Task<int> StopJogAsync(JogFrame frame, CancellationToken ct = default)
    {
        EnsureConnected();
        var proxy = _stopProxy ?? _proxy!;
        try
        {
            return await Task.Run(() => ToErr(proxy.StopJOG(frame.StopRef())), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Name} StopJOG 실패", _settings.Name);
            throw;
        }
    }

    /// <summary>점동 즉시 정지(비상). 세마포어 우회(_stopProxy)로 ImmStopJOG를 즉시 전송한다.</summary>
    public async Task<int> ImmStopJogImmediateAsync(CancellationToken ct = default)
    {
        EnsureConnected();
        var proxy = _stopProxy ?? _proxy!;
        try
        {
            return await Task.Run(() => ToErr(proxy.ImmStopJOG()), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Name} ImmStopJOG 실패", _settings.Name);
            throw;
        }
    }

    // ── 상태/제어 ──────────────────────────────────────────────────
    public Task<int> RobotEnableAsync(bool enable, CancellationToken ct = default)
        => InvokeAsync("RobotEnable", p => ToErr(p.RobotEnable(enable ? 1 : 0)), ct);

    public Task<int> ModeAsync(int mode, CancellationToken ct = default)
        => InvokeAsync("Mode", p => ToErr(p.Mode(mode)), ct);

    public Task<int> SetSpeedAsync(int velPct, CancellationToken ct = default)
        => InvokeAsync("SetSpeed", p => ToErr(p.SetSpeed(velPct)), ct);

    public Task<int> StopMotionAsync(CancellationToken ct = default)
        => InvokeAsync("StopMotion", p => ToErr(p.StopMotion()), ct, faultRecovery: false);

    /// <summary>컨트롤러의 걸린 오류 상태를 해제(복구). errcode 14("interface execution failed" — fault 상태)에서
    /// 복구하려면 이 호출이 필요하다. 반환 rc(0=성공).</summary>
    public Task<int> ResetAllErrorAsync(CancellationToken ct = default)
        => InvokeAsync("ResetAllError", p => ToErr(p.ResetAllError()), ct, faultRecovery: false);

    /// <summary>현재 로봇 오류 코드 [main, sub] 조회. 오류 없으면 (0,0). 호출 자체 실패 시 예외.</summary>
    public async Task<(int Main, int Sub)> GetRobotErrorCodeAsync(CancellationToken ct = default)
    {
        var raw = await InvokeAsync("GetRobotErrorCode", p => p.GetRobotErrorCode(), ct);
        if (raw is object[] a && a.Length >= 3 && ToErr(a[0]) == 0)
            return ((int)ToDouble(a[1]), (int)ToDouble(a[2]));
        var err = raw is object[] arr && arr.Length > 0 ? ToErr(arr[0]) : -1;
        throw new InvalidOperationException($"로봇 오류 코드 조회(GetRobotErrorCode) 실패 (errcode={err}).");
    }

    /// <summary>
    /// 긴급 정지. 직렬화 세마포어를 <b>우회</b>하여 전용 프록시(_stopProxy)로 StopMotion 을 즉시 전송한다.
    /// 진행 중인 블로킹 이동(MoveL 등)이 _proxy 와 세마포어를 점유한 상태에서도 가로채 멈출 수 있다.
    /// </summary>
    public async Task<int> StopMotionImmediateAsync(CancellationToken ct = default)
    {
        EnsureConnected();
        var proxy = _stopProxy ?? _proxy!;   // 폴백: 정지 전용 프록시가 없으면 일반 프록시 사용.
        try
        {
            // _semaphore 를 잡지 않는다 → in-flight 이동과 동시에 별도 연결로 전송.
            return await Task.Run(() => ToErr(proxy.StopMotion()), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Name} StopMotion(즉시) 실패", _settings.Name);
            throw;
        }
    }

    /// <summary>현재 TCP 포즈 [x,y,z,rx,ry,rz] 조회 (errcode 헤더 제거 후 반환).
    /// ⚠ 활성 작업물(user) 좌표계 기준. BASE 기준이 필요하면 <see cref="GetTcpPoseInBaseAsync"/>를 쓸 것.
    /// flag 0=블로킹,1=논블로킹.</summary>
    public Task<double[]> GetTcpPoseAsync(int flag = 0, CancellationToken ct = default)
        => InvokeAsync("GetActualTCPPose", p => ToPose(p.GetActualTCPPose(flag)), ct);

    // ── 공통 호출 래퍼 ─────────────────────────────────────────────
    /// <summary>
    /// 모든 RPC의 단일 통로. <paramref name="faultRecovery"/>가 true(기본)이고 반환 rc가 14
    /// ("interface execution failed")면, 같은 연결로 ResetAllError를 1회 보낸 뒤 300ms 대기하고
    /// 원호출을 1회 재시도한다(일시적/해제 가능한 fault 자동 복구). 모션 명령은 fault 해제 후 조용히
    /// 재실행되면 위험하므로 호출부에서 <paramref name="faultRecovery"/>=false로 비활성한다.
    /// </summary>
    private async Task<T> InvokeAsync<T>(string op, Func<IFairinoRpc, T> call, CancellationToken ct, bool faultRecovery = true)
    {
        EnsureConnected();
        await _semaphore.WaitAsync(ct);
        try
        {
            _logger.LogDebug("{Name} {Op} 호출", _settings.Name, op);
            // XML-RPC 프록시 호출은 동기(blocking HTTP)이므로 스레드풀로 오프로드.
            var proxy = _proxy!;
            var result = await Task.Run(() => call(proxy), ct);
            if (faultRecovery && TryGetErr(result) == 14)
            {
                _logger.LogWarning("{Name} {Op} errcode=14(interface execution failed) — ResetAllError 후 1회 재시도", _settings.Name, op);
                // 세마포어를 쥔 채 프록시로 직접 호출(ResetAllErrorAsync 경유 시 세마포어 재진입 데드락 회피).
                await Task.Run(() => { try { proxy.ResetAllError(); } catch { /* 진단 무관 — 재시도로 판정 */ } }, ct);
                await Task.Delay(300, ct);
                result = await Task.Run(() => call(proxy), ct);
            }
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

    /// <summary>예외를 던지지 않는 errcode 추출(unknown 타입은 null). rc=14 자동복구 판정용.</summary>
    private static int? TryGetErr(object? r) => r switch
    {
        int i => i,
        long l => (int)l,
        double d => (int)d,
        object[] a when a.Length > 0 => TryGetErr(a[0]),
        _ => null,
    };

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

    /// <summary>TCP 포즈 추출. [errcode,x,y,..] / [errcode,x,y,..] 모두 처리.</summary>
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
        _stopProxy = null;
        _semaphore.Dispose();
    }
}
