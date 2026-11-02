using HD.AMR.App.Service.Sequence.Steps;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service.Motion;

/// <summary>
/// <see cref="ReachMap"/> 를 실제 컨트롤러 역기구학으로 돌린다. <b>코봇을 움직이지 않는다</b> — IK 는 계산 호출이다.
/// 장착 보정(T_A_B)·공구 길이·자세 허용 범위·광축은 운영 시퀀스가 쓰는 저장값을 그대로 읽어,
/// 맵이 "지금 이 장비" 의 리치를 나타내게 한다. 결과 CSV 는 <see cref="OutputDirectory"/> 에 남긴다.
/// </summary>
public class ReachMapService
{
    private readonly CobotService _cobot;
    private readonly CalibrationService _calib;
    private readonly PostureLimitsService _limits;
    private readonly ParameterService _param;
    private readonly TeachingService _teaching;
    private readonly ILogger<ReachMapService> _logger;

    public ReachMapService(CobotService cobot, CalibrationService calib, PostureLimitsService limits,
        ParameterService param, TeachingService teaching, ILogger<ReachMapService> logger)
    {
        _cobot = cobot;
        _calib = calib;
        _limits = limits;
        _param = param;
        _teaching = teaching;
        _logger = logger;
    }

    /// <summary>
    /// 면별 운영 ② 출발 관절각 — <see cref="SequenceEntry.PrepareAsync"/> 가 실제로 ② 직전에 세우는 자세와 같다:
    /// 상부 면(T/PU/SU)은 그 면 검사 준비 슬롯(미티칭이면 홈 위치_2), 그 외는 작업 준비 위치(ready).
    /// 티칭이 없는 면은 넣지 않는다(그 면은 종전처럼 출발 자세 없이 판정).
    /// </summary>
    private async Task<Dictionary<string, double[]>> LoadStartJointsAsync(IEnumerable<string> wallCodes, CancellationToken ct)
    {
        var positions = (await _teaching.ListAsync(ct)).ToDictionary(p => p.Key, p => p);
        double[]? Joints(string key) => positions.TryGetValue(key, out var p) && p.IsTaught && p.J1.HasValue
            ? new[] { p.J1!.Value, p.J2!.Value, p.J3!.Value, p.J4!.Value, p.J5!.Value, p.J6!.Value }
            : null;

        var result = new Dictionary<string, double[]>();
        foreach (var code in wallCodes.Distinct())
        {
            var start = SequenceEntry.IsUpperRoute(code)
                ? Joints(TeachingService.WallSlotKey(code)) ?? Joints(SequenceEntry.Home2Key)
                : Joints("ready");
            if (start is not null) result[code] = start;
            else _logger.LogWarning("리치 맵: {Wall} 면의 ② 출발 자세(검사 준비/홈 위치_2/ready) 미티칭 — 가지 전환 판정 없이 계산합니다.", code);
        }
        return result;
    }

    /// <summary>CSV 저장 폴더 — 실행 폴더 아래 reachmaps.</summary>
    public static string OutputDirectory => Path.Combine(AppContext.BaseDirectory, "reachmaps");

