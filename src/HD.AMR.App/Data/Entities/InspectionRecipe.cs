using HD.AMR.App.Service.Inspection;
using HD.AMR.App.Models;

namespace HD.AMR.App.Data.Entities;

/// <summary>
/// 검사 타입(9종, 사양 §8.5.1·INSPECTION_TYPES.md §5)별 <b>실행 방법</b> 한 세트.
/// ACS `startWeldInspection` 액션의 `seamType` 으로 선택된다(wall_code 는 유효성 확인만 — 면 자세-독립, §9-8).
///
/// 경유점은 담지 않는다 — 경유점·코봇 튜닝값은 티칭 <see cref="InspectionProfile"/> 소관이며,
/// 레시피는 실행 구성(시퀀스 스텝, 실행할 티칭 프로필 지정, 폴백 파라미터, 판정 정책)만 가진다.
/// 기본값은 기동 시 upsert-if-missing 시드(코드)로 관리하고, 현장 조정값은 DB에 남는다.
/// </summary>
public class InspectionRecipe
{
    /// <summary>레시피 id — "LINE"/"CROSS4_L"/"CROSS3-R0".. 등 9종 문자열 그대로 (사양 §8.5.1, FK 없음).</summary>
    public string Id { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public SeamTypeKind SeamType { get; set; }

    /// <summary>대상 면 자세. 모든 레시피가 면 자세-독립(<see cref="SurfaceOrientation.Any"/>)이며, 표시·영속화 라벨일 뿐
    /// 런타임 실행 분기에 쓰지 않는다(§9-8 — 접근 자세·법선은 wall_code 로 계산).</summary>
    public SurfaceOrientation Orientation { get; set; }

    /// <summary>실행 가능 게이트 — 미구현 타입(CROSS4_L·R/CROSS3-R*/CORNER2/CORNER3)은 false 시드.
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

    /// <summary>LINE 전용 — 챔퍼 면(SL/PL/SU/PU)에서 수직(경사 방향)으로 진행할 때 쓰는 티칭 프로필.
    /// 경사 방향은 코로게이션 피치가 254.6mm 라 360mm 프로필의 경유점을 쓸 수 없다. 챔퍼 수평·일반 면은
    /// <see cref="InspectionProfileId"/> 를 쓴다. null 이면 챔퍼 수직 LINE 액션은 FAILED + inspectionFailed
    /// (기본 프로필로 대체하지 않음 — 엉뚱한 위치를 스캔하게 된다). 현장 지정값이라 '기본값' 재적용 시에도 보존된다.</summary>
    public int? ChamferSlopeProfileId { get; set; }

    /// <summary>십자(CROSS4_L/CROSS4_R) 전용 — 면(wall_code)별로 따로 쓸 티칭 프로필 지정. JSON object
    /// (예 {"SM":12,"PM":13}, 값 = <see cref="InspectionProfile.Id"/>). 여기 없는 면은 <see cref="InspectionProfileId"/> 를 쓴다.
    /// null = 모든 면이 기본 프로필. 해석은 <see cref="InspectionRecipeResolver.ParseWallProfiles"/>.
    /// 현장 지정값이라 '기본값' 재적용 시에도 보존된다.</summary>
    public string? WallProfilesJson { get; set; }

    /// <summary>CROSS4_R 전용 — ACS 가 좌/우를 밝히지 않은 구 seamType("CROSS4"·"CROSS")을 보냈을 때 <b>우측</b>으로 실행할 면
    /// (wall_code 쉼표 구분, 예 "SM,SL"). 여기 없는 면은 좌측(CROSS4_L)으로 실행한다. null = 전부 좌측.
    /// 좌/우는 면을 정면으로 바라본 기준이다. ACS 가 CROSS4_L/CROSS4_R 를 보내면 이 값은 쓰지 않는다.
    /// 현장 지정값이라 '기본값' 재적용 시에도 보존된다.</summary>
    public string? UnspecifiedSideWalls { get; set; }

    // (제거됨) ApproachTeachingKey — 레시피별 코봇 접근 자세 티칭 키. 런타임 미구현, 접근 자세는 Teaching 의 Wall ID
    //   로 결정된다. 코너부 등 위치별 접근은 고도화 단계에서 재설계(2026-09-18).

    // (제거됨) DefaultStandoffMm — action.standoffMm 부재/0 시 폴백으로 폐기(2026-09-18).
    //   툴 면 이격은 이후 CameraTargetDistanceMm 로 일원화됐다 — ② 접근점 후퇴와 ③ 거리 정렬이 같은
    //   값을 봐야 하기 때문. ACS 의 standoffMm 은 런타임에서 쓰지 않는다(로깅만, §10 N18).

    /// <summary>③ 카메라 거리 정렬(cameraAlign) 목표 [mm]. null 이면 전역 기본(400).
    /// ACS action.workingDistanceMm 은 산출 근거가 없어 사용하지 않는다(2026-09-18 결정 — 수신·로그만).</summary>
    public double? CameraTargetDistanceMm { get; set; }

    /// <summary>코봇 이동 속도 [%] — 접근/복귀성 모션(② 검사위치 이동·진입 준비·끝점 이동·홈 복귀·정렬·
    /// ⑱ 첫 경유점 스테이징) 공통. null = 설정 <c>Cobot:DefaultVelPct</c>(CobotService.DefaultMoveVelPercent). 프로필 RunVel 은 검사 속도라 쓰지 않는다.</summary>
    public int? MoveVelPercent { get; set; }

    /// <summary>이동 속도 미지정 시 기본값 [%] — 설정을 읽을 수 없는 곳의 폴백. 실행 경로는 CobotService.DefaultMoveVelPercent 를 쓴다.</summary>
    public const int DefaultMoveVelPercent = 20;

    /// <summary>코봇 검사 속도 [%] — ⑱ 경유점 순회(검사 스캔) 전용. null = 지정된 검사 프로파일에 저장된 속도
    /// (그것도 없으면 기본 검사 속도 파라미터 <c>Inspection.DefaultInspectVelPercent</c>).</summary>
    public int? InspectVelPercent { get; set; }

    /// <summary>코봇 검사 속도 [mm/s] — ⑱ 경유점 순회(검사 스캔) TCP 속도. 파라미터 Inspection.LinearSpeedMmPerSecAt100Pct 로 % 환산해 지령.
    /// 값이 있으면 <see cref="InspectVelPercent"/>·프로파일 속도(%)보다 우선한다. null = % 속도 사용.</summary>
    public double? InspectSpeedMmPerSec { get; set; }

    /// <summary>코봇 검사 가속도 [%] — ⑱ 경유점 순회 동안만 전역 가속도 배율(SetOaccScale)을 이 값으로 바꾸고,
    /// 순회가 끝나면(성공·실패·취소 모두) 평소 값(파라미터 cobot.accScalePercent, 비면 100%)으로 복원한다. null = 변경 안 함.</summary>
    public int? InspectAccPercent { get; set; }

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
