using HD.AMR.App.Communication;
using HD.AMR.App.Communication.Vision;
using HD.AMR.App.Models;

namespace HD.AMR.Tests;

/// <summary>
/// 합성 PnP 검증(OpenCV 네이티브 필요 — Windows 전용, 그 외 플랫폼은 스킵).
/// 알려진 T_C_Q 로 마커 코너를 컬러 K 에 투영해 가짜 코너를 만들고,
/// <see cref="QrPoseEstimator.EstimatePose"/> 가 같은 pose 를 복원하는지 확인한다.
/// FrameMath(ZYX) ↔ Rodrigues 회전 규약 불일치가 있으면 여기서 드러난다.
/// </summary>
public class QrPoseEstimatorTests
{
    private static readonly CameraD2CParams Intr = new(
        DepthFx: 640, DepthFy: 640, DepthCx: 640, DepthCy: 360, DepthW: 1280, DepthH: 720,
        ColorFx: 910, ColorFy: 910, ColorCx: 640, ColorCy: 360, ColorW: 1280, ColorH: 720,
        Rot: new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 },
        Trans: new double[] { 0, 0, 0 });

    [Fact]
    public void EstimatePose_SyntheticCorners_RecoversPose()
    {
        if (!OperatingSystem.IsWindows()) return; // OpenCV 네이티브 없음 — 스킵

        const double size = 150;
        double h = size / 2;
        // 마커 프레임 코너(TL,TR,BR,BL) — EstimatePose 의 IPPE_SQUARE 오브젝트 포인트와 동일 규약(Y 위쪽).
        var corners = new[]
        {
            new[] { -h, h, 0.0 }, new[] { h, h, 0.0 }, new[] { h, -h, 0.0 }, new[] { -h, -h, 0.0 },
        };
        // 카메라 앞 800mm, 약간 기울인 ground-truth T_C_Q.
        var truth = new[] { 40.0, -25, 800, 5, -20, 3 };
        var m = FrameMath.PoseToMatrix(truth);

        var px = new (double U, double V)[4];
        for (int i = 0; i < 4; i++)
        {
            var p = corners[i];
            double x = m[0, 0] * p[0] + m[0, 1] * p[1] + m[0, 2] * p[2] + m[0, 3];
            double y = m[1, 0] * p[0] + m[1, 1] * p[1] + m[1, 2] * p[2] + m[1, 3];
            double z = m[2, 0] * p[0] + m[2, 1] * p[1] + m[2, 2] * p[2] + m[2, 3];
            px[i] = (Intr.ColorFx * x / z + Intr.ColorCx, Intr.ColorFy * y / z + Intr.ColorCy);
        }

        var det = new QrDetectResult("QR1", px);
        var result = QrPoseEstimator.EstimatePose(det, 1280, 720, Intr, size);

        var solved = FrameMath.PoseToMatrix(result.PoseCQ);
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                Assert.True(Math.Abs(solved[i, j] - m[i, j]) < 1e-3,
                    $"[{i},{j}] {solved[i, j]} != {m[i, j]}");
        Assert.True(result.ReprojErrPx < 1e-3);
    }
}