    /// <summary>
    /// 격자를 평가하고 CSV 를 저장한다. 코봇 미연결·장착 보정 없음·공구 오프셋 0 이면 시작 전에 실패한다.
    /// </summary>
    /// <param name="tool">IK 에 쓸 공구 번호(검사 툴, 보통 1).</param>
    public async Task<(ReachMapResult Result, string CsvPath)> RunAsync(
        ReachMapSpec spec, int tool, IProgress<ReachMapProgress>? progress = null, CancellationToken ct = default)
    {
        if (!_cobot.IsConnected)
            throw new InvalidOperationException("코봇이 연결되어 있지 않습니다 — 리치 맵은 컨트롤러 역기구학을 씁니다.");

        var mount = await _calib.GetMountAsync();
        if (mount.All(v => Math.Abs(v) < 1e-9))
            throw new InvalidOperationException("장착 보정(T_A_B) 미수행 — 차체 기준 리치를 계산할 수 없습니다.");

        var toolLen = ApproachReach.ToolLengthMm(tool == 0 ? new double[6] : await _cobot.Rpc.GetToolCoordAsync(tool, ct));
        if (toolLen < 1.0)
            throw new InvalidOperationException($"공구 #{tool} 오프셋이 0 입니다 — 검사 툴 공구 번호를 확인하세요.");

        var limits = await _limits.GetAsync();
        if (!await _limits.IsConfiguredAsync())
            _logger.LogWarning("리치 맵: 자세 허용 범위가 저장되지 않아 기본값(툴 없는 하드웨어 범위)으로 판정합니다.");
        var (axis, _) = await WeldSequenceSupport.GetDepthAxisAsync(_param);

        var starts = await LoadStartJointsAsync(spec.WallCodes, ct);
        var robot = new ReachMapRobot(mount, toolLen, limits, axis, Envelope: CobotEnvelope.Default,
            StartJointsByWall: starts, MaxEntryJointTravelDeg: CobotInspectionMoveStep.MaxEntryJointTravelDeg);
        _logger.LogInformation(
            "리치 맵 시작: 셀 {Cells}개(면 {Walls}), ② {Approach}mm / 정렬 준비 {Align}mm / 초점 {Focus}mm, 공구 #{Tool} {ToolLen:0}mm, 최소 뻗음 {Min}mm, 출발 자세 {Starts}",
            spec.CellCount, string.Join(",", spec.WallCodes), spec.ApproachStandoffMm, spec.AlignStandoffMm, spec.FocusStandoffMm,
            tool, toolLen, limits.MinFlangeReachMm, starts.Count == 0 ? "없음" : string.Join(",", starts.Keys));

        // 시드가 있으면 컨트롤러 config 0–7 중 시드에 가장 가까운(하드웨어 범위 안) 해 — 운영 ② 와 같은 선택.
        var result = await ReachMap.BuildAsync(spec, robot,
            async (pose, _, seed, c) => seed is { Length: 6 }
                ? await _cobot.Rpc.GetInverseKinNearestAsync(pose, seed, tool, 0, c)
                : await _cobot.Rpc.GetInverseKinForMoveAsync(pose, tool, 0, c),
            null, progress, ct);
        return (result, await SaveAsync(result, "reachmap", ct));
    }

    /// <summary>
    /// 컨트롤러 없이 FR5-WML 모델 역기구학(<see cref="Fr5Kinematics"/>)으로 격자를 평가한다 — 코봇 미연결이어도 된다.
    /// 장착 보정이 저장돼 있으면 그 값을, 없으면 STEP 장착(<see cref="Fr5Kinematics.StepMount"/>)을 쓰고,
    /// 공구는 검사 툴 설계값(<see cref="Fr5Kinematics.InspectionTool"/>)이다. 결과는 'reachmap_offline_' CSV.
    /// </summary>
    public async Task<(ReachMapResult Result, string CsvPath)> RunOfflineAsync(
        ReachMapSpec spec, IProgress<ReachMapProgress>? progress = null, CancellationToken ct = default)
    {
        var mount = await _calib.GetMountAsync();
        var calibrated = mount.Any(v => Math.Abs(v) > 1e-9);
        if (!calibrated) mount = Fr5Kinematics.StepMount;

        var tool = Fr5Kinematics.InspectionTool;
        var limits = await _limits.GetAsync();
        // 설계 공구는 회전이 없어 광축 = 공구 +Z 다. 저장된 광축 설정은 컨트롤러 공구 #1 프레임 기준이라 여기 쓰지 않는다.
        var (savedAxis, fromParam) = await WeldSequenceSupport.GetDepthAxisAsync(_param);
        if (fromParam && savedAxis != ToolAxisDir.PlusZ)
            _logger.LogWarning("오프라인 리치 맵: 저장된 광축 {Axis} 대신 설계 공구 광축 +Z 로 계산합니다 — 컨트롤러 공구 #1 회전을 확인하세요.", savedAxis);
        var starts = await LoadStartJointsAsync(spec.WallCodes, ct);
        var robot = new ReachMapRobot(mount, ApproachReach.ToolLengthMm(tool), limits, ToolAxisDir.PlusZ,
            Envelope: CobotEnvelope.Default, StartJointsByWall: starts,
            MaxEntryJointTravelDeg: CobotInspectionMoveStep.MaxEntryJointTravelDeg);
        var kin = Fr5Kinematics.Fr5Wml;
        _logger.LogInformation(
            "오프라인 리치 맵 시작(FR5-WML 모델): 셀 {Cells}개(면 {Walls}), 장착 {Mount}, 공구 [{Tool}], 출발 자세 {Starts}",
            spec.CellCount, string.Join(",", spec.WallCodes), calibrated ? "보정값" : "STEP 기본",
            string.Join(" ", tool), starts.Count == 0 ? "없음" : string.Join(",", starts.Keys));

        var normalized = limits.Normalized();
        var result = await ReachMap.BuildAsync(spec, robot,
            OfflineInverseKin(kin, tool, normalized, CobotEnvelope.Default, mount[5]),
            (pose, start, _) => SameBranchBlocker(kin, tool, normalized, pose, start), progress, ct);
        return (result, await SaveAsync(result, "reachmap_offline", ct));
    }

