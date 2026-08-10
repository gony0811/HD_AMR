using HD_AMR.Contracts.Lidar;
using HD_AMR.LidarService.Device;

namespace HD_AMR.LidarService.Imaging;

/// <summary>렌더링 스케일. 화면에서 바꿀 수 있어야 해서 값 객체로 분리했다.</summary>
internal readonly record struct RenderScale(double MinRangeMm, double MaxRangeMm, int AmplitudeMax);

/// <summary>
/// 한 프레임을 모니터링용 이미지로 변환한다.
///
/// 센서 해상도(320x240) 그대로 인코딩하고 확대는 브라우저에 맡긴다. 서버에서 키우면
/// 대역폭만 4배가 되고, 픽셀 단위로 무효 코드를 읽어야 하는 이 화면에서는 보간이 오히려
/// 방해가 된다(브라우저 쪽은 <c>image-rendering: pixelated</c> 로 막는다).
/// </summary>
internal static class FrameRenderer
{
    /// <summary>
    /// 거리 의사색상 이미지.
    ///
    /// 컬러맵 구간을 <b>검출기의 깊이 게이트와 같은 값</b>으로 주면, 구간을 벗어난 픽셀이
    /// 회색으로 빠지면서 "검출기가 실제로 보고 있는 영역"이 그대로 화면이 된다. 깊이 게이트
    /// 튜닝이 슬라이더를 움직이며 색이 차오르는지 보는 일이 되도록 의도한 것이다.
    /// </summary>
    public static byte[] RenderDistance(LidarCapture frame, in RenderScale scale)
    {
        var n = frame.PixelCount;
        var buffer = new byte[n * 3];
        var span = scale.MaxRangeMm - scale.MinRangeMm;
        if (span <= 0) span = 1;

        for (int i = 0; i < n; i++)
        {
            var d = frame.Distance2D[i];

            Rgb color;
            if (PixelPalette.ForInvalidCode(d) is { } invalid)
                color = invalid;
            else if (d < scale.MinRangeMm)
                color = PixelPalette.NearerThanBand;
            else if (d > scale.MaxRangeMm)
                color = PixelPalette.FartherThanBand;
            else
                color = PixelPalette.Turbo((d - scale.MinRangeMm) / span);

            var o = i * 3;
            buffer[o] = color.R;
            buffer[o + 1] = color.G;
            buffer[o + 2] = color.B;
        }

        return PngWriter.Encode(buffer, frame.Width, frame.Height, 3);
    }

    /// <summary>
    /// 진폭(적외선) 이미지. 노출 튜닝용이다.
    ///
    /// 회색조로 두되 포화/ADC 오버플로/미충전 픽셀만 색으로 덧칠한다. 진폭 부족 픽셀은
    /// 원래 어둡게 나오므로 따로 칠하지 않는다 — 회색조 자체가 이미 그 정보다.
    /// </summary>
    public static byte[] RenderAmplitude(LidarCapture frame, in RenderScale scale)
    {
        var n = frame.PixelCount;
        var buffer = new byte[n * 3];
        var max = Math.Max(1, scale.AmplitudeMax);

        for (int i = 0; i < n; i++)
        {
            var d = frame.Distance2D[i];

            Rgb color;
            if (d == 0) color = PixelPalette.Unfilled;
            else if (d == NslNative.AdcOverflow) color = PixelPalette.AdcOverflow;
            else if (d == NslNative.Saturation) color = PixelPalette.Saturation;
            else
            {
                var v = (byte)Math.Clamp(frame.Amplitude[i] * 255L / max, 0, 255);
                color = new Rgb(v, v, v);
            }

            var o = i * 3;
            buffer[o] = color.R;
            buffer[o + 1] = color.G;
            buffer[o + 2] = color.B;
        }

        return PngWriter.Encode(buffer, frame.Width, frame.Height, 3);
    }

    /// <summary>
    /// 검출 결과 오버레이(투명 배경 RGBA).
    ///
    /// 두 평면의 인라이어를 각각 다른 색으로 칠하는 것이 이 화면의 핵심이다. 실측 덤프에서
    /// 검출이 "성공"으로 나왔지만 실제로는 배경 벽과 전경 면을 짝지은 오검출이었던 사례가
    /// 있었는데, 숫자만 봐서는 구분되지 않았다. 어느 픽셀이 어느 평면으로 갔는지 보이면
    /// 그런 오검출은 한눈에 드러난다.
    ///
    /// 능선은 3D 직선이라 픽셀로 투영하려면 렌즈 내부 파라미터가 필요한데, 그 값은 공개되어
    /// 있지 않다. 대신 픽셀별 3D 좌표가 이미 있으므로 <b>직선까지의 수직거리가 임계 이내인
    /// 픽셀</b>을 칠한다. 투영 없이 같은 결과를 얻고, 렌즈 모델 가정이 끼어들지 않는다.
    /// </summary>
    public static byte[] RenderOverlay(
        AveragedCapture capture,
        int[]? planeA,
        int[]? planeB,
        RidgeLine? ridge,
        double ridgeBandMm)
    {
        var n = capture.Width * capture.Height;
        var buffer = new byte[n * 4];   // 전부 0 = 완전 투명

        if (planeA is not null)
            Fill(buffer, planeA, new Rgb(0, 200, 255), 96);

        if (planeB is not null)
            Fill(buffer, planeB, new Rgb(255, 0, 200), 96);

        if (ridge is { Start: { } start, End: { } end })
        {
            var px = ridge.Point.X;
            var py = ridge.Point.Y;
            var pz = ridge.Point.Z;
            var dx = ridge.Direction.X;
            var dy = ridge.Direction.Y;
            var dz = ridge.Direction.Z;

            // 유효 구간 밖까지 칠하면 능선이 실제보다 길어 보인다. Start/End 를 방향축
            // 파라미터로 환산해 그 범위 안에서만 칠한다.
            var tStart = (start.X - px) * dx + (start.Y - py) * dy + (start.Z - pz) * dz;
            var tEnd = (end.X - px) * dx + (end.Y - py) * dy + (end.Z - pz) * dz;
            if (tStart > tEnd) (tStart, tEnd) = (tEnd, tStart);

            var bandSq = ridgeBandMm * ridgeBandMm;

            for (int i = 0; i < n; i++)
            {
                var x = capture.X[i];
                if (double.IsNaN(x)) continue;

                var vx = x - px;
                var vy = capture.Y[i] - py;
                var vz = capture.Z[i] - pz;

                var t = vx * dx + vy * dy + vz * dz;
                if (t < tStart || t > tEnd) continue;

                // 직선까지의 수직거리 제곱 = |v|² - t²
                var perpSq = vx * vx + vy * vy + vz * vz - t * t;
                if (perpSq > bandSq) continue;

                var o = i * 4;
                buffer[o] = 255;
                buffer[o + 1] = 235;
                buffer[o + 2] = 0;
                buffer[o + 3] = 245;
            }
        }

        return PngWriter.Encode(buffer, capture.Width, capture.Height, 4);
    }

    private static void Fill(byte[] rgba, int[] indices, in Rgb color, byte alpha)
    {
        foreach (var i in indices)
        {
            var o = i * 4;
            if (o < 0 || o + 3 >= rgba.Length) continue;

            rgba[o] = color.R;
            rgba[o + 1] = color.G;
            rgba[o + 2] = color.B;
            rgba[o + 3] = alpha;
        }
    }
}
