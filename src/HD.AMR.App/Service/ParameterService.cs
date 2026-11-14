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
        Changed?.Invoke(name);
    }

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
            ("inspection.useComputedNormalOrientation", "false",
                "'Cobot 검사위치 이동' 자세 = 계산된 면 법선(true) / 티칭 포즈(false). 현장 검증 후 켠다. 측위·장착보정·wall_code 가 없으면 자동으로 티칭 폴백."),
            (Sequence.Steps.CobotInspectionMoveStep.ApproachStandoffKey,
                Sequence.Steps.CobotInspectionMoveStep.DefaultApproachStandoffMm.ToString(CultureInfo.InvariantCulture),
                "② Cobot 검사위치 이동 — 용접선 시작점에서 벽면 법선 방향으로 TCP 를 물리는 거리(mm). 정차 이격·이동 경로 시험용. " +
                "모든 벽 동일 적용, 용접선 끝점 이동·ACS 시험 접근도 같은 값. 200~1000 밖이거나 비면 400."),
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
            .. Sequence.BeadManualSettings.SeedDefaults(),
            .. Motion.CobotObstacleZone.SeedDefaults(),
            (FlatReachOptions.TiltMarginKey,
                FlatReachOptions.DefaultTiltMarginDeg.ToString(CultureInfo.InvariantCulture),
                "④ 평탄 후보 도달성 판정의 틸트 보정 여유(°) — 레이저 위치에서 툴 Rx/Ry 를 ± 이만큼 돌려도 자세 한계 안이어야 후보로 인정. 0 = 생략."),
            (FlatMoveSettings.JointVelocityScaleKey,
                FlatMoveSettings.DefaultJointVelocityScale.ToString(CultureInfo.InvariantCulture),
                "④ 평탄면 센터링 관절 이동(MoveJ) 속도 비율 — 시퀀스/카메라 페이지 속도에 곱한다(0~1). MoveJ 는 같은 % 에서 MoveL 보다 약 2배 빨라 0.45 로 종전 체감 속도에 맞춤."),
            (TeachingMove.JointVelocityScaleKey,
                TeachingMove.DefaultJointVelocityScale.ToString(CultureInfo.InvariantCulture),
                "Teaching '이동'(저장 관절값 MoveJ) 속도 비율 — 화면 속도에 곱한다(0~1). MoveJ 는 같은 % 에서 MoveL 보다 약 2배 빨라 0.45 로 종전 체감 속도에 맞춤."),
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
    }

    /// <summary>id 로 파라미터 1건 삭제. 없으면 무시.</summary>
    public async Task DeleteAsync(int id)
    {
        var p = await _db.Parameters.FindAsync(id);
        if (p is not null)
        {
            _db.Parameters.Remove(p);
            await _db.SaveChangesAsync();
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
