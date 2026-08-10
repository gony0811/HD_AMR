using HD_AMR.Contracts.Lidar;

namespace HD_AMR.LidarService.Device;

/// <summary>
/// 한 프레임의 센서 데이터. <c>NslPCD</c> 의 언매니지드 버퍼에서 <b>유효 영역만</b>
/// 뽑아낸 것이다(원본 배열은 항상 800x600 으로 잡혀 있지만 TYPE_A 는 320x240 만 쓴다).
///
/// 모든 배열은 row-major, 길이 <see cref="Width"/> * <see cref="Height"/>.
///
/// 배열은 공유되므로(복사하지 않는다) 소비 측에서 수정하면 안 된다.
/// </summary>
internal sealed record LidarCapture
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required DateTimeOffset CapturedAt { get; init; }
    public required double TemperatureC { get; init; }

    /// <summary>거리(mm). 64000 이상은 무효 픽셀 코드(<see cref="NslNative.LowAmplitude"/> 등).</summary>
    public required int[] Distance2D { get; init; }

    /// <summary>진폭(적외선 반사 강도). 신뢰도 판정과 모니터링 영상에 쓴다.</summary>
    public required int[] Amplitude { get; init; }

    /// <summary>
    /// 센서 좌표계 3D 좌표(mm). nanolib 이 렌즈 모델로 계산한 값을 그대로 쓴다 —
    /// 렌즈 내부 파라미터가 공개되어 있지 않아 직접 재투영할 수 없다.
    ///
    /// 축 규약: 열 증가 = +X, 행 증가 = +Y, 광축 전방 = +Z (오른손계). 실측 확인됨.
    ///
    /// ⚠ <b>무효 픽셀에서는 X/Y/Z 에 0 이 아니라 무효 코드값이 그대로 들어간다</b>
    ///   (예: dist2D=64002 → X=Y=Z=64002.0). 0 으로 가정하고 거르면 64미터 지점에
    ///   유령 점이 생겨 능선 피팅이 조용히 망가진다. 반드시 <see cref="IsValid"/> 로
    ///   <see cref="Distance2D"/> 를 먼저 검사할 것.
    /// </summary>
    public required double[] X { get; init; }
    public required double[] Y { get; init; }
    public required double[] Z { get; init; }

    /// <summary>
    /// 모든 픽셀이 채워진 완전한 프레임인지.
    ///
    /// nanolib 은 손상 프레임을 알려주는 수단을 제공하지 않으므로(반환값·카운터·체크섬·
    /// 프레임번호 전부 없음) 데이터에서 직접 판정한다. 정상 프레임에서 <c>distance2D == 0</c>
    /// 은 768만 픽셀 표본에서 한 번도 나오지 않았다 — 유효값은 1~63999, 무효는 64001 이상의
    /// 코드다. 따라서 0 은 "채워지지 않은 픽셀"을 뜻한다.
    /// </summary>
    public required bool IsComplete { get; init; }

    public int PixelCount => Width * Height;

    /// <summary>유효한 거리값인지. 64000 이상은 전부 무효 코드다.</summary>
    public static bool IsValid(int distance) =>
        distance is > 0 and < NslNative.LimitForValidData;
}

/// <summary>무효 픽셀 분류 집계. 실패 원인을 좁히는 근거라 성공/실패 모두에서 만든다.</summary>
internal sealed record PixelStats
{
    public int Valid { get; init; }
    public int LowAmplitude { get; init; }
    public int AdcOverflow { get; init; }
    public int Saturation { get; init; }
    public int Other { get; init; }

    /// <summary>채워지지 않은 픽셀(<c>distance2D == 0</c>). 0 이 아니면 부분 프레임이다.</summary>
    public int Unfilled { get; init; }

    public int Total => Valid + LowAmplitude + AdcOverflow + Saturation + Other + Unfilled;
}

/// <summary>
/// 다중 프레임 평균 결과.
///
/// 정지 측정이므로 N 프레임을 평균해 거리 노이즈를 줄인다(이론상 1/√N). 이게 320x240
/// ToF 로 10mm 급 정밀도를 맞추는 핵심 수단이다. 다만 무효 픽셀은 프레임마다 달라지므로,
/// 단순 산술평균이 아니라 <b>픽셀별로 유효한 샘플만</b> 평균하고 그 개수를 함께 남긴다.
/// </summary>
internal sealed class AveragedCapture
{
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>첫 프레임의 캡처 시각.</summary>
    public required DateTimeOffset CapturedAt { get; init; }

    public required int FramesRequested { get; init; }
    public required int FramesUsed { get; init; }

    /// <summary>불완전 판정으로 버려진 프레임 수.</summary>
    public required int IncompleteFramesDropped { get; init; }

