using System.Globalization;
using System.Runtime.InteropServices;
using HD.AMR.App.Communication;
using HD.AMR.App.Models;

namespace HD.AMR.App.Service.Sequence.Steps;

/// <summary>
/// 작업물 좌표계 점1·점2 를 기록하는 순간의 면 거리 스냅샷과 두 점의 비교 — 진단 전용(동작을 바꾸지 않는다).
///
/// 점2 로 가는 이동(⑧ Peak2 이동, ACS 끝점 이동)은 <b>도면상의 면</b>에 평행하게 움직이고 점2 에서는 거리를 다시 재지 않는다.
/// 실제 면이 도면 면과 기울어 있으면(발판·리프트·장착 기울기, 면 자체의 기울기) 점2 의 면 거리가 점1 과 달라지고,
/// 작업물 X축(점1→점2)이 면 쪽으로 기울어 순회하는 동안 초점 거리가 변한다. 이 진단은 그 차이를 숫자로 남긴다.
///
/// 레이저 3점은 이 자리에서 주름에 걸릴 수 있어 절대값은 믿지 않는다. 다만 점1 과 점2 는 주름 한 피치 떨어진
/// 같은 모양의 자리라, <b>같은 채널끼리의 차이</b>는 면 거리 변화로 볼 수 있다. depth 카메라 중앙값도 같은 이유로 차이만 본다.
/// </summary>
/// <param name="TcpBase">기록 순간의 BASE 기준 TCP 포즈 [x,y,z,rx,ry,rz](비드 잔차 보정 전 — 센서가 실제로 있던 자리).</param>
/// <param name="LaserMm">CH1..3 거리(mm). 범위 밖·미연결 채널은 NaN.</param>
/// <param name="DepthMm">depth 영상 중앙 영역 중앙값(mm). 없으면 null.</param>
public sealed record WObjPointProbe(double[] TcpBase, double[] LaserMm, double? DepthMm)
{
    /// <summary>점 N 스냅샷을 담는 Bag 키.</summary>
    public static string BagKey(int n) => $"wobj.point{n}Probe";

    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    /// <summary>레이저 스냅샷 — 유효하지 않은 채널은 NaN.</summary>
    public static double[] LaserOf(IReadOnlyList<LaserChannelReading> readings)
    {
        var v = new[] { double.NaN, double.NaN, double.NaN };
        foreach (var r in readings)
            if (r.Channel is >= 1 and <= 3 && r.Enabled) v[r.Channel - 1] = r.Value;
        return v;
    }

    /// <summary>16bit 깊이(mm) 프레임 중앙 20% 영역의 유효값 중앙값. 유효값이 없으면 null.</summary>
    public static double? CenterDepthMm(CameraFrame? depth)
    {
        if (depth is null || depth.Width <= 0 || depth.Height <= 0) return null;
        var span = MemoryMarshal.Cast<byte, ushort>(depth.Pixels);
        int x0 = depth.Width * 2 / 5, x1 = depth.Width * 3 / 5, y0 = depth.Height * 2 / 5, y1 = depth.Height * 3 / 5;
        var vals = new List<ushort>((x1 - x0) * (y1 - y0));
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int idx = y * depth.Width + x;
                if ((uint)idx < (uint)span.Length && span[idx] > 0) vals.Add(span[idx]);
            }
        if (vals.Count == 0) return null;
        vals.Sort();
        return vals[vals.Count / 2];
    }

    /// <summary>한 점의 스냅샷 한 줄.</summary>
    public string Describe(int pointNum)
    {
        var laser = string.Join(" ", LaserMm.Select((v, i) => $"CH{i + 1}={(double.IsNaN(v) ? "-" : v.ToString("0.00", Ci))}"));
        var depth = DepthMm is { } d ? d.ToString("0", Ci) : "-";
        return $"[점 거리 진단] 점{pointNum} 레이저 {laser} depth 중앙={depth}mm " +
               $"TCP(BASE)=[{string.Join(", ", TcpBase.Take(6).Select(v => v.ToString("0.0", Ci)))}]";
    }

    /// <summary>
    /// 점1 → 점2 비교 한 줄.
    /// <list type="bullet">
    /// <item>면 거리 변화: 레이저 같은 채널끼리의 차이 평균(양수 = 점2 가 면에서 더 멀다)과 depth 중앙값 차이.</item>
    /// <item>이동: 점1→점2 TCP 변위를 점1 툴 자세 기준으로 나눈 광축 성분(양수 = 면 쪽으로 감)과 면 방향 길이.</item>
    /// <item>X축 기울기: atan(면 거리 변화 / 면 방향 길이). 360mm 순회하면 초점 거리가 그만큼 변한다.</item>
    /// </list>
    /// </summary>
    public static string Compare(WObjPointProbe p1, WObjPointProbe p2, ToolAxisDir depthAxis)
    {
        string S(double v, string fmt = "+0.00;-0.00") => v.ToString(fmt, Ci);

        // 점1 툴 자세 기준 변위 — 광축 성분과 면 방향(나머지) 길이.
        var t = PoseMath.FromPose(p1.TcpBase);
        int col = (int)depthAxis / 2;
        double sign = (int)depthAxis % 2 == 0 ? 1.0 : -1.0;
        var d = new[] { p2.TcpBase[0] - p1.TcpBase[0], p2.TcpBase[1] - p1.TcpBase[1], p2.TcpBase[2] - p1.TcpBase[2] };
        double toward = sign * (d[0] * t[0, col] + d[1] * t[1, col] + d[2] * t[2, col]);
        double total = Math.Sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]);
        double lateral = Math.Sqrt(Math.Max(0, total * total - toward * toward));

        var perChannel = new List<string>();
        var diffs = new List<double>();
        for (int i = 0; i < 3; i++)
        {
            if (double.IsNaN(p1.LaserMm[i]) || double.IsNaN(p2.LaserMm[i])) { perChannel.Add($"CH{i + 1}=-"); continue; }
            var dv = p2.LaserMm[i] - p1.LaserMm[i];
            diffs.Add(dv);
            perChannel.Add($"CH{i + 1}={S(dv)}");
        }
        double? laserDelta = diffs.Count > 0 ? diffs.Average() : null;
        double? depthDelta = p1.DepthMm is { } a && p2.DepthMm is { } b ? b - a : null;

        string Slope(double? delta)
        {
            if (delta is not { } dz || lateral < 1) return "-";
            var deg = Math.Atan2(dz, lateral) * 180.0 / Math.PI;
            return $"{S(deg, "+0.00;-0.00")}°(360mm 당 {S(dz / lateral * 360.0, "+0.0;-0.0")}mm)";
        }

        return "[점 거리 진단] 점1→점2 " +
               $"면 거리 변화(+ = 점2 가 더 멀다): 레이저 {string.Join(" ", perChannel)} 평균={(laserDelta is { } l ? S(l) : "-")}mm, " +
               $"depth={(depthDelta is { } dd ? S(dd, "+0;-0") : "-")}mm | " +
               $"이동: 면 방향 {lateral.ToString("0.0", Ci)}mm, 광축 성분(+ = 면 쪽) {S(toward)}mm | " +
               $"X축 기울기: 레이저 기준 {Slope(laserDelta)}, depth 기준 {Slope(depthDelta)}";
    }
}
