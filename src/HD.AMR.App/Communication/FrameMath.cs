namespace HD.AMR.App.Communication;

/// <summary>
/// 강체 변환(4×4) 유틸. FAIRINO pose [x,y,z,rx,ry,rz](mm/deg)와 좌표계 사이 변환에 쓴다.
/// 회전 규약은 <see cref="FairinoRpcClient.ComputeFramePose"/>와 동일한 RPY <b>ZYX</b>
/// (R = Rz(rz)·Ry(ry)·Rx(rx)). ⚠ 실물에서 라운드트립/펜던트 대조로 규약 검증 필요.
///
/// 용도(작업물 추종 절대위치):
///  - <see cref="ToFrame"/>: 베이스 pose → 작업물 좌표계 N 기준 상대 pose (티칭 저장용).
///  - <see cref="FromFrame"/>: 상대 pose + 현재 프레임 N → 베이스 목표 pose (이동용).
/// </summary>
public static class FrameMath
{
    private const double Deg2Rad = Math.PI / 180.0;
    private const double Rad2Deg = 180.0 / Math.PI;

    /// <summary>pose[6]=[x,y,z,rx,ry,rz](mm/deg) → 4×4 동차 변환. 회전은 ZYX(R=Rz·Ry·Rx).</summary>
    public static double[,] PoseToMatrix(double[] pose)
    {
        double a = pose[3] * Deg2Rad; // rx
        double b = pose[4] * Deg2Rad; // ry
        double c = pose[5] * Deg2Rad; // rz
        double ca = Math.Cos(a), sa = Math.Sin(a);
        double cb = Math.Cos(b), sb = Math.Sin(b);
        double cc = Math.Cos(c), sc = Math.Sin(c);

        var m = new double[4, 4];
        // R = Rz(c)·Ry(b)·Rx(a)
        m[0, 0] = cb * cc;                 m[0, 1] = cc * sa * sb - ca * sc;  m[0, 2] = ca * cc * sb + sa * sc;
        m[1, 0] = cb * sc;                 m[1, 1] = ca * cc + sa * sb * sc;  m[1, 2] = ca * sb * sc - cc * sa;
        m[2, 0] = -sb;                     m[2, 1] = cb * sa;                 m[2, 2] = ca * cb;
        m[0, 3] = pose[0]; m[1, 3] = pose[1]; m[2, 3] = pose[2];
        m[3, 3] = 1.0;
        return m;
    }

    /// <summary>4×4 동차 변환 → pose[6]=[x,y,z,rx,ry,rz](mm/deg). ZYX 역추출.
    /// ry=±90°(짐벌락)에서는 rx·rz 가 하나의 자유도로 묶여 일반식(atan2(m21,m22))이 atan2(0,0)=0 이 되어
    /// 회전 정보를 잃는다 — 그때는 rz=0 으로 두고 rx 를 m01·m11 에서 구한다(같은 회전 행렬을 재현).
    /// 예: 툴 X 가 정확히 연직인 자세(수직 용접선 접선 기준 법선 자세)가 광축이 뒤집힌 pose 로 나오던 원인
    /// (2026-10-07 현장).</summary>
    public static double[] MatrixToPose(double[,] m)
    {
        double cy = Math.Sqrt(m[0, 0] * m[0, 0] + m[1, 0] * m[1, 0]);
        double ry = Math.Atan2(-m[2, 0], cy);
        double rx, rz;
        if (cy > 1e-6)
        {
            rz = Math.Atan2(m[1, 0], m[0, 0]);
            rx = Math.Atan2(m[2, 1], m[2, 2]);
        }
        else
        {
            // R = Rz(c)·Ry(±90°)·Rx(a): ry=+90 → m01=sin(a−c), m11=cos(a−c); ry=−90 → m01=−sin(a+c), m11=cos(a+c).
            rz = 0.0;
            rx = m[2, 0] < 0
                ? Math.Atan2(m[0, 1], m[1, 1])     // ry = +90°
                : Math.Atan2(-m[0, 1], m[1, 1]);   // ry = −90°
        }
        return new[]
        {
            m[0, 3], m[1, 3], m[2, 3],
            rx * Rad2Deg, ry * Rad2Deg, rz * Rad2Deg,
        };
    }

    /// <summary>4×4 행렬 곱 A·B.</summary>
    public static double[,] Multiply(double[,] a, double[,] b)
    {
        var r = new double[4, 4];
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
            {
                double s = 0;
                for (int k = 0; k < 4; k++) s += a[i, k] * b[k, j];
                r[i, j] = s;
            }
        return r;
    }

    /// <summary>강체 변환의 역: R⁻¹=Rᵀ, t⁻¹=-Rᵀ·t.</summary>
    public static double[,] Invert(double[,] m)
    {
        var r = new double[4, 4];
        // Rᵀ
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                r[i, j] = m[j, i];
        // -Rᵀ·t
        double tx = m[0, 3], ty = m[1, 3], tz = m[2, 3];
        r[0, 3] = -(r[0, 0] * tx + r[0, 1] * ty + r[0, 2] * tz);
        r[1, 3] = -(r[1, 0] * tx + r[1, 1] * ty + r[1, 2] * tz);
        r[2, 3] = -(r[2, 0] * tx + r[2, 1] * ty + r[2, 2] * tz);
        r[3, 3] = 1.0;
        return r;
    }

    /// <summary>베이스 기준 pose를 작업물 좌표계(<paramref name="framePose"/>=T_N, 베이스 기준) 기준
    /// 상대 pose로 변환. P_rel = inv(T_N) · P_base.</summary>
    public static double[] ToFrame(double[] baseP, double[] framePose)
        => MatrixToPose(Multiply(Invert(PoseToMatrix(framePose)), PoseToMatrix(baseP)));

    /// <summary>작업물 좌표계 기준 상대 pose를 현재 프레임(<paramref name="framePose"/>=T_N)으로
    /// 되돌려 베이스 목표 pose 계산. P_base = T_N · P_rel.</summary>
    public static double[] FromFrame(double[] relP, double[] framePose)
        => MatrixToPose(Multiply(PoseToMatrix(framePose), PoseToMatrix(relP)));
}
