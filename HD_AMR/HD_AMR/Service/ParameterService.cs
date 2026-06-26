using System.Globalization;
using HD_AMR.Data;
using HD_AMR.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HD_AMR.Service;

/// <summary>
/// 범용 key/value 파라미터(<see cref="Parameter"/>) 저장소에 대한 타입 헬퍼.
/// <see cref="Parameter.Name"/> 유일 키로 upsert 하며, 숫자/불리언은 InvariantCulture 문자열로 보관한다.
/// 깊이 ROI 등 화면 설정을 DB에 영속화해 재시작 후에도 항상 복원하는 데 사용한다.
/// </summary>
public class ParameterService
{
    private readonly HdAmrDbContext _db;

    public ParameterService(HdAmrDbContext db) => _db = db;

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
    }

    public Task SetDoubleAsync(string name, double value, string? description = null)
        => SetAsync(name, value.ToString(CultureInfo.InvariantCulture), description);

    public Task SetBoolAsync(string name, bool value, string? description = null)
        => SetAsync(name, value ? "true" : "false", description);

    /// <summary>id 로 파라미터 1건 삭제. 없으면 무시.</summary>
    public async Task DeleteAsync(int id)
    {
        var p = await _db.Parameters.FindAsync(id);
        if (p is not null)
        {
            _db.Parameters.Remove(p);
            await _db.SaveChangesAsync();
        }
    }
}
