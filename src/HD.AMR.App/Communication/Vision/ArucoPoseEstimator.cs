using System.Runtime.InteropServices;
using HD.AMR.App.Models;
using OpenCvSharp;
using OpenCvSharp.Aruco;

namespace HD.AMR.App.Communication.Vision;

/// <summary>
/// D435 RGB 영상에서 ArUco를 검출하고 컬러 광학 프레임 기준 T_C_Q를 계산한다.
///
/// 정밀도 요점(핸드아이 잔차의 지배 요인이 마커 <b>회전</b> 추정이다):
///  · 코너는 서브픽셀 정제(<see cref="MakeDetectorParameters"/>)한다. 정제 없이는 코너 오차 0.5~1 px 이고,
///    정면에 가까운 마커는 원근 단서가 약해 1 px 가 기울기 약 1° 오차로 번진다.
///  · 렌즈 왜곡을 반영한다. 정방향 Brown-Conrady 계수는 solvePnP 에 그대로 넘기고, RealSense 색상 센서가
///    흔히 보고하는 Inverse Brown-Conrady 는 코너 픽셀을 먼저 편 뒤 무왜곡 PnP 를 한다(규약이 반대라
///    계수를 그대로 넘기면 오히려 틀어진다).
/// </summary>
public static class ArucoPoseEstimator
{
    private const SolvePnPFlags IppeSquare = (SolvePnPFlags)7;

    /// <summary>
    /// 검출 파라미터 — 서브픽셀 코너 정제 ON. 모든 검출 경로(자세 추정·프리뷰·ID 조사)가 같은 설정을 쓴다.
    /// 창 5 px, 최대 50회, 정확도 0.01 px 는 OpenCV 권장 범위이며 220 px 안팎 마커에서 코너를 0.1 px 수준으로 잡는다.
    /// </summary>
    public static DetectorParameters MakeDetectorParameters()
    {
        var p = new DetectorParameters
        {
            CornerRefinementMethod = CornerRefineMethod.Subpix,
            CornerRefinementWinSize = 5,
            CornerRefinementMaxIterations = 50,
            CornerRefinementMinAccuracy = 0.01,
        };
        return p;
    }

    public static ArucoPoseResult? DetectAndEstimate(
        CameraFrame color, CameraD2CParams intr, double sizeMm, int markerId,
        PredefinedDictionaryName dictionaryName = PredefinedDictionaryName.Dict4X4_50)
    {
        if (color.PixelFormat != "rgb24" || sizeMm <= 0) return null;
        using var rgb = WrapBytes(color.Height, color.Width, MatType.CV_8UC3, color.Pixels);
        using var gray = new Mat();
        Cv2.CvtColor(rgb, gray, ColorConversionCodes.RGB2GRAY);
        using var dictionary = CvAruco.GetPredefinedDictionary(dictionaryName);
        var parameters = MakeDetectorParameters();
        CvAruco.DetectMarkers(gray, dictionary, out Point2f[][] corners, out int[] ids, parameters, out _);
        int found = Array.IndexOf(ids, markerId);
        if (found < 0 || corners[found].Length != 4) return null;

        var raw = corners[found];

        // ArUco 코너 순서는 TL,TR,BR,BL. Q축은 X=오른쪽, Y=위, Z=카메라 쪽이다.
        double h = sizeMm / 2.0;
        var obj = new[] { new Point3f((float)-h, (float)h, 0), new Point3f((float)h, (float)h, 0),
            new Point3f((float)h, (float)-h, 0), new Point3f((float)-h, (float)-h, 0) };
        double sx = intr.ColorW > 0 ? (double)color.Width / intr.ColorW : 1;
        double sy = intr.ColorH > 0 ? (double)color.Height / intr.ColorH : 1;
        double fx = intr.ColorFx * sx, fy = intr.ColorFy * sy, cx = intr.ColorCx * sx, cy = intr.ColorCy * sy;
        var k = new double[,] { { fx, 0, cx }, { 0, fy, cy }, { 0, 0, 1 } };

        // 왜곡 처리: 정방향 모델은 계수를 PnP 에, 역방향 모델은 코너를 먼저 편다(둘 다 아니면 무왜곡).
        var dist = intr.OpenCvColorDistCoeffs();
        var pts = raw;
        if (intr.ColorDistortionIsInverse)
        {
            pts = new Point2f[4];
            for (int i = 0; i < 4; i++)
            {
                var (u, v) = intr.ColorUndistortPixel(raw[i].X, raw[i].Y, fx, fy, cx, cy);
                pts[i] = new Point2f((float)u, (float)v);
            }
        }

        var rvec = new double[3]; var tvec = new double[3];
        Cv2.SolvePnP(obj, pts, k, dist, ref rvec, ref tvec, false, IppeSquare);
        Cv2.Rodrigues(rvec, out double[,] rot, out _);
        var m = new double[4, 4];
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) m[i, j] = rot[i, j];
        m[0, 3] = tvec[0]; m[1, 3] = tvec[1]; m[2, 3] = tvec[2]; m[3, 3] = 1;
        Cv2.ProjectPoints(obj, rvec, tvec, k, dist, out Point2f[] projected, out _);
        double se = 0;
        for (int i = 0; i < 4; i++) { double u = projected[i].X - pts[i].X, v = projected[i].Y - pts[i].Y; se += u * u + v * v; }

