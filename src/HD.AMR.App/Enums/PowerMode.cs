namespace HD.AMR.App.Enums;

/// <summary>
/// 온보드 배터리 전력 상태(사양 §3). 미션 상태머신과 직교 — Order 수락 경로에만 게이트로 쓴다.
/// Phase 1 범위: <see cref="Normal"/> / <see cref="Low"/> / <see cref="Critical"/>.
/// <c>SWAP</c> 상태는 Phase 3 에서 <c>BatterySwapMoveAction</c> 처리와 함께 추가 예정.
/// </summary>
public enum PowerMode : byte
{
    Normal = 0,
    Low = 1,
    Critical = 2,
}
