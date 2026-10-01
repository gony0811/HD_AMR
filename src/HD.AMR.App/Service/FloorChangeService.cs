using HD.AMR.App.Communication.Vda5050;
using HD.AMR.App.Enums;
using HD.AMR.App.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HD.AMR.App.Service;

/// <summary>
/// 엘리베이터 층간 이동 후 <b>층 전환</b> — 해당 층 initpose 로 재측위(PoseSearch) → 수렴 검증 → mapId 변경·ACS 회신.
///
/// 운영 전제(2026-10-01 결정):
/// <list type="bullet">
/// <item>TARS-M 맵은 4개 층을 한 통합맵에 타일로 배치한 <b>1개 파일</b>이다 — 맵 파일 교체 없이 그 층 타일 위
///   좌표로 재측위만 하면 된다. mapId(L1~L4)는 어댑터가 보유하는 논리 구분이다.</item>
/// <item>엘리베이터에서 AMR 을 내려놓는 위치를 층별 initpose 로 Parameters 표에 등록해 둔다
///   (<see cref="FloorRelocalization"/> 의 키 규약).</item>
/// </list>
///
/// <b>순서가 핵심이다</b> — 재측위가 검증되기 전에 mapId 를 바꾸면 ACS 가 "새 층 mapId + 이전 층 좌표" state 를 받는다.
/// 그래서 검증 통과 시에만 <see cref="Vda5050AdapterService.SetMapId"/> 를 호출하고, 실패하면 mapId 를 유지한다.
///
/// ⚠ PoseSearch 의 탐색 범위·완료/실패 신호는 벤더 미회신(ADENT_VENDOR_INQUIRY §3, D-10)이라 완료 신호를 쓰지 않고
/// 맵 일치율(Input 30)과 initpose 대비 위치·각도 편차를 <b>연속 N회 새 상태 스냅샷</b>으로 직접 판정한다.
/// 재측위는 로봇을 움직이지 않는다(위치 추정만 이동) — 주행 명령이 아니다(<see cref="AmrDriveService"/> 주석 참조).
/// </summary>
public sealed class FloorChangeService
{
    private readonly AMRService _amr;
    private readonly Vda5050AdapterService _vda;
    private readonly Vda5050OrderExecutor _executor;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly OperationLogService _opLog;
    private readonly Vda5050AdapterSettings _vdaSettings;
    private readonly ILogger<FloorChangeService> _logger;
    private int _running;

    public FloorChangeService(AMRService amr, Vda5050AdapterService vda, Vda5050OrderExecutor executor,
        IServiceScopeFactory scopeFactory, OperationLogService opLog, IOptions<Vda5050AdapterSettings> vdaOptions,
        ILogger<FloorChangeService> logger)
    {
        _amr = amr;
        _vda = vda;
        _executor = executor;
        _scopeFactory = scopeFactory;
        _opLog = opLog;
        _vdaSettings = vdaOptions.Value;
        _logger = logger;
    }

    /// <summary>층 전환 진행 중 여부(중복 실행 방지·UI 버튼 비활성).</summary>
    public bool IsRunning => Volatile.Read(ref _running) != 0;