        // 화면상 마커 한 변 길이(px) — 네 변 평균. 작을수록 기울기 추정이 불안정하므로 진단에 쓴다.
        double side = 0;
        for (int i = 0; i < 4; i++)
        {
            var a = raw[i]; var b = raw[(i + 1) % 4];
            side += Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        }
        side /= 4;

        return new ArucoPoseResult(FrameMath.MatrixToPose(m), Math.Sqrt(se / 4), markerId,
            raw.Average(p => p.X), raw.Average(p => p.Y), side);
    }

    /// <summary>
    /// 프리뷰용: 프레임에서 모든 ArUco 마커를 검출해 영상 위에 박스+ID를 그려 JPEG 로 반환한다.
    /// <paramref name="targetId"/> 마커는 굵은 초록 외곽선으로 강조한다. 검출이 없어도 원본 영상은 반환한다.
    /// <see cref="DetectAndEstimate"/> 와 달리 mjpg 프레임도 받는다(디코드 후 검출).
    /// </summary>
    public static ArucoPreviewResult? DetectAndRender(
        CameraFrame color, int? targetId, int jpegQuality = 80,
        PredefinedDictionaryName dictionaryName = PredefinedDictionaryName.Dict4X4_50)
    {
        using var bgr = ToBgr(color);
        if (bgr is null) return null;

        using var gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        using var dictionary = CvAruco.GetPredefinedDictionary(dictionaryName);
        CvAruco.DetectMarkers(gray, dictionary, out Point2f[][] corners, out int[] ids, MakeDetectorParameters(), out _);

        if (ids.Length > 0)
        {
            CvAruco.DrawDetectedMarkers(bgr, corners, ids);
            if (targetId is { } t)
            {
                int found = Array.IndexOf(ids, t);
                if (found >= 0)
                {
                    var pts = corners[found].Select(p => new Point((int)Math.Round(p.X), (int)Math.Round(p.Y)));
                    Cv2.Polylines(bgr, new[] { pts }, isClosed: true, new Scalar(0, 255, 0), thickness: 3);
                }
            }
        }

        Cv2.ImEncode(".jpg", bgr, out byte[] jpeg,
            new[] { new ImageEncodingParam(ImwriteFlags.JpegQuality, Math.Clamp(jpegQuality, 1, 100)) });
        return new ArucoPreviewResult(jpeg, ids,
            targetId is { } id && Array.IndexOf(ids, id) >= 0);
    }

    /// <summary>
    /// 프레임에 보이는 모든 마커의 ID 와 코너 사각형 면적(px²)을 반환한다 — 캡처 실패 진단
    /// ("어떤 ID 가 보였는가")과 기대 ID 미지정 시 가장 큰 마커 자동 채택에 쓴다.
    /// </summary>
    public static ArucoMarkerInfo[] DetectMarkerInfos(
        CameraFrame color, PredefinedDictionaryName dictionaryName = PredefinedDictionaryName.Dict4X4_50)
    {
        using var bgr = ToBgr(color);
        if (bgr is null) return Array.Empty<ArucoMarkerInfo>();

        using var gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        using var dictionary = CvAruco.GetPredefinedDictionary(dictionaryName);
        CvAruco.DetectMarkers(gray, dictionary, out Point2f[][] corners, out int[] ids, MakeDetectorParameters(), out _);

        var infos = new ArucoMarkerInfo[ids.Length];
        for (int i = 0; i < ids.Length; i++)
            infos[i] = new ArucoMarkerInfo(ids[i], Cv2.ContourArea(corners[i]));
        return infos;
    }

    /// <summary>프레임을 BGR Mat 로 변환. rgb24/mjpg 외 포맷이나 디코드 실패 시 null.</summary>
    private static Mat? ToBgr(CameraFrame color)
    {
        switch (color.PixelFormat)
        {
            case "rgb24":
            {
                using var rgb = WrapBytes(color.Height, color.Width, MatType.CV_8UC3, color.Pixels);
                var bgr = new Mat();
                Cv2.CvtColor(rgb, bgr, ColorConversionCodes.RGB2BGR);
                return bgr;
            }
            case "mjpg":
            {
                var bgr = Cv2.ImDecode(color.Pixels, ImreadModes.Color);
                if (bgr.Empty()) { bgr.Dispose(); return null; }
                return bgr;
            }
            default:
                return null;
        }
    }

    private static Mat WrapBytes(int rows, int cols, MatType type, byte[] data)
    {
        var mat = new Mat(rows, cols, type);
        Marshal.Copy(data, 0, mat.Data, (int)Math.Min((long)mat.Total() * mat.ElemSize(), data.Length));
        return mat;
    }
}

/// <summary>마커 자세 추정 결과. <paramref name="SidePx"/> 는 화면상 마커 한 변 길이(px, 네 변 평균).</summary>
public sealed record ArucoPoseResult(double[] PoseCQ, double ReprojectionErrorPx, int MarkerId, double CenterU, double CenterV, double SidePx = 0);

/// <summary>실시간 프리뷰 결과 — 마커가 그려진 JPEG + 검출된 ID 목록 + 대상 마커 검출 여부.</summary>
public sealed record ArucoPreviewResult(byte[] Jpeg, int[] DetectedIds, bool TargetFound);

/// <summary>검출된 마커 한 개의 요약 — ID 와 화면상 크기(코너 사각형 면적 px²).</summary>
public sealed record ArucoMarkerInfo(int Id, double AreaPx);
