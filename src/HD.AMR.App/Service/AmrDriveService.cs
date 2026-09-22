using System.Text.Json;
using HD.AMR.App.Communication;
using HD.AMR.App.Enums;
using HD.AMR.App.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HD.AMR.App.Service;

/// <summary>
/// AMR 좌표 주행 — <c>POST /api/v3/robot/go</c> 발행 + 정차 대기. 자동 루틴(캘리브레이션 등)이
/// "지정 좌표로 가서 완전히 멈출 때까지" 를 한 번에 요구할 수 있게 한다.
///
/// <b>⚠ Modbus <c>PoseSearch</c>(Holding 19~25)는 주행 명령이 아니다 — 재측위(re-localization)다.</b>
/// 좌표를 쓰고 PoseSearch=1 을 때리면 로봇은 움직이지 않고 <b>자기 위치 추정만 그 좌표로 옮긴다</b>.
/// 캘리브레이션은 "AMR 이 실제로 어디 있는가"(<c>T_W_A</c>)를 표본의 기준으로 쓰므로, 재측위로
/// 만들어진 pose 를 그대로 믿으면 잔차에 드러나지 않는 조용한 오보정이 된다
/// (docs/ADENT_VENDOR_INQUIRY.md §3 — 재측위 사양은 벤더 미회신 상태).
/// 주행은 반드시 REST <c>/robot/go</c> 로만 낸다.
///
/// <b>⚠ <c>/go</c> 는 목적지 큐에 <i>추가</i>한다</b> — 이전 목적지가 남아 있으면 엉뚱한 곳을 먼저
/// 경유한다(부록 D-3). 그래서 기본값으로 매 이동 전에 <see cref="AmrRestClient.StopAsync"/> 로 큐를 비운다.
/// </summary>
public sealed class AmrDriveService
{
    private readonly AmrRestClient _rest;
    private readonly AMRService _amr;
    private readonly AmrRestSettings _restSettings;
    private readonly ILogger<AmrDriveService> _logger;

    public AmrDriveService(AmrRestClient rest, AMRService amr, IOptions<AmrRestSettings> restSettings,
        ILogger<AmrDriveService> logger)
    {
        _rest = rest;
        _amr = amr;
        _restSettings = restSettings.Value;
        _logger = logger;
    }

    /// <summary>
    /// 목표 좌표(SLAM 맵 기준, m·rad)로 이동하고 <b>완전히 정차</b>할 때까지 기다린다.
    /// 반환 pose 는 지령값이 아니라 <b>실제 정차 pose</b> — 호출측(캘리브레이션)은 이 값을 표본 기준으로 쓴다.
    /// </summary>
    public async Task<AmrDriveResult> DriveToAsync(double x, double y, double rz,
        AmrDriveOptions? options, Action<string>? progress, CancellationToken ct)
    {
        options ??= DefaultOptions();

        void Report(string m)
        {
            _logger.LogInformation("AMR 주행: {Message}", m);
            progress?.Invoke(m);
        }

        var before = _amr.LatestStatus;
        if (before is null)
            return AmrDriveResult.Fail("AMR 상태를 읽을 수 없습니다 — 연결을 확인하세요.", new RobotPose(0, 0, 0));
        if (before.RobotStopActive == 1)
            return AmrDriveResult.Fail("AMR 주행 정지가 활성화돼 있습니다 — 해제 후 다시 시도하세요.", before.Pose);
        if (before.ErrorCode != 0)
            return AmrDriveResult.Fail($"AMR 오류 상태입니다(code={before.ErrorCode}) — 해제 후 다시 시도하세요.", before.Pose);
        if (before.DrivingMode != DrivingMode.Drive)
            return AmrDriveResult.Fail($"AMR 주행 모드가 '{before.DrivingMode}' 입니다 — 드라이브 모드에서만 자동 주행합니다.",
                before.Pose);

        double commandedMm = DistanceMm(before.Pose, x, y);
        double commandedDeg = Math.Abs(NormalizeRad(rz - before.Pose.Angle)) * 180 / Math.PI;
        // 이미 목표에 서 있으면(복귀 등) 로봇이 움직이지 않는 것이 정상이다 — '움직임 관측'을 요구하면
        // 정차 판정이 영원히 서지 않는다.
        bool requireMotion = commandedMm > options.MotionDetectMm || commandedDeg > options.MotionDetectDeg;

        // ① 큐 비우기 — /go 는 append 라 이전 목적지가 남으면 엉뚱한 곳을 먼저 경유한다(D-3).
        if (options.StopBeforeGo)
        {
            var stop = await _rest.StopAsync(ct);
            if (!stop.Ok)
                Report($"선행 정지 실패(code={stop.Code}: {stop.Message}) — 이전 목적지가 큐에 남아 있을 수 있습니다.");
            await Task.Delay(options.PostStopDelay, ct);
        }

        // ② 이동 명령. stopFlag=true = 목적지에 정차하고 각도까지 보정(D-2).
        var go = await _rest.GoAsync(x, y, rz, stopFlag: true, ct);
        if (!go.Ok)
            return AmrDriveResult.Fail($"이동 명령(/robot/go) 실패 (code={go.Code}): {go.Message}", before.Pose);

        // ③ 정차 대기 — 지령 pose 가 아니라 '멈춤'을 기다린다. 도착 정밀도는 로봇 몫이고,
        //    캘리브레이션은 실제 정차 pose 를 그대로 표본 기준으로 쓰기 때문이다.
        return await WaitUntilStoppedAsync(before, x, y, rz, commandedMm, requireMotion, options, Report, ct);
    }

