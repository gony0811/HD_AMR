using HD_AMR.Models;

namespace HD_AMR.Communication;

/// <summary>
/// 레이저 변위 센서 3점 측정 → 평면 기울기(cobot base 좌표계) 계산.
///
/// 규약(사용자 확정): 레이저 빔 = base −Z(센서가 위, 표면이 아래). 변위 부호는 가까워짐=+, 멀어짐=−이며
/// 가까워지면 표면의 base Z가 커지므로 <b>표면점 Z = +변위(mm)</b>(<paramref name="signForUp"/>=false면 반전).
/// 헤드 배치는 Ch1→Ch3 = base +X, Ch2(상단) = +Y쪽.
///
/// 회전은 <see cref="PoseMath"/>·<see cref="FairinoRpcClient.ComputeFramePose"/> 와 동일한
/// <b>ZYX RPY(R = Rz·Ry·Rx, 도)</b> 규약. 3점 거리는 법선(2 DOF)만 결정하므로 rz는 항상 0.
/// </summary>
public static class PlaneTiltCalculator
{
    private const double Rad2Deg = 180.0 / Math.PI;

    /// <summary>
    /// 세 헤드의 base XY 위치 <paramref name="hx"/>/<paramref name="hy"/>(mm, 인덱스 0=Ch1,1=Ch2,2=Ch3)와
    /// 세 변위 <paramref name="d"/>(mm)로 평면 기울기를 계산한다.
    /// </summary>
    /// <param name="hx">헤드 X(mm) [Ch1,Ch2,Ch3].</param>
    /// <param name="hy">헤드 Y(mm) [Ch1,Ch2,Ch3].</param>
    /// <param name="d">변위(mm) [Ch1,Ch2,Ch3].</param>
    /// <param name="signForUp">변위 +가 base +Z(위)에 대응하면 true. 실장비 극성이 반대면 false.</param>
    public static PlaneTilt Compute(double[] hx, double[] hy, double[] d, bool signForUp)
    {
        if (hx.Length < 3 || hy.Length < 3 || d.Length < 3)
            return PlaneTilt.Invalid("헤드 좌표/측정값이 3개 미만입니다.");

        double zSign = signForUp ? 1.0 : -1.0;

        // 각 채널의 base 좌표점. Z = 부호 적용한 변위(mm). 절대 오프셋은 기울기에 영향 없음.
        // P1=Ch1(좌), P2=Ch2(상,+Y), P3=Ch3(우,+X).
        var p1 = new[] { hx[0], hy[0], zSign * d[0] };
        var p2 = new[] { hx[1], hy[1], zSign * d[1] };
        var p3 = new[] { hx[2], hy[2], zSign * d[2] };

        // 수평 평면일 때 법선이 +Z를 향하도록 (P3−P1)×(P2−P1) 순서 사용.
        var n = Cross(Sub(p3, p1), Sub(p2, p1));
        double mag = Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
        if (mag < 1e-9)
            return PlaneTilt.Invalid("세 점이 일직선이거나 헤드 간격이 0입니다. 헤드 좌표 설정을 확인하세요.");

        n[0] /= mag; n[1] /= mag; n[2] /= mag;
        if (n[2] < 0) { n[0] = -n[0]; n[1] = -n[1]; n[2] = -n[2]; } // 법선을 센서쪽(+Z)으로.

        // ZYX(rz=0)에서 공구 Z축=법선 = (sin(ry)cos(rx), −sin(rx), cos(ry)cos(rx)).
        double rx = Math.Atan2(-n[1], Math.Sqrt(n[0] * n[0] + n[2] * n[2])) * Rad2Deg;
        double ry = Math.Atan2(n[0], n[2]) * Rad2Deg;

        return new PlaneTilt(rx, ry, 0.0, n, true, null);
    }

