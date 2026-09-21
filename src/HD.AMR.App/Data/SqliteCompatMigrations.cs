using Microsoft.EntityFrameworkCore;

namespace HD.AMR.App.Data;

/// <summary>
/// 멱등(idempotent) 후방호환 스키마 패치. EnsureCreated 는 파일이 이미 존재하면 아무것도
/// 하지 않으므로(컬럼 검증 없음), 과거 버전에서 만들어진 DB 를 열 때 여기서 누락 테이블/컬럼을
/// 보충한다. 새 DB/기존 DB 모두 안전 — EnsureCreated 직후 웹·데스크톱 양쪽에서 호출한다.
/// </summary>
public static class SqliteCompatMigrations
{
    public static void Apply(HdAmrDbContext db)
    {
        // Backward-compatible schema add for ExcludedRegions (preserves existing data).
        db.Database.ExecuteSqlRaw(@"
CREATE TABLE IF NOT EXISTS ExcludedRegions (
    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    DrawingId INTEGER NOT NULL,
    MinX REAL NOT NULL,
    MinY REAL NOT NULL,
    MaxX REAL NOT NULL,
    MaxY REAL NOT NULL,
    CreatedAt TEXT NOT NULL,
    CONSTRAINT FK_ExcludedRegions_Drawings_DrawingId FOREIGN KEY (DrawingId) REFERENCES Drawings (Id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS IX_ExcludedRegions_DrawingId ON ExcludedRegions (DrawingId);
");

        // 과거 TeachingProfiles 테이블을 InspectionProfiles 로 이름 변경(기존 데이터 보존).
        // 구 테이블이 있고 신 테이블이 없을 때만 RENAME 한다(SQLite는 RENAME IF EXISTS 미지원).
        var hasOldTeachingProfiles = db.Database
            .SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = 'TeachingProfiles'")
            .AsEnumerable().First() > 0;
        var hasInspectionProfiles = db.Database
            .SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = 'InspectionProfiles'")
            .AsEnumerable().First() > 0;
        if (hasOldTeachingProfiles && !hasInspectionProfiles)
        {
            db.Database.ExecuteSqlRaw("ALTER TABLE TeachingProfiles RENAME TO InspectionProfiles;");
        }

        // Backward-compatible schema add for InspectionProfiles (preserves existing data).
        db.Database.ExecuteSqlRaw(@"
CREATE TABLE IF NOT EXISTS InspectionProfiles (
    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    DrawingId INTEGER NOT NULL,
    Name TEXT NOT NULL,
    SpacingMm REAL NOT NULL,
    CorrugThresholdDeg REAL NOT NULL,
    CorrugStepDeg REAL NOT NULL,
    RunTool INTEGER NOT NULL,
    RunUser INTEGER NOT NULL,
    RunVel INTEGER NOT NULL,
    DelaySec REAL NOT NULL,
    ThMax REAL NOT NULL,
    SettleDelaySec REAL NOT NULL DEFAULT 0,
    MoveHomeFirst INTEGER NOT NULL,
    SeamType TEXT NOT NULL DEFAULT 'LINE',
    PoseAbsolute INTEGER NOT NULL DEFAULT 0,
    WaypointsJson TEXT NOT NULL,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    CONSTRAINT FK_InspectionProfiles_Drawings_DrawingId FOREIGN KEY (DrawingId) REFERENCES Drawings (Id) ON DELETE SET NULL
);
CREATE INDEX IF NOT EXISTS IX_InspectionProfiles_DrawingId ON InspectionProfiles (DrawingId);
");

        // 기존 DB에 SettleDelaySec 컬럼이 없으면 추가(엔티티가 나중에 추가된 칼럼). SQLite는
        // ADD COLUMN IF NOT EXISTS 가 없으므로 pragma 로 존재 여부를 확인한 뒤에만 ALTER 한다.
        var hasSettleDelaySec = db.Database
            .SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM pragma_table_info('InspectionProfiles') WHERE name = 'SettleDelaySec'")
            .AsEnumerable().First() > 0;
        if (!hasSettleDelaySec)
        {
            db.Database.ExecuteSqlRaw(
                "ALTER TABLE InspectionProfiles ADD COLUMN SettleDelaySec REAL NOT NULL DEFAULT 0;");
        }

        // SeamType 컬럼(LINE/CROSS 티칭 구분)도 동일 패턴으로 후방호환 추가.
        var hasSeamType = db.Database
            .SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM pragma_table_info('InspectionProfiles') WHERE name = 'SeamType'")
            .AsEnumerable().First() > 0;
        if (!hasSeamType)
        {
            db.Database.ExecuteSqlRaw(
                "ALTER TABLE InspectionProfiles ADD COLUMN SeamType TEXT NOT NULL DEFAULT 'LINE';");
        }

        // PoseAbsolute 컬럼(절대 6-DOF 자세 모드)도 동일 패턴으로 후방호환 추가.
        var hasPoseAbsolute = db.Database
            .SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM pragma_table_info('InspectionProfiles') WHERE name = 'PoseAbsolute'")
            .AsEnumerable().First() > 0;
        if (!hasPoseAbsolute)
        {
            db.Database.ExecuteSqlRaw(
                "ALTER TABLE InspectionProfiles ADD COLUMN PoseAbsolute INTEGER NOT NULL DEFAULT 0;");
        }

        // InspectionProfiles.DrawingId NOT NULL → NULL 완화 + FK CASCADE → SET NULL (도면↔프로파일 디커플링).
        // SQLite 는 ALTER 로 NOT NULL/FK 를 못 바꾸므로 테이블 재생성(rebuild)으로 처리.
        // DrawingId 의 notnull 플래그가 1인 경우에만 실행 — 멱등. 위의 컬럼 보충(SettleDelaySec 등)이
        // 끝난 뒤에 와야 SELECT 컬럼 목록이 항상 존재한다.
        var drawingIdNotNull = db.Database
            .SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM pragma_table_info('InspectionProfiles') WHERE name = 'DrawingId' AND \"notnull\" = 1")
            .AsEnumerable().First() > 0;
        if (drawingIdNotNull)
        {
            db.Database.ExecuteSqlRaw(@"
PRAGMA foreign_keys=off;
CREATE TABLE InspectionProfiles_rebuild (
    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    DrawingId INTEGER NULL,
    Name TEXT NOT NULL,
    SpacingMm REAL NOT NULL,
    CorrugThresholdDeg REAL NOT NULL,
    CorrugStepDeg REAL NOT NULL,
    RunTool INTEGER NOT NULL,
    RunUser INTEGER NOT NULL,
    RunVel INTEGER NOT NULL,
    DelaySec REAL NOT NULL,
    ThMax REAL NOT NULL,
    SettleDelaySec REAL NOT NULL DEFAULT 0,
    MoveHomeFirst INTEGER NOT NULL,
    SeamType TEXT NOT NULL DEFAULT 'LINE',
    PoseAbsolute INTEGER NOT NULL DEFAULT 0,
    WaypointsJson TEXT NOT NULL,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    CONSTRAINT FK_InspectionProfiles_Drawings_DrawingId FOREIGN KEY (DrawingId) REFERENCES Drawings (Id) ON DELETE SET NULL
);
INSERT INTO InspectionProfiles_rebuild
    (Id, DrawingId, Name, SpacingMm, CorrugThresholdDeg, CorrugStepDeg, RunTool, RunUser, RunVel,
     DelaySec, ThMax, SettleDelaySec, MoveHomeFirst, SeamType, PoseAbsolute, WaypointsJson, CreatedAt, UpdatedAt)
SELECT Id, DrawingId, Name, SpacingMm, CorrugThresholdDeg, CorrugStepDeg, RunTool, RunUser, RunVel,
       DelaySec, ThMax, SettleDelaySec, MoveHomeFirst, SeamType, PoseAbsolute, WaypointsJson, CreatedAt, UpdatedAt
FROM InspectionProfiles;
DROP TABLE InspectionProfiles;
ALTER TABLE InspectionProfiles_rebuild RENAME TO InspectionProfiles;
CREATE INDEX IF NOT EXISTS IX_InspectionProfiles_DrawingId ON InspectionProfiles (DrawingId);
PRAGMA foreign_keys=on;
");
        }

        // Backward-compatible schema add for TeachingPositions (고정 슬롯형 티칭 위치; 기존 데이터 보존).
        db.Database.ExecuteSqlRaw(@"
CREATE TABLE IF NOT EXISTS TeachingPositions (
    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    ""Key"" TEXT NOT NULL,
    Name TEXT NOT NULL,
    SortOrder INTEGER NOT NULL DEFAULT 0,
    X REAL NULL, Y REAL NULL, Z REAL NULL,
    Rx REAL NULL, Ry REAL NULL, Rz REAL NULL,
    J1 REAL NULL, J2 REAL NULL, J3 REAL NULL, J4 REAL NULL, J5 REAL NULL, J6 REAL NULL,
    Tool INTEGER NOT NULL DEFAULT 1,
    CapturedAt TEXT NULL,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    UserFrame INTEGER NULL,
    RelX REAL NULL, RelY REAL NULL, RelZ REAL NULL,
    RelRx REAL NULL, RelRy REAL NULL, RelRz REAL NULL,
    SurfaceId INTEGER NOT NULL DEFAULT 0
);
CREATE UNIQUE INDEX IF NOT EXISTS IX_TeachingPositions_Key ON TeachingPositions (""Key"");
");

        // 기존 TeachingPositions 에 작업물 좌표계(추종 절대위치) 컬럼이 없으면 추가(기존 데이터 보존).
        // SQLite는 ADD COLUMN IF NOT EXISTS 미지원 → pragma 로 확인 후에만 ALTER.
        foreach (var col in new[] { "UserFrame INTEGER", "RelX REAL", "RelY REAL", "RelZ REAL", "RelRx REAL", "RelRy REAL", "RelRz REAL" })
        {
            var name = col.Split(' ')[0];
            var exists = db.Database
                .SqlQueryRaw<long>($"SELECT COUNT(*) AS Value FROM pragma_table_info('TeachingPositions') WHERE name = '{name}'")
                .AsEnumerable().First() > 0;
            if (!exists)
                db.Database.ExecuteSqlRaw($"ALTER TABLE TeachingPositions ADD COLUMN {col} NULL;");
        }

        // 기존 TeachingPositions 에 SurfaceId(0x00~0xFF, 0=디폴트 위치) 컬럼이 없으면 추가(기존 데이터 보존).
        var hasSurfaceId = db.Database
            .SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM pragma_table_info('TeachingPositions') WHERE name = 'SurfaceId'")
            .AsEnumerable().First() > 0;
        if (!hasSurfaceId)
            db.Database.ExecuteSqlRaw("ALTER TABLE TeachingPositions ADD COLUMN SurfaceId INTEGER NOT NULL DEFAULT 0;");

        // Backward-compatible schema add for Parameters (범용 key/value 설정 저장소; 기존 데이터 보존).
        db.Database.ExecuteSqlRaw(@"
CREATE TABLE IF NOT EXISTS Parameters (
    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    Name TEXT NOT NULL,
    Value TEXT NOT NULL,
    Description TEXT NULL,
    UpdatedAt TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS IX_Parameters_Name ON Parameters (Name);
");

        // Backward-compatible schema add for InspectionRecipes (검사 타입 17종 레시피, 사양 §8.5.1; 기존 데이터 보존).
        db.Database.ExecuteSqlRaw(@"
CREATE TABLE IF NOT EXISTS InspectionRecipes (
    Id TEXT NOT NULL PRIMARY KEY,
    DisplayName TEXT NOT NULL,
    SeamType TEXT NOT NULL,
    Orientation TEXT NOT NULL,
    Enabled INTEGER NOT NULL,
    StepKeysJson TEXT NULL,
    CameraTargetDistanceMm REAL NULL,
    InspectionProfileId INTEGER NULL,
    VisionFailRatioMax REAL NOT NULL DEFAULT 1.0,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL
);
");

        // (PatternJson 컬럼 제거 — CROSS 십자 패턴 런타임 생성 폐기, 캡처 교시 단일화. 기존 DB 의 잔여 컬럼은
        //  EF 모델에서 매핑하지 않으므로 무해하게 무시된다.)

        // SurfaceOverride / AlignRetryCount / ApproachTeachingKey / DefaultStandoffMm 컬럼 제거(레시피 필드 폐기). EnsureCreated 로 만든 DB 는
        // NOT NULL 컬럼에 DEFAULT 가 없을 수 있어 남겨두면 시드 INSERT 가 실패한다 — 실제로 DROP 한다.
        // SQLite 3.35+ DROP COLUMN (Microsoft.Data.Sqlite 번들 SQLite 는 이를 충족).
        foreach (var col in new[] { "SurfaceOverride", "AlignRetryCount", "ApproachTeachingKey", "DefaultStandoffMm" })
        {
            var exists = db.Database
                .SqlQueryRaw<long>($"SELECT COUNT(*) AS Value FROM pragma_table_info('InspectionRecipes') WHERE name = '{col}'")
                .AsEnumerable().First() > 0;
            if (exists)
                db.Database.ExecuteSqlRaw($"ALTER TABLE InspectionRecipes DROP COLUMN {col};");
        }

        // InspectionProfileId 컬럼(레시피 → 실행 티칭 프로필 지정)도 후방호환 추가. 기존 행은 NULL(미지정).
        // FK 없음 — 프로필 삭제 시 DrawingService.DeleteProfileAsync 가 참조를 NULL 로 정리한다.
        var hasProfileId = db.Database
            .SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM pragma_table_info('InspectionRecipes') WHERE name = 'InspectionProfileId'")
            .AsEnumerable().First() > 0;
        if (!hasProfileId)
            db.Database.ExecuteSqlRaw("ALTER TABLE InspectionRecipes ADD COLUMN InspectionProfileId INTEGER NULL;");
    }
}
