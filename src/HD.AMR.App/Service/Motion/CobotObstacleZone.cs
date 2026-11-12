using System.Globalization;
using System.Text.Json;
using HD.AMR.App.Communication.Vda5050;

namespace HD.AMR.App.Service.Motion;

/// <summary>
/// 주행 중 코봇 반경 장애물 감시의 판정부(순수 함수) — AMR 자체 장애물 감지는 차체만 보므로, 차체 밖으로 나온
/// 코봇 근처에 라이다 점이 들어오면 장애물로 본다.
/// <para>
/// <b>감시 영역.</b> 기본은 코봇을 바닥에 투영한 외곽(<see cref="CobotEnvelope.Footprint"/>) + 여유다. 홈 자세에서 검사 툴은
/// 차체 중심에서 전방으로 약 1.03 m 나오지만 좌우는 차체 폭 안팎(최대 약 0.41 m)이라, 원으로 잡으면(반경 1.1 m) 벽 옆 정차(벽에서
/// 0.8~1.2 m)마다 벽이 걸린다. 반경(<see cref="RadiusKey"/>)을 직접 주면 그 원을 쓴다.
/// </para>
/// <para>
/// <b>한계.</b> 라이다는 2D 한 대(전방 약 ±135°, 바닥 근처 한 높이)다 — 후방과 코봇 높이에만 있는 장애물은 못 본다.
/// REST 폴링이라 반응도 수백 ms 늦다. 안전 기능이 아니라 운영 보조다(안전은 AMR 세이프티 존·EMO·코봇 충돌 감지).
/// </para>
/// </summary>
public static class CobotObstacleZone
{
    /// <summary>state.errors[] 에 싣는 errorType(WARNING) — 장애물이 반경 안에 있는 동안만.</summary>
    public const string ErrorType = "cobotZoneObstacle";

    public const string EnabledKey = "cobotGuard.enabled";
    public const string MarginKey = "cobotGuard.marginMm";
    public const string RadiusKey = "cobotGuard.radiusMm";
    public const string MinPointsKey = "cobotGuard.minPoints";
    public const string PollKey = "cobotGuard.pollMs";

    public const double DefaultMarginMm = 100;
    public const int DefaultMinPoints = 3;
    public const int DefaultPollMs = 400;

    /// <summary>코봇 관절값을 못 읽었을 때 쓰는 자세 — 홈 위치_2(현장 티칭값). 주행 중 자세가 이것이라고 본다.</summary>
    public static readonly double[] FallbackJointsDeg = { 0.158, 59.456, -140.907, 80.848, 90.048, -179.696 };

    /// <summary>장착 보정(T_A_B)이 없을 때 쓰는 장착 yaw [도] — 실기 값.</summary>
    public const double FallbackMountYawDeg = 180;

    /// <summary>차체 상자를 이만큼 부풀린 안쪽 점은 차체 자신·부착물의 반사로 보고 버린다 [mm].</summary>
    public const double SelfIgnoreMm = 50;

    public static IEnumerable<(string Name, string Value, string Desc)> SeedDefaults() =>
    [
        (EnabledKey, "false",
            "주행 중 코봇 반경 장애물 감시 — true 면 ACS 지령 주행 중 라이다 점이 코봇 반경 안에 들어오면 AMR 을 세우고 경고 알람을 낸다. " +
            "장애물이 사라지면 같은 목표로 다시 출발한다. 라이다가 2D 한 대(전방 ±135°, 바닥 근처 높이)라 후방·코봇 높이 장애물은 못 본다."),
        (RadiusKey, "0",
            "코봇 반경 장애물 감시 — 차체 중심 기준 감시 반경(mm). 0 = 코봇 현재 자세의 바닥 투영 외곽을 쓴다(권장 — 홈 자세는 " +
            "전방으로 약 1.03m, 좌우는 차체 폭 안팎). 값을 주면 그 반경의 원으로 감시한다(홈 자세를 다 덮으려면 1100 이상, 벽 옆에서 자주 선다)."),
        (MarginKey, DefaultMarginMm.ToString(CultureInfo.InvariantCulture),
            "코봇 반경 장애물 감시 — 코봇 외곽(또는 반경)에 더하는 여유(mm). 크게 잡으면 벽을 보고 서는 정차에서 벽을 장애물로 본다. 0~1000."),
        (MinPointsKey, DefaultMinPoints.ToString(CultureInfo.InvariantCulture),
            "코봇 반경 장애물 감시 — 감시 영역 안 라이다 점이 이 수 이상이고 두 번 연속 잡혀야 장애물로 본다(잡음 제거). 1~50."),
        (PollKey, DefaultPollMs.ToString(CultureInfo.InvariantCulture),
            "코봇 반경 장애물 감시 — 라이다 조회 주기(ms). 200~2000."),
    ];

