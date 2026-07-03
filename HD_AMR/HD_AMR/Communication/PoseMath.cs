namespace HD_AMR.Communication;

/// <summary>
/// 강체 변환(rigid transform) 수학. 포즈는 [x,y,z,rx,ry,rz](mm + 도)로, 회전은 <b>ZYX RPY(고정축)</b>
/// 규약 R = Rz(rz)·Ry(ry)·Rx(rx)을 쓴다. 이는 <see cref="FairinoRpcClient.ComputeFramePose"/>
/// (동일 규약)와 반드시 일치해야 한다.
///
/// ⚠ 이 규약은 FAIRINO 컨트롤러의 FK 출력(GetForwardKin)과 공구 좌표 입력(GetToolCoord)이 둘 다
///   ZYX-도-고정축이라는 <b>가정</b>이다. 실제 규약이 다르면(ZYZ/내재회전 등) 합성된 앵커가 조용히
///   틀어지고, 그 앵커가 실제 이동(MoveL/MoveByOffset)을 구동하므로 위험하다. 실물 검증(감독된
///   MoveJ 후 GetForwardKin 대조) 전까지는 신뢰하지 말 것.
/// </summary>
internal static class PoseMath
{
    private const double Deg2Rad = Math.PI / 180.0;
    private const double Rad2Deg = 180.0 / Math.PI;

    /// <summary>[x,y,z,rx,ry,rz](도) → 4x4 동차변환. R = Rz(rz)·Ry(ry)·Rx(rx).</summary>
    public static double[,] FromPose(double[] p)
    {
        double x = p[0], y = p[1], z = p[2];
        double rx = p[3] * Deg2Rad, ry = p[4] * Deg2Rad, rz = p[5] * Deg2Rad;
        double sx = Math.Sin(rx), cx = Math.Cos(rx);
        double sy = Math.Sin(ry), cy = Math.Cos(ry);
        double sz = Math.Sin(rz), cz = Math.Cos(rz);

        var t = new double[4, 4];
        t[0, 0] = cy * cz;             t[0, 1] = cz * sx * sy - cx * sz; t[0, 2] = cx * cz * sy + sx * sz; t[0, 3] = x;
        t[1, 0] = cy * sz;             t[1, 1] = cx * cz + sx * sy * sz; t[1, 2] = cx * sy * sz - cz * sx; t[1, 3] = y;
        t[2, 0] = -sy;                 t[2, 1] = cy * sx;                t[2, 2] = cx * cy;                t[2, 3] = z;
        t[3, 0] = 0;                   t[3, 1] = 0;                      t[3, 2] = 0;                      t[3, 3] = 1;
        return t;
    }

    /// <summary>4x4 동차변환 → [x,y,z,rx,ry,rz](도). <see cref="FromPose"/>의 정확한 역이며,
    /// 오일러 추출식은 ComputeFramePose:512-514와 동일하다.</summary>
    public static double[] ToPose(double[,] t)
    {
        double r00 = t[0, 0], r10 = t[1, 0], r20 = t[2, 0], r21 = t[2, 1], r22 = t[2, 2];
        double rz = Math.Atan2(r10, r00);
        double ry = Math.Atan2(-r20, Math.Sqrt(r00 * r00 + r10 * r10));
        double rx = Math.Atan2(r21, r22);
        return new[] { t[0, 3], t[1, 3], t[2, 3], rx * Rad2Deg, ry * Rad2Deg, rz * Rad2Deg };
    }

    /// <summary>4x4 · 4x4 행렬곱.</summary>
    public static double[,] Multiply(double[,] a, double[,] b)
    {
        var m = new double[4, 4];
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
            {
                double s = 0;
                for (int k = 0; k < 4; k++) s += a[i, k] * b[k, j];
                m[i, j] = s;
            }
        return m;
    }

    /// <summary>강체 역변환: R' = Rᵀ, t' = -Rᵀ·t.</summary>
    public static double[,] Inverse(double[,] t)
    {
        var m = new double[4, 4];
        // R' = Rᵀ
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                m[i, j] = t[j, i];
        // t' = -Rᵀ·t
        for (int i = 0; i < 3; i++)
            m[i, 3] = -(m[i, 0] * t[0, 3] + m[i, 1] * t[1, 3] + m[i, 2] * t[2, 3]);
        m[3, 3] = 1;
        return m;
    }
}
