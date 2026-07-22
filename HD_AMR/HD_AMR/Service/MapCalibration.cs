using HD_AMR.Communication;

namespace HD_AMR.Service;

/// <summary>
/// 맵 정합(2D 강체) 및 좌표 변환 수학.
///
/// 도면 좌표계 G(mm)와 SLAM 맵 좌표계 W(mm) 사이의 2D 강체변환
/// <c>T_W_G = (θ, tx, ty)</c> 를 대응점들로 최소자승(2D Kabsch/Umeyama) 추정한다:
/// <c>w ≈ R(θ)·g + t</c>. 스케일은 1(둘 다 metric)로 가정하는 강체 정합이다.
///
/// 또한 코봇 BASE 좌표에서 찍은 점을 AMR pose·장착 오프셋(T_A_B)으로 맵 좌표(mm)로 환산한다
/// (<see cref="BasePointToMap"/>). 회전 규약은 <see cref="FrameMath"/>(ZYX RPY, 도)와 동일.
/// </summary>
public static class MapCalibration
{
    private const double Rad2Deg = 180.0 / Math.PI;

    /// <summary>대응점 (g_i → w_i)로 2D 강체변환 추정. w ≈ R(θ)·g + t.
    /// 반환 (θ[도], tx, ty, RMS[mm]). 점 개수 불일치/2개 미만이면 예외.</summary>
    public static (double ThetaDeg, double Tx, double Ty, double RmsMm) SolveRigid2D(
        IReadOnlyList<(double X, double Y)> g, IReadOnlyList<(double X, double Y)> w)
    {
        if (g.Count != w.Count) throw new ArgumentException("대응점 개수가 서로 다릅니다.");
        int n = g.Count;
        if (n < 2) throw new InvalidOperationException("2D 정합에는 대응점이 최소 2개 필요합니다.");

        double gcx = 0, gcy = 0, wcx = 0, wcy = 0;
        for (int i = 0; i < n; i++) { gcx += g[i].X; gcy += g[i].Y; wcx += w[i].X; wcy += w[i].Y; }
        gcx /= n; gcy /= n; wcx /= n; wcy /= n;

        // 최적 회전: θ = atan2(Σ(dg×dw), Σ(dg·dw))
        double a = 0, b = 0;
        for (int i = 0; i < n; i++)
        {
            double dgx = g[i].X - gcx, dgy = g[i].Y - gcy;
            double dwx = w[i].X - wcx, dwy = w[i].Y - wcy;
            a += dgx * dwy - dgy * dwx; // sin 성분(외적)
            b += dgx * dwx + dgy * dwy; // cos 성분(내적)
        }
        double theta = Math.Atan2(a, b);
        double c = Math.Cos(theta), s = Math.Sin(theta);
        double tx = wcx - (c * gcx - s * gcy);
        double ty = wcy - (s * gcx + c * gcy);

        double se = 0;
        for (int i = 0; i < n; i++)
        {
            double px = c * g[i].X - s * g[i].Y + tx;
            double py = s * g[i].X + c * g[i].Y + ty;
            se += (px - w[i].X) * (px - w[i].X) + (py - w[i].Y) * (py - w[i].Y);
        }
        double rms = Math.Sqrt(se / n);
        return (theta * Rad2Deg, tx, ty, rms);
    }

    /// <summary>도면 점(gx,gy)을 정합값으로 맵 점(mm)으로 변환. w = R(θ)·g + t.</summary>
    public static (double X, double Y) Apply(double thetaDeg, double tx, double ty, double gx, double gy)
    {
        double th = thetaDeg / Rad2Deg;
        double c = Math.Cos(th), s = Math.Sin(th);
        return (c * gx - s * gy + tx, s * gx + c * gy + ty);
    }

    /// <summary>코봇 BASE 좌표의 점 <paramref name="pBase"/>(mm)을 맵 좌표(mm)로 환산.
    /// <paramref name="amrPose"/> = 맵 기준 AMR 차체 pose [x,y,z,rx,ry,rz](mm/도),
    /// <paramref name="mount"/> = 장착 오프셋 T_A_B [x,y,z,rx,ry,rz](mm/도).
    /// p_W = T_W_A · T_A_B · p_B. 맵 바닥 평면의 (x,y)만 반환(높이는 등록에 미사용).</summary>
    public static (double X, double Y) BasePointToMap(double[] amrPose, double[] mount, double[] pBase)
    {
        var tWA = FrameMath.PoseToMatrix(amrPose);
        var tAB = FrameMath.PoseToMatrix(mount);
        var tWB = FrameMath.Multiply(tWA, tAB);
        double x = tWB[0, 0] * pBase[0] + tWB[0, 1] * pBase[1] + tWB[0, 2] * pBase[2] + tWB[0, 3];
        double y = tWB[1, 0] * pBase[0] + tWB[1, 1] * pBase[1] + tWB[1, 2] * pBase[2] + tWB[1, 3];
        return (x, y);
    }

    /// <summary>AMR pose(맵 기준, m/rad)를 코봇 변환용 [x,y,z,rx,ry,rz](mm/도)로. z=0, roll=pitch=0.</summary>
    public static double[] AmrPoseToMmDeg(double xMeters, double yMeters, double angleRad)
        => new[] { xMeters * 1000.0, yMeters * 1000.0, 0.0, 0.0, 0.0, angleRad * Rad2Deg };
}
