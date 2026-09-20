using System.Runtime.InteropServices;
using HD.AMR.App.Communication.Vision;
using HD.AMR.App.Models;
using OpenCvSharp;
using OpenCvSharp.Aruco;

namespace HD.AMR.Tests;

public class ArucoPreviewTests
{
    private const int MarkerId = 7;

    /// <summary>합성 ArUco 마커(Dict4X4_50)를 흰 배경 가운데 배치한 rgb24 프레임을 만든다.</summary>
    private static CameraFrame MakeMarkerFrame(int width = 640, int height = 480, int markerPx = 200)
    {
        using var dictionary = CvAruco.GetPredefinedDictionary(PredefinedDictionaryName.Dict4X4_50);
        using var marker = new Mat();
        dictionary.GenerateImageMarker(MarkerId, markerPx, marker, borderBits: 1);

        using var canvas = new Mat(height, width, MatType.CV_8UC1, Scalar.White);
        var roi = new Rect((width - markerPx) / 2, (height - markerPx) / 2, markerPx, markerPx);
        marker.CopyTo(canvas[roi]);

        using var rgb = new Mat();
        Cv2.CvtColor(canvas, rgb, ColorConversionCodes.GRAY2RGB);
        return ToFrame(rgb);
    }

    private static CameraFrame ToFrame(Mat rgb)
    {
        var pixels = new byte[(int)(rgb.Total() * rgb.ElemSize())];
        Marshal.Copy(rgb.Data, pixels, 0, pixels.Length);
        return new CameraFrame(pixels, rgb.Width, rgb.Height, "rgb24", DateTime.UtcNow);
    }

    [Fact]
    public void DetectAndRender_FindsTargetMarker()
    {
        var result = ArucoPoseEstimator.DetectAndRender(MakeMarkerFrame(), MarkerId);
        Assert.NotNull(result);
        Assert.True(result.TargetFound);
        Assert.Contains(MarkerId, result.DetectedIds);
        Assert.NotEmpty(result.Jpeg);
    }

    [Fact]
    public void DetectAndRender_ReportsTargetMissingButListsDetected()
    {
        var result = ArucoPoseEstimator.DetectAndRender(MakeMarkerFrame(), targetId: 3);
        Assert.NotNull(result);
        Assert.False(result.TargetFound);
        Assert.Contains(MarkerId, result.DetectedIds);
    }

    [Fact]
    public void DetectAndRender_BlankFrameStillReturnsImage()
    {
        using var gray = new Mat(480, 640, MatType.CV_8UC3, new Scalar(128, 128, 128));
        var result = ArucoPoseEstimator.DetectAndRender(ToFrame(gray), MarkerId);
        Assert.NotNull(result);
        Assert.False(result.TargetFound);
        Assert.Empty(result.DetectedIds);
        Assert.NotEmpty(result.Jpeg);
    }

    [Fact]
    public void DetectMarkerInfos_ReturnsIdAndArea()
    {
        var infos = ArucoPoseEstimator.DetectMarkerInfos(MakeMarkerFrame(markerPx: 200));
        var info = Assert.Single(infos);
        Assert.Equal(MarkerId, info.Id);
        // 코너 사각형 면적은 대략 마커 픽셀 크기의 제곱 근처여야 한다(검출 경계 오차 허용).
        Assert.InRange(info.AreaPx, 150 * 150, 220 * 220);
    }

    [Fact]
    public void DetectMarkerInfos_EmptyOnBlankFrame()
    {
        using var gray = new Mat(480, 640, MatType.CV_8UC3, new Scalar(128, 128, 128));
        Assert.Empty(ArucoPoseEstimator.DetectMarkerInfos(ToFrame(gray)));
    }

    [Fact]
    public void DetectAndRender_RejectsUnknownPixelFormat()
    {
        var frame = new CameraFrame(new byte[16], 2, 2, "z16", DateTime.UtcNow);
        Assert.Null(ArucoPoseEstimator.DetectAndRender(frame, MarkerId));
    }
}
