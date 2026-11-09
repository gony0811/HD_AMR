using System.Runtime.InteropServices;
using OpenCvSharp;

namespace HD.AMR.App.Service.Vision;

/// <summary>OpenCV Mat(BGR 8UC3) 에 <see cref="ImageClahe"/> 를 적용 — 운영 검출기(Windows)용. 학습 데이터셋과 같은 C# 구현을 쓴다.</summary>
public static class ImageClaheMat
{
    /// <summary>전체 프레임 BGR 에 CLAHE 를 건 사본(원본은 그대로 — 오버레이는 원본 위에 그린다).</summary>
    public static Mat Apply(Mat bgr)
    {
        if (bgr.Type() != MatType.CV_8UC3) throw new ArgumentException("BGR 8UC3 Mat 이어야 합니다.", nameof(bgr));
        var dst = bgr.Clone();   // Clone 은 연속 메모리
        int w = dst.Width, h = dst.Height;
        var buf = new byte[w * h * 3];
        Marshal.Copy(dst.Data, buf, 0, buf.Length);
        ImageClahe.ApplyColorInPlace(buf, w, h, 3, bgr: true);
        Marshal.Copy(buf, 0, dst.Data, buf.Length);
        return dst;
    }
}
