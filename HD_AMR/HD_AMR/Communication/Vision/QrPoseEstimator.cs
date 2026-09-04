using System.Runtime.InteropServices;
using HD_AMR.Models;
using OpenCvSharp;

namespace HD_AMR.Communication.Vision;

/// <summary>
/// 컬러 프레임에서 QR 코드를 검출(<see cref="Detect"/>)하고, 실측 한 변 길이(mm)와 컬러 내부
/// 파라미터로 카메라(컬러 광학 프레임) 기준 QR pose T_C_Q 를 추정(<see cref="EstimatePose"/>)한다.
///
/// solvePnP(IPPE_SQUARE) 오브젝트 포인트 순서(TL,TR,BR,BL ↔ (−s/2,+s/2), (+s/2,+s/2),
/// (+s/2,−s/2), (−s/2,−s/2))에서 유도되는 마커 좌표계: X=코드 오른쪽, Y=코드 위쪽,
/// Z=코드 면에서 카메라 쪽 법선. <c>QrLocalization.MarkerDrawingPose</c> 와 동일 규약이어야 한다.
///
/// 한계: <see cref="CameraD2CParams"/> 에 왜곡 계수가 없어 무왜곡 가정
/// (D435 컬러 왜곡은 작아 수 mm 수준 바이어스). OpenCV 네이티브는 Windows 전용 —
/// 호출 측에서 <c>OperatingSystem.IsWindows()</c> 가드 필요.
/// </summary>
public static class QrPoseEstimator
{
    /// <summary>cv::SOLVEPNP_IPPE_SQUARE(=7). OpenCvSharp 4.10 의 SolvePnPFlags 열거형에는 아직
    /// 없지만 네이티브 OpenCV 4.x 가 지원하므로 캐스팅해 전달한다(정사각 평면 4점 전용 해법).</summary>
    private const SolvePnPFlags IppeSquare = (SolvePnPFlags)7;

    /// <summary>rgb24 컬러 프레임에서 QR 1개를 검출·디코딩. 미검출/미디코딩이면 null.
    /// 코너는 컬러 픽셀 좌표, 순서 TL,TR,BR,BL(코드 정방향 기준).</summary>
    public static QrDetectResult? Detect(CameraFrame color)
    {
        if (color.PixelFormat != "rgb24") return null;

        using var rgb = WrapBytes(color.Height, color.Width, MatType.CV_8UC3, color.Pixels);
        using var gray = new Mat();
        Cv2.CvtColor(rgb, gray, ColorConversionCodes.RGB2GRAY);

        using var detector = new QRCodeDetector();
        string text = detector.DetectAndDecode(gray, out Point2f[] corners);
        if (string.IsNullOrEmpty(text) || corners is not { Length: 4 }) return null;

        var pts = new (double U, double V)[4];
        for (int i = 0; i < 4; i++) pts[i] = (corners[i].X, corners[i].Y);
        return new QrDetectResult(text, pts);
    }

    /// <summary>검출 코너 + 실측 한 변 길이(mm)로 T_C_Q [x,y,z,rx,ry,rz](mm/deg, FrameMath ZYX)를
    /// 추정. 내부 파라미터는 프레임 해상도가 캘리브레이션 해상도와 다르면 비례 스케일한다.</summary>
    public static QrPoseResult EstimatePose(
        QrDetectResult det, int frameW, int frameH, CameraD2CParams intr, double sizeMm)
    {
        double sx = intr.ColorW > 0 ? (double)frameW / intr.ColorW : 1.0;
        double sy = intr.ColorH > 0 ? (double)frameH / intr.ColorH : 1.0;
        var k = new double[3, 3]
        {
            { intr.ColorFx * sx, 0, intr.ColorCx * sx },
            { 0, intr.ColorFy * sy, intr.ColorCy * sy },
            { 0, 0, 1 },
        };

        double h = sizeMm / 2.0;
        // IPPE_SQUARE 요구 순서 (Y 위쪽 규약) — 이미지 코너 TL,TR,BR,BL 과 대응.
        var objPts = new[]
        {
            new Point3f((float)-h, (float)h, 0),
            new Point3f((float)h, (float)h, 0),
            new Point3f((float)h, (float)-h, 0),
            new Point3f((float)-h, (float)-h, 0),
        };
        var imgPts = new Point2f[4];
        for (int i = 0; i < 4; i++) imgPts[i] = new Point2f((float)det.CornersPx[i].U, (float)det.CornersPx[i].V);

        var dist = new double[5]; // 왜곡 계수 미보유 → 0 (무왜곡 가정)
        var rvec = new double[3];
        var tvec = new double[3];
        Cv2.SolvePnP(objPts, imgPts, k, dist, ref rvec, ref tvec,
            useExtrinsicGuess: false, IppeSquare);

        Cv2.Rodrigues(rvec, out double[,] r, out _);
        var m = new double[4, 4];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                m[i, j] = r[i, j];
        m[0, 3] = tvec[0]; m[1, 3] = tvec[1]; m[2, 3] = tvec[2];
        m[3, 3] = 1.0;
        var pose = FrameMath.MatrixToPose(m);

        Cv2.ProjectPoints(objPts, rvec, tvec, k, dist, out Point2f[] reproj, out _);
        double se = 0;
        for (int i = 0; i < 4; i++)
        {
            double du = reproj[i].X - imgPts[i].X, dv = reproj[i].Y - imgPts[i].Y;
            se += du * du + dv * dv;
        }
        double rms = Math.Sqrt(se / 4.0);

        double cu = det.CornersPx.Average(p => p.U);
        double cv = det.CornersPx.Average(p => p.V);
        return new QrPoseResult(pose, rms, cu, cv);
    }

    /// <summary>원시 픽셀 바이트를 Mat 으로 복사 래핑(<c>WeldFrameDecoder</c>와 동일 방식).</summary>
    private static Mat WrapBytes(int rows, int cols, MatType type, byte[] data)
    {
        var mat = new Mat(rows, cols, type);
        long bytes = (long)mat.Total() * mat.ElemSize();
        int n = (int)Math.Min(bytes, data.Length);
        Marshal.Copy(data, 0, mat.Data, n);
        return mat;
    }
}

/// <summary>QR 검출 결과: 디코딩 텍스트 + 컬러 픽셀 코너(TL,TR,BR,BL).</summary>
public sealed record QrDetectResult(string Text, (double U, double V)[] CornersPx);

/// <summary>QR pose 추정 결과: T_C_Q pose[6](mm/deg) + 재투영 RMS(px) + 코드 중심 컬러 픽셀.</summary>
public sealed record QrPoseResult(double[] PoseCQ, double ReprojErrPx, double CenterU, double CenterV);