    /// <summary>
    /// <paramref name="mapId"/> 층으로 전환한다. 성공 시에만 mapId 를 바꾸고 state 를 즉시 발행한다.
    /// </summary>
    public async Task<FloorChangeResult> ChangeFloorAsync(string mapId, Action<string>? progress, CancellationToken ct)
    {
        mapId = mapId?.Trim() ?? "";
        if (Interlocked.Exchange(ref _running, 1) != 0)
            return FloorChangeResult.Fail("층 전환이 이미 진행 중입니다.", relocalized: false);
        try
        {
            var r = await RunAsync(mapId, progress, ct);
            _opLog.Log(OperationLogService.SourceUi, "AMR", "층 전환", r.Success,
                $"{_vda.CurrentMapId}{(r.Success ? "" : $" (요청 {mapId})")} — {r.Message}");
            return r;
        }
        catch (OperationCanceledException)
        {
            _opLog.Log(OperationLogService.SourceUi, "AMR", "층 전환", false, $"요청 {mapId} — 취소됨");
            throw;
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task<FloorChangeResult> RunAsync(string mapId, Action<string>? progress, CancellationToken ct)
    {
        void Report(string m)
        {
            _logger.LogInformation("층 전환({MapId}): {Message}", mapId, m);
            progress?.Invoke(m);
        }

        if (mapId.Length == 0)
            return FloorChangeResult.Fail("전환할 층(mapId)을 선택하세요.", relocalized: false);

        // ── ① 사전 점검 — 주행·임무 중 재측위는 위치 추정을 순간 이동시켜 진행 중 주행을 망친다 ──
        var st = _amr.LatestStatus;
        if (!_amr.IsConnected || st is null)
            return FloorChangeResult.Fail("AMR 상태를 읽을 수 없습니다 — 연결을 확인하세요.", relocalized: false);
        if (_executor.IsMissionActive)
            return FloorChangeResult.Fail("ACS order 를 실행 중입니다 — 임무 종료 후 전환하세요.", relocalized: false);
        if (st.WorkStatus is WorkStatus.Moving or WorkStatus.Docking or WorkStatus.Jog)
            return FloorChangeResult.Fail($"AMR 이 정지 상태가 아닙니다(작업 상태 {st.WorkStatus}) — 정지 후 전환하세요.",
                relocalized: false);

        // ── ② 층별 initpose·검증 기준 로드(Parameters 표) ──
        FloorInitPose? pose;
        FloorVerifyCriteria criteria;
        using (var scope = _scopeFactory.CreateScope())
        {
            var param = scope.ServiceProvider.GetRequiredService<ParameterService>();
            pose = await FloorRelocalization.LoadInitPoseAsync(param, mapId);
            criteria = await FloorRelocalization.LoadCriteriaAsync(param, _vdaSettings.LocalizedThresholdPercent);
        }
        if (pose is null)
            return FloorChangeResult.Fail(
                $"{mapId} 의 initpose 가 등록되지 않았습니다 — SETTINGS ▸ Parameters 에 " +
                $"{FloorRelocalization.KeyX(mapId)} [m], {FloorRelocalization.KeyY(mapId)} [m], " +
                $"{FloorRelocalization.KeyThetaDeg(mapId)} [deg] 를 입력하세요.", relocalized: false);

        // ── ③ 재측위: 좌표(Holding 20~25, m·rad) 쓰기 → PoseSearch(Holding 19) 활성화 ──
        Report($"재측위 요청 — initpose x={pose.X:0.000} m, y={pose.Y:0.000} m, θ={pose.ThetaDeg:0.0}°");
        var before = _amr.LatestStatus;
        await _amr.SetPoseTargetAsync((float)pose.X, (float)pose.Y, (float)pose.ThetaRad, ct);
        await _amr.SetPoseSearchAsync(1, ct);

        // ── ④ 수렴 검증 — 새 상태 스냅샷(1초 폴링 캐시)만 세어 연속 N회 통과해야 성공 ──
        var started = DateTime.UtcNow;
        var deadline = started + TimeSpan.FromSeconds(criteria.TimeoutSec);
        RobotStatus? last = before;
        int passes = 0;
        FloorVerifyEval? lastEval = null;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(200, ct);
            var cur = _amr.LatestStatus;
            if (cur is null || ReferenceEquals(cur, last)) continue;
            last = cur;

            lastEval = FloorRelocalization.Evaluate(cur.Pose, cur.MapStatusPercent, pose, criteria);
            // 쓰기 직후 첫 스냅샷은 PoseSearch 반영 전일 수 있다 — 최소 안정화 시간 전에는 통과로 세지 않는다.
            bool settled = DateTime.UtcNow - started >= TimeSpan.FromSeconds(criteria.MinSettleSec);
            passes = lastEval.Pass && settled ? passes + 1 : 0;
            Report($"검증 {passes}/{criteria.StablePolls} — {lastEval.Describe()}");
            if (passes >= criteria.StablePolls) break;
        }

        if (passes < criteria.StablePolls)
        {
            var why = lastEval is null ? "검증 시간 동안 AMR 상태를 새로 받지 못했습니다" : lastEval.Describe();
            return FloorChangeResult.Fail(
                $"재측위 검증 실패({criteria.TimeoutSec:0}s) — {why}. 층은 {_vda.CurrentMapId} 로 유지합니다. " +
                "AMR 위치 추정이 initpose 로 옮겨졌을 수 있으니 하차 위치·방향을 확인하고 다시 시도하거나 " +
                "TARS-M 에서 위치를 바로잡기 전에는 주행하지 마세요.", relocalized: true);
        }

        // ── ⑤ 검증 통과 → mapId 변경 + state 즉시 발행(ACS 회신) ──
        _vda.SetMapId(mapId, $"재측위 검증 통과 ({lastEval!.Describe()})");
        return FloorChangeResult.Ok($"{mapId} 전환 완료 — {lastEval.Describe()}");
    }
}

public sealed record FloorChangeResult(bool Success, string Message, bool Relocalized)
{
    public static FloorChangeResult Ok(string message) => new(true, message, true);
    public static FloorChangeResult Fail(string message, bool relocalized) => new(false, message, relocalized);
}
