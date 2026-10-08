using HD.AMR.App.Enums;
using HD.AMR.App.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service;

/// <summary>
/// AMR 장비 자체 상태 변화를 운영 로그(출처 AMR)로 남긴다 — 1초 주기로 <see cref="AMRService.LatestStatus"/> 를
/// 직전 값과 비교해 <b>바뀐 것만</b> 기록한다(주기 폴링 자체는 기록하지 않음).
///
/// 기록 대상: 연결 두절/복구, 주행 모드(Drive/Cart), 작업 상태(이동·조그·도킹 시작/종료 + 이동 거리),
/// 주행 정지(RobotStop), 오류 코드, 실행 상태, 전원 상태.
/// 이동이 ACS order 주행 중이 아니면 "수동/외부 명령 주행"(AMR_MANUAL)으로 분류해, 펜던트·조이스틱·
/// 장비 측 조작으로 움직인 이력을 ACS 동작과 구분할 수 있게 한다. 모드 변경도 앱의 모드 명령 직후(5초)가
/// 아니면 "장비 측 변경"으로 표시한다.
/// </summary>
public sealed class AmrActivityLogService : BackgroundService
{
    private static readonly TimeSpan CommandAttributionWindow = TimeSpan.FromSeconds(5);

    private readonly AMRService _amr;
    private readonly Vda5050OrderExecutor _executor;
    private readonly OperationLogService _opLog;
    private readonly ILogger<AmrActivityLogService> _logger;

    private RobotStatus? _prev;
    private bool _wasConnected;
    private RobotPose? _motionStartPose;
    private DateTime _motionStartUtc;

    public AmrActivityLogService(AMRService amr, Vda5050OrderExecutor executor, OperationLogService opLog,
        ILogger<AmrActivityLogService> logger)
    {
        _amr = amr;
        _executor = executor;
        _opLog = opLog;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { Check(_amr.LatestStatus); }
            catch (Exception ex) { _logger.LogWarning(ex, "AMR 상태 로그 비교 실패 — 계속"); }
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    private void Log(string category, string name, bool? success, string detail)
        => _opLog.Log(OperationLogService.SourceAmr, category, name, success, detail,
            _executor.Snapshot().OrderId is { Length: > 0 } o ? o : null);

    private void Check(RobotStatus? cur)
    {
        // 연결 — 첫 연결은 기준선만 잡는다(앱 기동 시 로그 폭주 방지).
        if (cur is null)
        {
            if (_wasConnected)
            {
                Log(OpCategory.AmrState, "AMR 통신 두절", false,
                    _prev is { } p ? $"마지막 {Pose(p.Pose)}, {p.WorkStatus}, 모드 {p.DrivingMode}" : "상태 미확보");
                _wasConnected = false;
            }
            return;
        }
        if (!_wasConnected)
        {
            if (_prev is not null)
                Log(OpCategory.AmrState, "AMR 통신 복구", true, $"{Pose(cur.Pose)}, {cur.WorkStatus}, 모드 {cur.DrivingMode}");
            _wasConnected = true;
            _prev = cur;
            return;
        }

        var prev = _prev!;
        _prev = cur;

        if (cur.DrivingMode != prev.DrivingMode)
        {
            var byApp = _amr.LastDrivingModeCommandUtc is { } t && DateTime.UtcNow - t <= CommandAttributionWindow
                        && _amr.LastCommandedDrivingMode == cur.DrivingMode;
            Log(OpCategory.AmrMode, $"주행 모드 {prev.DrivingMode} → {cur.DrivingMode}", null,
                (byApp ? "앱 모드 명령 반영" : "장비 측 변경(펜던트/외부 조작) — 앱 명령 없음") +
                (cur.DrivingMode == DrivingMode.Cart ? " · 카트 모드 = 수동 밀기" : "") + $", {Pose(cur.Pose)}");
        }

        if (cur.WorkStatus != prev.WorkStatus) OnWorkStatusChanged(prev, cur);

        if (cur.RobotStopActive != prev.RobotStopActive)
            Log(OpCategory.AmrState, cur.RobotStopActive == 1 ? "주행 정지 활성" : "주행 정지 해제",
                cur.RobotStopActive == 1 ? false : true, $"RobotStop={cur.RobotStopActive}, {Pose(cur.Pose)}");

        if (cur.ErrorCode != prev.ErrorCode)
            Log(OpCategory.AmrState, cur.ErrorCode != 0 ? $"AMR 오류 code={cur.ErrorCode}" : "AMR 오류 해제",
                cur.ErrorCode == 0, $"이전 code={prev.ErrorCode}, {cur.WorkStatus}, {Pose(cur.Pose)}");

        if (cur.RobotState != prev.RobotState)
            Log(OpCategory.AmrState, $"실행 상태 {prev.RobotState} → {cur.RobotState}", null, Pose(cur.Pose));

        if (cur.PowerState != prev.PowerState)
            Log(OpCategory.AmrState, $"전원 상태 {prev.PowerState} → {cur.PowerState}",
                cur.PowerState == PowerState.Normal ? true : null, "");
    }

    private void OnWorkStatusChanged(RobotStatus prev, RobotStatus cur)
    {
        bool acsDriving = _executor.Snapshot().Driving;
        bool wasMoving = prev.WorkStatus is WorkStatus.Moving or WorkStatus.Jog or WorkStatus.Docking;
        bool isMoving = cur.WorkStatus is WorkStatus.Moving or WorkStatus.Jog or WorkStatus.Docking;

        if (isMoving && !wasMoving)
        {
            _motionStartPose = prev.Pose;
            _motionStartUtc = DateTime.UtcNow;
        }

        string what = cur.WorkStatus switch
        {
            WorkStatus.Jog => "수동 조그 시작",
            WorkStatus.Moving => acsDriving ? "주행 시작(ACS order)" : "주행 시작(ACS order 아님)",
            WorkStatus.Docking => "도킹 시작",
            WorkStatus.Idle when wasMoving => prev.WorkStatus == WorkStatus.Jog ? "수동 조그 종료" : "정지(대기)",
            _ => $"작업 상태 {prev.WorkStatus} → {cur.WorkStatus}",
        };

        // 조그, 또는 ACS 주행이 아닌 이동은 수동/외부 조작으로 분류.
        bool manual = cur.WorkStatus == WorkStatus.Jog || prev.WorkStatus == WorkStatus.Jog
                      || (cur.WorkStatus == WorkStatus.Moving && !acsDriving);
        var detail = $"{prev.WorkStatus} → {cur.WorkStatus}, 모드 {cur.DrivingMode}, {Pose(cur.Pose)}";
        if (!isMoving && wasMoving && _motionStartPose is { } s)
        {
            var dist = Math.Sqrt(Math.Pow(cur.Pose.X - s.X, 2) + Math.Pow(cur.Pose.Y - s.Y, 2));
            var dTheta = (cur.Pose.Angle - s.Angle) * 180 / Math.PI;
            detail += $", 이동 {dist:0.###}m / {dTheta:+0.#;-0.#}°, {(DateTime.UtcNow - _motionStartUtc).TotalSeconds:0}s";
            _motionStartPose = null;
        }
        if (!acsDriving && cur.WorkStatus == WorkStatus.Moving)
            detail += " — 펜던트/외부 명령 또는 캘리브레이션 자동 주행";

        Log(manual ? OpCategory.AmrManual : OpCategory.AmrState, what, null, detail);
    }

    private static string Pose(RobotPose p) => $"pose ({p.X:0.###}, {p.Y:0.###}, θ={p.Angle:0.###})";
}
