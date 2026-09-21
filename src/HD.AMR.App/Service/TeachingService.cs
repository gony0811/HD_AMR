using HD.AMR.App.Data;
using HD.AMR.App.Data.Entities;
using Microsoft.EntityFrameworkCore;
using HD.AMR.App.Models;

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

    /// <summary>Wall Code 고정 슬롯 키.</summary>
    public static string WallSlotKey(string code) => $"wall.{code}";

    /// <summary>사용자 항목이 쓸 수 없는 Wall ID 범위(Wall Code 고정 슬롯 전용).</summary>
    public static bool IsReservedSurfaceId(int surfaceId) => WallCodes.FindBySurfaceId(surfaceId) is not null;

    /// <summary>Wall ID → "0x01 · B 바닥" 표시 문자열. 고정 범위 밖이면 "0x0B" 처럼 hex 만.</summary>
    public static string SurfaceLabel(int surfaceId) =>
        WallCodes.FindBySurfaceId(surfaceId) is { } w
            ? $"0x{surfaceId:X2} · {w.Code} {w.Label}"
            : $"0x{surfaceId:X2}";

    /// <summary>고정 슬롯 정의(키, 표시 이름, Wall ID) — 삭제·이름/Wall ID 편집 불가, 좌표만 티칭.
    /// 순서 = 화면 표시 순서: 홈 → Wall Code 10면 검사 준비 위치 → CORNER3 슬롯. 사용자 항목은 그 뒤.</summary>
    public static readonly (string Key, string Name, int SurfaceId)[] Slots = BuildSlots();

    private static (string Key, string Name, int SurfaceId)[] BuildSlots()
    {
        var list = new List<(string, string, int)> { ("home", "홈 위치", 0x00) };
        // Wall Code 10면 검사 준비 위치 — 코드·Wall ID·이름은 정본 표(WallCodes)에서만 가져온다.
        foreach (var w in WallCodes.All)
            list.Add((WallSlotKey(w.Code), $"검사 준비 — {w.Label}", w.SurfaceId));
        // CORNER3 삼면 코너 검사(cornerInspectionRun 스텝)가 키로 직접 순회하는 고정 슬롯 —
        // 좌(L)/우(R) 거울 각 5점: 접근(via, 촬영 없음) → 3면 촬영 → 복귀(via). 좌표는 현장 티칭.
        foreach (var side in new[] { "L", "R" })
        {
            list.Add(($"corner3.{side}.approach", $"코너3 {side} — 접근", 0x00));
            list.Add(($"corner3.{side}.face1", $"코너3 {side} — 면1 (135°)", 0x00));
            list.Add(($"corner3.{side}.face2", $"코너3 {side} — 면2 (90°)", 0x00));
            list.Add(($"corner3.{side}.face3", $"코너3 {side} — 면3 (90°)", 0x00));
            list.Add(($"corner3.{side}.retreat", $"코너3 {side} — 복귀", 0x00));
        }
        return list.ToArray();
    }

    /// <summary>과거 사용자형 검사 위치 → Wall Code 슬롯 이관표(2026-09-18, 이름 기준 대응).
    /// 대상 슬롯이 미티칭일 때만 좌표를 옮기고 구 행은 삭제한다.</summary>
    private static readonly (string LegacyKey, string WallCode)[] LegacyWallMigration =
    {
        ("inspectionReady", "F"),    // 검사 준비 위치 (전면)
        ("inspectionGround", "B"),   // 검사 준비 위치 (바닥)
        ("inspectionCeiling", "T"),  // 검사 준비 위치 (천정)
    };

    /// <summary>시드 보장(멱등):
    /// ① <see cref="Slots"/> 중 없는 슬롯 생성(좌표 null), 기존 슬롯의 이름·Wall ID·표시 순서를 정의대로 교정
    /// ② 구 검사 위치 행(<see cref="LegacyWallMigration"/>)의 좌표를 Wall Code 슬롯으로 이관 후 삭제
    /// ③ 사용자 항목 표시 순서를 고정 슬롯 뒤로 정렬.</summary>
    public async Task EnsureSeededAsync(CancellationToken ct = default)
    {
        var rows = await _db.TeachingPositions.ToListAsync(ct);
        var byKey = rows.ToDictionary(p => p.Key, p => p);

        var now = DateTime.UtcNow;
        var changed = false;
        for (var i = 0; i < Slots.Length; i++)
        {
            var (key, name, surfaceId) = Slots[i];
            if (byKey.TryGetValue(key, out var row))
            {
                if (row.Name != name || row.SurfaceId != surfaceId || row.SortOrder != i)
                {
                    row.Name = name;
                    row.SurfaceId = surfaceId;
                    row.SortOrder = i;
                    row.UpdatedAt = now;
                    changed = true;
                }
                continue;
            }
            row = new TeachingPosition
            {
                Key = key,
                Name = name,
                SurfaceId = surfaceId,
                SortOrder = i,
                Tool = 1,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.TeachingPositions.Add(row);
            byKey[key] = row;
            changed = true;
        }

        foreach (var (legacyKey, code) in LegacyWallMigration)
        {
            if (!byKey.TryGetValue(legacyKey, out var legacy)) continue;
            var target = byKey[WallSlotKey(code)];
            if (legacy.IsTaught && !target.IsTaught)
                CopyCoordinates(legacy, target, now);
            _db.TeachingPositions.Remove(legacy);
            byKey.Remove(legacyKey);
            changed = true;
        }

        var order = Slots.Length;
        foreach (var user in byKey.Values.Where(p => !IsSeedSlot(p.Key)).OrderBy(p => p.SortOrder).ThenBy(p => p.Id))
        {
            if (user.SortOrder != order)
            {
                user.SortOrder = order;
                user.UpdatedAt = now;
                changed = true;
            }
            order++;
        }

        if (changed) await _db.SaveChangesAsync(ct);
    }

    private static void CopyCoordinates(TeachingPosition from, TeachingPosition to, DateTime now)
    {
        to.X = from.X; to.Y = from.Y; to.Z = from.Z;
        to.Rx = from.Rx; to.Ry = from.Ry; to.Rz = from.Rz;
        to.J1 = from.J1; to.J2 = from.J2; to.J3 = from.J3;
        to.J4 = from.J4; to.J5 = from.J5; to.J6 = from.J6;
        to.Tool = from.Tool;
        to.UserFrame = from.UserFrame;
        to.RelX = from.RelX; to.RelY = from.RelY; to.RelZ = from.RelZ;
        to.RelRx = from.RelRx; to.RelRy = from.RelRy; to.RelRz = from.RelRz;
        to.CapturedAt = from.CapturedAt;
        to.UpdatedAt = now;
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

    private static void EnsureUserSurfaceId(int surfaceId)
    {
        if (IsReservedSurfaceId(surfaceId))
            throw new InvalidOperationException(
                $"Wall ID 0x{surfaceId:X2} 는 {SurfaceLabel(surfaceId)} 고정 슬롯 전용입니다 — 사용자 항목은 0x00 또는 0x0B~0xFF 를 쓰세요.");
    }

    /// <summary>시드 슬롯(삭제/이름·Surface 편집 불가) 여부.</summary>
    public static bool IsSeedSlot(string key) => Slots.Any(s => s.Key == key);

    /// <summary>사용자 정의 위치 행 추가. Key 는 자동 생성, 좌표는 비운 채(미티칭) 생성된다.
    /// Wall ID 0x01~0x0A 는 Wall Code 고정 슬롯 전용이라 거부한다.</summary>
    public async Task<TeachingPosition> AddAsync(string name, int surfaceId, CancellationToken ct = default)
    {
        EnsureUserSurfaceId(surfaceId);
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

    /// <summary>이름/Surface ID(0x00, 0x0B~0xFF) 갱신. 시드 슬롯은 무시(이름·Surface 고정).</summary>
    public async Task UpdateInfoAsync(int id, string name, int surfaceId, CancellationToken ct = default)
    {
        EnsureUserSurfaceId(surfaceId);
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