    /// <summary>설정(appsettings <c>AmrRest</c>)의 폴링 주기·주행 제한시간을 반영한 기본 옵션.</summary>
    public AmrDriveOptions DefaultOptions() => new()
    {
        PollInterval = TimeSpan.FromMilliseconds(Math.Clamp(_restSettings.StatusPollMs, 100, 5000)),
        Timeout = TimeSpan.FromSeconds(Math.Clamp(_restSettings.DriveTimeoutSec, 10, 3600)),
    };

    /// <summary>진행 중인 주행을 즉시 중단하고 목적지 큐를 비운다.</summary>
    public async Task<bool> StopAsync(CancellationToken ct = default)
    {
        var r = await _rest.StopAsync(ct);
        if (!r.Ok) _logger.LogWarning("AMR 주행 정지(/robot/state) 실패 (code={Code}): {Msg}", r.Code, r.Message);
        return r.Ok;
    }

    private async Task<AmrDriveResult> WaitUntilStoppedAsync(RobotStatus before, double x, double y, double rz,
        double commandedMm, bool requireMotion, AmrDriveOptions options, Action<string> report,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + options.Timeout;
        var motionDeadline = DateTime.UtcNow + options.MotionStartTimeout;
        bool movedObserved = false;
        int stable = 0;
        RobotPose? prev = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(options.PollInterval, ct);

            var st = await _amr.ReadStatusAsync(ct);
            if (st.ErrorCode != 0)
                return AmrDriveResult.Fail($"주행 중 AMR 오류(code={st.ErrorCode}).", st.Pose);
            if (st.RobotStopActive == 1)
                return AmrDriveResult.Fail("주행 중 AMR 주행 정지가 활성화됐습니다.", st.Pose);

            if (!movedObserved &&
                (st.WorkStatus == WorkStatus.Moving || DistanceMm(st.Pose, before.Pose) > options.MotionDetectMm))
                movedObserved = true;

            // 명령은 수리됐는데 로봇이 끝내 움직이지 않는 경우를 조기에 잡는다 — 이 상태로 표본을 모으면
            // 서로 다른 AMR 자세가 필요한 캘리브레이션이 같은 자세만 반복 수집하게 된다.
            if (!movedObserved && requireMotion && DateTime.UtcNow > motionDeadline)
                return AmrDriveResult.Fail(
                    $"이동 명령을 수리했지만 {options.MotionStartTimeout.TotalSeconds:0}초 동안 움직이지 않았습니다" +
                    $"(지령 이동량 {commandedMm:0}mm). 로봇 모드·비상정지·경로 장애물을 확인하세요.", st.Pose);

            stable = IsStoppedTick(st.WorkStatus, st.Pose, prev, movedObserved, requireMotion, options)
                ? stable + 1 : 0;
            prev = st.Pose;
            if (stable >= options.StablePollCount)
            {
                double resMm = DistanceMm(st.Pose, x, y);
                double resDeg = Math.Abs(NormalizeRad(st.Pose.Angle - rz)) * 180 / Math.PI;
                report($"정차 — 목표와의 차이 {resMm:0}mm / {resDeg:0.0}°.");
                if (resMm > options.MaxResidualMm || resDeg > options.MaxResidualDeg)
                    return AmrDriveResult.Fail(
                        $"목표에서 {resMm:0}mm / {resDeg:0.0}° 떨어진 곳에 정차했습니다" +
                        $"(허용 {options.MaxResidualMm:0}mm / {options.MaxResidualDeg:0.0}°).", st.Pose);
                return new AmrDriveResult(true, null, st.Pose, resMm, resDeg);
            }

            // 보조: REST status.error 감시(값 해석 미확정 — 비어 있지 않으면 실패로 본다).
            var status = await _rest.GetStatusAsync(ct);
            if (status.Ok && status.Data is JsonElement data && TryGetErrorText(data, out var err))
                return AmrDriveResult.Fail($"로봇 이동 오류 보고: {err}", st.Pose);

            if (DateTime.UtcNow > deadline)
                return AmrDriveResult.Fail(
                    $"{options.Timeout.TotalSeconds:0}초 안에 정차하지 못했습니다(작업상태 {st.WorkStatus}).", st.Pose);
        }
    }

    /// <summary>
    /// 정차 판정 1회분 — 작업상태가 대기이고 pose 가 직전 폴링 대비 멈춰 있으면 true.
    ///
    /// <paramref name="requireMotion"/> 이 true 면 <b>한 번이라도 움직인 것을 본 뒤</b>라야 정차로 인정한다 —
    /// 명령 직후 로봇이 아직 출발하지 않은 순간(대기 + 정지)을 "도착" 으로 오판하지 않기 위해서다.
    /// 반대로 이미 목표에 서 있어 움직일 필요가 없는 경우(복귀 등)에는 false 로 넘겨야 한다.
    /// 그렇지 않으면 영원히 움직임을 기다린다.
    /// </summary>
    public static bool IsStoppedTick(WorkStatus workStatus, RobotPose cur, RobotPose? prev,
        bool movedObserved, bool requireMotion, AmrDriveOptions options)
    {
        if (workStatus != WorkStatus.Idle) return false;
        if (requireMotion && !movedObserved) return false;
        if (prev is null) return false;
        return DistanceMm(cur, prev) <= options.StableMm &&
               Math.Abs(NormalizeRad(cur.Angle - prev.Angle)) * 180 / Math.PI <= options.StableDeg;
    }

    /// <summary>status 응답의 error 필드가 '존재하고 비어 있지 않은가'. 값 목록은 벤더 미회신(D-12).</summary>
    private static bool TryGetErrorText(JsonElement data, out string text)
    {
        text = "";
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("error", out var e)) return false;
        text = e.ValueKind switch
        {
            JsonValueKind.String => e.GetString() ?? "",
            JsonValueKind.Number => e.ToString(),
            JsonValueKind.Object or JsonValueKind.Array => e.GetRawText(),
            _ => "",
        };
        if (text is "0" or "null" or "{}" or "[]") text = "";
        return !string.IsNullOrWhiteSpace(text);
    }

    private static double DistanceMm(RobotPose a, RobotPose b)
        => DistanceMm(a, b.X, b.Y);

    private static double DistanceMm(RobotPose a, double x, double y)
        => Math.Sqrt(Math.Pow((a.X - x) * 1000, 2) + Math.Pow((a.Y - y) * 1000, 2));

    private static double NormalizeRad(double v)
    {
        while (v > Math.PI) v -= 2 * Math.PI;
        while (v <= -Math.PI) v += 2 * Math.PI;
        return v;
    }
}

