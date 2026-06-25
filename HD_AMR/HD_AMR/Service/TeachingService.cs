using HD_AMR.Data;
using HD_AMR.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HD_AMR.Service;

/// <summary>
/// Teaching 위치(고정 슬롯) 저장/조회 서비스. DB(SQLite)에 BASE 자세 + 관절각을 보관한다.
/// 슬롯은 <see cref="Slots"/> 정의에 따라 자동 시드되며, 좌표는 "현재 위치 저장"으로 채워진다.
/// </summary>
public class TeachingService
{
    private readonly HdAmrDbContext _db;

    public TeachingService(HdAmrDbContext db)
    {
        _db = db;
    }

    /// <summary>고정 슬롯 정의(키, 표시 이름). 슬롯을 추가하려면 이 목록에 항목을 더하면 된다.</summary>
    public static readonly (string Key, string Name)[] Slots =
    {
        ("home", "홈 위치"),
        ("inspectionReady", "검사 준비 위치"),
    };

    /// <summary><see cref="Slots"/> 중 DB에 없는 슬롯을 생성한다(좌표는 null). 멱등.</summary>
    public async Task EnsureSeededAsync(CancellationToken ct = default)
    {
        var existingKeys = await _db.TeachingPositions
            .Select(p => p.Key)
            .ToListAsync(ct);
        var existing = new HashSet<string>(existingKeys);

        var now = DateTime.UtcNow;
        var added = false;
        for (var i = 0; i < Slots.Length; i++)
        {
            var (key, name) = Slots[i];
            if (existing.Contains(key)) continue;
            _db.TeachingPositions.Add(new TeachingPosition
            {
                Key = key,
                Name = name,
                SortOrder = i,
                Tool = 1,
                CreatedAt = now,
                UpdatedAt = now,
            });
            added = true;
        }
        if (added) await _db.SaveChangesAsync(ct);
    }

    /// <summary>시드 보장 후 전체 슬롯을 표시 순서대로 반환.</summary>
    public async Task<List<TeachingPosition>> ListAsync(CancellationToken ct = default)
    {
        await EnsureSeededAsync(ct);
        return await _db.TeachingPositions.AsNoTracking()
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.Id)
            .ToListAsync(ct);
    }

    public Task<TeachingPosition?> GetAsync(int id, CancellationToken ct = default) =>
        _db.TeachingPositions.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);

    /// <summary>해당 슬롯에 현재 BASE 자세(pose[6]: x,y,z,rx,ry,rz)와 관절각(joints[6])을 저장한다.</summary>
    public async Task SaveCaptureAsync(int id, double[] pose, double[] joints, int tool, CancellationToken ct = default)
    {
        var p = await _db.TeachingPositions.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p == null) throw new InvalidOperationException($"Teaching slot {id} not found.");

        p.X = pose[0]; p.Y = pose[1]; p.Z = pose[2];
        p.Rx = pose[3]; p.Ry = pose[4]; p.Rz = pose[5];
        p.J1 = joints[0]; p.J2 = joints[1]; p.J3 = joints[2];
        p.J4 = joints[3]; p.J5 = joints[4]; p.J6 = joints[5];
        p.Tool = tool;
        var now = DateTime.UtcNow;
        p.CapturedAt = now;
        p.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>슬롯의 좌표/관절각을 비운다(슬롯 자체는 삭제하지 않음).</summary>
    public async Task ClearAsync(int id, CancellationToken ct = default)
    {
        var p = await _db.TeachingPositions.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p == null) return;

        p.X = p.Y = p.Z = p.Rx = p.Ry = p.Rz = null;
        p.J1 = p.J2 = p.J3 = p.J4 = p.J5 = p.J6 = null;
        p.CapturedAt = null;
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }
}
