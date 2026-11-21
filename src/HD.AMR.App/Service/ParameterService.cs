using System.Globalization;
using HD.AMR.App.Data;
using HD.AMR.App.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HD.AMR.App.Service;

/// <summary>
/// 범용 key/value 파라미터(<see cref="Parameter"/>) 저장소에 대한 타입 헬퍼.
/// <see cref="Parameter.Name"/> 유일 키로 upsert 하며, 숫자/불리언은 InvariantCulture 문자열로 보관한다.
/// 깊이 ROI 등 화면 설정을 DB에 영속화해 재시작 후에도 항상 복원하는 데 사용한다.
/// </summary>
public class ParameterService
{
    private readonly HdAmrDbContext _db;

    public ParameterService(HdAmrDbContext db) => _db = db;

    /// <summary>
    /// 파라미터가 저장/삭제된 뒤(커밋 후) 이름과 함께 발생. 이 서비스는 Scoped 라 인스턴스 이벤트로는
    /// 싱글톤(예: <see cref="WeldTrackingService"/> 의 ROI 캐시)이 다른 화면의 변경을 알 수 없어 정적 이벤트로 둔다.
    /// 호출 스레드에서 동기 발생하므로 구독자는 무거운 일을 백그라운드로 넘겨야 한다.
    /// </summary>
    public static event Action<string>? Changed;

    /// <summary>
    /// 더 이상 어떤 코드도 읽지 않는 키. 기동 시 <see cref="RemoveObsoleteAsync"/> 가 지운다.
    /// Camera.Weld.Roi.* — 초기 Web 카메라 페이지의 Peak·Bead 공용 ROI. Camera.Peak/Bead.Roi.* 로 분리되며 폐기.
    /// </summary>
    public static readonly string[] ObsoleteNames =
    [
        "Camera.Weld.Roi.Enabled", "Camera.Weld.Roi.X", "Camera.Weld.Roi.Y",
        "Camera.Weld.Roi.W", "Camera.Weld.Roi.H",
    ];

    /// <summary>
    /// 이름이 바뀐 키(옛 이름 → 새 이름). 기동 시 <see cref="RenameLegacyAsync"/> 가 옮긴다.
    /// Weld.Dl.* — YOLO 검출 파라미터. 검출 방식 표기(YOLO)에 맞춰 Weld.Yolo.* 로 변경(Weld.Method 값 'Dl' 은 유지).
    /// </summary>
    public static readonly (string Old, string New)[] RenamedNames =
    [
        ("Weld.Dl.ModelPath", "Weld.Yolo.ModelPath"),
        ("Weld.Dl.Confidence", "Weld.Yolo.Confidence"),
        ("Weld.Dl.MaskThreshold", "Weld.Yolo.MaskThreshold"),
    ];

    /// <summary>이름으로 원시 문자열 값을 조회. 없으면 null.</summary>
    public async Task<string?> GetAsync(string name)
        => (await _db.Parameters.AsNoTracking().FirstOrDefaultAsync(p => p.Name == name))?.Value;

