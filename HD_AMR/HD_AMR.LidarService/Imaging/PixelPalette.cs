namespace HD_AMR.LidarService.Imaging;

internal readonly record struct Rgb(byte R, byte G, byte B);

/// <summary>
/// 미리보기 영상의 색 규약.
///
/// <b>무효 픽셀을 검게 칠하지 않는 것이 요점이다.</b> ToF 에서 "값이 없다"는 한 가지가 아니라
/// 네 가지 서로 다른 사건이고, 대응도 정반대다 — 진폭 부족은 노출을 <b>올려야</b> 하고,
/// 포화와 ADC 오버플로는 <b>내려야</b> 한다. 전부 검게 칠하면 화면을 봐도 어느 쪽인지 알 수
/// 없어서 노출 튜닝이 시행착오가 된다. 코드마다 색을 다르게 준 이유가 이것이다.
///
/// 금속 코러게이션은 마루에서 정반사로 포화되고 골에서 신호가 부족해지는, 두 실패가 한
/// 화면에 같이 나타나는 대상이라 특히 중요하다.
/// </summary>
internal static class PixelPalette
{
    /// <summary>채워지지 않은 픽셀(distance == 0). 부분 프레임의 증거라 눈에 띄어야 한다.</summary>
    public static readonly Rgb Unfilled = new(255, 0, 255);

    /// <summary>진폭 부족(NSL_LOW_AMPLITUDE). 노출을 올리거나 minAmplitude 를 낮춰야 한다.</summary>
    public static readonly Rgb LowAmplitude = new(20, 24, 38);

    /// <summary>ADC 오버플로(NSL_ADC_OVERFLOW). 노출 과다.</summary>
    public static readonly Rgb AdcOverflow = new(255, 140, 0);

    /// <summary>포화(NSL_SATURATION). 정반사. 노출 과다이거나 각도 문제.</summary>
    public static readonly Rgb Saturation = new(255, 45, 45);

    /// <summary>그 외 무효 코드.</summary>
    public static readonly Rgb OtherInvalid = new(90, 90, 90);

    /// <summary>유효하지만 검출 깊이 구간보다 가까운 픽셀. 형상은 보이되 색은 죽인다.</summary>
    public static readonly Rgb NearerThanBand = new(38, 42, 52);

    /// <summary>유효하지만 검출 깊이 구간보다 먼 픽셀.</summary>
    public static readonly Rgb FartherThanBand = new(104, 110, 124);

    /// <summary>
    /// Turbo 유사 컬러맵. Jet 대신 쓰는 이유는 Jet 이 초록 대역에서 명도가 꺾여
    /// 실제로는 단조 변화인 깊이에 가짜 경계선이 보이기 때문이다.
    /// </summary>
    private static readonly Rgb[] Stops =
    [
        new(48, 18, 59),
        new(65, 69, 171),
        new(70, 117, 237),
        new(57, 162, 252),
        new(27, 207, 212),
        new(36, 236, 166),
        new(97, 252, 108),
        new(164, 252, 59),
        new(222, 224, 49),
        new(251, 155, 35),
        new(169, 32, 10),
    ];

    /// <summary>0~1 을 컬러맵 색으로. 범위를 벗어난 값은 양 끝으로 자른다.</summary>
    public static Rgb Turbo(double t)
    {
        if (double.IsNaN(t)) return OtherInvalid;

        t = Math.Clamp(t, 0.0, 1.0);
        var scaled = t * (Stops.Length - 1);
        var i = (int)scaled;
        if (i >= Stops.Length - 1) return Stops[^1];

        var f = scaled - i;
        var a = Stops[i];
        var b = Stops[i + 1];

        return new Rgb(
            (byte)(a.R + (b.R - a.R) * f),
            (byte)(a.G + (b.G - a.G) * f),
            (byte)(a.B + (b.B - a.B) * f));
    }

    /// <summary>무효 거리 코드를 색으로. 유효값이면 null.</summary>
    public static Rgb? ForInvalidCode(int distance)
    {
        if (distance == 0) return Unfilled;
        if (distance < Device.NslNative.LimitForValidData) return null;
        if (distance == Device.NslNative.LowAmplitude) return LowAmplitude;
        if (distance == Device.NslNative.AdcOverflow) return AdcOverflow;
        if (distance == Device.NslNative.Saturation) return Saturation;
        return OtherInvalid;
    }
}
