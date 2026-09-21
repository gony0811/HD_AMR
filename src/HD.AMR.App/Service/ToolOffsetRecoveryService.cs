using HD.AMR.App.Communication;
using HD.AMR.App.Data.Entities;
using HD.AMR.App.Models;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service;

/// <summary>
/// 지워진 공구(tool) 좌표계 정의를 <b>교시 위치</b>로부터 되짚는다.
///
/// 교시 위치(<see cref="TeachingPosition"/>)는 캡처 당시의 TCP pose(해당 tool 기준, BASE)와 관절각 J1~J6 을 함께
/// 저장한다. 같은 관절각에서 플랜지(tool 0) pose 를 컨트롤러 정기구학으로 구하면
/// <c>offset = inv(T_플랜지) · T_TCP</c> 가 그 공구의 정의(플랜지 → TCP, 회전 포함)다. 로봇을 움직이지 않는다.
///
/// 행이 여럿이면 값이 서로 일치해야 한다. 어긋나면 그 사이에 공구를 재정의한 것이므로 최신 행을 권장한다.
/// 공구 하중(질량·무게중심)·설치 방향 같은 부가 설정은 복원되지 않는다.
/// 결과를 컨트롤러에 쓰는 것은 호출측(UI)의 명시적 조작으로만 한다.
/// </summary>
public class ToolOffsetRecoveryService
{
    /// <summary>행 간 오프셋 불일치 경고 임계 — 이보다 크면 공구가 재정의됐거나 표본이 오염된 것.</summary>
    public const double SpreadWarnMm = 0.5;
    public const double SpreadWarnDeg = 0.1;

    private readonly CobotService _cobot;
    private readonly TeachingService _teaching;
    private readonly ILogger<ToolOffsetRecoveryService> _logger;

    public ToolOffsetRecoveryService(CobotService cobot, TeachingService teaching, ILogger<ToolOffsetRecoveryService> logger)
    {
        _cobot = cobot; _teaching = teaching; _logger = logger;
    }

    /// <summary>플랜지 pose 와 TCP pose(둘 다 BASE 기준)로 공구 오프셋(플랜지 → TCP) 계산: inv(T_F) · T_T.</summary>
    public static double[] ComputeOffset(double[] flangeBase, double[] tcpBase)
        => FrameMath.MatrixToPose(FrameMath.Multiply(
            FrameMath.Invert(FrameMath.PoseToMatrix(flangeBase)), FrameMath.PoseToMatrix(tcpBase)));

    /// <summary>오프셋 목록의 쌍별 최대 위치 차(mm)·회전 차(도).</summary>
    public static (double PosMm, double RotDeg) Spread(IReadOnlyList<double[]> offsets)
    {
        double pos = 0, rot = 0;
        for (int i = 0; i < offsets.Count; i++)
            for (int j = i + 1; j < offsets.Count; j++)
            {
                pos = Math.Max(pos, FrameMath.DistanceMm(offsets[i], offsets[j]));
                rot = Math.Max(rot, FrameMath.RelativeRotationDeg(offsets[i], offsets[j]));
            }
        return (pos, rot);
    }

    /// <summary>
    /// 공구 <paramref name="tool"/> 로 캡처된 모든 교시 위치에서 오프셋을 계산한다. 예외를 던지지 않고
    /// 결과 객체로 돌려준다(취소는 예외). 컨트롤러 연결이 필요하다(정기구학 RPC).
    /// </summary>
    public async Task<ToolOffsetRecoveryResult> RecoverAsync(int tool, CancellationToken ct = default)
    {
        if (tool <= 0)
            return ToolOffsetRecoveryResult.Fail(tool, "공구 번호는 1 이상이어야 합니다(0 은 플랜지).");
        if (!_cobot.IsConnected)
            return ToolOffsetRecoveryResult.Fail(tool, "코봇 미연결 — 플랜지 pose 계산에 컨트롤러 정기구학이 필요합니다.");

        List<TeachingPosition> all;
        try { all = await _teaching.ListAsync(ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return ToolOffsetRecoveryResult.Fail(tool, $"교시 위치 목록 조회 실패: {ex.Message}"); }

        var usable = all
            .Where(p => p.Tool == tool && p.IsTaught && HasJoints(p))
            .OrderByDescending(p => p.CapturedAt ?? DateTime.MinValue)
            .ToList();
        if (usable.Count == 0)
            return ToolOffsetRecoveryResult.Fail(tool,
                $"tool {tool} 로 캡처된(관절각 포함) 교시 위치가 없습니다 — 복원할 근거가 없습니다.");

        var rows = new List<ToolOffsetRecoveryRow>();
        var errors = new List<string>();
        foreach (var p in usable)
        {
            ct.ThrowIfCancellationRequested();
            var joints = new[] { p.J1!.Value, p.J2!.Value, p.J3!.Value, p.J4!.Value, p.J5!.Value, p.J6!.Value };
            var tcp = new[] { p.X!.Value, p.Y!.Value, p.Z!.Value, p.Rx!.Value, p.Ry!.Value, p.Rz!.Value };
            try
            {
                var flange = await _cobot.Rpc.GetToolPoseInBaseAtJointsAsync(joints, 0, ct);
                var offset = ComputeOffset(flange, tcp);
                rows.Add(new ToolOffsetRecoveryRow(p.Key, p.Name, p.CapturedAt, offset));
                _logger.LogInformation("공구 #{Tool} 오프셋 복원: {Key} → [{Off}] (플랜지 [{F}], TCP [{T}])",
                    tool, p.Key,
                    string.Join(",", offset.Select(v => v.ToString("0.###"))),
                    string.Join(",", flange.Select(v => v.ToString("0.##"))),
                    string.Join(",", tcp.Select(v => v.ToString("0.##"))));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                errors.Add($"{p.Key}: {ex.Message}");
                _logger.LogWarning(ex, "공구 #{Tool} 오프셋 복원: {Key} 정기구학 실패", tool, p.Key);
            }
        }

        if (rows.Count == 0)
            return ToolOffsetRecoveryResult.Fail(tool, "플랜지 pose 계산이 모두 실패했습니다: " + string.Join("; ", errors));

        var (posSpread, rotSpread) = Spread(rows.Select(r => r.Offset).ToList());
        var warnings = new List<string>();
        if (errors.Count > 0)
            warnings.Add($"일부 행은 계산하지 못했습니다: {string.Join("; ", errors)}");
        if (rows.Count == 1)
            warnings.Add("교시 위치가 1개뿐이라 교차 검증이 없습니다 — 적용 후 조그 정확도로 확인하세요.");
        else if (posSpread > SpreadWarnMm || rotSpread > SpreadWarnDeg)
            warnings.Add($"행 간 오프셋이 어긋납니다(위치 {posSpread:0.##}mm, 회전 {rotSpread:0.###}°) — " +
                         "그 사이에 공구를 재정의한 것으로 보입니다. 가장 최근 행(권장값)을 쓰세요.");

        return new ToolOffsetRecoveryResult(true, null, tool, rows, (double[])rows[0].Offset.Clone(),
            posSpread, rotSpread, warnings);
    }

    private static bool HasJoints(TeachingPosition p)
        => p.J1.HasValue && p.J2.HasValue && p.J3.HasValue && p.J4.HasValue && p.J5.HasValue && p.J6.HasValue
           && p.Rx.HasValue && p.Ry.HasValue && p.Rz.HasValue;
}
