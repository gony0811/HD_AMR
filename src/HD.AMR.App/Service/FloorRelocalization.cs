using HD.AMR.App.Models;

namespace HD.AMR.App.Service;

/// <summary>
/// 층 전환 재측위의 <b>순수 계층</b> — Parameters 키 규약, 값 로드, 수렴 판정. 하드웨어 없이 테스트한다.
///
/// 키 규약(SETTINGS ▸ Parameters 표에 직접 입력):
/// <list type="table">
/// <item><c>Floor.{mapId}.InitPose.X</c> / <c>.Y</c> — 통합맵(SLAM) 좌표 [m]. 엘리베이터 하차 위치.</item>
/// <item><c>Floor.{mapId}.InitPose.ThetaDeg</c> — 하차 방향 [deg], +X 기준 CCW(지도 화면 각도 규약과 동일).</item>
/// <item><c>Floor.Verify.*</c> — 공통 검증 기준(없으면 기본값).</item>
/// </list>
/// </summary>
public static class FloorRelocalization
{
    public static string KeyX(string mapId) => $"Floor.{mapId}.InitPose.X";
    public static string KeyY(string mapId) => $"Floor.{mapId}.InitPose.Y";
    public static string KeyThetaDeg(string mapId) => $"Floor.{mapId}.InitPose.ThetaDeg";

    public const string KeyMinMapMatchPercent = "Floor.Verify.MinMapMatchPercent";
    public const string KeyMaxPosErrorM = "Floor.Verify.MaxPosErrorM";
    public const string KeyMaxAngleErrorDeg = "Floor.Verify.MaxAngleErrorDeg";
    public const string KeyTimeoutSec = "Floor.Verify.TimeoutSec";

    /// <summary>층 initpose 로드. 세 값 중 하나라도 없거나 비유한값이면 null(미등록).</summary>
    public static async Task<FloorInitPose?> LoadInitPoseAsync(ParameterService param, string mapId)
    {
        var x = await param.GetDoubleAsync(KeyX(mapId));
        var y = await param.GetDoubleAsync(KeyY(mapId));
        var t = await param.GetDoubleAsync(KeyThetaDeg(mapId));
        if (x is not { } vx || y is not { } vy || t is not { } vt) return null;
        if (!double.IsFinite(vx) || !double.IsFinite(vy) || !double.IsFinite(vt)) return null;
        return new FloorInitPose(vx, vy, vt);
    }

    /// <summary>검증 기준 로드. 맵 일치율 기본값은 VDA <c>positionInitialized</c> 판정 임계값과 같게 둔다.</summary>
    public static async Task<FloorVerifyCriteria> LoadCriteriaAsync(ParameterService param, double defaultMinMapMatch)
    {
        var d = new FloorVerifyCriteria { MinMapMatchPercent = defaultMinMapMatch };
        return d with
        {
            MinMapMatchPercent = Positive(await param.GetDoubleAsync(KeyMinMapMatchPercent)) ?? d.MinMapMatchPercent,
            MaxPosErrorM = Positive(await param.GetDoubleAsync(KeyMaxPosErrorM)) ?? d.MaxPosErrorM,
            MaxAngleErrorDeg = Positive(await param.GetDoubleAsync(KeyMaxAngleErrorDeg)) ?? d.MaxAngleErrorDeg,
            TimeoutSec = Positive(await param.GetDoubleAsync(KeyTimeoutSec)) ?? d.TimeoutSec,
        };
    }

    private static double? Positive(double? v) => v is { } x && double.IsFinite(x) && x > 0 ? x : null;

    /// <summary>상태 스냅샷 1건 판정 — 맵 일치율·위치 편차·각도 편차(래핑 고려) 모두 기준 이내여야 통과.</summary>
    public static FloorVerifyEval Evaluate(RobotPose pose, double mapMatchPercent, FloorInitPose target,
        FloorVerifyCriteria c)
    {
        double dx = pose.X - target.X, dy = pose.Y - target.Y;
        double posErr = Math.Sqrt(dx * dx + dy * dy);
        double angErrDeg = Math.Abs(NormalizeRad(pose.Angle - target.ThetaRad)) * 180 / Math.PI;
        return new FloorVerifyEval(mapMatchPercent, posErr, angErrDeg,
            mapMatchPercent >= c.MinMapMatchPercent, posErr <= c.MaxPosErrorM, angErrDeg <= c.MaxAngleErrorDeg, c);
    }

    private static double NormalizeRad(double a)
    {
        a %= 2 * Math.PI;
        if (a > Math.PI) a -= 2 * Math.PI;
        else if (a < -Math.PI) a += 2 * Math.PI;
        return a;
    }
}

/// <summary>층 initpose — 통합맵 좌표 [m], 방향 [deg].</summary>
public sealed record FloorInitPose(double X, double Y, double ThetaDeg)
{
    public double ThetaRad => ThetaDeg * Math.PI / 180;
}

public sealed record FloorVerifyCriteria
{
    /// <summary>맵 일치율 하한 [%].</summary>
    public double MinMapMatchPercent { get; init; } = 30;
    /// <summary>initpose 대비 위치 편차 상한 [m] — 엘리베이터 하차 재현성 + SLAM 수렴 오차.</summary>
    public double MaxPosErrorM { get; init; } = 0.5;
    /// <summary>initpose 대비 각도 편차 상한 [deg].</summary>
    public double MaxAngleErrorDeg { get; init; } = 10;
    /// <summary>검증 대기 시간 [s].</summary>
    public double TimeoutSec { get; init; } = 20;
    /// <summary>PoseSearch 쓰기 후 통과로 세기 시작하는 최소 시간 [s] — 반영 전 스냅샷 오판 방지.</summary>
    public double MinSettleSec { get; init; } = 2;
    /// <summary>연속 통과해야 하는 새 상태 스냅샷 수.</summary>
    public int StablePolls { get; init; } = 3;
}

public sealed record FloorVerifyEval(double MapMatchPercent, double PosErrorM, double AngleErrorDeg,
    bool MapOk, bool PosOk, bool AngleOk, FloorVerifyCriteria Criteria)
{
    public bool Pass => MapOk && PosOk && AngleOk;

    public string Describe() =>
        $"맵 일치율 {MapMatchPercent:0.0}%{Mark(MapOk)}(≥{Criteria.MinMapMatchPercent:0}), " +
        $"위치 편차 {PosErrorM:0.000} m{Mark(PosOk)}(≤{Criteria.MaxPosErrorM:0.###}), " +
        $"각도 편차 {AngleErrorDeg:0.0}°{Mark(AngleOk)}(≤{Criteria.MaxAngleErrorDeg:0.#})";

    private static string Mark(bool ok) => ok ? " ✓" : " ✗";
}
