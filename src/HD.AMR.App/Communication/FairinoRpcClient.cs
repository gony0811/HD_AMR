using System.Net.Sockets;
using Horizon.XmlRpc.Client;
using Horizon.XmlRpc.Core;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Communication;

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

    // 컨트롤러의 현재 활성 작업물(User) 좌표계 번호 추적값. MoveL/MoveJ의 user 인자가 활성 프레임을
    // 바꾸므로(세터 RPC 부재 → per-move user 가 유일한 수단) 성공 시 갱신한다.
    // GetActualWObjNum 실측을 못 읽는 펌웨어에서 활성 프레임의 폴백 소스로 쓰인다(ResolveActiveUserAsync).
    // 이 펌웨어의 GetForwardKin 은 '활성 작업물 프레임' 기준 pose 를 주므로, 이 값으로 베이스 변환
    // (GetForwardKinInBaseAsync)에 쓴다. -1 = 미상(연결 직후). 연결/해제 시 재설정된다.
    private int _activeUser = -1;

    // 20004 상태 스트림에서 컨트롤러의 실시간 활성 좌표계(tool,user)를 읽어오는 제공자(느슨한 결합 —
    // 상태 클라이언트를 직접 참조하지 않는다). CobotService 가 _state.Latest 로 연결한다.
    // 반환 null = 미수신/스테일/소켓단절/범위밖 → ResolveActive*Async 가 기존 추적 캐시 폴백을 그대로 탄다.
    // 이동은 블로킹이라 rc=0 반환 시점엔 컨트롤러가 이미 새 활성 프레임을 반영·완료한 상태이므로,
    // 다음 앵커 계산 시 라이브 스트림은 안정된 최신 프레임을 준다(자기 명령과의 레이스 없음).
    private Func<(int tool, int user)?>? _liveActiveFrameProvider;

    /// <summary>20004 상태 스트림의 실시간 활성 좌표계 제공자를 등록한다(CobotService 배선용).
    /// <see cref="ResolveActiveUserAsync"/>/<see cref="ResolveActiveToolAsync"/> 가 RPC 실측 다음,
    /// 추적 캐시보다 <b>우선</b>하여 이 값을 쓴다.</summary>
    public void SetLiveActiveFrameProvider(Func<(int tool, int user)?> provider)
        => _liveActiveFrameProvider = provider;

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
            _activeUser = -1;   // 새 연결: 활성 작업물 프레임 추적값 초기화.
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
        _activeUser = -1;
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

    /// <summary>조그-캡처(SetWObjCoordPoint ×3 선행)한 점들로 좌표계 계산 후 id에 등록. 등록된 pose 반환.
    /// ⚠ 컨트롤러의 3점 버퍼는 각 점을 <b>기록 시점의 활성 작업물 프레임</b> 기준으로 저장한다(이 펌웨어의
    /// GetActualTCPPose 와 동일). 점 사이에 활성 프레임이 바뀌면(예: 작업물 원점 이동 후 점1 캡처 → 베이스 조그
    /// → 점2·3) 점1이 (0,0,0)으로 기록돼 원점=베이스 원점인 엉뚱한 프레임이 등록되고 원점 이동이 112 를 낸다.
    /// UI 는 베이스 기준 포즈를 직접 모아 <see cref="RegisterWObjFromPointsAsync"/> 를 쓴다.</summary>
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
            $"역기구학(GetInverseKin) 실패 (errcode={err}){FairinoErrorCodes.Suffix(err)}. 목표 자세가 작업영역 밖이거나 도달 불가일 수 있습니다. " +
            $"IK 입력 pose(활성 프레임 기준)=[{string.Join(", ", descPose.Select(x => x.ToString("0.#")))}]");
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

    /// <summary>정기구학 결과를 <b>베이스 좌표계</b> 기준 pose 로 반환한다.
    /// ⚠ 이 펌웨어의 <see cref="GetForwardKinAsync"/> 은 베이스가 아니라 '현재 활성 작업물 프레임'
    /// 기준 pose 를 준다(실물 확인: 활성 user=5 에서 원점 근처 값 반환). 활성 프레임 N&gt;0 이면 등록된
    /// 좌표계 T_N(베이스 기준)으로 되돌린다: P_base = T_N · P_N. 활성 프레임 0(베이스)이면 그대로 반환.</summary>
    /// <param name="strict">활성 프레임 미상일 때 0 을 가정하지 않고 예외(모션 앵커 기본값).</param>
    private async Task<double[]> GetForwardKinInBaseAsync(double[] jointPos, CancellationToken ct = default,
                                                          bool strict = true)
    {
        var pFk = await GetForwardKinAsync(jointPos, ct);      // 활성 작업물 프레임 기준
        int n = await ResolveActiveUserAsync(ct, strict);
        if (n <= 0)
        {
            _logger.LogInformation("{Name} FK→베이스: 활성 작업물 #{N}(≤0) → 변환 없음. fk=[{Fk}]",
                _settings.Name, n, string.Join(",", pFk.Select(x => x.ToString("0.##"))));
            return pFk;                                        // 이미 베이스(또는 활성 프레임 미상)
        }
        var tN = await GetWObjCoordAsync(n, ct);               // 베이스 기준 좌표계 pose
        var pBase = FrameMath.FromFrame(pFk, tN);              // = T_N · pFk → 베이스
        _logger.LogInformation("{Name} FK→베이스: 활성 작업물 #{N}, T=[{T}], fk=[{Fk}] → base=[{B}]",
            _settings.Name, n,
            string.Join(",", tN.Select(x => x.ToString("0.##"))),
            string.Join(",", pFk.Select(x => x.ToString("0.##"))),
            string.Join(",", pBase.Select(x => x.ToString("0.##"))));
        return pBase;
    }

    /// <summary>
    /// 베이스 좌표계 기준, 이동 공구 <paramref name="tool"/> 프레임의 현재 TCP 포즈 [x,y,z,rx,ry,rz].
    /// GetActualTCPPose는 활성 작업물 좌표계 기준이므로, 현재 관절각을 읽어 정기구학으로 BASE 기준 포즈를 구한다.
    /// ⚠ 이 펌웨어의 GetForwardKin 은 베이스가 아니라 '현재 활성 작업물 프레임' 기준이므로,
    /// <see cref="GetForwardKinInBaseAsync"/> 로 활성 프레임 N&gt;0 이면 T_N 으로 베이스로 변환한다
    /// (변환 없이 쓰면 활성 user≠0 에서 앵커가 원점 근처로 오염됨 → MoveL rc=38).
    /// ⚠ FK는 <b>현재 활성 공구</b> 프레임 기준이다. 과거에는 무변위 MoveJ로 활성 공구를 tool로 바꿔
    /// 프레임을 맞췄으나(실물에서 rc=154로 거부), 이제는 <b>모션을 전혀 보내지 않고</b> 공구 오프셋으로
    /// 클라이언트에서 재프레임한다: P_T = P_active ∘ inv(offset_active) ∘ offset_T.
    /// 이렇게 하면 포즈 조회가 로봇을 움직이지 않고 enable/자동 모드도 요구하지 않는다. 후속 이동
    /// (MoveL/MoveByOffset)이 같은 tool을 쓰므로 앵커와 이동 공구 프레임은 항상 일치한다.
    /// </summary>
    public async Task<double[]> GetTcpPoseInBaseAsync(int tool, CancellationToken ct = default)
    {
        var joints = await GetActualJointPosAsync(ct: ct);
        var pActive = await GetForwardKinInBaseAsync(joints, ct);   // BASE, 현재 활성 공구 프레임(활성 작업물 프레임 → 베이스 변환)
        int active = await ResolveActiveToolAsync(ct, strict: true);
        return await ReframeToolAsync(pActive, active, tool, ct);
    }

    /// <summary>공구 <paramref name="fromTool"/> TCP 기준 pose 를 공구 <paramref name="toTool"/> TCP
    /// 기준으로 재프레임한다(모션 없음): P_to = P_from ∘ inv(offset_from) ∘ offset_to.
    /// 같은 공구면 그대로 반환(공구 좌표 조회 생략). 공구 좌표를 못 읽으면 예외.
    /// 앵커 조회(활성 공구→이동 공구)와 역기구학 입력(이동 공구→활성 공구)이 <b>같은 식을 서로 반대
    /// 방향으로</b> 쓴다 — 한쪽만 적용하면 앵커와 IK 가 다른 TCP 를 가리켜 rc=112(도달 불가)가 난다.</summary>
    private async Task<double[]> ReframeToolAsync(double[] pose, int fromTool, int toTool, CancellationToken ct)
    {
        if (fromTool == toTool) return pose;                   // 재프레임 불필요 → 공구 좌표 조회 생략.

        var offFrom = await GetToolOffsetAsync(fromTool, ct);  // 0 → identity(flange)
        var offTo = await GetToolOffsetAsync(toTool, ct);      // 0 → identity(flange)
        return PoseMath.ReframeTool(pose, offFrom, offTo);
    }

    /// <summary>공구 <paramref name="id"/>의 flange→TCP 오프셋 [x,y,z,rx,ry,rz]. 공구 0 = flange(identity).
    /// 그 외는 GetToolCoordAsync(실패 시 throw)로 읽는다.</summary>
    private async Task<double[]> GetToolOffsetAsync(int id, CancellationToken ct)
        => id == 0 ? new double[6] : await GetToolCoordAsync(id, ct);

    /// <summary>현재 활성 공구 번호를 확정한다: GetActualTCPNum 실측(read-only) → 라이브 20004 스트림 →
    /// 추적 캐시 <see cref="_activeTool"/> → 기본값(DefaultToolId). 실측/라이브가 되면 캐시도 갱신한다.
    /// 라이브를 캐시보다 우선해, 펜던트 등 외부에서 활성 공구가 바뀌어도 앵커 계산이 실제 프레임을 따른다.</summary>
    /// <param name="strict">true 면 근거(실측·라이브·추적 캐시)가 하나도 없을 때 기본값을 가정하지 않고
    /// 예외를 던진다. 모션에 쓰이는 앵커·IK 경로는 항상 strict — 틀린 가정은 엉뚱한 위치 이동으로 이어진다.</param>
    private async Task<int> ResolveActiveToolAsync(CancellationToken ct, bool strict = false)
    {
        var actual = await TryGetActualToolNumAsync(ct: ct);   // 미지원 시 null
        if (actual is int a) { _activeTool = a; return a; }
        // 라이브 20004 스트림(신선/범위검증 통과 시)을 캐시보다 우선. 범위는 SeedActiveFrames 의 tool 0~15 와 동일.
        var live = _liveActiveFrameProvider?.Invoke();
        if (live is { tool: var lt } && lt is >= 0 and <= 15) { _activeTool = lt; return lt; }
        if (_activeTool >= 0) return _activeTool;
        if (strict) throw new InvalidOperationException(UnknownFrameMessage("공구"));
        _logger.LogWarning("{Name} 활성 공구 미상 — 기본 공구 #{Def} 가정(실제와 다르면 앵커가 틀어질 수 있음).",
            _settings.Name, _settings.DefaultToolId);
        return _settings.DefaultToolId;
    }

    /// <summary>활성 좌표계 미상 시의 안내 메시지 — 사용자가 취할 조치를 담는다.</summary>
    private static string UnknownFrameMessage(string what)
        => $"활성 {what} 좌표계 번호를 확인할 수 없습니다(상태 패킷 미수신/단절, 펌웨어 실측 조회 미지원). " +
           "틀린 값으로 이동하면 엉뚱한 위치로 가므로 명령을 중단했습니다. " +
           "상태 소켓 연결을 확인하거나, 조그 리본의 '활성 프레임 알려주기'로 컨트롤러의 현재 공구·작업물 번호를 입력한 뒤 다시 시도하세요.";

    /// <summary>현재 활성 작업물(User) 프레임 번호를 확정한다: GetActualWObjNum 실측(read-only) →
    /// 라이브 20004 스트림 → 추적 캐시 <see cref="_activeUser"/> → 기본값(DefaultUserId, 통상 0=베이스).
    /// 실측/라이브가 되면 캐시도 갱신한다. 라이브를 캐시보다 우선해, 펜던트 등 외부에서 활성 프레임이
    /// 바뀌어도 FK→베이스 변환이 실제 프레임을 따라 좌표계 불일치(rc=154/38)를 막는다.
    /// ⚠ 라이브 미수신(콜드스타트/소켓단절)이고 GetActualWObjNum 미지원이면 캐시→0 가정으로 폴백한다.</summary>
    /// <param name="strict">true 면 근거가 하나도 없을 때 0 을 가정하지 않고 예외를 던진다(모션 경로 기본).</param>
    private async Task<int> ResolveActiveUserAsync(CancellationToken ct, bool strict = false)
    {
        var actual = await GetActualWObjNumAsync(ct: ct);   // 미지원 시 null
        if (actual is int u) { _activeUser = u; return u; }
        // 라이브 20004 스트림(신선/범위검증 통과 시)을 캐시보다 우선. 범위는 SeedActiveFrames 의 user 0~14 와 동일.
        var live = _liveActiveFrameProvider?.Invoke();
        if (live is { user: var lu } && lu is >= 0 and <= 14) { _activeUser = lu; return lu; }
        if (_activeUser >= 0) return _activeUser;
        if (strict) throw new InvalidOperationException(UnknownFrameMessage("작업물"));
        _logger.LogWarning("{Name} 활성 작업물 프레임 미상 — 기본 #{Def} 가정(실제와 다르면 베이스 변환이 틀어질 수 있음).",
            _settings.Name, _settings.DefaultUserId);
        return _settings.DefaultUserId;
    }

    /// <summary>현재 활성 (공구, 작업물) 번호를 한 번에 확정한다. UI 가 연속 조그 기준을 맞출 때 쓴다.
    /// <paramref name="strict"/> 면 근거가 없을 때 가정 대신 예외.</summary>
    public async Task<(int tool, int user)> ResolveActiveFramesAsync(CancellationToken ct = default, bool strict = true)
        => (await ResolveActiveToolAsync(ct, strict), await ResolveActiveUserAsync(ct, strict));

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

        double[] j = jointPos ?? await GetInverseKinForMoveAsync(descPose, t, u, ct);

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
        if (rc != 0)
            _logger.LogWarning("{Name} MoveL rc={Rc}{Desc} (tool={T}, user={U}, pose=[{P}], joints=[{J}], offsetFlag={F}, offset=[{O}])",
                _settings.Name, rc, FairinoErrorCodes.Suffix(rc), t, u,
                string.Join(",", descPose.Select(x => x.ToString("0.##"))),
                string.Join(",", j.Select(x => x.ToString("0.##"))),
                offsetFlag, string.Join(",", off.Select(x => x.ToString("0.##"))));
        if (rc == 0) { _activeTool = t; _activeUser = u; }   // tool/user 인자가 컨트롤러 활성 프레임을 바꾸므로 추적값 동기.
        return rc;
    }

    /// <summary>
    /// 이동 목표 프레임(<paramref name="tool"/>/<paramref name="user"/>)에 맞춘 역기구학 관절 시드.
    /// <c>GetInverseKin</c> 은 <b>tool·user 인자가 없어</b> 입력 pose 를 컨트롤러의 <b>현재 활성
    /// 공구·작업물</b> 기준으로 해석한다. 그래서 이동에 쓸 프레임 기준 pose 를 활성 프레임 기준으로
    /// 되돌려 넘긴다. 두 축을 모두 보정해야 한다 — 한쪽만 맞추면 IK 가 엉뚱한 TCP/원점을 요구받는다.
    ///
    /// <b>공구</b>: P_active = P_tool ∘ inv(off_tool) ∘ off_active
    /// (<see cref="GetTcpPoseInBaseAsync"/> 의 앵커 재프레임과 정확히 반대 방향). 보정이 없으면 예컨대
    /// 앵커는 tool #0(플랜지) 기준인데 활성 공구가 #1(TCP 수백 mm)이라, 이미 작업영역 테두리에 있는
    /// 자세가 도달 불가로 판정돼 <b>errcode=112</b> 가 난다. MoveL 의 tool 인자가 성공 시 활성 공구를
    /// 바꾸므로 "첫 명령만 실패/오이동하고 이후 정상"으로 나타난다(docs/bugfix_jog_first_move.md).
    ///
    /// <b>작업물</b>: P_M = T_M⁻¹ · T_user · P_user (T_0 = identity). 이 펌웨어의 GetInverseKin 은 입력을
    /// 현재 활성 작업물 프레임 기준으로 해석한다(GetForwardKin 과 대칭 — WObjResetStep 의 112 순환에서 확인).
    /// 변환 없이 부르면 활성=0(베이스) 상태에서 작업물 좌표(원점 근처 값)를 베이스로 오해석해 rc=38·112 가 난다.
    /// </summary>
    public async Task<double[]> GetInverseKinForMoveAsync(double[] descPose, int tool, int user,
                                                          CancellationToken ct = default)
    {
        // ① 공구 축 — 본문(body) 변환이라 ②의 좌변 변환과 교환 가능하므로 순서는 무관하다.
        int activeTool = await ResolveActiveToolAsync(ct, strict: true);
        var pose = await ReframeToolAsync(descPose, tool, activeTool, ct);
        if (activeTool != tool)
            _logger.LogInformation("{Name} IK 공구 보정: 이동 공구 #{T}, 활성 공구 #{A} — pose=[{P}] → [{Q}]",
                _settings.Name, tool, activeTool,
                string.Join(",", descPose.Select(x => x.ToString("0.##"))),
                string.Join(",", pose.Select(x => x.ToString("0.##"))));

        // ② 작업물 축
        int activeUser = await ResolveActiveUserAsync(ct, strict: true);
        if (activeUser == user)
            return await GetInverseKinAsync(pose, ct: ct);

        double[] pBase = pose;
        if (user > 0)
        {
            var tUser = await GetWObjCoordAsync(user, ct);
            if (tUser.All(v => v == 0))
                _logger.LogWarning("{Name} IK 프레임 보정: 목표 작업물 #{U} 가 미등록(원점=0) — pose 를 베이스로 간주. " +
                                   "좌표계 등록을 먼저 확인하세요.", _settings.Name, user);
            pBase = FrameMath.FromFrame(pose, tUser);
        }
        var pActive = activeUser > 0 ? FrameMath.ToFrame(pBase, await GetWObjCoordAsync(activeUser, ct)) : pBase;
        _logger.LogInformation("{Name} IK 프레임 보정: 목표 user=#{U}, 활성=#{M} — pose=[{P}] → IK 입력=[{Q}]",
            _settings.Name, user, activeUser,
            string.Join(",", pose.Select(x => x.ToString("0.##"))),
            string.Join(",", pActive.Select(x => x.ToString("0.##"))));
        return await GetInverseKinAsync(pActive, ct: ct);
    }

    /// <summary>
    /// 작업물/베이스 좌표계(user) 기준으로 앵커 포즈에서 offset만큼 이동. offset_flag=1(base/작업물 좌표계 오프셋) 사용.
    /// ⚠ FAIRINO offset_flag: 0=오프셋 없음(무시), 1=base/작업물 좌표 오프셋, 2=tool 좌표 오프셋.
    /// user=0이면 BASE축, user=N이면 작업물 N축 기준으로 offset이 적용된다.
    /// anchorPose는 베이스 기준 유효 TCP 포즈(IK 계산용). offset=[dx,dy,dz,drx,dry,drz].
    /// </summary>
    public Task<int> MoveByOffsetAsync(double[] anchorPose, int user, double[] offset, int? tool = null, double? vel = null, CancellationToken ct = default)
        => MoveLAsync(anchorPose, tool: tool, user: user, vel: vel, offsetFlag: 1, offsetPos: offset, ct: ct);

    /// <summary>
    /// tool 좌표계 기준으로 앵커 포즈에서 offset만큼 이동. offset_flag=2(tool 좌표 오프셋) 사용.
    /// 회전 오프셋이 tool 프레임 축으로 적용되므로, 툴 프레임에서 측정한 자세 보정에 쓴다.
    /// anchorPose는 베이스 기준 유효 TCP 포즈(IK 계산용). offset=[dx,dy,dz,drx,dry,drz].
    /// </summary>
    public Task<int> MoveByToolOffsetAsync(double[] anchorPose, int user, double[] offset, int? tool = null, double? vel = null, CancellationToken ct = default)
        => MoveLAsync(anchorPose, tool: tool, user: user, vel: vel, offsetFlag: 2, offsetPos: offset, ct: ct);

    /// <summary>
    /// <see cref="MoveByToolOffsetAsync"/> 의 <b>관절 이동(MoveJ) 판</b>. 긴 이동에서 손목 특이점을
    /// 지나지 않으려면 직선 이동이 아니라 관절 이동이어야 한다(관절 보간은 각 축이 두 끝값 사이에서
    /// 단조로 변하므로 중간에 J5=0 을 쓸고 지나가지 않는다).
    ///
    /// ⚠ MoveL 과 달리 오프셋을 컨트롤러에 맡기지 않고 <b>여기서 최종 pose 를 만든다</b> — MoveJ 의
    /// joint_pos 는 '최종' 자세여야 하는데, offset_flag 로 넘기면 컨트롤러가 pose 에만 오프셋을 적용해
    /// 관절각과 어긋나기 때문이다. 툴 좌표 오프셋이므로 최종 pose = 앵커 ∘ offset (앵커 자신의 축 기준).
    /// </summary>
    public async Task<int> MoveJByToolOffsetAsync(double[] anchorPose, int user, double[] offset,
                                                  int? tool = null, double? vel = null, CancellationToken ct = default)
    {
        int t = tool ?? _settings.DefaultToolId;
        var target = (offset is { Length: >= 6 } && offset.Any(v => v != 0.0))
            ? FrameMath.FromFrame(offset, anchorPose)
            : anchorPose;

        var joints = await GetInverseKinForMoveAsync(target, t, user, ct);
        return await MoveJAsync(joints, target, tool: t, user: user, vel: vel, ct: ct);
    }

    /// <summary>관절 이동(MoveJ). jointPos = 6축 각도, descPose = 대응 직교 포즈.
    /// ⚠ 실물 펌웨어는 desc_pos=0(전부 0)을 rc=154(관절 지령점 오류)로 거부한다 — joint_pos 에 대응하는
    /// 유효 pose 가 필요하므로, 0 배열/미제공이면 정기구학(GetForwardKin)으로 채워 보낸다.</summary>
    /// <param name="allowUnknownFrames">true 면 활성 좌표계 미상일 때도 기본값 가정으로 진행한다.
    /// <see cref="ResetActiveFrameAsync"/> 전용 — 프레임을 '알려진 값으로 만드는' 복구 경로가 strict 검사에
    /// 스스로 막히면 사용자가 빠져나올 수단이 없어진다. 현재 관절각을 그대로 지령하는 무변위 호출에만 쓴다.</param>
    public async Task<int> MoveJAsync(double[] jointPos, double[] descPose, int? tool = null, int? user = null,
                                      double? vel = null, double acc = 0, double ovl = 100, double blendT = -1,
                                      CancellationToken ct = default, bool allowUnknownFrames = false)
    {
        int t = tool ?? _settings.DefaultToolId;
        int u = user ?? _settings.DefaultUserId;
        bool fkFill = descPose is null || descPose.All(v => v == 0.0);
        int activeTool = -1;
        double[]? descActive = null;   // FK-채움 시 '활성 공구' 기준 desc(재프레임 실패/거부 폴백용)
        if (fkFill)
        {
            // descPose 를 정기구학으로 채운다. 이 펌웨어의 GetForwardKin 은 '현재 활성 작업물 프레임'
            // 기준 pose 를 주므로, GetForwardKinInBaseAsync 로 베이스 pose 를 만들어 desc_pos↔user
            // 프레임을 일치시킨다(불일치 시 rc=154). FK 는 '현재 활성 공구' 기준이므로, 요청 공구 t 가
            // 활성 공구와 다르면 공구 오프셋으로 재프레임해 t 기준 desc 를 만든다 — 그래야 MoveJ 의
            // tool 인자가 활성 공구로 덮이지 않고, 관절 조그/프레임 반납이 요청 공구(통상 1)를 유지·복원한다.
            // (예전에는 t 를 활성 공구로 강제해, 활성 공구가 0 으로 바뀐 뒤엔 반납이 0 을 그대로 남겼다.)
            activeTool = await ResolveActiveToolAsync(ct, strict: !allowUnknownFrames);
            descActive = await GetForwardKinInBaseAsync(jointPos, ct, strict: !allowUnknownFrames);
            try
            {
                descPose = await ReframeToolAsync(descActive, activeTool, t, ct);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "{Name} MoveJ: 공구 #{T} 재프레임 실패 — 활성 공구 #{A} 로 대체(공구 미복원)",
                    _settings.Name, t, activeTool);
                t = activeTool;
                descPose = descActive;
            }
            if (u > 0)
                descPose = FrameMath.ToFrame(descPose, await GetWObjCoordAsync(u, ct));   // desc 는 user 프레임 기준
        }
        var rc = await MoveJRawAsync(jointPos, descPose!, t, u, vel, acc, ovl, blendT, ct);
        if (rc != 0 && fkFill && descActive is not null && t != activeTool)
        {
            // 펌웨어가 tool≠활성 공구의 재프레임 desc 를 거부(rc=154 등)하면 활성 공구 기준으로 1회 재시도 —
            // 관절 이동 자체는 성공시키되 공구는 복원되지 않으므로 경고를 남긴다.
            _logger.LogWarning("{Name} MoveJ rc={Rc} (tool=#{T} 재프레임) — 활성 공구 #{A} 기준으로 재시도(공구 미복원)",
                _settings.Name, rc, t, activeTool);
            t = activeTool;
            var descRetry = u > 0 ? FrameMath.ToFrame(descActive, await GetWObjCoordAsync(u, ct)) : descActive;
            rc = await MoveJRawAsync(jointPos, descRetry, t, u, vel, acc, ovl, blendT, ct);
        }
        if (rc == 0) { _activeTool = t; _activeUser = u; }   // tool/user 인자가 컨트롤러 활성 프레임을 바꾸므로 추적값 동기.
        return rc;
    }

    private async Task<int> MoveJRawAsync(double[] jointPos, double[] descPose, int t, int u,
                                          double? vel, double acc, double ovl, double blendT, CancellationToken ct)
    {
        var rc = await InvokeAsync("MoveJ", p => ToErr(p.MoveJ(
                jointPos, descPose,
                t, u,
                vel ?? _settings.DefaultVelPct, acc, ovl,
                new double[4], blendT, 0, new double[6])), ct, faultRecovery: false);
        if (rc != 0)
            _logger.LogWarning("{Name} MoveJ rc={Rc} (tool={T}, user={U}, j=[{J}], desc=[{D}])",
                _settings.Name, rc, t, u,
                string.Join(",", jointPos.Select(x => x.ToString("0.##"))),
                string.Join(",", descPose.Select(x => x.ToString("0.##"))));
        return rc;
    }

    /// <summary>현재 활성 작업물(User) 좌표계 번호. flag 0=블로킹,1=논블로킹. 미지원 펌웨어면 null.
    /// GetActualTCPNum 처럼 이 펌웨어는 미지원일 수 있어(errcode=-1) 예외/형식불일치를 null로 흡수하고
    /// 실패 사유를 로그로 남긴다(추적 캐시 _activeUser 로 폴백하게 함).</summary>
    public async Task<int?> GetActualWObjNumAsync(int flag = 1, CancellationToken ct = default)
    {
        try
        {
            var raw = await InvokeAsync("GetActualWObjNum", p => p.GetActualWObjNum(flag), ct);
            if (raw is object[] a && a.Length >= 2 && ToErr(a[0]) == 0) return (int)ToDouble(a[1]);
            var err = raw is object[] arr && arr.Length > 0 ? ToErr(arr[0]) : -1;
            _logger.LogWarning("{Name} 활성 작업물 번호 조회(GetActualWObjNum) 실패 (errcode={Err}) — 추적 캐시로 폴백", _settings.Name, err);
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Name} 활성 작업물 번호 조회(GetActualWObjNum) 예외 — 추적 캐시로 폴백", _settings.Name);
            return null;
        }
    }

    /// <summary>컨트롤러의 현재 활성 작업물(User) 프레임 번호를 클라이언트 추적값에 <b>수동으로</b> 알린다.
    /// 이 펌웨어는 활성 프레임 실측 조회(GetActualWObjNum)가 미지원이라, 앱 재시작 후 컨트롤러가 0이 아닌
    /// 프레임에 있을 때(콜드스타트) 사용자가 알려주는 유일한 경로다. 이후 <see cref="GetForwardKinInBaseAsync"/>
    /// 가 이 값으로 FK pose 를 베이스로 변환해 조그/초기화의 rc=154(프레임 불일치)를 막는다.</summary>
    public void SetAssumedActiveUser(int n)
    {
        _activeUser = n;
        _logger.LogInformation("{Name} 활성 작업물 프레임 추적값 수동 설정 → #{N}", _settings.Name, n);
    }

    /// <summary>컨트롤러의 현재 활성 <b>공구</b> 번호를 클라이언트 추적값에 수동으로 알린다.
    /// <see cref="SetAssumedActiveUser"/> 와 같은 목적 — 앵커 재프레임(<see cref="GetTcpPoseInBaseAsync"/>)이
    /// 활성 공구를 알아야 공구 오프셋만큼의 어긋남을 막을 수 있고, strict 해석의 마지막 근거가 된다.</summary>
    public void SetAssumedActiveTool(int n)
    {
        _activeTool = n;
        _logger.LogInformation("{Name} 활성 공구 추적값 수동 설정 → #{N}", _settings.Name, n);
    }

    /// <summary>시작(연결) 시 20004 상태 패킷에서 읽은 활성 공구/작업물 번호로 추적값을 시딩한다
    /// (RPC 실측 조회 GetActualTCPNum/WObjNum 이 이 펌웨어에서 errcode=-1/미지원이므로 상태 패킷이 유일한 자동 소스).
    /// ⚠ 상태 패킷 오프셋이 펌웨어 버전차로 어긋나면 garbage 값이 올 수 있어 <b>범위 검증</b> 후에만 반영한다
    /// (tool 0~15, user 0~14). 범위 밖이면 무시하고 기존 폴백(0 가정 + 수동 알려주기)을 유지한다.</summary>
    public void SeedActiveFrames(int tool, int user)
    {
        bool okT = tool is >= 0 and <= 15;
        bool okU = user is >= 0 and <= 14;
        if (okT) _activeTool = tool;
        if (okU) _activeUser = user;
        _logger.LogInformation("{Name} 시작 시 활성 좌표계 시딩(상태 패킷): tool=#{T}{Tok}, user=#{U}{Uok}",
            _settings.Name, tool, okT ? "" : "(범위밖·무시)", user, okU ? "" : "(범위밖·무시)");
    }

    /// <summary>
    /// MoveJ 로 컨트롤러의 활성 tool/user 좌표계를 재설정한다(활성 프레임 전환 세터 RPC가 없어 이동
    /// 명령의 tool/user 파라미터가 유일한 수단). MoveJ 는 IK를 쓰지 않아 활성 프레임이 어긋나 있어도 성공한다.
    /// 현재 관절각을 그대로 지령하므로 실질 무변위. descPose 는 MoveJAsync 가 정기구학으로 채우되
    /// 요청 공구 <paramref name="tool"/> 기준으로 재프레임하므로 <b>공구도 함께 복원</b>된다
    /// (작업물 반납이 활성 공구를 0 등 엉뚱한 값으로 남기지 않는다).
    /// 이미 원하는 user·tool 이면(실측 조회 가능 시) 건너뛴다.
    /// </summary>
    public async Task<int> ResetActiveFrameAsync(int tool, int user, CancellationToken ct = default)
    {
        var curUser = await GetActualWObjNumAsync(ct: ct);   // 미지원이면 null → 무조건 재설정
        var curTool = await TryGetActualToolNumAsync(ct: ct);
        if (curUser == user && curTool == tool) return 0;

        var joints = await GetActualJointPosAsync(ct: ct);
        // 무변위(현재 관절각 지령) + 프레임 복구 경로이므로 활성 좌표계 미상이어도 진행한다.
        return await MoveJAsync(joints, new double[6], tool: tool, user: user, vel: 5, ct: ct,
                                allowUnknownFrames: true);
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
        var dx = Sub(xAxisPt, o);
        var v = Sub(planePt, o);
        RequireSeparation(dx, "점1(원점)-점2(X축)");
        RequireSeparation(v, "점1(원점)-점3(평면/Z)");
        var x = Normalize(dx, "X축");

        double[] y, z;
        if (method == 0) // 원점-X축-Z축
        {
            var zApprox = Normalize(v, "Z축");
            RequireNotCollinear(x, zApprox);
            y = Normalize(Cross(zApprox, x), "Y축(X×Z 직교)");
            z = Cross(x, y);
        }
        else // 원점-X축-XY평면
        {
            RequireNotCollinear(x, Normalize(v, "평면점"));
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

    /// <summary>3점법 점 사이 최소 거리(mm). 이보다 가까우면 잡음이 축 방향을 지배한다.</summary>
    public const double MinPointSeparationMm = 1.0;

    /// <summary>3점법 X축과 점3 방향 사이 최소 각도(°). 이보다 작으면 세 점이 사실상 일직선이다.</summary>
    public const double MinAxisAngleDeg = 1.0;

    private static void RequireSeparation(double[] d, string what)
    {
        double m = Math.Sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]);
        if (m < MinPointSeparationMm)
            throw new InvalidOperationException(
                $"좌표계 계산 실패: {what} 거리가 {m:0.00}mm 로 너무 가깝습니다(최소 {MinPointSeparationMm:0}mm). " +
                "같은 위치에서 두 번 캡처했거나 점 캡처 순서가 잘못됐는지 확인하세요.");
    }

    private static void RequireNotCollinear(double[] xUnit, double[] vUnit)
    {
        var c = Cross(xUnit, vUnit);
        double sinA = Math.Sqrt(c[0] * c[0] + c[1] * c[1] + c[2] * c[2]);
        if (sinA < Math.Sin(MinAxisAngleDeg * Math.PI / 180.0))
            throw new InvalidOperationException(
                $"좌표계 계산 실패: 점3이 X축(점1→점2)과 {Math.Asin(Math.Min(1, sinA)) * 180 / Math.PI:0.0}° 로 거의 일직선입니다" +
                $"(최소 {MinAxisAngleDeg:0}°). 점3을 X축에서 벗어난 위치(Z 방향 또는 XY 평면 위)로 잡으세요.");
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
        _activeTool = -1;
        _activeUser = -1;
        _semaphore.Dispose();
    }
}
