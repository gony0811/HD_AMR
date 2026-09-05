using HD_AMR.Communication;

namespace HD_AMR.Service;

/// <summary>
/// QR 마커 기반 AMR 위치 역산의 순수 수학(OpenCV 무의존, 단위 테스트 대상).
///
/// 변환 체인: T_W_A(측정) = T_W_Q · inv(T_C_Q) · inv(T_T_C) · inv(T_B_T) · inv(T_A_B)
///  - T_W_Q: 마커 등록값(도면 Gx/Gy + 코드방향 방위각) → 맵 프레임 pose (<see cref="MarkerWorldPose"/>)
///  - T_C_Q: 카메라(컬러 광학) 기준 QR pose — <c>QrPoseEstimator</c> 의 solvePnP 결과
///  - T_T_C: 코봇 툴(TCP) → 카메라 광학 프레임(핸드아이, Parameters 저장)
///  - T_B_T: 코봇 BASE 기준 툴 TCP pose (페이지에서 지정한 공구 번호)
///  - T_A_B: AMR 차체 → 코봇 BASE 장착 오프셋(기존 캘리브레이션)
/// 모든 pose 는 FAIRINO 규약 [x,y,z,rx,ry,rz](mm/deg, ZYX) — <see cref="FrameMath"/> 재사용.
///
/// 마커 좌표계 규약(QrPoseEstimator 의 IPPE_SQUARE 오브젝트 포인트와 동일해야 함):
///  X = 코드의 오른쪽, Y = 코드의 위쪽, Z = 코드 면에서 관찰자 쪽으로 나오는 법선.
/// 마커는 바닥에 수평 부착 — Z 는 맵 위쪽(+Z), 코드 '위쪽'이 향하는 도면 기준 방위각을 등록한다.
/// </summary>
public static class QrLocalization
{
    private const double Deg2Rad = Math.PI / 180.0;
    private const double Rad2Deg = 180.0 / Math.PI;

    /// <summary>도면(G) 기준 마커 pose T_G_Q. 바닥은 코드 위쪽 방위각, 벽은 바깥 법선 방위각을 쓴다.</summary>
    public static double[] MarkerDrawingPose(QrMarkerReg m)
    {
        double psi = m.YawDegG * Deg2Rad;
        double c = Math.Cos(psi), s = Math.Sin(psi);
        var mat = new double[4, 4];

        if (m.Surface == QrMountSurface.Floor)
        {
            // X=코드 오른쪽, Y=코드 위쪽, Z=관찰자 방향(+Gz)
            mat[0, 0] = s;  mat[0, 1] = c; mat[0, 2] = 0;
            mat[1, 0] = -c; mat[1, 1] = s; mat[1, 2] = 0;
            mat[2, 0] = 0;  mat[2, 1] = 0; mat[2, 2] = 1;
        }
        else
        {
            // 벽: 코드 위쪽=+Gz, 법선 방위각=psi
            mat[0, 0] = -s; mat[0, 1] = 0; mat[0, 2] = c;
            mat[1, 0] = c;  mat[1, 1] = 0; mat[1, 2] = s;
            mat[2, 0] = 0;  mat[2, 1] = 1; mat[2, 2] = 0;
        }

        mat[0, 3] = m.Gx; mat[1, 3] = m.Gy; mat[2, 3] = m.Zmm; mat[3, 3] = 1;
        return FrameMath.MatrixToPose(mat);
    }

    /// <summary>한 QR 관측으로 도면→SLAM 변환 T_W_G를 직접 계산한다.</summary>
    public static QrMapTransform SolveMapTransform(
        double[] tWA, double[] tAB, double[] tBT, double[] tTC, double[] tCQ, double[] tGQ)
    {
        var tWQ = FrameMath.Multiply(FrameMath.PoseToMatrix(tWA), FrameMath.PoseToMatrix(tAB));
        tWQ = FrameMath.Multiply(tWQ, FrameMath.PoseToMatrix(tBT));
        tWQ = FrameMath.Multiply(tWQ, FrameMath.PoseToMatrix(tTC));
        tWQ = FrameMath.Multiply(tWQ, FrameMath.PoseToMatrix(tCQ));
        var tWG = FrameMath.Multiply(tWQ, FrameMath.Invert(FrameMath.PoseToMatrix(tGQ)));
        var pose = FrameMath.MatrixToPose(tWG);
        return new QrMapTransform(pose[5], pose[0], pose[1], pose[2], pose[3], pose[4]);
    }