/// <summary>좌표 주행 옵션. 기본값은 캘리브레이션(실내 저속 단거리) 기준.</summary>
public sealed class AmrDriveOptions
{
    /// <summary>이동 전 <c>/robot/state</c> 정지로 목적지 큐를 비운다(D-3). 끄지 말 것 — 큐 잔여는 오경유가 된다.</summary>
    public bool StopBeforeGo { get; init; } = true;

    /// <summary>선행 정지 후 <c>/go</c> 까지의 간격.</summary>
    public TimeSpan PostStopDelay { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>상태 폴링 간격.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>정차 판정까지의 전체 제한 시간.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>명령 후 이 시간 안에 움직임이 관측되지 않으면 '명령 미수행'으로 본다.</summary>
    public TimeSpan MotionStartTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>움직임으로 인정할 최소 변위 — 측위 노이즈보다 커야 한다. 지령 이동량이 이보다 작으면
    /// '이미 목표에 있음'으로 보고 움직임을 요구하지 않는다.</summary>
    public double MotionDetectMm { get; init; } = 30;
    public double MotionDetectDeg { get; init; } = 1.0;

    /// <summary>정지 판정 연속 폴링 횟수.</summary>
    public int StablePollCount { get; init; } = 3;

    /// <summary>정지 판정 허용 변화량(연속 폴링 간).</summary>
    public double StableMm { get; init; } = 5;
    public double StableDeg { get; init; } = 0.2;

    /// <summary>정차 후 목표와의 허용 잔차. 초과하면 실패로 본다(캘리브레이션은 마커가 FOV 를 벗어난다).
    /// ⚠ 이 값은 '도달 정밀도 요구'가 아니다 — 표본 기준은 지령이 아니라 실제 정차 pose 를 쓴다.</summary>
    public double MaxResidualMm { get; init; } = 500;
    public double MaxResidualDeg { get; init; } = 10;
}

/// <summary>주행 결과. <see cref="Pose"/> 는 항상 <b>실제 pose</b>(실패 시 마지막 관측값).</summary>
public sealed record AmrDriveResult(bool Success, string? Error, RobotPose Pose,
    double ResidualMm = 0, double ResidualDeg = 0)
{
    public static AmrDriveResult Fail(string error, RobotPose pose) => new(false, error, pose);
}
