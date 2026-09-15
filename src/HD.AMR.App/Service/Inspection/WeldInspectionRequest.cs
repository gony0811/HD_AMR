namespace HD.AMR.App.Service.Inspection;

/// <summary>`params.seamType` — 용접라인 형태 (사양 §8.1/§8.5.1). POLYLINE 등 미정의 값은 파싱 단계에서 거부.
/// <see cref="Cross3"/>(3갈래 T자)는 <b>온보드 카탈로그 전용</b> — ACS 계약 enum(LINE/CROSS/CORNER)에는 없으므로
/// 파서는 CROSS3 을 수용하지 않는다(계약 확장은 N13). CROSS3 레시피는 온보드 교시/실행 경로로만 도달한다.</summary>
public enum SeamTypeKind
{
    Line,
    Cross,      // 4갈래 십자 (계약 "CROSS")
    Cross3,     // 3갈래 T자 (온보드 전용, N13 계약 확장 전까지 ACS 미발행)
    Corner,
}

/// <summary>`wall_code` → 면 자세 5군 (사양 §8.5.1 (2), INSPECTION_TYPES.md §2).</summary>
public enum SurfaceOrientation
{
    Floor,          // B
    Ceiling,        // T
    Wall,           // SM PM F A
    ChamferLower,   // SL PL (45°)
    ChamferUpper,   // SU PU (45°)
    Any,            // CORNER3 — 면 자세 무관 단일 레시피
}

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
    double? WorkingDistanceMm,
    string AnchorGroupId,
    int SeqInGroup);
