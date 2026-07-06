using System.Runtime.InteropServices;
using HD_AMR.Models;
using OpenCvSharp;

namespace HD_AMR.Communication.Weld;

/// <summary>
/// CameraFrame(컬러/IR, 여러 픽셀 포맷)을 OpenCvSharp Mat 으로 디코드하는 공유 헬퍼.
/// 고전 CV 검출기(<see cref="WeldVisionDetector"/>)와 DL 검출기가 <b>동일한 방식</b>으로
/// 프레임을 해석하도록 한 곳에 모았다(오버레이용 BGR + 작업용 Gray).
/// </summary>
public static class WeldFrameDecoder
{
    /// <summary>CameraFrame → (BGR overlay 용, Gray 작업용) Mat. 호출자가 Dispose. 실패면 (null,null).</summary>
    public static (Mat? bgr, Mat? gray) Decode(CameraFrame f, WeldImageMode mode)
    {
        switch (f.PixelFormat)
        {
            case "mjpg":
            {
                var bgr = Cv2.ImDecode(f.Pixels, ImreadModes.Color);
                if (bgr.Empty()) return (null, null);
                return (bgr, ToWorkGray(bgr, mode));
            }
            case "rgb24":
            {
                using var rgb = WrapBytes(f.Height, f.Width, MatType.CV_8UC3, f.Pixels);
                var bgr = new Mat();
                Cv2.CvtColor(rgb, bgr, ColorConversionCodes.RGB2BGR);
                return (bgr, ToWorkGray(bgr, mode));
            }
            case "ir8":
            {
                var gray = WrapBytes(f.Height, f.Width, MatType.CV_8UC1, f.Pixels);
                var bgr = new Mat();
                Cv2.CvtColor(gray, bgr, ColorConversionCodes.GRAY2BGR);
                return (bgr, gray);
            }
            case "ir16":
            {
                using var m16 = WrapBytes(f.Height, f.Width, MatType.CV_16UC1, f.Pixels);
                var gray = new Mat();
                Cv2.Normalize(m16, gray, 0, 255, NormTypes.MinMax, (int)MatType.CV_8UC1);
                var bgr = new Mat();
                Cv2.CvtColor(gray, bgr, ColorConversionCodes.GRAY2BGR);
                return (bgr, gray);
            }
            default:
                return (null, null);
        }
    }

    /// <summary>원시 픽셀 바이트를 Mat 으로 래핑(새 버퍼에 복사). 버전 간 안전한 방식.</summary>
    private static Mat WrapBytes(int rows, int cols, MatType type, byte[] data)
    {
        var mat = new Mat(rows, cols, type);
        long bytes = (long)mat.Total() * mat.ElemSize();
        int n = (int)Math.Min(bytes, data.Length);
        Marshal.Copy(data, 0, mat.Data, n);
        return mat;
    }

    private static Mat ToWorkGray(Mat bgr, WeldImageMode mode)
    {
        var gray = new Mat();
        if (mode == WeldImageMode.RgbHsv)
        {
            using var hsv = new Mat();
            Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
            var ch = Cv2.Split(hsv);
            try { ch[1].CopyTo(gray); }   // 채도(S) 채널 — 변색된 비드 분리에 유리
            finally { foreach (var c in ch) c.Dispose(); }
        }
        else
        {
            Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        }
        return gray;
    }
}