    public async Task<int?> GetIntAsync(string name)
        => int.TryParse(await GetAsync(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    public async Task<double?> GetDoubleAsync(string name)
        => double.TryParse(await GetAsync(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    public async Task<bool?> GetBoolAsync(string name)
        => bool.TryParse(await GetAsync(name), out var v) ? v : null;

    /// <summary>모든 파라미터(이름순). 범용 설정 화면 등에서 사용.</summary>
    public Task<List<Parameter>> GetAllAsync()
        => _db.Parameters.AsNoTracking().OrderBy(p => p.Name).ToListAsync();

    /// <summary>
    /// 이름 기준 upsert. <paramref name="description"/> 가 null 이면 기존 설명을 보존한다(신규면 null).
    /// </summary>
    public async Task SetAsync(string name, string value, string? description = null)
    {
        var existing = await _db.Parameters.FirstOrDefaultAsync(p => p.Name == name);
        if (existing is null)
        {
            _db.Parameters.Add(new Parameter
            {
                Name = name,
                Value = value,
                Description = description,
                UpdatedAt = DateTime.UtcNow,
            });
        }
        else
        {
            existing.Value = value;
            if (description is not null) existing.Description = description;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
        if (IsSafeJ6Key(name)) await ApplySafeJ6RangeAsync();
        if (name == Motion.Fr5Kinematics.LinksKey) await ApplyKinematicsLinksAsync();
        if (name == Motion.CobotEnvelope.BaseHeightKey) await ApplyCobotBaseHeightAsync();
        Changed?.Invoke(name);
    }

    /// <summary>코봇 BASE 높이 파라미터를 읽어 <see cref="Motion.CobotEnvelope.Default"/> 에 적용한다(기동·저장·삭제 때).</summary>
    public async Task<bool> ApplyCobotBaseHeightAsync()
        => Motion.CobotEnvelope.ApplyBaseHeight(await GetAsync(Motion.CobotEnvelope.BaseHeightKey));

    /// <summary>링크 치수 파라미터를 읽어 <see cref="Motion.Fr5Kinematics.Fr5Wml"/> 에 적용한다(기동·저장·삭제 때).
    /// 6개가 아니거나 공칭에서 5 mm 넘게 벗어나면 공칭을 쓰고 false.</summary>
    public async Task<bool> ApplyKinematicsLinksAsync()
        => Motion.Fr5Kinematics.ApplyLinks(await GetAsync(Motion.Fr5Kinematics.LinksKey));

    private static bool IsSafeJ6Key(string name)
        => name is Inspection.WObjAttitude.SafeJ6MinKey or Inspection.WObjAttitude.SafeJ6MaxKey
            or Inspection.WObjAttitude.SafeJ6StandbyKey;

    /// <summary>J6 안전 범위 파라미터를 읽어 <see cref="Inspection.WObjAttitude"/> 에 적용한다(기동·저장·삭제 때).
    /// 잘못된 값(하한 ≥ 상한, 대기 자세 J6(기본 −180°) 미포함, 폭 360° 이상)이면 기본값을 쓰고 false.</summary>
    public async Task<bool> ApplySafeJ6RangeAsync()
        => Inspection.WObjAttitude.ApplySafeJ6Range(
            await GetDoubleAsync(Inspection.WObjAttitude.SafeJ6MinKey),
            await GetDoubleAsync(Inspection.WObjAttitude.SafeJ6MaxKey),
            await GetDoubleAsync(Inspection.WObjAttitude.SafeJ6StandbyKey));

    public Task SetDoubleAsync(string name, double value, string? description = null)
        => SetAsync(name, value.ToString(CultureInfo.InvariantCulture), description);

    public Task SetBoolAsync(string name, bool value, string? description = null)
        => SetAsync(name, value ? "true" : "false", description);

    /// <summary>
    /// 기본 파라미터 시드 — 이름이 없는 행만 추가(현장 조정값 보존).
    /// 앱 시작 시 한 번 호출하여 UI(Parameter 페이지)에 키가 노출되도록 한다.
    /// </summary>
    public async Task SeedDefaultsAsync()
    {
        (string Name, string Value, string Desc)[] defaults =
        [
            ("arrival.deviationXy",    "0.1", "도착 판정 XY 허용 오차(m)"),
            ("arrival.deviationTheta", "0.1", "도착 판정 각도 허용 오차(rad)"),
            .. StationOffset.SeedDefaults(),
            ("inspection.useComputedNormalOrientation", "false",
                "'Cobot 검사위치 이동' 자세 = 계산된 면 법선(true) / 티칭 포즈(false). 현장 검증 후 켠다. 측위·장착보정·wall_code 가 없으면 자동으로 티칭 폴백."),
            (Sequence.Steps.CobotInspectionMoveStep.ApproachStandoffKey,
                Sequence.Steps.CobotInspectionMoveStep.DefaultApproachStandoffMm.ToString(CultureInfo.InvariantCulture),
                "② Cobot 검사위치 이동 — 용접선 시작점에서 벽면 법선 방향으로 TCP 를 물리는 거리(mm). 정차 이격·이동 경로 시험용. " +
                "모든 벽 동일 적용, 용접선 끝점 이동·ACS 시험 접근도 같은 값. 200~1000 밖이거나 비면 300."),
            (Sequence.Steps.CobotInspectionMoveStep.ChainStandoffKey,
                Sequence.Steps.CobotInspectionMoveStep.DefaultChainStandoffMm.ToString(CultureInfo.InvariantCulture),
                "같은 anchor 그룹 연속 LINE — 직전 검사 종료 자세에서 다음 시작점으로 옮길 때 면에서 TCP 를 띄우는 거리(mm). " +
                "첫 진입 이격(inspection.approachStandoffMm)까지 빠지지 않는다. 30~첫 진입 이격 밖이거나 비면 100."),
            (Sequence.Steps.CobotInspectionMoveStep.ChainContinueTolKey,
                Sequence.Steps.CobotInspectionMoveStep.DefaultChainContinueTolMm.ToString(CultureInfo.InvariantCulture),
                "이어지는 용접선 판정 거리(mm) — 직전 검사 종료 위치와 다음 시작점이 면을 따라 이 거리 안이면 면에서 띄우지 않고 " +
                "그 높이에서 바로 이어 간다. 0 = 끔(항상 inspection.chainStandoffMm 까지 띄움). 0~100 밖이면 50."),
            (SeamStartTagSettings.DictionaryKey, "DictAprilTag_36h11",
                "moveToSeamStart 도달 후 중심 편차 측정 태그 사전(DictAprilTag_36h11 등). ArUco 장착 보정 설정과 별개."),
            (SeamStartTagSettings.MarkerIdKey, "",
                "중심 편차 측정 태그 기대 ID. 비우면 화면에서 가장 큰 태그."),
            (SeamStartTagSettings.SizeMmKey, "0",
                "중심 편차 측정 태그 한 변(mm) — 검은 테두리 바깥 정사각형 실측(흰 여백 제외). 0 이면 측정 안 함."),
            .. FlatCandidateOptions.SeedDefaults(),
            .. LaserFootprintOptions.SeedDefaults(),
            (Motion.Fr5Kinematics.LinksKey,
                Motion.Fr5Kinematics.FormatLinks(Motion.Fr5Kinematics.WeldAmrLinksMm),
                "코봇 FR5-WML 링크 치수 d1,a2,a3,d4,d5,d6 [mm] — 오프라인 기구학(리치 맵·도달 사전 점검·자기 간섭)이 쓴다. " +
                "컨트롤러의 DH 보정값을 반영한 값이며 장비마다 다르다(기본값 = 2026-11-16 용접검사 AMR 실측, 공칭은 180,-970,-816,159,114,106). " +
                "6개가 아니거나 공칭에서 5 mm 넘게 벗어나면 공칭을 쓴다. ACS 의 Acs:Reach:LinksMm 와 같게 둘 것(다르면 프로필 전송 때 경고)."),
            (Motion.CobotEnvelope.BaseHeightKey,
                "",
                "AMR 차체 바닥(바퀴 닿는 면) → 코봇 BASE 장착면 높이 [mm] 덮어쓰기 — 자기 간섭(팔 ↔ 차체·기둥·신호탑) 점검과 도달 사전 점검이 쓴다. " +
                "비워 두면 appsettings \"CobotEnvelope\"(차체 BodyHeightMm + 기둥 ColumnHeightMm, 현 장비 767+160=927)를 쓴다. 저장하면 바로 적용. " +
                "차체 윗면 아래이거나 800~2500 밖이면 무시. 좌표 환산은 장착 보정(Calib.Mount.Pose)이 따로 하므로 둘 다 맞춰야 한다."),
            (Sequence.Steps.LaserWorkingDistanceStep.MaxReturnKey,
                Sequence.Steps.LaserWorkingDistanceStep.DefaultMaxReturnMm.ToString(CultureInfo.InvariantCulture),
                "④⁺ 레이저 WD 뒤 용접선 쪽 횡복귀 한계(mm, 툴 X·Y·Z 성분별) — 평탄면 센터링이 이보다 멀리 옮겨 갔으면 돌아가지 않고 실패한다 " +
                "(앵커·자세 이상 가드). 실행할 때마다 읽는다. 50~1000 밖이거나 비면 400."),
            (Sequence.Steps.LaserWorkingDistanceStep.Point2CrossCheckKey,
                Sequence.Steps.LaserWorkingDistanceStep.DefaultPoint2CrossCheckMm.ToString(CultureInfo.InvariantCulture),
                "⑧⁺ 묶음 점2 레이저·depth 교차 검증 허용차(mm) — 점1 WD 자리 대비 면 거리 변화를 레이저와 depth 카메라로 따로 재서 " +
                "이보다 다르면 레이저가 다른 높이(주름 꼭대기·경사·비드)를 잰 것으로 보고 묶음을 풀어 단독 교시한다. 0 = 끔, 0~200 밖이거나 비면 15. 실행할 때마다 읽는다."),
            (Sequence.Steps.BundleEndWdStep.EnabledKey, "",
                "⑰⁺ 묶음 마지막 용접선 끝 WD 확인 사용(on/off) — 켜면 마지막 구역 순회 전에 용접선 끝까지 갔다 와서 레이저 거리 차로 " +
                "좌표계 기울기를 다시 잡는다(구역마다 약 20초 추가). 끄면 리더가 잡은 좌표계를 그대로 연장해 끊김 없이 순회한다. " +
                "비우면 검사 센서 모드로 정한다 — 가스 검사기 끔, 비전 켬. 실행할 때마다 읽는다."),
            (Inspection.WObjAttitude.SafeJ6MinKey,
                Inspection.WObjAttitude.DefaultSafeJ6MinDeg.ToString(CultureInfo.InvariantCulture),
                "J6 안전 범위 하한(도, 실제 관절값 — 대기 자세 −180° 쪽 감김 기준). 이보다 더 감기는 J6 로는 움직이지 않는다 " +
                "(센서 헤드–링크암 간섭·케이블 감김 방지). 진입·용접선 가로 정렬·연속 이동·검사 순회 공통. 저장하면 바로 적용. " +
                "대기 자세 J6(inspection.safeJ6StandbyDeg, 기본 −180) 이하여야 하고 상한보다 작아야 한다 — 잘못된 값이면 기본값(−320~−140)을 쓴다. ACS 의 Acs:Reach:SafeJ6MinDeg 와 같게 둘 것."),
            (Inspection.WObjAttitude.SafeJ6MaxKey,
                Inspection.WObjAttitude.DefaultSafeJ6MaxDeg.ToString(CultureInfo.InvariantCulture),
                "J6 안전 범위 상한(도, 실제 관절값). 대기 자세 J6(inspection.safeJ6StandbyDeg, 기본 −180) 이상이어야 하고 하한보다 커야 한다. 저장하면 바로 적용. " +
                "ACS 의 Acs:Reach:SafeJ6MaxDeg 와 같게 둘 것."),
            (Inspection.WObjAttitude.SafeJ6StandbyKey,
                Inspection.WObjAttitude.DefaultSafeJ6StandbyDeg.ToString(CultureInfo.InvariantCulture),
                "대기(홈) 자세의 J6(도, 실제 관절값) — J6 안전 범위(하한~상한)가 반드시 포함해야 하는 각. 용접검사 AMR 은 −180. " +
                "툴을 플랜지에 180° 돌려 단 장비(가스검사 AMR, 홈 J6 0°)는 0 으로 두고 하한·상한도 같이 옮긴다(예: −140~40). 저장하면 바로 적용."),
            (Sequence.Steps.WeldSequenceSupport.ProgressAxisAlwaysHorizontalKey, "true",
                "Peak·Bead 찾기와 센터링의 진행축을 항상 영상 가로로 둔다(true). ② 가 툴을 돌려 용접선을 늘 영상 가로선으로 맞추기 때문이다. " +
                "false 면 종전 규칙(가로 정렬 표시·용접선 기록이 없으면 검사방향으로 결정 — 수평 용접선은 영상 세로)."),
            (Sequence.Steps.BeadCenteringStep.FailOnLargeResidualKey, "true",
                "⑦⁺/⑪⁺ 비드 센터링 — 1회 이동(±100mm) 뒤 남는 잔차가 점 보정 상한(±30mm)을 넘으면 그 용접선을 실패 처리(true). " +
                "넘는 잔차는 보정되지 않아 작업물 좌표계가 기울어진 채 순회한다. false 면 종전대로 경고만 남기고 진행."),
            (Sequence.Steps.WObjPointStep.ResidualCorrectionKey, "true",
                "⑦⁺/⑪⁺ 비드 센터링 후 남은 cross 잔차를 작업물 좌표계 점1/점2 기록에 보정(true). 잔차가 X축을 돌려 연속 LINE 이 " +
                "V01 에서 멀수록 옆으로 벗어나는 것을 줄인다. 점2 는 각도(⑫)를 건드리지 않는 별도 재측정으로 잔차를 잰다."),
            (Sequence.Steps.WObjRegisterStep.Point1HeightKey,
                Inspection.InternalWorkpieceFrame.DefaultCorrugationTopHeightMm.ToString(CultureInfo.InvariantCulture),
                "작업물 좌표계 — 점1 을 기록한 TCP 높이가 티칭 프로필 Z=0(평탄부 초점 높이)에서 떨어진 거리(mm). " +
                "33.8 = 점1 을 주름 꼭대기 초점 높이에서 기록(종전). 0 = 점1 을 평탄부 초점 높이(레이저 WD 로 맞춘 높이)에서 기록 — " +
                "33.8 인 채로 WD 높이에서 점1 을 기록하면 순회 전체가 면 쪽으로 33.8mm 들어간다. 뒤에 .T/.PU 등 면 코드를 붙인 키로 면별 지정 가능. 0~100."),
            (Inspection.ProfileMirror.EnabledKey, "true",
                "검사 수행 — 우현 면(SM·SL·SU)에서 티칭 프로필 경유점을 거울 반전(Y·Rz·Rx 부호)해 실행(true). 프로필은 좌현에서 " +
                "선미→선수로 교시하고 우현은 그 거울상이라, 반전하지 않으면 CROSS 세로 팔이 위아래로 어긋난다. false = 교시값 그대로."),
            .. Sequence.BeadManualSettings.SeedDefaults(),
            .. Motion.CobotObstacleZone.SeedDefaults(),
            (FlatReachOptions.TiltMarginKey,
                FlatReachOptions.DefaultTiltMarginDeg.ToString(CultureInfo.InvariantCulture),
                "④ 평탄 후보 도달성 판정의 틸트 보정 여유(°) — 레이저 위치에서 툴 Rx/Ry 를 ± 이만큼 돌려도 자세 한계 안이어야 후보로 인정. 0 = 생략."),
            (Sequence.Steps.InspectionRunStep.DefaultInspectVelKey,
                Sequence.Steps.InspectionRunStep.DefaultInspectVelPercent.ToString(CultureInfo.InvariantCulture),
                "⑱ 검사 수행 기본 검사 속도(%) — 경유점 순회(검사 스캔) 속도. 레시피 검사 속도(또는 시퀀스 화면 검사 속도)가 비어 있고 " +
                "검사 프로파일에도 속도가 없을 때 쓴다. 기본 2, 1~100 (밖이면 2). 다음 검사부터 적용."),
            (Sequence.Steps.BundleTeach.SoloPoint2FlatWdKey, "true",
                "⑧⁺ 단독 교시에서도 점2 에서 평탄면 탐색·레이저 WD 조정을 한 뒤 점2 자리로 돌아온다(true). false = 묶음 점2 에서만. " +
                "피치 이동 뒤 점2 의 면 거리·틸트를 점1 과 같은 기준으로 다시 맞춘다."),
            (Sequence.Steps.BundleTeach.PitchLiftKey,
                Sequence.Steps.BundleTeach.DefaultPitchLiftMm.ToString(CultureInfo.InvariantCulture),
                "⑧ Peak2 피치 이동 들어올림(mm) — 점1 에서 면 바깥으로 이만큼 띄운 채 피치를 이동하고, 도착 뒤 레이저로 점1 과 같은 " +
                "면 거리까지 내린다(모델 면과 실제 면 각도 차로 이동 중 팁이 코로게이션에 닿는 것 방지). 0 = 종전 면 평행 직선 이동. 기본 30, 0~100."),
            (Sequence.Steps.InspectionRunStep.LinearSpeedAt100PctKey,
                Sequence.Steps.InspectionRunStep.DefaultLinearSpeedAt100Pct.ToString(CultureInfo.InvariantCulture),
                "⑱ 검사 수행 — 검사 속도를 mm/s 로 지정했을 때(레시피·시퀀스 화면 '검사 속도(mm/s)') % 로 환산하는 기준: 검사 속도 100% 일 때의 " +
                "TCP 직선 속도(mm/s). 지령 % = mm/s ÷ 이 값 × 100. 기본 120(M2 실측 ≈1.2mm/s per %). 실측 평균 속도는 무정지 측정 로그에 나온다. 다음 검사부터 적용."),
            (Sequence.Steps.FlatSurfaceAlignStep.TiltThresholdKey,
                Sequence.Steps.FlatSurfaceAlignStep.DefaultTiltThresholdDeg.ToString(CultureInfo.InvariantCulture),
                "④ 평탄면 레이저 정렬 완료 기준(°) — 레이저 3점 틸트 |rx|, |ry| 가 모두 이 값 미만이면 정렬 완료. 기본 1, 0 초과~5 (밖이면 1). 다음 정렬부터 적용."),
            (FlatMoveSettings.JointVelocityScaleKey,
                FlatMoveSettings.DefaultJointVelocityScale.ToString(CultureInfo.InvariantCulture),
                "④ 평탄면 센터링 관절 이동(MoveJ) 속도 비율 — 시퀀스/카메라 페이지 속도에 곱한다(0~1). MoveJ 는 같은 % 에서 MoveL 보다 약 2배 빨라 0.45 로 종전 체감 속도에 맞춤."),
            (TeachingMove.JointVelocityScaleKey,
                TeachingMove.DefaultJointVelocityScale.ToString(CultureInfo.InvariantCulture),
                "Teaching '이동'(저장 관절값 MoveJ) 속도 비율 — 화면 속도에 곱한다(0~1). MoveJ 는 같은 % 에서 MoveL 보다 약 2배 빨라 0.45 로 종전 체감 속도에 맞춤."),
            (Models.TeachingLiftMode.ActiveKey, Models.TeachingLiftMode.Short,
                "운영에 쓰는 티칭 세트(리프트 모드) — SHORT / LONG. Teaching 페이지 탭으로 전환. 알 수 없는 값은 SHORT."),
        ];

        var existing = await _db.Parameters.Select(p => p.Name).ToListAsync();
        foreach (var (name, value, desc) in defaults)
        {
            if (existing.Contains(name)) continue;
            _db.Parameters.Add(new Parameter
            {
                Name = name,
                Value = value,
                Description = desc,
                UpdatedAt = DateTime.UtcNow,
            });
        }
        await _db.SaveChangesAsync();
        await ApplySafeJ6RangeAsync();   // 기동 시 저장된 J6 안전 범위 적용
        await ApplyKinematicsLinksAsync();  // 기동 시 저장된 링크 치수(DH 보정) 적용
        await ApplyCobotBaseHeightAsync();  // 기동 시 저장된 코봇 BASE 높이 적용
    }

    /// <summary>id 로 파라미터 1건 삭제. 없으면 무시.</summary>
    public async Task DeleteAsync(int id)
    {
        var p = await _db.Parameters.FindAsync(id);
        if (p is not null)
        {
            _db.Parameters.Remove(p);
            await _db.SaveChangesAsync();
            if (IsSafeJ6Key(p.Name)) await ApplySafeJ6RangeAsync();
            if (p.Name == Motion.Fr5Kinematics.LinksKey) await ApplyKinematicsLinksAsync();
            if (p.Name == Motion.CobotEnvelope.BaseHeightKey) await ApplyCobotBaseHeightAsync();
            Changed?.Invoke(p.Name);
        }
    }

    /// <summary>
    /// 이름이 바뀐 키(<see cref="RenamedNames"/>) 이전 — 앱 시작 시 <see cref="SeedDefaultsAsync"/> 전에 한 번 호출(멱등).
    /// 새 이름이 없으면 옛 행의 이름만 바꿔 값·설명을 그대로 옮기고, 새 이름이 이미 있으면(새 값 우선) 옛 행을 지운다.
    /// 처리한 "옛→새" 목록 반환.
    /// </summary>
    public async Task<List<string>> RenameLegacyAsync()
    {
        var olds = RenamedNames.Select(r => r.Old).ToArray();
        var news = RenamedNames.Select(r => r.New).ToArray();
        var rows = await _db.Parameters.Where(p => olds.Contains(p.Name) || news.Contains(p.Name)).ToListAsync();
        var done = new List<string>();
        foreach (var (oldName, newName) in RenamedNames)
        {
            var oldRow = rows.FirstOrDefault(p => p.Name == oldName);
            if (oldRow is null) continue;
            if (rows.Any(p => p.Name == newName)) _db.Parameters.Remove(oldRow);
            else { oldRow.Name = newName; oldRow.UpdatedAt = DateTime.UtcNow; }
            done.Add($"{oldName}→{newName}");
        }
        if (done.Count > 0) await _db.SaveChangesAsync();
        return done;
    }

    /// <summary>폐기된 키(<see cref="ObsoleteNames"/>) 삭제 — 앱 시작 시 한 번 호출. 삭제한 이름 목록 반환.</summary>
    public async Task<List<string>> RemoveObsoleteAsync()
    {
        var rows = await _db.Parameters.Where(p => ObsoleteNames.Contains(p.Name)).ToListAsync();
        if (rows.Count == 0) return [];
        _db.Parameters.RemoveRange(rows);
        await _db.SaveChangesAsync();
        return rows.Select(p => p.Name).ToList();
    }
}
