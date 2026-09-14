using HD.AMR.App.Communication;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service.Sequence.Steps;

/// <summary>
/// ② Cobot 검사위치 이동 — 티칭된 검사 준비 위치에 툴프레임 u/v 오프셋을 합성한 목표로 MoveL 직선 이동.
/// </summary>
public class CobotInspectionMoveStep : ISequenceStep
{
    private readonly CobotService _cobot;
    private readonly ILogger<CobotInspectionMoveStep> _logger;

    public CobotInspectionMoveStep(CobotService cobot, ILogger<CobotInspectionMoveStep> logger)
    {
        _cobot = cobot;
        _logger = logger;
    }

    public string Key => "cobotInspection";
    public string DisplayName => "Cobot 검사위치 이동";
    public int DefaultOrder => 200;

    /// <summary>context.InspectionSurfaceId(0x01~0xFF)에 해당하는 티칭 위치를 찾는다 —
    /// ②의 이동 목표이자 ③(CameraAlign)/④(FlatSurfaceAlign)의 검증에도 동일 규칙 사용.
    /// 같은 SurfaceId 가 여러 행이면 표시 순서(SortOrder→Id) 첫 행. 범위 밖/0 이면 null.</summary>
    public static Data.Entities.TeachingPosition? FindBySurfaceId(SequenceContext context) =>
        context.InspectionSurfaceId is > 0x00 and <= 0xFF
            ? context.Positions.Values
                .Where(p => p.SurfaceId == context.InspectionSurfaceId)
                .OrderBy(p => p.SortOrder).ThenBy(p => p.Id)
                .FirstOrDefault()
            : null;

    public StepValidation Validate(SequenceContext context)
    {
        if (!_cobot.IsConnected)
            return StepValidation.Fail("코봇 RPC 미연결");

        if (context.InspectionSurfaceId is <= 0x00 or > 0xFF)
            return StepValidation.Fail("검사 Wall ID 미설정 (0x01~0xFF) — ② 파라미터에서 선택하세요.");

        var pos = FindBySurfaceId(context);
        if (pos is null)
            return StepValidation.Fail(
                $"Wall 0x{context.InspectionSurfaceId:X2}에 해당하는 티칭 위치가 없습니다 — Teaching에서 Wall ID를 지정하세요.");
        if (!pos.IsTaught)
            return StepValidation.Fail(
                $"Wall 0x{context.InspectionSurfaceId:X2} '{pos.Name}' 미티칭 — Teaching에서 먼저 저장하세요.");

        if (Math.Abs(context.InspectionOffsetU) > 500 || Math.Abs(context.InspectionOffsetV) > 500)
            return StepValidation.Fail("검사 오프셋 u/v 범위 초과 (±500 mm 이내).");

        return StepValidation.Ok();
    }

    /// <summary>티칭된 검사 준비 위치의 베이스 목표 포즈 계산 (일반/작업물 추종 공용).
    /// ③ 단계의 도달 확인(<see cref="CameraAlignStep"/>)에서도 같은 목표를 재계산하는 데 쓴다.</summary>
    internal static async Task<(double[] Target, string Where)> ComputeTargetPoseAsync(
        CobotService cobot, Data.Entities.TeachingPosition inspection, CancellationToken ct)
    {
        if (inspection.UserFrame is int n && n > 0 && inspection.RelX.HasValue)
        {
            // 작업물 추종: 현재(재등록된) 프레임 T_N에 저장된 상대 pose를 적용해 베이스 목표 계산.
            // 최종 이동은 계산된 베이스 목표로 user:0 → rc=74(좌표계 불일치) 회피.
            var tN = await cobot.Rpc.GetWObjCoordAsync(n, ct);
            var rel = new[]
            {
                inspection.RelX!.Value, inspection.RelY!.Value, inspection.RelZ!.Value,
                inspection.RelRx!.Value, inspection.RelRy!.Value, inspection.RelRz!.Value,
            };
            return (FrameMath.FromFrame(rel, tN), $"검사 준비 위치(작업물 #{n} 추종)로");
        }

        var target = new[]
        {
            inspection.X!.Value, inspection.Y!.Value, inspection.Z!.Value,
            inspection.Rx!.Value, inspection.Ry!.Value, inspection.Rz!.Value,
        };
        return (target, "검사 준비 위치로");
    }

