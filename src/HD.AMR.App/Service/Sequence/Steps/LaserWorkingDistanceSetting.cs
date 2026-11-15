using HD.AMR.App.Models;

namespace HD.AMR.App.Service.Sequence.Steps;

/// <summary>
/// 레이저 WD 목표 거리(mm) 조회 — 면별 값이 있으면 그것을, 없으면 전체 공통 값을 쓴다.
/// <list type="bullet">
/// <item>면별 키: <c>Sequence.Laser.WorkingDistanceMm.{wall_code}</c> (예: <c>….T</c>, <c>….PU</c>, <c>….SU</c>)</item>
/// <item>공통 키: <see cref="LaserWorkingDistanceStep.WorkingDistanceKey"/></item>
/// <item>둘 다 없으면 <see cref="LaserWorkingDistanceStep.DefaultWorkingDistanceMm"/></item>
/// </list>
/// 면마다 캡처 화면이 맞는 거리가 달라(원인 조사 중) 면별로 따로 맞출 수 있게 둔 것이다. 면별 행을 지우면 공통 값으로 돌아간다.
/// ④⁺ WD 조정(<see cref="LaserWorkingDistanceStep"/>)과 도달 사전 점검(<c>CobotReachPrecheck</c>)이 같은 함수를 써야
/// 점검한 정렬 준비 거리와 실제로 맞추는 거리가 같다.
/// </summary>
public static class LaserWorkingDistanceSetting
{
    /// <summary>면별 키 — wall_code 가 알려진 10면이 아니면 null(공통 값만 본다).</summary>
    public static string? KeyFor(string? wallCode)
    {
        var info = WallCodes.Find(wallCode?.Trim().ToUpperInvariant());
        return info is null ? null : $"{LaserWorkingDistanceStep.WorkingDistanceKey}.{info.Code}";
    }

    /// <summary>시퀀스 컨텍스트의 면 — ACS wall_code 가 있으면 그것, 없으면 선택한 Surface ID 로 찾는다.</summary>
    public static string? WallOf(SequenceContext context)
        => string.IsNullOrWhiteSpace(context.WallCode)
            ? WallCodes.FindBySurfaceId(context.InspectionSurfaceId)?.Code
            : context.WallCode;

    /// <summary>
    /// 목표 거리와 그 값을 읽은 곳을 돌려준다. 읽기 실패(DB 오류)는 던지지 않고 기본값으로 돌린다 — 호출부가 범위를 검사한다.
    /// </summary>
    /// <returns>Mm = 목표 거리, Source = 값을 읽은 키(기본값이면 "기본값").</returns>
    public static async Task<(double Mm, string Source)> ResolveAsync(ParameterService param, string? wallCode)
    {
        try
        {
            if (KeyFor(wallCode) is { } wallKey && await param.GetDoubleAsync(wallKey).ConfigureAwait(false) is { } perWall)
                return (perWall, wallKey);
            if (await param.GetDoubleAsync(LaserWorkingDistanceStep.WorkingDistanceKey).ConfigureAwait(false) is { } common)
                return (common, LaserWorkingDistanceStep.WorkingDistanceKey);
            return (LaserWorkingDistanceStep.DefaultWorkingDistanceMm, "기본값");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (LaserWorkingDistanceStep.DefaultWorkingDistanceMm, $"기본값(읽기 실패: {ex.Message})");
        }
    }

    /// <summary>
    /// 면마다 맞는 WD 가 다른 원인을 찾기 위한 진단 한 줄 — 로그에서 "[WD 진단]" 으로 모아 면끼리 비교한다.
    /// <list type="bullet">
    /// <item>CH1~3 개별 거리와 최대−최소: 한 점이 주름 능선·경사에 걸리면 평균(평면 z)이 달라진다.</item>
    /// <item>틸트 원시/보정: ④ 뒤에 남은 기울기.</item>
    /// <item>광축 앙각과 툴 기준 중력 방향: 툴이 위를 볼 때(T)와 비스듬할 때(PU·SU) 브래킷이 휘는 방향이 다르다.</item>
    /// <item>관절각(J6 = 광축 둘레 비틀림)과 BASE 기준 TCP.</item>
    /// </list>
    /// 값이 없는 항목은 "-" 로 적는다(진단이 검사를 막지 않도록 호출부가 읽기 실패를 삼킨다).
    /// </summary>
    public static string Diagnostics(
        string phase, string? wallCode, InspectionMoveDirection direction, double targetMm, string source,
        IReadOnlyList<LaserChannelReading> readings, PlanePose raw, PlanePose corrected,
        double[]? jointsDeg, double[]? tcpBase, ToolAxisDir depthAxis)
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        string F(double v, string fmt = "0.00") => v.ToString(fmt, ci);

        var channels = readings.Count == 0
            ? "-"
            : string.Join(" ", readings.Select(r => $"CH{r.Channel}={F(r.Value)}{(r.Enabled ? "" : "(범위 밖)")}"));
        var valid = readings.Where(r => r.Enabled).Select(r => r.Value).ToArray();
        var spread = valid.Length >= 2 ? F(valid.Max() - valid.Min()) : "-";

        var plane = corrected.Valid
            ? $"z={F(corrected.Z)} 잔차={F(corrected.Z - targetMm, "+0.00;-0.00")}"
            : $"z=- ({corrected.Note})";
        var tilt = raw.Valid && corrected.Valid
            ? $"Rx={F(raw.Rx, "+0.000;-0.000")}/{F(corrected.Rx, "+0.000;-0.000")} Ry={F(raw.Ry, "+0.000;-0.000")}/{F(corrected.Ry, "+0.000;-0.000")}"
            : "-";

        string elevation = "-", gravity = "-", tcp = "-";
        if (tcpBase is { Length: >= 6 })
        {
            var t = Communication.PoseMath.FromPose(tcpBase);
            int col = (int)depthAxis / 2;
            double sign = (int)depthAxis % 2 == 0 ? 1.0 : -1.0;
            // 광축의 BASE z 성분 → 앙각(+90 = 위를 봄, 0 = 수평, −90 = 아래를 봄).
            // 반올림해 0 이 되는 값은 부호 없는 0 으로 적는다(−0.000 표기 방지).
            string S(double v, int digits)
            {
                v = Math.Round(v, digits);
                return F(v == 0 ? 0.0 : v, digits == 1 ? "+0.0;-0.0" : "+0.000;-0.000");
            }
            elevation = S(Math.Asin(Math.Clamp(sign * t[2, col], -1, 1)) * 180.0 / Math.PI, 1);
            // 중력(BASE −z)을 툴 좌표로: g_tool = Rᵀ·(0,0,−1) = −(R 의 셋째 행).
            gravity = $"({S(-t[2, 0], 3)},{S(-t[2, 1], 3)},{S(-t[2, 2], 3)})";
            tcp = "[" + string.Join(", ", tcpBase.Take(6).Select(v => F(v, "0.0"))) + "]";
        }
        var joints = jointsDeg is { Length: >= 6 }
            ? "[" + string.Join(", ", jointsDeg.Take(6).Select(v => F(v, "0.0"))) + "]"
            : "-";

        return $"[WD 진단] 단계={phase} 면={wallCode ?? "-"} 방향={direction} 목표={F(targetMm, "0.#")}mm({source}) " +
               $"레이저 {channels} 최대−최소={spread} 평면 {plane} 틸트(원시/보정) {tilt} " +
               $"광축 앙각={elevation}° 중력(툴)={gravity} 관절={joints} TCP(BASE)={tcp}";
    }
}