    /// <summary>
    /// 세 헤드의 툴-XY(<paramref name="hx"/>/<paramref name="hy"/>, mm, 0=Ch1,1=Ch2,2=Ch3)와 변위
    /// <paramref name="d"/>(mm)로 삼각형 평면 중심의 <b>툴 좌표계 pose</b> [x,y,z,rx,ry,rz]를 계산한다.
    /// 빔 = 툴 +Z 평행, 헤드는 툴 Z=0 평면 가정. 중심 z = <paramref name="standoffMm"/> + 부호적용 변위평균.
    /// 좌표계: X=Ch1→Ch3(평면 투영), Z=법선(<paramref name="normalTowardSensor"/>면 센서쪽), Y=Z×X.
    /// </summary>
    /// <param name="signForUp">변위 +가 툴 +Z 방향(표면점 Z 증가)에 대응하면 true, 반대면 false.</param>
    /// <param name="normalTowardSensor">법선 Z를 센서쪽(툴 −Z측)으로. false면 표면쪽(툴 +Z측).</param>
    public static PlanePose ComputePose(
        double[] hx, double[] hy, double[] d, double standoffMm, bool signForUp, bool normalTowardSensor)
    {
        if (hx.Length < 3 || hy.Length < 3 || d.Length < 3)
            return PlanePose.Invalid("헤드 좌표/측정값이 3개 미만입니다.");

        double zSign = signForUp ? 1.0 : -1.0;

        // 표면점(툴): 빔 ∥ 툴 +Z, 헤드 툴 Z=0 → Z = standoff + 부호적용 변위.
        var p1 = new[] { hx[0], hy[0], standoffMm + zSign * d[0] };
        var p2 = new[] { hx[1], hy[1], standoffMm + zSign * d[1] };
        var p3 = new[] { hx[2], hy[2], standoffMm + zSign * d[2] };

        // 중심 = 세 점의 무게중심. XY는 헤드 XY 평균(빔 평행이라 거리 무관).
        double cx = (hx[0] + hx[1] + hx[2]) / 3.0;
        double cy = (hy[0] + hy[1] + hy[2]) / 3.0;
        double cz = (p1[2] + p2[2] + p3[2]) / 3.0;

        // 법선: 수평 평면일 때 +툴Z 를 향하도록 (P3−P1)×(P2−P1).
        var n = Cross(Sub(p3, p1), Sub(p2, p1));
        double nmag = Math.Sqrt(Dot(n, n));
        if (nmag < 1e-9)
            return PlanePose.Invalid("세 점이 일직선이거나 헤드 간격이 0입니다. 헤드 좌표 설정을 확인하세요.");
        n = new[] { n[0] / nmag, n[1] / nmag, n[2] / nmag };
        if (normalTowardSensor ? n[2] > 0 : n[2] < 0)
            n = new[] { -n[0], -n[1], -n[2] };

        // X축 = Ch1→Ch3 를 평면(법선 n)에 투영. Y = Z×X 로 우수 직교 프레임 완성.
        var xr = Sub(p3, p1);
        double proj = Dot(xr, n);
        var xax = new[] { xr[0] - proj * n[0], xr[1] - proj * n[1], xr[2] - proj * n[2] };
        double xmag = Math.Sqrt(Dot(xax, xax));
        if (xmag < 1e-9)
            return PlanePose.Invalid("Ch1→Ch3 방향이 법선과 평행해 X축을 정의할 수 없습니다.");
        xax = new[] { xax[0] / xmag, xax[1] / xmag, xax[2] / xmag };
        var yax = Cross(n, xax); // n·xax 직교 → 단위벡터.

        // ZYX RPY 추출 (ComputeFramePose:560-562 / PoseMath.ToPose 와 동일 규약).
        double rz = Math.Atan2(xax[1], xax[0]) * Rad2Deg;
        double ry = Math.Atan2(-xax[2], Math.Sqrt(xax[0] * xax[0] + xax[1] * xax[1])) * Rad2Deg;
        double rx = Math.Atan2(yax[2], n[2]) * Rad2Deg;

        return new PlanePose(cx, cy, cz, rx, ry, rz, n, true, null);
    }

    private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

    private static double[] Sub(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };

    private static double[] Cross(double[] a, double[] b) => new[]
    {
        a[1] * b[2] - a[2] * b[1],
        a[2] * b[0] - a[0] * b[2],
        a[0] * b[1] - a[1] * b[0],
    };
}
