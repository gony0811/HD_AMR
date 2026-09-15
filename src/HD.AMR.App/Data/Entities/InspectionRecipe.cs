using HD.AMR.App.Service.Inspection;

namespace HD.AMR.App.Data.Entities;

/// <summary>
/// 검사 타입(17종, 사양 §8.5.1·INSPECTION_TYPES.md §5)별 <b>실행 방법</b> 한 세트.
/// ACS `startWeldInspection` 액션의 `(seamType, wall_code)` 조합으로 선택된다.
///
/// 경유점은 담지 않는다 — 경유점·코봇 튜닝값은 도면별 티칭 <see cref="InspectionProfile"/> 소관이며,
/// 레시피는 타입 수준 실행 구성(시퀀스 스텝, 접근 자세 키, 폴백 파라미터, 판정 정책)만 가진다.
/// 기본값은 기동 시 upsert-if-missing 시드(코드)로 관리하고, 현장 조정값은 DB에 남는다.
/// </summary>
public class InspectionRecipe
{
    /// <summary>레시피 id — "LINE-WALL" 등 17종 문자열 그대로 (사양 §8.5.1, FK 없음).</summary>
    public string Id { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public SeamTypeKind SeamType { get; set; }

    /// <summary>대상 면 자세. CORNER3 은 면 자세 무관(<see cref="SurfaceOrientation.Any"/>).</summary>
    public SurfaceOrientation Orientation { get; set; }

    /// <summary>실행 가능 게이트 — 미구현 타입(CROSS4-*/CORNER3)은 false 시드.
    /// false 인 레시피로 매핑된 액션은 FAILED + inspectionFailed(실행 불가) 보고.</summary>
    public bool Enabled { get; set; }

    /// <summary>실행할 시퀀스 스텝 Key 배열(JSON, DefaultOrder 순 무관 — 실행은 등록 순서 기준).
    /// null/빈 값이면 등록된 풀시퀀스 전체 실행.</summary>
    public string? StepKeysJson { get; set; }

    /// <summary>면 자세군별 코봇 접근 자세 티칭 키(<see cref="TeachingPosition.Key"/>). 빈 값이면 현행 스텝 기본 동작.</summary>
    public string ApproachTeachingKey { get; set; } = "";

    /// <summary>action.standoffMm 부재/0 시 폴백 [mm].</summary>
    public double DefaultStandoffMm { get; set; }

    /// <summary>action.workingDistanceMm 부재 시 폴백 [mm]. null 이면 전역 기본(400).</summary>
    public double? CameraTargetDistanceMm { get; set; }

    /// <summary>비전 Surface 강제값(0=Flat,1=Corner,2=Corrugation). null=|θ| 자동 판정(부록 D.3).
    /// CORNER3 은 1(Corner) 고정 시드.</summary>
    public byte? SurfaceOverride { get; set; }

    /// <summary>타입별 스캔 패턴 파라미터(JSON). null=패턴 없음(LINE — 프로필 경유점 사용).
    /// CROSS4-*: <see cref="Service.Inspection.CrossPatternParams"/> 직렬화
    /// (<c>{"ArmMm":180,"SpacingMm":30,"PerpRzDeg":-90}</c>) — 교차점(wobj 원점) 중심 4-arm 경유점 생성.</summary>
    public string? PatternJson { get; set; }

    /// <summary>정렬 스텝군 실패 시 재시도 횟수 (0=재시도 없음).</summary>
    public int AlignRetryCount { get; set; }

    /// <summary>경유점 비전 실패율 상한(0~1). 초과 시 액션 FAILED + inspectionFailed.
    /// 1.0 = 실패율 판정 안 함(현행 단독 실행과 동일).</summary>
    public double VisionFailRatioMax { get; set; } = 1.0;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
