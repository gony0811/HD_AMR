namespace HD_AMR.Models;

/// <summary>
/// Task/Job 진행 상태 (Input Register 60~63)
/// </summary>
public record TaskProgress
{
    /// <summary>전체 Task 수</summary>
    public ushort TotalTaskCount { get; init; }

    /// <summary>실행중인 Task 번호</summary>
    public ushort CurrentTaskNumber { get; init; }

    /// <summary>전체 Job 수</summary>
    public ushort TotalJobCount { get; init; }

    /// <summary>실행중인 Job 번호</summary>
    public ushort CurrentJobNumber { get; init; }
}