    /// <param name="RadiusMm">0 이하면 코봇 자세의 바닥 투영 외곽을 쓴다.</param>
    public sealed record Options(bool Enabled, double RadiusMm, double MarginMm, int MinPoints, int PollMs)
    {
        public static Options Off { get; } = new(false, 0, DefaultMarginMm, DefaultMinPoints, DefaultPollMs);

        /// <summary>파라미터 원문 → 옵션. 비었거나 범위 밖이면 기본값.</summary>
        public static Options From(bool? enabled, double? radiusMm, double? marginMm, int? minPoints, int? pollMs) => new(
            enabled ?? false,
            radiusMm is > 0 and <= 3000 ? radiusMm.Value : 0,
            marginMm is >= 0 and <= 1000 ? marginMm.Value : DefaultMarginMm,
            minPoints is >= 1 and <= 50 ? minPoints.Value : DefaultMinPoints,
            pollMs is >= 200 and <= 2000 ? pollMs.Value : DefaultPollMs);
    }

    /// <summary>반경 안에서 잡힌 장애물. 좌표는 차체 기준 mm(+X 전방 · +Y 좌측).</summary>
    public sealed record Hit(int Count, double NearestMm, double XMm, double YMm)
    {
        /// <summary>가장 가까운 점의 방향 [도] — 0 = 정면, + = 왼쪽.</summary>
        public double BearingDeg => Math.Atan2(YMm, XMm) * 180 / Math.PI;

        public string Describe()
        {
            var b = BearingDeg;
            var side = Math.Abs(b) <= 30 ? "전방" : Math.Abs(b) >= 150 ? "후방" : b > 0 ? "좌측" : "우측";
            return $"{side} {NearestMm / 1000:0.00}m (방향 {b:+0;-0}°, 점 {Count}개)";
        }
    }

    /// <summary>
    /// 감시 영역 — 차체 좌표 mm. <see cref="Hull"/> 이 있으면 그 다각형 + 여유, 없으면 차체 중심 원(<see cref="RadiusMm"/> + 여유).
    /// </summary>
    public sealed record Zone(IReadOnlyList<(double X, double Y)>? Hull, double RadiusMm, double MarginMm)
    {
        /// <summary>영역이 차체 중심에서 가장 멀리 나간 거리 [mm](여유 포함).</summary>
        public double ReachMm => (Hull is { Count: > 0 } h ? h.Max(p => Math.Sqrt(p.X * p.X + p.Y * p.Y)) : RadiusMm) + MarginMm;

        /// <summary>점이 영역 안인가 — 영역 경계까지 남은 거리가 여유 이하.</summary>
        public bool Contains(double x, double y) => Hull is { Count: > 0 } h
            ? CobotEnvelope.DistanceToConvex(h, x, y) <= MarginMm
            : Math.Sqrt(x * x + y * y) <= RadiusMm + MarginMm;

        public string Describe()
        {
            if (Hull is not { Count: > 0 } h) return $"반경 {(RadiusMm + MarginMm) / 1000:0.00}m";
            return $"코봇 외곽(전방 {h.Max(p => p.X) / 1000:0.00}m · 후방 {-h.Min(p => p.X) / 1000:0.00}m · " +
                   $"좌 {h.Max(p => p.Y) / 1000:0.00}m · 우 {-h.Min(p => p.Y) / 1000:0.00}m) + 여유 {MarginMm / 1000:0.00}m";
        }
    }

