namespace HD.AMR.App.Models;

/// <summary>교시 위치 1건에서 되짚은 공구 오프셋.</summary>
/// <param name="Key">교시 위치 키.</param>
/// <param name="Name">교시 위치 표시 이름.</param>
/// <param name="CapturedAt">그 위치를 캡처한 시각(UTC).</param>
/// <param name="Offset">복원된 공구 오프셋 [x,y,z,rx,ry,rz] (mm/도, 플랜지 → TCP).</param>
public sealed record ToolOffsetRecoveryRow(string Key, string Name, DateTime? CapturedAt, double[] Offset);

/// <summary>
/// 지워진 공구 좌표계 정의 복원 결과(<c>ToolOffsetRecoveryService</c>).
/// 교시 위치마다 `offset = inv(T_플랜지) · T_TCP` 로 계산한 값들과, 행 사이 일치도·권장값을 담는다.
/// </summary>
/// <param name="Success">계산 성공 여부. 사용할 교시 위치가 없으면 false.</param>
/// <param name="Error">실패 사유.</param>
/// <param name="Tool">복원 대상 공구 번호.</param>
/// <param name="Rows">행별 결과(최신 캡처 순).</param>
/// <param name="Recommended">권장 오프셋 — 가장 최근에 캡처된 행의 값. 실패 시 0 배열.</param>
/// <param name="MaxPosSpreadMm">행 간 오프셋 위치 차이의 최댓값(mm). 행이 1개면 0.</param>
/// <param name="MaxRotSpreadDeg">행 간 오프셋 회전 차이의 최댓값(도). 행이 1개면 0.</param>
/// <param name="Warnings">주의 메시지(행 간 불일치 등).</param>
public sealed record ToolOffsetRecoveryResult(
    bool Success, string? Error, int Tool,
    IReadOnlyList<ToolOffsetRecoveryRow> Rows,
    double[] Recommended,
    double MaxPosSpreadMm, double MaxRotSpreadDeg,
    IReadOnlyList<string> Warnings)
{
    public static ToolOffsetRecoveryResult Fail(int tool, string error) =>
        new(false, error, tool, Array.Empty<ToolOffsetRecoveryRow>(), new double[6], 0, 0, Array.Empty<string>());
}
