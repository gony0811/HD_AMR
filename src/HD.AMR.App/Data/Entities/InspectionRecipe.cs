using HD.AMR.App.Service.Inspection;
using HD.AMR.App.Models;

namespace HD.AMR.App.Data.Entities;

/// <summary>
/// 검사 타입(17종, 사양 §8.5.1·INSPECTION_TYPES.md §5)별 <b>실행 방법</b> 한 세트.
/// ACS `startWeldInspection` 액션의 `(seamType, wall_code)` 조합으로 선택된다.
///
/// 경유점은 담지 않는다 — 경유점·코봇 튜닝값은 티칭 <see cref="InspectionProfile"/> 소관이며,
/// 레시피는 실행 구성(시퀀스 스텝, 실행할 티칭 프로필 지정, 폴백 파라미터, 판정 정책)만 가진다.
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

    /// <summary>드라이런 모드 — 켜면 비전 정렬(③~⑯) 없이 ACS 용접선 시작/끝점으로 2점 교시만 해
    /// 내부 작업물 좌표계를 잡고, 도면 경유점을 <b>이동만</b>(비전 CAPTURE_REQ 없음) 순회한다.
    /// 켜지면 오케스트레이터가 <see cref="StepKeysJson"/>을 무시하고 드라이런 스텝 집합을 자동 구성한다
    /// (커미셔닝·경로/좌표 검증용). 기본 false = 일반 검사.</summary>
    public bool DryRun { get; set; }

    /// <summary>실행할 시퀀스 스텝 Key 배열(JSON, DefaultOrder 순 무관 — 실행은 등록 순서 기준).
    /// null/빈 값이면 등록된 풀시퀀스 전체 실행.</summary>
    public string? StepKeysJson { get; set; }

    /// <summary>ACS 실행 시 사용할 티칭 프로필(<see cref="InspectionProfile.Id"/>, 경유점 소스). null = 미지정 →
    /// LINE/CROSS 액션은 FAILED + inspectionFailed. 프로필 SeamType 은 레시피 타입과 일치해야 한다.
    /// CORNER3 는 실행 시퀀스가 제거되어 사용하지 않는다. 현장 지정값이라 '기본값' 재적용 시에도 보존된다.</summary>
    public int? InspectionProfileId { get; set; }

    // (제거됨) ApproachTeachingKey — 레시피별 코봇 접근 자세 티칭 키. 런타임 미구현, 접근 자세는 Teaching 의 Wall ID
    //   로 결정된다. 코너부 등 위치별 접근은 고도화 단계에서 재설계(2026-09-18).

    // (제거됨) DefaultStandoffMm — action.standoffMm 부재/0 시 폴백으로 폐기(2026-09-18).
    //   툴 면 이격은 이후 CameraTargetDistanceMm 로 일원화됐다 — ② 접근점 후퇴와 ③ 거리 정렬이 같은
    //   값을 봐야 하기 때문. ACS 의 standoffMm 은 런타임에서 쓰지 않는다(로깅만, §10 N18).

    /// <summary>③ 카메라 거리 정렬(cameraAlign) 목표 [mm]. null 이면 전역 기본(400).
    /// ACS action.workingDistanceMm 은 산출 근거가 없어 사용하지 않는다(2026-09-18 결정 — 수신·로그만).</summary>
    public double? CameraTargetDistanceMm { get; set; }

    /// <summary>코봇 이동 속도 [%] — 접근/복귀성 모션(② 검사위치 이동·진입 준비·끝점 이동·홈 복귀·정렬·
    /// ⑱ 원점 이동) 공통. null = 티칭 프로필 RunVel(미지정 시 20) 폴백.</summary>
    public int? MoveVelPercent { get; set; }

    /// <summary>코봇 검사 속도 [%] — ⑱ 경유점 순회(검사 스캔) 전용. null = 이동 속도와 동일.</summary>
    public int? InspectVelPercent { get; set; }

    // (제거됨) SurfaceOverride — 비전 Surface 강제값. X-Y 교시 경유점은 점별 Surface 를 수동 지정하고
    //   CORNER 스텝은 Corner 고정이라 무효화되어 폐기(2026-09-18). Surface 는 경유점 단위로 일원화.
    // (제거됨) PatternJson — CROSS4 십자 패턴 런타임 생성 파라미터. 캡처 교시 단일화로 폐기(2026-09-15).
    //   CROSS3/CROSS4 는 /inspection-points 6-DOF 캡처 프로필 경유점을 실행한다.

    // (제거됨) AlignRetryCount — 정렬 스텝군 재시도 횟수. 런타임 미구현 상태로 불필요 판단되어 폐기(2026-09-18).

    /// <summary>경유점 비전 실패율 상한(0~1). 초과 시 액션 FAILED + inspectionFailed.
    /// 1.0 = 실패율 판정 안 함(현행 단독 실행과 동일).</summary>
    public double VisionFailRatioMax { get; set; } = 1.0;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
