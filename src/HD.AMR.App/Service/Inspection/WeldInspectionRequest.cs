using HD.AMR.App.Models;

namespace HD.AMR.App.Service.Inspection;

/// <summary>`params.seamType` — 용접 형상 (사양 §8.1/§8.5.1). 계약 enum(N13 확장):
/// `LINE`/`CROSS3_R0`·`CROSS3_R90`·`CROSS3_R180`·`CROSS3_R270`/`CROSS4`/`CORNER2`/`CORNER3`.
/// 파서는 하위호환으로 legacy `CROSS`(→CROSS4)·`CORNER`(→CORNER3)·bare `CROSS3`(→CROSS3_R0)도
/// 수용한다. POLYLINE 등 미정의 값은 거부. 내부 enum 이름은 계약 문자열과 다음처럼 대응한다(주석 참조).</summary>
public enum SeamTypeKind
{
    Line,       // 직선 1갈래 (계약 "LINE")
    Cross,      // 십자 4갈래 (계약 "CROSS4", legacy "CROSS") → 레시피 CROSS4-*
    Cross3,     // (tombstone) 레거시 per-surface CROSS3 — 파서 미발행, 구 DB 행 판독용. 회전 4종으로 대체됨.
    Corner,     // 3면 코너 (계약 "CORNER3", legacy "CORNER") → 레시피 CORNER3
    Corner2,    // 2면 코너 (계약 "CORNER2") → 레시피 CORNER2 (실행 미구현 — Enabled=false)
    // T자 3갈래 회전 4종 — 면 자세 무관(CORNER처럼 단일 레시피로 short-circuit), 회전으로만 키잉.
    Cross3R0,   // T자 0°   (계약 "CROSS3_R0", legacy "CROSS3") → 레시피 CROSS3-R0
    Cross3R90,  // T자 90°  (계약 "CROSS3_R90")  → 레시피 CROSS3-R90
    Cross3R180, // T자 180° (계약 "CROSS3_R180") → 레시피 CROSS3-R180
    Cross3R270, // T자 270° (계약 "CROSS3_R270") → 레시피 CROSS3-R270
}

// SurfaceOrientation(면 자세 5군)은 검사 면 정본 표와 함께 HD.AMR.App.Models.WallCodes 로 이동.

/// <summary>`position.drawingPos` echo — tank/level/wall_code + 벽면-로컬 u,v + 도면 x,y,z (§8.1).</summary>
public sealed record WeldDrawingPos(
    string Tank, int Level, string WallCode,
    double? U, double? V,
    double X, double Y, double Z);

/// <summary>
/// `startWeldInspection` 액션 파라미터의 해석 결과 (사양 §8.1 — jobRef/position/params 3쌍).
/// <see cref="WeldInspectionActionParser"/>가 생성한다.
/// </summary>
public sealed record WeldInspectionRequest(
    string JobRef,
    double[] SeamStartW,            // [x,y,z] m — 맵(월드) 좌표
    double[] SeamEndW,
    WeldDrawingPos DrawingPos,
    SeamTypeKind SeamType,
    string SectionDxfId,
    string InspectionProfileId,     // 촬영/측정 프리셋(자유 문자열) — 레시피 선택에 사용하지 않음(N13 대기)
    double StandoffMm,
    double? WorkingDistanceMm,      // ACS 선택 항목 — AMR 미사용(로그용). 카메라 거리는 레시피 CameraTargetDistanceMm
    string AnchorGroupId,
    int SeqInGroup,
    // ACS 발급 검사 작업 식별자 [사양 §8.1/§8.1.1, N14] — 둘 다 선택 필드.
    Guid? TaskId = null,            // 용접선 1구간의 영구 GUID → CAPTURE_REQ [15-30] → SAIGE productId. null=구버전 ACS(폴백 Guid.Empty)
    byte? Attempt = null);          // taskId별 누적 시도 번호 1~255 → CAPTURE_REQ [31]. null=폴백 1. AMR은 증감하지 않는다

/// <summary>
/// `moveToSeamStart` 액션 파라미터의 해석 결과 (사양 §8.7 — 코봇 seam 시작점 reach 시험).
/// 검사가 아니므로 seamType·profile·taskId/attempt·anchor 가 없다. position 은 startWeldInspection 과 동일 구조.
/// </summary>
public sealed record MoveToSeamStartRequest(
    string JobRef,
    double[] SeamStartW,            // [x,y,z] m — 맵(월드) 좌표, 코봇툴 접근 목표
    double[] SeamEndW,              // 방향 참고(시험은 시작점까지만)
    WeldDrawingPos DrawingPos);     // wall_code = 면 법선 자세 키
