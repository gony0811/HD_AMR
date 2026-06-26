namespace HD_AMR.Data.Entities;

/// <summary>
/// 범용 key/value 설정 한 개. 화면/서비스가 자유롭게 쓰는 일반 파라미터 저장소로,
/// <see cref="Name"/> 가 유일 키이고 <see cref="Value"/> 는 문자열(숫자/불리언도 문자열로 보관),
/// <see cref="Description"/> 는 사람이 읽는 설명이다. 예: 깊이 ROI 설정(Camera.Depth.Roi.*).
/// </summary>
public class Parameter
{
    public int Id { get; set; }

    /// <summary>유일 식별 키(예: <c>Camera.Depth.Roi.X</c>).</summary>
    public string Name { get; set; } = "";

    /// <summary>값(문자열로 보관 — 숫자/불리언은 호출 측에서 파싱).</summary>
    public string Value { get; set; } = "";

    /// <summary>사람이 읽는 설명(선택).</summary>
    public string? Description { get; set; }

    /// <summary>마지막 갱신 시각(UTC).</summary>
    public DateTime UpdatedAt { get; set; }
}
