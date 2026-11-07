using System.Globalization;
using HD.AMR.App.Communication;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service.Sequence.Steps;

/// <summary>수동 비드 입력 팝업 조그 방향 — 화면(영상) 기준. 카메라가 그 방향으로 움직인다.</summary>
public enum BeadJogDirection { Left, Right, Up, Down }

/// <summary>
/// 수동 비드 입력 팝업의 <b>조그</b> — 화면에 비드가 안 보일 때 카메라를 좌/우/상/하로 옮겨 찾는다.
/// <b>툴 좌표계 평면 이동만</b> 한다(광축 방향·회전 없음): 영상 +X(화면 오른쪽)·+Y(화면 아래)를 툴축에 싣는 카메라 페이지 매핑
/// (<see cref="WeldSequenceSupport.ImageXAxisKey"/>·<see cref="WeldSequenceSupport.ImageYAxisKey"/>, 미설정이면 툴 +X·+Y)을
/// 그대로 쓰므로 버튼 방향과 화면 이동 방향이 맞는다. 속도·1회 이동량은 파라미터(<see cref="BeadManualSettings.JogVelPctKey"/>,
/// <see cref="BeadManualSettings.JogStepMmKey"/>)와 연결된다.
/// </summary>
public sealed class BeadManualJogService
{
    private readonly CobotService _cobot;
    private readonly ParameterService _param;
    private readonly ILogger<BeadManualJogService> _logger;

    public BeadManualJogService(CobotService cobot, ParameterService param, ILogger<BeadManualJogService> logger)
    {
        _cobot = cobot;
        _param = param;
        _logger = logger;
    }

    /// <summary>저장된 조그 속도[%]·1회 이동량[mm]. 없거나 범위 밖이면 기본값/범위로.</summary>
    public async Task<(double VelPct, double StepMm)> GetSettingsAsync()
    {
        double vel = BeadManualSettings.DefaultJogVelPct, step = BeadManualSettings.DefaultJogStepMm;
        try
        {
            vel = await _param.GetDoubleAsync(BeadManualSettings.JogVelPctKey) ?? vel;
            step = await _param.GetDoubleAsync(BeadManualSettings.JogStepMmKey) ?? step;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "수동 비드 조그 설정 읽기 실패 — 기본값 사용"); }
        return (BeadManualSettings.ClampJogVelPct(vel), BeadManualSettings.ClampJogStepMm(step));
    }

    /// <summary>팝업에서 바꾼 값을 파라미터에 저장한다(범위로 자른 값).</summary>
    public async Task SaveSettingsAsync(double velPct, double stepMm)
    {
        var desc = BeadManualSettings.SeedDefaults().ToDictionary(r => r.Name, r => r.Desc);
        await _param.SetAsync(BeadManualSettings.JogVelPctKey,
            BeadManualSettings.ClampJogVelPct(velPct).ToString(CultureInfo.InvariantCulture), desc[BeadManualSettings.JogVelPctKey]);
        await _param.SetAsync(BeadManualSettings.JogStepMmKey,
            BeadManualSettings.ClampJogStepMm(stepMm).ToString(CultureInfo.InvariantCulture), desc[BeadManualSettings.JogStepMmKey]);
    }

    /// <summary>화면 방향 → 영상 평면 이동량(x = 화면 오른쪽 +, y = 화면 아래 +) [mm].</summary>
    public static (double XMm, double YMm) ImageShift(BeadJogDirection dir, double stepMm) => dir switch
    {
        BeadJogDirection.Left => (-stepMm, 0),
        BeadJogDirection.Right => (stepMm, 0),
        BeadJogDirection.Up => (0, -stepMm),
        _ => (0, stepMm),
    };

    public static string Name(BeadJogDirection dir) => dir switch
    {
        BeadJogDirection.Left => "좌",
        BeadJogDirection.Right => "우",
        BeadJogDirection.Up => "상",
        _ => "하",
    };

    /// <summary>
    /// 현재 TCP 에서 툴 좌표계 평면으로 <paramref name="stepMm"/> 만큼 움직인다(MoveL, 툴 오프셋).
    /// 반환: (성공 여부, 사용자에게 보일 메시지). 코봇 미연결·이동 실패는 예외 대신 실패로 돌려준다.
    /// </summary>
    /// <param name="tool">공구 번호 — null 이면 코봇 기본 공구.</param>
    public async Task<(bool Ok, string Message)> JogAsync(BeadJogDirection dir, double stepMm, double velPct,
        int? tool, CancellationToken ct = default)
    {
        if (!_cobot.IsConnected) return (false, "코봇 미연결 — 조그할 수 없습니다.");
        stepMm = BeadManualSettings.ClampJogStepMm(stepMm);
        velPct = BeadManualSettings.ClampJogVelPct(velPct);

        var (imageX, _) = await WeldSequenceSupport.GetImageXAxisAsync(_param);
        var (imageY, _) = await WeldSequenceSupport.GetImageYAxisAsync(_param);
        var (x, y) = ImageShift(dir, stepMm);
        var offset = WeldSequenceSupport.ImageShiftToToolOffset(imageX, imageY, x, y);

        try
        {
            var t = tool ?? (await _cobot.Rpc.ResolveActiveFramesAsync(ct, strict: false)).tool;
            var anchor = await _cobot.Rpc.GetTcpPoseInBaseAsync(t, ct);
            var rc = await _cobot.Rpc.MoveByToolOffsetAsync(anchor, user: 0, offset, tool: t, vel: velPct, ct: ct);
            var axisNote = $"툴 [{string.Join(", ", offset.Take(3).Select(v => v.ToString("+0.0;-0.0;0")))}]mm";
            if (rc != 0)
            {
                _logger.LogWarning("수동 비드 조그 {Dir} {Step}mm 실패 rc={Rc}", Name(dir), stepMm, rc);
                return (false, $"조그 {Name(dir)} 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}");
            }
            _logger.LogInformation("수동 비드 조그 {Dir} {Step}mm ({Axis}, 속도 {Vel}%, tool {Tool})",
                Name(dir), stepMm, axisNote, velPct, t);
            return (true, $"조그 {Name(dir)} {stepMm:0.#}mm 완료 ({axisNote})");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "수동 비드 조그 {Dir} 실패", Name(dir));
            return (false, $"조그 {Name(dir)} 실패: {ex.Message}");
        }
    }
}