    public required double TemperatureC { get; init; }

    /// <summary>픽셀별 유효 샘플 수. 0 이면 그 픽셀은 무효다.</summary>
    public required int[] SampleCount { get; init; }

    /// <summary>유효 샘플의 평균 거리(mm). <see cref="SampleCount"/> 가 0 인 픽셀은 NaN.</summary>
    public required double[] Distance { get; init; }

    public required double[] Amplitude { get; init; }

    /// <summary>센서 좌표계 평균 3D 좌표(mm). 무효 픽셀은 NaN.</summary>
    public required double[] X { get; init; }
    public required double[] Y { get; init; }
    public required double[] Z { get; init; }

    /// <summary>마지막 프레임 기준 픽셀 분류. 품질 보고용.</summary>
    public required PixelStats Stats { get; init; }

    public MeasurementQuality ToQuality() => new()
    {
        FramesRequested = FramesRequested,
        FramesUsed = FramesUsed,
        IncompleteFramesDropped = IncompleteFramesDropped,
        ValidPixels = Stats.Valid,
        LowAmplitudePixels = Stats.LowAmplitude,
        SaturatedPixels = Stats.Saturation,
        AdcOverflowPixels = Stats.AdcOverflow,
        SensorTemperatureC = TemperatureC,
    };
}

internal static class CaptureAverager
{
    /// <summary>
    /// 프레임들을 픽셀별로 평균한다. 프레임마다 무효 픽셀 위치가 달라지므로 유효한
    /// 샘플만 누적하고 개수를 따로 센다 — 무효 코드(64001 등)를 그대로 평균에 넣으면
    /// 거리값이 통째로 오염된다.
    /// </summary>
    /// <exception cref="ArgumentException">프레임 목록이 비었거나 해상도가 서로 다를 때.</exception>
    public static AveragedCapture Average(
        IReadOnlyList<LidarCapture> frames, int framesRequested, int incompleteDropped = 0)
    {
        if (frames.Count == 0)
            throw new ArgumentException("평균할 프레임이 없다.", nameof(frames));

        var first = frames[0];
        int w = first.Width, h = first.Height, n = w * h;

        foreach (var f in frames)
        {
            if (f.Width != w || f.Height != h)
                throw new ArgumentException(
                    $"프레임 해상도가 일치하지 않는다: {w}x{h} vs {f.Width}x{f.Height}", nameof(frames));
        }

        var count = new int[n];
        var dist = new double[n];
        var ampl = new double[n];
        var sx = new double[n];
        var sy = new double[n];
        var sz = new double[n];

        foreach (var f in frames)
        {
            for (int i = 0; i < n; i++)
            {
                if (!LidarCapture.IsValid(f.Distance2D[i])) continue;

                count[i]++;
                dist[i] += f.Distance2D[i];
                ampl[i] += f.Amplitude[i];
                sx[i] += f.X[i];
                sy[i] += f.Y[i];
                sz[i] += f.Z[i];
            }
        }

        for (int i = 0; i < n; i++)
        {
            if (count[i] == 0)
            {
                dist[i] = ampl[i] = sx[i] = sy[i] = sz[i] = double.NaN;
                continue;
            }

            double c = count[i];
            dist[i] /= c;
            ampl[i] /= c;
            sx[i] /= c;
            sy[i] /= c;
            sz[i] /= c;
        }

        return new AveragedCapture
        {
            Width = w,
            Height = h,
            CapturedAt = first.CapturedAt,
            FramesRequested = framesRequested,
            FramesUsed = frames.Count,
            IncompleteFramesDropped = incompleteDropped,
            TemperatureC = frames[^1].TemperatureC,
            SampleCount = count,
            Distance = dist,
            Amplitude = ampl,
            X = sx,
            Y = sy,
            Z = sz,
            Stats = Classify(frames[^1]),
        };
    }

    /// <summary>무효 픽셀 코드를 분류 집계한다.</summary>
    public static PixelStats Classify(LidarCapture frame)
    {
        int valid = 0, low = 0, overflow = 0, sat = 0, other = 0, unfilled = 0;

        foreach (var d in frame.Distance2D)
        {
            if (d == 0) unfilled++;
            else if (d < NslNative.LimitForValidData) valid++;
            else if (d == NslNative.LowAmplitude) low++;
            else if (d == NslNative.AdcOverflow) overflow++;
            else if (d == NslNative.Saturation) sat++;
            else other++;
        }

        return new PixelStats
        {
            Valid = valid,
            LowAmplitude = low,
            AdcOverflow = overflow,
            Saturation = sat,
            Other = other,
            Unfilled = unfilled,
        };
    }
}
