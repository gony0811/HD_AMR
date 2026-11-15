using HD.AMR.App.Service.Inspection;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service.Sequence.Steps;

/// <summary>
/// ⑯⁺ 내부 작업물 좌표계 계산 — 점1(코로게이션 Top)·점2(용접선 X+ 방향)와 점1 교시 자세의
/// 툴 +Z로 BASE 기준 프레임을 만든다. 레시피 원점은 코로게이션 바닥이므로 Top에서 작업물 Z- 방향
/// 33.8mm 내린 가상점으로 설정한다. 계산 결과는 실행 컨텍스트에만 보관하며 컨트롤러에는 등록하지 않는다.
/// </summary>
public class WObjRegisterStep : ISequenceStep
{
    private readonly CobotService _cobot;
    private readonly ParameterService _param;
    private readonly ILogger<WObjRegisterStep> _logger;

    private const int DefaultWObjId = 1;

    /// <summary>
    /// 점1 을 기록한 TCP 높이가 티칭 프로필 Z=0(주름 바닥 = 평탄부 초점 높이)에서 Z+ 로 얼마나 떨어져 있는지 [mm].
    /// 원점은 점1 에서 작업물 Z− 로 이 값만큼 내린 자리가 된다.
    /// <list type="bullet">
    /// <item>33.8(기본, 종전 동작): 점1 을 <b>주름 꼭대기에 초점이 맞는 높이</b>에서 기록했다고 본다.</item>
    /// <item>0: 점1 을 <b>평탄부에 초점이 맞는 높이</b>에서 기록했다고 본다 — ④⁺ 레이저 WD 가 평탄부 기준으로 거리를 맞춘 뒤
    /// 옆으로만 움직여 점1 을 기록하는 현재 순서가 이 경우다. 33.8 로 두면 순회 전체가 면 쪽으로 33.8mm 들어간다
    /// (실기 2026-11-11 T 면: 평탄부·꼭대기 초점 높이를 따로 확인해 대조).</item>
    /// </list>
    /// 면별 값은 뒤에 <c>.{wall_code}</c> 를 붙인 키로 따로 둘 수 있다(레이저 WD 와 같은 규칙).
    /// </summary>
    public const string Point1HeightKey = "Sequence.WObj.Point1HeightMm";

    /// <summary>허용 범위 [mm] — 밖이면 좌표계를 만들지 않고 실패한다(순회 높이를 조용히 엉뚱하게 두지 않는다).</summary>
    public const double MaxPoint1HeightMm = 100.0;

    /// <summary>
    /// 점1 기준 높이와 그 값을 읽은 곳 — 면별 키 → 공통 키 → 기본 33.8. 읽기 실패는 던지지 않고 기본값으로 돌린다.
    /// </summary>
    public static async Task<(double Mm, string Source)> ResolvePoint1HeightAsync(ParameterService param, string? wallCode)
    {
        try
        {
            var info = Models.WallCodes.Find(wallCode?.Trim().ToUpperInvariant());
            if (info is not null && await param.GetDoubleAsync($"{Point1HeightKey}.{info.Code}") is { } perWall)
                return (perWall, $"{Point1HeightKey}.{info.Code}");
            if (await param.GetDoubleAsync(Point1HeightKey) is { } common)
                return (common, Point1HeightKey);
            return (InternalWorkpieceFrame.DefaultCorrugationTopHeightMm, "기본값");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (InternalWorkpieceFrame.DefaultCorrugationTopHeightMm, $"기본값(읽기 실패: {ex.Message})");
        }
    }

    public WObjRegisterStep(CobotService cobot, ParameterService param, ILogger<WObjRegisterStep> logger)
    {
        _cobot = cobot;
        _param = param;
        _logger = logger;
    }

    public string Key => "wobjRegister";
    public string DisplayName => "내부 작업물 좌표계 계산";
    public int DefaultOrder => 1170;

    public StepValidation Validate(SequenceContext context)
    {
        if (!_cobot.IsConnected)
            return StepValidation.Fail("코봇 RPC 미연결");

        if (!context.Bag.ContainsKey(WObjPointStep.PointPoseBagKey(1))
            || !context.Bag.ContainsKey(WObjPointStep.PointPoseBagKey(2)))
            return StepValidation.Fail("점1·점2 기록 없음 — 작업물 좌표계 점1/점2 기록 단계를 먼저 실행하세요.");

        return StepValidation.Ok();
    }

