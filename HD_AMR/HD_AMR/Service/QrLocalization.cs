using HD_AMR.Communication;

namespace HD_AMR.Service;

/// <summary>
/// QR 마커 기반 AMR 위치 역산의 순수 수학(OpenCV 무의존, 단위 테스트 대상).
///
/// 변환 체인: T_W_A(측정) = T_W_Q · inv(T_C_Q) · inv(T_F_C) · inv(T_B_F) · inv(T_A_B)
///  - T_W_Q: 마커 등록값(도면 Gx/Gy + 높이 Z + 법선 방위각) → 맵 프레임 pose (<see cref="MarkerWorldPose"/>)
///  - T_C_Q: 카메라(컬러 광학) 기준 QR pose — <c>QrPoseEstimator</c> 의 solvePnP 결과
///  - T_F_C: 코봇 플랜지 → 카메라 광학 프레임(핸드아이, Parameters 저장)
///  - T_B_F: 코봇 BASE 기준 플랜지 pose (tool 0)
///  - T_A_B: AMR 차체 → 코봇 BASE 장착 오프셋(기존 캘리브레이션)
/// 모든 pose 는 FAIRINO 규약 [x,y,z,rx,ry,rz](mm/deg, ZYX) — <see cref="FrameMath"/> 재사용.
///
/// 마커 좌표계 규약(QrPoseEstimator 의 IPPE_SQUARE 오브젝트 포인트와 동일해야 함):
///  X = 코드의 오른쪽, Y = 코드의 위쪽, Z = 코드 면에서 관찰자 쪽으로 나오는 법선.
/// 마커는 벽면에 수직·정방향(회전 없이 똑바로) 부착을 가정한다.
/// </summary>
public static class QrLocalization
{
    private const double Deg2Rad = Math.PI / 180.0;
    private const double Rad2Deg = 180.0 / Math.PI;

    /// <summary>
    /// 마커 등록값 → 맵(W) 프레임 기준 마커 pose T_W_Q [mm/deg].
    /// 법선 방위각 ψ(맵 기준) = 등록 방위각(도면 기준) + T_W_G 회전.
    /// 회전 열벡터: X=(-sinψ, cosψ, 0), Y=(0,0,1), Z=(cosψ, sinψ, 0) — 오른손계.
    /// </summary>
    public static double[] MarkerWorldPose(MapRegistration reg, QrMarkerReg m)
    {
        var (wx, wy) = MapCalibration.Apply(reg.ThetaDeg, reg.Tx, reg.Ty, m.Gx, m.Gy);
        double psi = (m.AzimuthDegG + reg.ThetaDeg) * Deg2Rad;
        double c = Math.Cos(psi), s = Math.Sin(psi);

        var mat = new double[4, 4];
        mat[0, 0] = -s; mat[0, 1] = 0; mat[0, 2] = c; mat[0, 3] = wx;
        mat[1, 0] = c;  mat[1, 1] = 0; mat[1, 2] = s; mat[1, 3] = wy;
        mat[2, 0] = 0;  mat[2, 1] = 1; mat[2, 2] = 0; mat[2, 3] = m.Zmm;
        mat[3, 3] = 1.0;
        return FrameMath.MatrixToPose(mat);
    }

    /// <summary>
    /// 변환 체인으로 AMR 중심의 맵 pose 를 역산한다. 평면 성분(x,y,θ)이 결과이고,
    /// 면외 성분(z 잔차, roll, pitch)은 0 에 가까워야 정상 — T_F_C/장착 오차 진단 지표로 반환.
    /// </summary>
    public static QrAmrPoseResult SolveAmrPose(
        double[] tWQ, double[] tCQ, double[] tFC, double[] tBF, double[] tAB)
    {
        var m = FrameMath.Multiply(FrameMath.PoseToMatrix(tWQ), FrameMath.Invert(FrameMath.PoseToMatrix(tCQ)));
        m = FrameMath.Multiply(m, FrameMath.Invert(FrameMath.PoseToMatrix(tFC)));
        m = FrameMath.Multiply(m, FrameMath.Invert(FrameMath.PoseToMatrix(tBF)));
        m = FrameMath.Multiply(m, FrameMath.Invert(FrameMath.PoseToMatrix(tAB)));

        var pose = FrameMath.MatrixToPose(m);
        double theta = Math.Atan2(m[1, 0], m[0, 0]) * Rad2Deg;
        return new QrAmrPoseResult(m[0, 3], m[1, 3], theta, m[2, 3], pose[3], pose[4]);
    }

