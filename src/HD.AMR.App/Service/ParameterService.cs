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
            (SeamStartTagSettings.DictionaryKey, "DictAprilTag_36h11",
                "moveToSeamStart 도달 후 중심 편차 측정 태그 사전(DictAprilTag_36h11 등). ArUco 장착 보정 설정과 별개."),
            (SeamStartTagSettings.MarkerIdKey, "",
                "중심 편차 측정 태그 기대 ID. 비우면 화면에서 가장 큰 태그."),
            (SeamStartTagSettings.SizeMmKey, "0",
                "중심 편차 측정 태그 한 변(mm) — 검은 테두리 바깥 정사각형 실측(흰 여백 제외). 0 이면 측정 안 함."),
            .. FlatCandidateOptions.SeedDefaults(),
            (FlatReachOptions.TiltMarginKey,
                FlatReachOptions.DefaultTiltMarginDeg.ToString(CultureInfo.InvariantCulture),
                "④ 평탄 후보 도달성 판정의 틸트 보정 여유(°) — 레이저 위치에서 툴 Rx/Ry 를 ± 이만큼 돌려도 자세 한계 안이어야 후보로 인정. 0 = 생략."),
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
