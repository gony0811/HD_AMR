namespace HD.AMR.App.Models;

/// <summary>
/// 티칭 세트를 나누는 리프트 모드. 리프트 구성(LONG / SHORT)에 따라 코봇 기준 자세가 달라
/// 같은 슬롯(홈·검사 준비 등)을 모드별로 따로 티칭한다. 기존 DB 의 티칭은 모두 SHORT 다.
/// 운영(시퀀스·홈 복귀·리치 맵 등)은 <see cref="ActiveKey"/> 파라미터가 가리키는 모드의 세트를 쓴다.
/// </summary>
public static class TeachingLiftMode
{
    public const string Short = "SHORT";
    public const string Long = "LONG";

    /// <summary>표시 순서.</summary>
    public static readonly string[] All = { Short, Long };

    /// <summary>현재 운영에 적용할 티칭 세트 파라미터 키(값 SHORT/LONG).</summary>
    public const string ActiveKey = "Teaching.LiftMode";

    /// <summary>대소문자·공백 무시, 알 수 없는 값은 SHORT.</summary>
    public static string Normalize(string? mode)
        => string.Equals(mode?.Trim(), Long, StringComparison.OrdinalIgnoreCase) ? Long : Short;
}
