using HD_AMR.Contracts.Lidar;

namespace HD_AMR.Models;

/// <summary>
/// 좌표 변환을 거친 능선(코러게이션 마루). <see cref="Frame"/> 이 어느 좌표계인지 명시한다.
///
/// 좌표계를 필드로 들고 다니는 이유는, 값만 봐서는 센서 기준인지 로봇 기준인지 구분할 수
/// 없기 때문이다. 둘 다 mm 단위의 그럴듯한 숫자라서 섞이면 눈으로는 절대 못 잡는다.
/// </summary>
/// <param name="Frame">아래 값들이 표현된 좌표계.</param>
/// <param name="Ridge">변환된 능선. <see cref="Valid"/> 가 false 면 null.</param>
/// <param name="Confidence">젯슨이 산출한 검출 신뢰도 0~1. 변환은 이 값을 바꾸지 않는다.</param>
/// <param name="Valid">측정과 변환이 모두 성공했는지.</param>
/// <param name="Note">Valid=false 사유(정상이면 null).</param>
public record RidgePose(
    LidarFrame Frame,
    RidgeLine? Ridge,
    double Confidence,
    bool Valid,
    string? Note)
{
    /// <summary>계산 불가 상태(안내 메시지 포함) 헬퍼.</summary>
    public static RidgePose Invalid(LidarFrame frame, string note) =>
        new(frame, null, 0, false, note);
}
