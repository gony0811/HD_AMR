using HD.AMR.App.Models;

namespace HD.AMR.App.Service.Inspection;

/// <summary>`params.seamType` — 용접 형상 (사양 §8.1/§8.5.1, 독립 5종). 계약 enum(N13 확장):
/// `LINE`/`CROSS3`/`CROSS4`/`CORNER2`/`CORNER3`. 파서는 하위호환으로 legacy `CROSS`(→CROSS4)·`CORNER`(→CORNER3)도
/// 수용한다. POLYLINE 등 미정의 값은 거부. 내부 enum 이름은 계약 문자열과 다음처럼 대응한다(주석 참조).</summary>
public enum SeamTypeKind
{
    Line,       // 직선 1갈래 (계약 "LINE")
    Cross,      // 십자 4갈래 (계약 "CROSS4", legacy "CROSS") → 레시피 CROSS4-*
    Cross3,     // T자 3갈래 (계약 "CROSS3") → 레시피 CROSS3-*
    Corner,     // 3면 코너 (계약 "CORNER3", legacy "CORNER") → 레시피 CORNER3
    Corner2,    // 2면 코너 (계약 "CORNER2") → 레시피 CORNER2 (실행 미구현 — Enabled=false)
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
    Guid? TaskId = null,            // ACS 발급 검사 작업 식별자 — 비전 CAPTURE_REQ taskId(=SAIGE productId)로 전달
    string? TaskIdRaw = null,       // 수신 원문(GUID 파싱 실패 진단용 — 파싱 성공 시에도 원문 보존)
    byte? Attempt = null);          // ACS 발급 시도 번호(1~255) — 미수신이면 null(=CAPTURE_REQ 에 1)
