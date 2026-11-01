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
    private readonly ILogger<ReachMapService> _logger;

    public ReachMapService(CobotService cobot, CalibrationService calib, PostureLimitsService limits,
        ParameterService param, ILogger<ReachMapService> logger)
    {
        _cobot = cobot;
        _calib = calib;
        _limits = limits;
        _param = param;
        _logger = logger;
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

        var robot = new ReachMapRobot(mount, toolLen, limits, axis, Envelope: CobotEnvelope.Default);
        _logger.LogInformation(
            "리치 맵 시작: 셀 {Cells}개(면 {Walls}), ② {Approach}mm / 정렬 준비 {Align}mm / 초점 {Focus}mm, 공구 #{Tool} {ToolLen:0}mm, 최소 뻗음 {Min}mm",
            spec.CellCount, string.Join(",", spec.WallCodes), spec.ApproachStandoffMm, spec.AlignStandoffMm, spec.FocusStandoffMm,
            tool, toolLen, limits.MinFlangeReachMm);

        var result = await ReachMap.BuildAsync(spec, robot,
            async (pose, c) => await _cobot.Rpc.GetInverseKinForMoveAsync(pose, tool, 0, c),
            progress, ct);
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
        var robot = new ReachMapRobot(mount, ApproachReach.ToolLengthMm(tool), limits, ToolAxisDir.PlusZ,
            Envelope: CobotEnvelope.Default);
        var kin = Fr5Kinematics.Fr5Wml;
        _logger.LogInformation(
            "오프라인 리치 맵 시작(FR5-WML 모델): 셀 {Cells}개(면 {Walls}), 장착 {Mount}, 공구 [{Tool}]",
            spec.CellCount, string.Join(",", spec.WallCodes), calibrated ? "보정값" : "STEP 기본",
            string.Join(" ", tool));

        var normalized = limits.Normalized();
        var result = await ReachMap.BuildAsync(spec, robot,
            OfflineInverseKin(kin, tool, normalized, CobotEnvelope.Default, mount[5]), progress, ct);
        return (result, await SaveAsync(result, "reachmap_offline", ct));
    }

    /// <summary>
    /// 오프라인 역기구학 델리게이트. <see cref="ReachMap"/> 은 셀마다 ② → 정렬 준비 → 초점 순으로 풀고,
    /// 앞 자세가 풀렸을 때만 다음을 푼다. ② 는 여유 최대 해를, 그 뒤는 바로 앞 자세의 해에 가장 가까운 해를 골라
    /// 세 자세를 같은 가지로 잇는다 — 운영에서 연속 이동하는 것과 같은 선택이다(가지가 갈라지면 경로 판정이
    /// '휘두름' 으로 거짓 실패한다). <paramref name="envelope"/> 를 주면 팔·툴이 차체·리프트 기둥에 닿는 가지는
    /// 고르지 않는다(상완이 기둥 옆으로 늘어지는 elbow-down 가지가 흔히 여유 최대로 뽑힌다).
    /// </summary>
    /// <param name="mountYawDeg">장착 yaw(T_A_B rz) [도] — 자기 간섭 판정용.</param>
    public static Func<double[], double, CancellationToken, Task<double[]?>> OfflineInverseKin(
        Fr5Kinematics kin, double[] tool, PostureLimits limits, CobotEnvelope? envelope = null, double mountYawDeg = 0)
    {
        const int posesPerCell = 3;
        var stage = 0;               // 0 = 다음 호출은 ②
        double[]? previous = null;
        return (pose, strokeMm, _) =>
        {
            Func<double[], bool>? collides = envelope is null
                ? null
                : j => envelope.Evaluate(kin, j, strokeMm, mountYawDeg).Collides;
            var sol = stage == 0
                ? kin.InverseBest(pose, tool, limits, collides: collides)
                : kin.InverseBest(pose, tool, limits, previous, collides);
            // 실패(null)면 ReachMap 이 그 셀의 나머지를 부르지 않으므로 다음 호출은 새 셀의 ② 다.
            stage = sol is null ? 0 : (stage + 1) % posesPerCell;
            previous = sol;
            return Task.FromResult(sol);
        };
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