    /// <summary>바닥 QR pose를 만든다. X/Y는 QR 중심, yaw는 코드 위쪽(+Y_Q)의 방위각이다.</summary>
    public static double[] FloorMarkerPose(double xMm, double yMm, double zMm, double yawDeg)
        => MarkerDrawingPose(new QrMarkerReg
        {
            Gx = xMm, Gy = yMm, Zmm = zMm, YawDegG = yawDeg,
            Surface = QrMountSurface.Floor,
        });

    /// <summary>
    /// 현재 SLAM pose와 QR 상대 관측으로 QR 기준 목표 정차 SLAM pose를 계산한다.
    /// T_W_A(target) = T_W_A(current) · T_A_Q(measured) · inv(T_A_Q(target)).
    /// </summary>
    public static QrStopPoseSolution SolveStopPose(
        double[] tWA, double[] tAB, double[] tBT, double[] tTC, double[] tCQ, double[] tAQTarget)
    {
        var tAQ = FrameMath.Multiply(FrameMath.PoseToMatrix(tAB), FrameMath.PoseToMatrix(tBT));
        tAQ = FrameMath.Multiply(tAQ, FrameMath.PoseToMatrix(tTC));
        tAQ = FrameMath.Multiply(tAQ, FrameMath.PoseToMatrix(tCQ));

        var tWATarget = FrameMath.Multiply(FrameMath.PoseToMatrix(tWA), tAQ);
        tWATarget = FrameMath.Multiply(tWATarget, FrameMath.Invert(FrameMath.PoseToMatrix(tAQTarget)));
        var target = FrameMath.MatrixToPose(tWATarget);
        var measured = FrameMath.MatrixToPose(tAQ);
        var desired = FrameMath.PoseToMatrix(tAQTarget);

        // 바닥 QR 방위각은 QR의 +Y(코드 위쪽) 열벡터로 계산한다.
        double measuredYaw = Math.Atan2(tAQ[1, 1], tAQ[0, 1]) * Rad2Deg;
        double desiredYaw = Math.Atan2(desired[1, 1], desired[0, 1]) * Rad2Deg;
        return new QrStopPoseSolution(
            measured[0], measured[1], measured[2], measuredYaw,
            target[0], target[1], target[5],
            measured[0] - tAQTarget[0], measured[1] - tAQTarget[1],
            AngleDiffDeg(measuredYaw, desiredYaw));
    }

    /// <summary>여러 마커에서 얻은 T_W_G 후보를 원형평균하고 후보 간 산포를 RMS로 반환한다.</summary>
    public static MapRegistration AverageMapTransforms(IReadOnlyList<QrMapTransform> samples)
    {
        if (samples.Count < 2) throw new InvalidOperationException("QR 캘리브레이션에는 서로 다른 마커 측정이 최소 2개 필요합니다.");
        double th = CircularMeanDeg(samples.Select(x => x.ThetaDeg).ToList());
        double tx = samples.Average(x => x.TxMm), ty = samples.Average(x => x.TyMm);
        double se = samples.Sum(x =>
            (x.TxMm - tx) * (x.TxMm - tx) + (x.TyMm - ty) * (x.TyMm - ty));
        return new MapRegistration(th, tx, ty, Math.Sqrt(se / samples.Count), samples.Count);
    }

