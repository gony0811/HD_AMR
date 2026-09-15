using HD.AMR.App.Data;
using HD.AMR.App.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HD.AMR.App.Service;

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

    /// <summary>고정 슬롯 정의(키, 표시 이름) — 삭제 불가·코드가 키로 직접 참조하는 위치만.
    /// 검사 위치는 고정 슬롯이 아니라 사용자가 행을 추가하고 SurfaceId(0x01~0xFF)를 부여해 만든다
    /// (시퀀스 ②가 SurfaceId 로 목표를 결정). 과거 시드였던 inspectionReady 등 기존 행은 DB에
    /// 남아 사용자 행처럼 편집/삭제할 수 있다.</summary>
    public static readonly (string Key, string Name)[] Slots =
    {
        ("home", "홈 위치"),
        // CORNER3 삼면 코너 검사(cornerInspectionRun 스텝)가 키로 직접 순회하는 고정 슬롯 —
        // 좌(L)/우(R) 거울 각 5점: 접근(via, 촬영 없음) → 3면 촬영 → 복귀(via). 좌표는 현장 티칭.
        ("corner3.L.approach", "코너3 L — 접근"),
        ("corner3.L.face1", "코너3 L — 면1 (135°)"),
        ("corner3.L.face2", "코너3 L — 면2 (90°)"),
        ("corner3.L.face3", "코너3 L — 면3 (90°)"),
        ("corner3.L.retreat", "코너3 L — 복귀"),
        ("corner3.R.approach", "코너3 R — 접근"),
        ("corner3.R.face1", "코너3 R — 면1 (135°)"),
        ("corner3.R.face2", "코너3 R — 면2 (90°)"),
        ("corner3.R.face3", "코너3 R — 면3 (90°)"),
        ("corner3.R.retreat", "코너3 R — 복귀"),
    };

    /// <summary><see cref="Slots"/> 중 DB에 없는 슬롯을 생성하고(좌표는 null), 표시 이름이
    /// 시드 정의와 다른 기존 슬롯은 이름만 갱신한다. 멱등.</summary>
    public async Task EnsureSeededAsync(CancellationToken ct = default)
    {
        var rows = await _db.TeachingPositions.ToListAsync(ct);
        var byKey = rows.ToDictionary(p => p.Key, p => p);

        var now = DateTime.UtcNow;
        var changed = false;
        for (var i = 0; i < Slots.Length; i++)
        {
            var (key, name) = Slots[i];
            if (byKey.TryGetValue(key, out var row))
            {
                if (row.Name != name)
                {
                    row.Name = name;
                    row.UpdatedAt = now;
                    changed = true;
                }
                continue;
            }
            _db.TeachingPositions.Add(new TeachingPosition
            {
                Key = key,
                Name = name,
                SortOrder = i,
                Tool = 1,
                CreatedAt = now,
                UpdatedAt = now,
            });
            changed = true;
        }
        if (changed) await _db.SaveChangesAsync(ct);
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

    /// <summary>시드 슬롯(삭제/이름·Surface 편집 불가) 여부.</summary>
    public static bool IsSeedSlot(string key) => Slots.Any(s => s.Key == key);

    /// <summary>사용자 정의 위치 행 추가. Key 는 자동 생성, 좌표는 비운 채(미티칭) 생성된다.</summary>
    public async Task<TeachingPosition> AddAsync(string name, int surfaceId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var maxOrder = await _db.TeachingPositions.MaxAsync(p => (int?)p.SortOrder, ct) ?? -1;
        var row = new TeachingPosition
        {
            Key = $"user-{Guid.NewGuid():N}",
            Name = string.IsNullOrWhiteSpace(name) ? "새 위치" : name.Trim(),
            SurfaceId = Math.Clamp(surfaceId, 0x00, 0xFF),
            SortOrder = maxOrder + 1,
            Tool = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.TeachingPositions.Add(row);
        await _db.SaveChangesAsync(ct);
        return row;
    }

    /// <summary>사용자 행 삭제. 시드 슬롯(home 등)은 무시하고 false 반환.</summary>
    public async Task<bool> DeleteAsync(int id, CancellationToken ct = default)
    {
        var row = await _db.TeachingPositions.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (row is null || IsSeedSlot(row.Key)) return false;
        _db.TeachingPositions.Remove(row);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>이름/Surface ID(0x00~0xFF) 갱신. 시드 슬롯은 무시(이름·Surface 고정).</summary>
    public async Task UpdateInfoAsync(int id, string name, int surfaceId, CancellationToken ct = default)
    {
        var row = await _db.TeachingPositions.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (row is null || IsSeedSlot(row.Key)) return;
        row.Name = string.IsNullOrWhiteSpace(name) ? row.Name : name.Trim();
        row.SurfaceId = Math.Clamp(surfaceId, 0x00, 0xFF);
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>해당 슬롯에 현재 BASE 자세(pose[6]: x,y,z,rx,ry,rz)와 관절각(joints[6])을 저장한다.
    /// <paramref name="userFrame"/>&gt;0 이면 작업물 좌표계 N 기준으로 고정하고, 그 프레임 기준 상대
    /// pose(<paramref name="relPose"/>[6])를 함께 저장한다(프레임 N 재등록 시 목표가 따라감).
    /// 0/null 이면 베이스 기준(기존 동작).</summary>
    public async Task SaveCaptureAsync(int id, double[] pose, double[] joints, int tool,
        int? userFrame = null, double[]? relPose = null, CancellationToken ct = default)
    {
        var p = await _db.TeachingPositions.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p == null) throw new InvalidOperationException($"Teaching slot {id} not found.");

        p.X = pose[0]; p.Y = pose[1]; p.Z = pose[2];
        p.Rx = pose[3]; p.Ry = pose[4]; p.Rz = pose[5];
        p.J1 = joints[0]; p.J2 = joints[1]; p.J3 = joints[2];
        p.J4 = joints[3]; p.J5 = joints[4]; p.J6 = joints[5];
        p.Tool = tool;

        if (userFrame is int n && n > 0 && relPose is { Length: >= 6 })
        {
            p.UserFrame = n;
            p.RelX = relPose[0]; p.RelY = relPose[1]; p.RelZ = relPose[2];
            p.RelRx = relPose[3]; p.RelRy = relPose[4]; p.RelRz = relPose[5];
        }
        else
        {
            p.UserFrame = null;
            p.RelX = p.RelY = p.RelZ = p.RelRx = p.RelRy = p.RelRz = null;
        }

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
        p.UserFrame = null;
        p.RelX = p.RelY = p.RelZ = p.RelRx = p.RelRy = p.RelRz = null;
        p.CapturedAt = null;
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }
}