    /// <summary>u/v 오프셋 합성용 앵커 정규화 — 티칭 자세가 광축(툴 Z) 둘레로 비틀려 저장돼 있어도
    /// (예: 수직 모드 RZ−90° 상태에서 재티칭) 툴 +Y가 베이스 상방(+Z)을 향하도록 트위스트를 제거한다.
    /// u=수평/v=수직 매핑과 수평/수직(RZ) 회전, ④의 이미지↔툴 축 매핑은 모두 이 표준 자세를 전제하므로,
    /// 정규화 없이는 비틀린 티칭에서 u/v가 90° 돌아간 축으로 움직인다.
    /// 광축이 연직에 가까우면(상방 성분의 XY 투영이 미소) 기준이 모호해 티칭 자세를 그대로 둔다.</summary>
    internal static double[] NormalizeUvAnchor(double[] target, ILogger? logger = null)
    {
        var m = FrameMath.PoseToMatrix(target);
        // 베이스 +Z(상방)의 툴 X/Y축 성분: up·x̂_t = R[2,0], up·ŷ_t = R[2,1]
        double ux = m[2, 0], uy = m[2, 1];
        var n = Math.Sqrt(ux * ux + uy * uy);
        if (n < 0.2) return target;                       // 광축이 연직 근방 — 정규화 불가, 티칭 자세 유지
        // T' = T·Rz(θ): 새 툴 Y=(−sinθ, cosθ)가 상방 투영(ux,uy)와 나란하도록 θ = atan2(−ux, uy)
        var theta = Math.Atan2(-ux, uy) * 180.0 / Math.PI;
        if (Math.Abs(theta) < 1.0) return target;         // 이미 정렬(±1°)
        logger?.LogInformation(
            "② u/v 앵커 정규화: 티칭 자세 광축 트위스트 {Theta:0.#}° 제거 (툴 +Y → 베이스 상방 정렬)", theta);
        return FrameMath.FromFrame(new[] { 0.0, 0.0, 0.0, 0.0, 0.0, theta }, target);
    }

    public async Task<StepResult> ExecuteAsync(SequenceContext context, CancellationToken ct)
    {
        var inspection = FindBySurfaceId(context)
            ?? throw new InvalidOperationException($"Wall 0x{context.InspectionSurfaceId:X2} 티칭 위치 없음");
        var (target, where) = await ComputeTargetPoseAsync(_cobot, inspection, ct);
        target = NormalizeUvAnchor(target, _logger);
        where = $"[0x{context.InspectionSurfaceId:X2} {inspection.Name}] {where}";

        // 툴프레임 오프셋: offset[0]=u(툴 X = 수평, 좌+/우−), offset[1]=v(툴 Y = 수직, 상+/하−).
        // 실측 확인 매핑 — 과거 [v, u] 순서는 v 가 수평으로 나가는 축 교차 오류였음.
        // 수직 검사 방향이면 툴 RZ −90° 회전을 합성 (병진 u/v는 회전 전 대기자세 축 기준이라 의미 불변).
        var rz = context.InspectionDirection == InspectionMoveDirection.Vertical ? -90.0 : 0.0;
        var offset = new[] { context.InspectionOffsetU, context.InspectionOffsetV, 0.0, 0.0, 0.0, rz };
        var hasOffset = context.InspectionOffsetU != 0 || context.InspectionOffsetV != 0;

        var rc = await _cobot.Rpc.MoveByToolOffsetAsync(target, user: 0, offset,
            tool: context.Tool, vel: context.Velocity, ct: ct);

        var offsetNote = hasOffset
            ? $" (오프셋 u={context.InspectionOffsetU:0.###}, v={context.InspectionOffsetV:0.###} mm)"
            : "";
        if (rz != 0)
            offsetNote += " [수직, RZ−90°]";

        return rc == 0
            ? StepResult.Ok($"{where} 이동 완료{offsetNote}.")
            : StepResult.Fail($"이동 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}.");
    }
}