    public async Task<StepResult> ExecuteAsync(SequenceContext context, CancellationToken ct)
    {
        var wobjId = (int)(await GetWObjIdAsync());
        if (wobjId is < 0 or > 14)
            return StepResult.Fail($"'{WObjPointStep.WObjIdKey}' 범위 초과 ({wobjId}) — 0~14 로 설정하세요.");

        if (context.Bag[WObjPointStep.PointPoseBagKey(1)] is not double[] p1 || p1.Length != 6
            || context.Bag[WObjPointStep.PointPoseBagKey(2)] is not double[] p2 || p2.Length != 6)
            return StepResult.Fail("점1·점2 포즈를 읽을 수 없습니다 — 기록 단계를 다시 실행하세요.");

        var (heightMm, heightSource) = await ResolvePoint1HeightAsync(_param, LaserWorkingDistanceSetting.WallOf(context));
        if (!double.IsFinite(heightMm) || heightMm < 0 || heightMm > MaxPoint1HeightMm)
            return StepResult.Fail(
                $"'{heightSource}' 범위 초과 ({heightMm:0.#}mm) — 0~{MaxPoint1HeightMm:0}mm 로 설정하세요 " +
                "(0 = 점1 을 평탄부 초점 높이에서 기록, 33.8 = 주름 꼭대기 초점 높이에서 기록).");

        double[] frame;
        try
        {
            frame = InternalWorkpieceFrame.Compute(p1, p2, heightMm);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return StepResult.Fail($"내부 작업물 좌표계 계산 실패 — {ex.Message}");
        }

        context.Bag[InternalWorkpieceFrame.BagKey] = frame;
        var slopeNote = "";
        if (context.Bag.TryGetValue(BundleTeach.Point2WdKey, out var wd) && wd is true)
        {
            var slope = InternalWorkpieceFrame.SlopeAlongSeamDeg(p1, p2);
            slopeNote = $" 묶음 보정 — X는 비드 센터(Y), Z는 두 점 WD 실측에 직교(기울기 {slope:+0.00;-0.00}°).";
            _logger.LogInformation("내부 작업물 좌표계 묶음 보정: 기울기 {Slope:+0.00;-0.00}°", slope);
        }
        var span = Math.Sqrt(
            Math.Pow(p2[0] - p1[0], 2) + Math.Pow(p2[1] - p1[1], 2) + Math.Pow(p2[2] - p1[2], 2));

        _logger.LogInformation(
            "내부 작업물 좌표계 계산: id {Id}, Top [{X1:0.0},{Y1:0.0},{Z1:0.0}], X+점 [{X2:0.0},{Y2:0.0},{Z2:0.0}], " +
            "간격 {Span:0.0}mm, 바닥 원점(점1 Z-{Height:0.0}, {Source}) [{Ox:0.0},{Oy:0.0},{Oz:0.0}], RPY [{Rx:0.00},{Ry:0.00},{Rz:0.00}]",
            wobjId, p1[0], p1[1], p1[2], p2[0], p2[1], p2[2], span,
            heightMm, heightSource,
            frame[0], frame[1], frame[2], frame[3], frame[4], frame[5]);

        return StepResult.Ok(
            $"내부 작업물 좌표계 계산 완료 — 교시 간격 {span:0.0}mm, 바닥 원점 " +
            $"[{frame[0]:0.0}, {frame[1]:0.0}, {frame[2]:0.0}], RPY " +
            $"[{frame[3]:0.00}, {frame[4]:0.00}, {frame[5]:0.00}] " +
            $"(점1 에서 작업물 Z−{heightMm:0.0}mm — {heightSource}, 컨트롤러 등록 없음).{slopeNote}");
    }

    private async Task<double> GetWObjIdAsync()
    {
        try { return await _param.GetDoubleAsync(WObjPointStep.WObjIdKey) ?? DefaultWObjId; }
        catch { return DefaultWObjId; }
    }
}