    /// <summary>
    /// 핸드아이 부트스트랩: 현재 지점의 SLAM pose(T_W_A)를 참으로 놓고 T_F_C 를 역산.
    /// T_F_C = inv(T_B_F) · inv(T_A_B) · inv(T_W_A) · T_W_Q · inv(T_C_Q)
    /// </summary>
    public static double[] SolveHandEye(
        double[] tWQ, double[] tCQ, double[] tBF, double[] tAB, double[] tWASlam)
    {
        var m = FrameMath.Multiply(
            FrameMath.Invert(FrameMath.PoseToMatrix(tBF)),
            FrameMath.Invert(FrameMath.PoseToMatrix(tAB)));
        m = FrameMath.Multiply(m, FrameMath.Invert(FrameMath.PoseToMatrix(tWASlam)));
        m = FrameMath.Multiply(m, FrameMath.PoseToMatrix(tWQ));
        m = FrameMath.Multiply(m, FrameMath.Invert(FrameMath.PoseToMatrix(tCQ)));
        return FrameMath.MatrixToPose(m);
    }

    /// <summary>각도 목록(도)의 원형 평균. 랩어라운드(±180°) 안전.</summary>
    public static double CircularMeanDeg(IReadOnlyList<double> degs)
    {
        double sx = 0, sy = 0;
        foreach (var d in degs) { sx += Math.Cos(d * Deg2Rad); sy += Math.Sin(d * Deg2Rad); }
        return Math.Atan2(sy, sx) * Rad2Deg;
    }

    /// <summary>두 각(도)의 차 a−b 를 (−180,180] 로 정규화.</summary>
    public static double AngleDiffDeg(double a, double b)
    {
        double d = (a - b) % 360.0;
        if (d > 180.0) d -= 360.0;
        if (d <= -180.0) d += 360.0;
        return d;
    }
}

/// <summary>체인 역산 결과. 평면 pose(x,y,θ) + 면외 품질 잔차(z, roll, pitch — 0 근처가 정상).</summary>
public sealed record QrAmrPoseResult(
    double Xmm, double Ymm, double ThetaDeg,
    double ZResidMm, double RollDeg, double PitchDeg);

/// <summary>QR 마커 등록값 한 개(@bind 용 mutable — <see cref="MapRefPoint"/> 패턴).
/// 위치는 도면(G) 좌표로 등록하고 T_W_G 정합으로 맵 좌표로 변환한다.</summary>
public class QrMarkerReg
{
    public QrMarkerReg() { }
    public QrMarkerReg(int index) { Index = index; }

    public int Index { get; set; }
    /// <summary>QR 디코딩 텍스트 = 마커 식별자(예: "QR1"). 짧을수록 원거리 검출에 유리.</summary>
    public string Text { get; set; } = "";
    /// <summary>QR 중심의 도면 좌표(mm).</summary>
    public double Gx { get; set; }
    public double Gy { get; set; }
    /// <summary>QR 중심 높이(맵 평면 기준, mm).</summary>
    public double Zmm { get; set; }
    /// <summary>벽면 바깥쪽 법선의 방위각(도면 기준, 도).</summary>
    public double AzimuthDegG { get; set; }
    /// <summary>QR 한 변 실측 길이(mm). 인쇄물 실측값 필수.</summary>
    public double SizeMm { get; set; } = 150;
}