    /// <summary>
    /// 오프라인 역기구학 델리게이트. <see cref="ReachMap"/> 은 셀마다 ② → 정렬 준비 → 초점 순으로 풀고, ② 는 운영 출발
    /// 자세를, 그 뒤는 바로 앞 자세의 해를 시드로 준다. 시드가 있으면 허용 범위 안에서 시드에 가장 가까운 해를,
    /// 없으면 여유 최대 해를 고른다 — 세 자세를 운영 ② 와 같은 가지로 잇는다(가지가 갈라지면 경로 판정이
    /// '휘두름' 으로 거짓 실패한다). <paramref name="envelope"/> 를 주면 팔·툴이 차체·리프트 기둥에 닿는 가지는
    /// 고르지 않는다(상완이 기둥 옆으로 늘어지는 elbow-down 가지가 흔히 여유 최대로 뽑힌다).
    /// </summary>
    /// <param name="mountYawDeg">장착 yaw(T_A_B rz) [도] — 자기 간섭 판정용.</param>
    public static Func<double[], double, double[]?, CancellationToken, Task<double[]?>> OfflineInverseKin(
        Fr5Kinematics kin, double[] tool, PostureLimits limits, CobotEnvelope? envelope = null, double mountYawDeg = 0)
        => (pose, strokeMm, seed, _) =>
        {
            Func<double[], bool>? collides = envelope is null
                ? null
                : j => envelope.Evaluate(kin, j, strokeMm, mountYawDeg).Collides;
            return Task.FromResult(kin.InverseBest(pose, tool, limits, seed, collides));
        };

    /// <summary>
    /// 출발 관절각 <paramref name="start"/> 과 같은 가지(팔꿈치 J3·손목 J5 부호 같음)의 해 중 가장 가까운 것이 왜 쓸 수
    /// 없는지 — 허용 범위 위반이면 그 사유(예: "J4 관절한계 (91.0° ∈ [-265, 85])"), 같은 가지 해가 아예 없으면 그렇다고.
    /// 쓸 수 있으면 null. 리치 맵의 가지 전환 셀 사유용.
    /// </summary>
    public static string? SameBranchBlocker(Fr5Kinematics kin, double[] tool, PostureLimits limits, double[] pose, double[] start)
    {
        var s = limits.NormalizeJoints(start);
        double[]? nearest = null;
        var nearestDist = double.PositiveInfinity;
        foreach (var sol in kin.InverseAll(pose, tool))
        {
            var j = limits.NormalizeJoints(sol);
            if (Math.Sign(j[2]) != Math.Sign(s[2]) || Math.Sign(j[4]) != Math.Sign(s[4])) continue;
            var d = Enumerable.Range(0, 5).Max(i => Math.Abs(j[i] - s[i]));
            if (d < nearestDist) { nearestDist = d; nearest = j; }
        }
        if (nearest is null) return "같은 팔꿈치·손목 가지의 해 없음";
        var m = limits.Evaluate(new[] { nearest[0], nearest[1], nearest[2], nearest[3], nearest[4], 0.0 });
        return m.Feasible ? null : m.Limiting;
    }

    private async Task<string> SaveAsync(ReachMapResult result, string prefix, CancellationToken ct)
    {
        Directory.CreateDirectory(OutputDirectory);
        var path = Path.Combine(OutputDirectory, $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        await File.WriteAllTextAsync(path, result.ToCsv(), ct);
        _logger.LogInformation("리치 맵 완료: {Path}\n{Summary}", path, result.SummaryText());
        return path;
    }
}