    /// <summary>
    /// 마커 등록값 → 맵(W) 프레임 기준 마커 pose T_W_Q [mm/deg]. 바닥 수평 부착:
    /// 코드 위쪽 방위각 ψ(맵 기준) = 등록 방위각(도면 기준) + T_W_G 회전.
    /// 회전 열벡터: X=(sinψ, -cosψ, 0), Y=(cosψ, sinψ, 0), Z=(0,0,1) — 오른손계.
    /// </summary>
    public static double[] MarkerWorldPose(MapRegistration reg, QrMarkerReg m)
    {
        var (wx, wy) = MapCalibration.Apply(reg.ThetaDeg, reg.Tx, reg.Ty, m.Gx, m.Gy);
        double psi = (m.YawDegG + reg.ThetaDeg) * Deg2Rad;
        double c = Math.Cos(psi), s = Math.Sin(psi);

        var mat = new double[4, 4];
        mat[0, 0] = s;  mat[0, 1] = c; mat[0, 2] = 0; mat[0, 3] = wx;
        mat[1, 0] = -c; mat[1, 1] = s; mat[1, 2] = 0; mat[1, 3] = wy;
        mat[2, 0] = 0;  mat[2, 1] = 0; mat[2, 2] = 1; mat[2, 3] = m.Zmm;
        mat[3, 3] = 1.0;
        return FrameMath.MatrixToPose(mat);
    }

    /// <summary>
    /// 변환 체인으로 AMR 중심의 맵 pose 를 역산한다. 평면 성분(x,y,θ)이 결과이고,
    /// 면외 성분(z 잔차, roll, pitch)은 0 에 가까워야 정상 — T_T_C/장착 오차 진단 지표로 반환.
    /// </summary>
    public static QrAmrPoseResult SolveAmrPose(
        double[] tWQ, double[] tCQ, double[] tTC, double[] tBT, double[] tAB)
    {
        var m = FrameMath.Multiply(FrameMath.PoseToMatrix(tWQ), FrameMath.Invert(FrameMath.PoseToMatrix(tCQ)));
        m = FrameMath.Multiply(m, FrameMath.Invert(FrameMath.PoseToMatrix(tTC)));
        m = FrameMath.Multiply(m, FrameMath.Invert(FrameMath.PoseToMatrix(tBT)));
        m = FrameMath.Multiply(m, FrameMath.Invert(FrameMath.PoseToMatrix(tAB)));

        var pose = FrameMath.MatrixToPose(m);
        double theta = Math.Atan2(m[1, 0], m[0, 0]) * Rad2Deg;
        return new QrAmrPoseResult(m[0, 3], m[1, 3], theta, m[2, 3], pose[3], pose[4]);
    }

    /// <summary>
    /// 핸드아이 부트스트랩: 현재 지점의 SLAM pose(T_W_A)를 참으로 놓고 T_T_C 를 역산.
    /// T_T_C = inv(T_B_T) · inv(T_A_B) · inv(T_W_A) · T_W_Q · inv(T_C_Q)
    /// </summary>
    public static double[] SolveHandEye(
        double[] tWQ, double[] tCQ, double[] tBT, double[] tAB, double[] tWASlam)
    {
        var m = FrameMath.Multiply(
            FrameMath.Invert(FrameMath.PoseToMatrix(tBT)),
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

public sealed record QrMapTransform(
    double ThetaDeg, double TxMm, double TyMm,
    double ZResidMm, double RollDeg, double PitchDeg);

public sealed record QrStopPoseSolution(
    double MeasuredQrXmm, double MeasuredQrYmm, double MeasuredQrZmm, double MeasuredQrYawDeg,
    double TargetSlamXmm, double TargetSlamYmm, double TargetSlamYawDeg,
    double ErrorXmm, double ErrorYmm, double ErrorYawDeg);

public enum QrMountSurface
{
    Floor,
    Wall,
}

public sealed class QrStopReference
{
    public string Text { get; set; } = "STOP-QR";
    public double SizeMm { get; set; } = 150;
    public double TargetXmm { get; set; }
    public double TargetYmm { get; set; }
    public double TargetZmm { get; set; }
    public double TargetYawDeg { get; set; }
    public double PositionToleranceMm { get; set; } = 10;
    public double YawToleranceDeg { get; set; } = 0.5;
}

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
    /// <summary>QR 면 높이(맵 평면 기준, mm — 바닥 부착이면 0).</summary>
    public double Zmm { get; set; }
    public QrMountSurface Surface { get; set; } = QrMountSurface.Floor;
    /// <summary>바닥: 코드 위쪽 방위각. 벽: 벽 바깥 법선 방위각(도면 기준, 도).</summary>
    public double YawDegG { get; set; }
    /// <summary>QR 한 변 실측 길이(mm). 인쇄물 실측값 필수.</summary>
    public double SizeMm { get; set; } = 150;
}
