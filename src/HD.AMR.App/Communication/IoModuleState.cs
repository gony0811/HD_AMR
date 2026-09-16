namespace HD.AMR.App.Communication;

/// <summary>
/// LS산전 IO 모듈의 한 시점 상태 스냅샷. 서비스가 주기적으로 읽어 캐싱하고, UI가 당겨간다.
/// <see cref="Inputs"/>는 XBE-DC16A 입력 16점, <see cref="Outputs"/>는 XBE-TN16A 출력 16점.
/// </summary>
public sealed class IoModuleState
{
    public IoModuleState(bool[] inputs, bool[] outputs, DateTime updatedUtc)
    {
        Inputs = inputs;
        Outputs = outputs;
        UpdatedUtc = updatedUtc;
    }

    /// <summary>디지털 입력 접점 상태 (XBE-DC16A, 16점)</summary>
    public bool[] Inputs { get; }

    /// <summary>디지털 출력 상태 (XBE-TN16A, 16점) — 장비 echo 영역에서 되읽은 실제 출력 값</summary>
    public bool[] Outputs { get; }

    /// <summary>이 스냅샷을 읽은 시각(UTC)</summary>
    public DateTime UpdatedUtc { get; }
}