    /// <summary>
    /// 감시 영역 — 반경을 지정했으면 그 원, 아니면 관절값(없으면 <see cref="FallbackJointsDeg"/>)의 바닥 투영 외곽.
    /// </summary>
    public static Zone ResolveZone(Options options, double[]? jointsDeg, double? mountYawDeg,
        CobotEnvelope envelope, Fr5Kinematics kin)
    {
        if (options.RadiusMm > 0) return new Zone(null, options.RadiusMm, options.MarginMm);
        var joints = jointsDeg is { Length: 6 } ? jointsDeg : FallbackJointsDeg;
        return new Zone(envelope.Footprint(kin, joints, mountYawDeg ?? FallbackMountYawDeg), 0, options.MarginMm);
    }

    /// <summary>
    /// 라이다 점(맵 좌표 m) 가운데 감시 영역 안에 든 점을 센다.
    /// 차체 상자(+<see cref="SelfIgnoreMm"/>) 안쪽 점은 버린다. <paramref name="minPoints"/> 미만이면 null.
    /// </summary>
    public static Hit? Detect(double poseX, double poseY, double poseRz, IReadOnlyList<(double X, double Y)> lidarMap,
        Zone zone, CobotEnvelope envelope, int minPoints)
    {
        double c = Math.Cos(poseRz), s = Math.Sin(poseRz);
        var reach = zone.ReachMm;
        var count = 0;
        double best = double.PositiveInfinity, bx = 0, by = 0;
        foreach (var (mx, my) in lidarMap)
        {
            double dx = (mx - poseX) * 1000, dy = (my - poseY) * 1000;
            double fx = c * dx + s * dy, fy = -s * dx + c * dy;   // 맵 → 차체
            var d = Math.Sqrt(fx * fx + fy * fy);
            if (d > reach) continue;
            if (fx >= -envelope.AmrRearMm - SelfIgnoreMm && fx <= envelope.AmrFrontMm + SelfIgnoreMm
                && Math.Abs(fy) <= envelope.AmrHalfWidthMm + SelfIgnoreMm)
                continue;
            if (!zone.Contains(fx, fy)) continue;
            count++;
            if (d < best) { best = d; bx = fx; by = fy; }
        }
        return count >= Math.Max(1, minPoints) ? new Hit(count, best, bx, by) : null;
    }

    /// <summary><c>GET /robot/pose</c> 의 data → 포즈와 라이다 점(맵 좌표 m). 포즈가 없으면 false.</summary>
    public static bool TryParsePose(JsonElement data, out double x, out double y, out double rz,
        out List<(double X, double Y)> lidar)
    {
        x = y = rz = 0;
        lidar = new List<(double, double)>();
        if (data.ValueKind != JsonValueKind.Object) return false;
        if (!Num(data, "x", out x) || !Num(data, "y", out y) || !Num(data, "rz", out rz)) return false;
        if (data.TryGetProperty("lidar", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            lidar.Capacity = arr.GetArrayLength();
            foreach (var p in arr.EnumerateArray())
                if (Num(p, "x", out var lx) && Num(p, "y", out var ly))
                    lidar.Add((lx, ly));
        }
        return true;
    }

    /// <summary>state.errors[] 항목 — 장애물이 반경 안에 있는 동안만 싣는다(실패 아님, AMR 이 기다리는 중).</summary>
    public static VdaError ToError(CobotObstacleAlert alert) => new()
    {
        ErrorType = ErrorType,
        ErrorLevel = "WARNING",
        ErrorDescription = $"코봇 반경 장애물 감지 — 주행 정지: {alert.Hit.Describe()}, 감시 영역 {alert.Zone.Describe()} " +
                           $"({alert.SinceUtc.ToLocalTime():HH:mm:ss} 부터). 장애물이 사라지면 다시 출발합니다.",
        ErrorReferences = new List<VdaErrorReference>
        {
            new() { ReferenceKey = "distanceMm", ReferenceValue = alert.Hit.NearestMm.ToString("0", CultureInfo.InvariantCulture) },
            new() { ReferenceKey = "bearingDeg", ReferenceValue = alert.Hit.BearingDeg.ToString("0", CultureInfo.InvariantCulture) },
        },
    };

    private static bool Num(JsonElement obj, string name, out double value)
    {
        value = 0;
        return obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var p)
               && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out value) && double.IsFinite(value);
    }
}

/// <summary>지금 감시 영역 안에 있는 장애물과 감지 시각·감시 영역.</summary>
public sealed record CobotObstacleAlert(CobotObstacleZone.Hit Hit, CobotObstacleZone.Zone Zone, DateTime SinceUtc);
