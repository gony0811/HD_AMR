using System.Runtime.InteropServices;
using HD.AMR.App.Models;
using OpenCvSharp;
using OpenCvSharp.Aruco;

namespace HD.AMR.App.Communication.Vision;

/// <summary>D435 RGB 영상에서 ArUco를 검출하고 컬러 광학 프레임 기준 T_C_Q를 계산한다.</summary>
public static class ArucoPoseEstimator
{
    private const SolvePnPFlags IppeSquare = (SolvePnPFlags)7;

    public static ArucoPoseResult? DetectAndEstimate(
        CameraFrame color, CameraD2CParams intr, double sizeMm, int markerId,
        PredefinedDictionaryName dictionaryName = PredefinedDictionaryName.Dict4X4_50)
    {
        if (color.PixelFormat != "rgb24" || sizeMm <= 0) return null;
        using var rgb = WrapBytes(color.Height, color.Width, MatType.CV_8UC3, color.Pixels);
        using var gray = new Mat();
        Cv2.CvtColor(rgb, gray, ColorConversionCodes.RGB2GRAY);
        using var dictionary = CvAruco.GetPredefinedDictionary(dictionaryName);
        var parameters = new DetectorParameters();
        CvAruco.DetectMarkers(gray, dictionary, out Point2f[][] corners, out int[] ids, parameters, out _);
        int found = Array.IndexOf(ids, markerId);
        if (found < 0 || corners[found].Length != 4) return null;

        // ArUco 코너 순서는 TL,TR,BR,BL. Q축은 X=오른쪽, Y=위, Z=카메라 쪽이다.
        double h = sizeMm / 2.0;
        var obj = new[] { new Point3f((float)-h, (float)h, 0), new Point3f((float)h, (float)h, 0),
            new Point3f((float)h, (float)-h, 0), new Point3f((float)-h, (float)-h, 0) };
        double sx = intr.ColorW > 0 ? (double)color.Width / intr.ColorW : 1;
        double sy = intr.ColorH > 0 ? (double)color.Height / intr.ColorH : 1;
        var k = new double[,] { { intr.ColorFx * sx, 0, intr.ColorCx * sx },
            { 0, intr.ColorFy * sy, intr.ColorCy * sy }, { 0, 0, 1 } };
        var dist = new double[5];
        var rvec = new double[3]; var tvec = new double[3];
        Cv2.SolvePnP(obj, corners[found], k, dist, ref rvec, ref tvec, false, IppeSquare);
        Cv2.Rodrigues(rvec, out double[,] rot, out _);
        var m = new double[4, 4];
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) m[i, j] = rot[i, j];
        m[0, 3] = tvec[0]; m[1, 3] = tvec[1]; m[2, 3] = tvec[2]; m[3, 3] = 1;
        Cv2.ProjectPoints(obj, rvec, tvec, k, dist, out Point2f[] projected, out _);
        double se = 0;
        for (int i = 0; i < 4; i++) { double u = projected[i].X - corners[found][i].X, v = projected[i].Y - corners[found][i].Y; se += u * u + v * v; }
        return new ArucoPoseResult(FrameMath.MatrixToPose(m), Math.Sqrt(se / 4), markerId,
            corners[found].Average(p => p.X), corners[found].Average(p => p.Y));
    }

    private static Mat WrapBytes(int rows, int cols, MatType type, byte[] data)
    {
        var mat = new Mat(rows, cols, type);
        Marshal.Copy(data, 0, mat.Data, (int)Math.Min((long)mat.Total() * mat.ElemSize(), data.Length));
        return mat;
    }
}

public sealed record ArucoPoseResult(double[] PoseCQ, double ReprojectionErrorPx, int MarkerId, double CenterU, double CenterV);
