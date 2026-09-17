using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Data;

/// <summary>
/// 과거 상대경로 시절(웹: 프로젝트 폴더, 데스크톱: 실행 폴더)에 만들어진 레거시 SQLite 파일을
/// 정본 경로(<see cref="SqliteConnectionResolver"/> 가 해석한 %LocalAppData%/HD.AMR/hd_amr.db)로
/// 1회 가져온다. 완료 마커 파일이 있으면 이후 기동에서는 아무것도 하지 않는다.
/// 웹·데스크톱 모두 첫 DB 접근 전에 호출한다 — 어느 앱이 먼저 뜨든 한 번만 실행된다.
/// </summary>
public static class LegacyDatabaseImporter
{
    private const string MarkerFileName = "legacy-import-done.marker";
    private const string MutexName = @"Global\HD.AMR.LegacyDbImport";

    public static void ImportIfNeeded(IConfiguration config, string contentRootPath, ILogger? logger = null)
    {
        var canonical = SqliteConnectionResolver.ResolveDbPath(config);
        var canonicalDir = Path.GetDirectoryName(canonical)!;
        var markerPath = Path.Combine(canonicalDir, MarkerFileName);

        // 두 앱이 동시에 기동해도 가져오기는 한 프로세스만 수행하도록 전역 뮤텍스로 직렬화.
        using var mutex = new Mutex(initiallyOwned: false, MutexName);
        try
        {
            try { mutex.WaitOne(TimeSpan.FromSeconds(30)); }
            catch (AbandonedMutexException) { /* 이전 소유자 비정상 종료 — 락은 획득됨 */ }

            if (File.Exists(markerPath))
            {
                logger?.LogDebug("레거시 DB 가져오기 스킵 — 마커 존재: {Marker}", markerPath);
                return;
            }

            var candidates = CollectCandidates(config, contentRootPath, canonical);
            if (candidates.Count == 0)
            {
                // 가져올 것이 없으면 마커를 남겨, 수개월 뒤 낡은 파일이 갑자기 덮어쓰는 일을 막는다.
                File.WriteAllText(markerPath, $"{DateTime.Now:O} — 레거시 DB 없음, 가져오기 생략");
                logger?.LogInformation("레거시 DB 후보 없음 — 정본 {Canonical} 을 그대로 사용", canonical);
                return;
            }

            // 정본 백업(리네임). -wal/-shm 도 함께 치운다 — 가져온 파일 옆에 낡은 WAL 이 남으면 안 된다.
            string? backupPath = null;
            if (File.Exists(canonical))
            {
                var stem = Path.GetFileNameWithoutExtension(canonical);
                backupPath = Path.Combine(canonicalDir, $"{stem}.backup-{DateTime.Now:yyyyMMdd-HHmmss}.db");
                File.Move(canonical, backupPath);
                MoveIfExists(canonical + "-wal", backupPath + "-wal");
                MoveIfExists(canonical + "-shm", backupPath + "-shm");
            }

            foreach (var candidate in candidates)
            {
                if (TryImport(candidate, canonical, logger))
                {
                    File.WriteAllText(markerPath, $"{DateTime.Now:O} — imported from {candidate}");
                    logger?.LogInformation(
                        "레거시 DB 가져오기 완료: {Source} → {Canonical} (기존 정본 백업: {Backup})",
                        candidate, canonical, backupPath ?? "(없음)");
                    return;
                }
            }

            // 전부 실패 — 정본 원복, 마커 미기록(다음 기동에서 재시도).
            logger?.LogError("레거시 DB 가져오기 실패 — 후보 {Count}개 모두 실패, 기존 정본으로 계속 진행", candidates.Count);
            if (backupPath is not null && !File.Exists(canonical))
            {
                File.Move(backupPath, canonical);
                MoveIfExists(backupPath + "-wal", canonical + "-wal");
                MoveIfExists(backupPath + "-shm", canonical + "-shm");
            }
        }
        finally
        {
            try { mutex.ReleaseMutex(); } catch (ApplicationException) { /* 미획득 시 */ }
        }
    }

    /// <summary>VACUUM INTO(WAL 내용 포함 스냅샷) 우선, 실패 시 파일 복사 폴백.</summary>
    private static bool TryImport(string source, string destination, ILogger? logger)
    {
        try
        {
            // Pooling=false — 풀링된 연결이 파일 핸들을 잡고 있으면 Windows 에서 이후 파일 조작이 막힌다.
            using var conn = new SqliteConnection($"Data Source={source};Mode=ReadOnly;Pooling=false");
            conn.Open();
            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA busy_timeout=5000;";
                pragma.ExecuteNonQuery();
            }
            using (var vacuum = conn.CreateCommand())
            {
                vacuum.CommandText = $"VACUUM INTO '{destination.Replace("'", "''")}';";
                vacuum.ExecuteNonQuery();
            }
            return true;
        }
        catch (SqliteException ex)
        {
            logger?.LogWarning(ex, "VACUUM INTO 실패({Source}) — 파일 복사로 폴백", source);
            try
            {
                if (File.Exists(destination)) File.Delete(destination);
                File.Copy(source, destination);
                CopyIfExists(source + "-wal", destination + "-wal");
                CopyIfExists(source + "-shm", destination + "-shm");
                return true;
            }
            catch (Exception copyEx)
            {
                logger?.LogWarning(copyEx, "파일 복사 폴백도 실패({Source})", source);
                return false;
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "레거시 DB 가져오기 실패({Source})", source);
            return false;
        }
    }

    /// <summary>레거시 후보 수집: 설정(Database:LegacyImportPaths) + 기본 후보(컨텐트루트/작업디렉터리/
    /// 저장소 루트의 src\HD.AMR.Web·Desktop). 정본 자신은 제외, 최근 수정 순으로 정렬.</summary>
    private static List<string> CollectCandidates(IConfiguration config, string contentRootPath, string canonical)
    {
        var dbFileName = Path.GetFileName(canonical);
        var cwd = Directory.GetCurrentDirectory();
        var names = new List<string>();

        foreach (var p in config.GetSection("Database:LegacyImportPaths").Get<string[]>() ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            names.Add(Path.IsPathRooted(p) ? p : Path.Combine(contentRootPath, p));
            if (!Path.IsPathRooted(p))
                names.Add(Path.Combine(cwd, p));
        }

        names.Add(Path.Combine(contentRootPath, dbFileName));
        names.Add(Path.Combine(cwd, dbFileName));

        foreach (var start in new[] { contentRootPath, cwd })
        {
            var repo = FindRepoRoot(start);
            if (repo is null) continue;
            names.Add(Path.Combine(repo, "src", "HD.AMR.Web", dbFileName));
            names.Add(Path.Combine(repo, "src", "HD.AMR.Desktop", dbFileName));
        }

        return names
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(p => !string.Equals(p, canonical, StringComparison.OrdinalIgnoreCase))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();
    }

    /// <summary>.git 또는 *.sln 이 있는 디렉터리까지 상향 탐색(최대 10단계).</summary>
    private static string? FindRepoRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                dir.EnumerateFiles("*.sln").Any())
                return dir.FullName;
        }
        return null;
    }

    private static void MoveIfExists(string from, string to)
    {
        if (File.Exists(from)) File.Move(from, to, overwrite: true);
    }

    private static void CopyIfExists(string from, string to)
    {
        if (File.Exists(from)) File.Copy(from, to, overwrite: true);
    }
}
