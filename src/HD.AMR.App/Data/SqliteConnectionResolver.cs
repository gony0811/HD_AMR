using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace HD.AMR.App.Data;

/// <summary>
/// 웹/데스크톱이 같은 SQLite 파일을 열도록, 연결 문자열의 상대 Data Source 를
/// %LocalAppData%/HD.AMR (macOS: ~/Library/Application Support/HD.AMR) 아래 절대 경로로 통일한다.
/// 상대 경로를 그대로 쓰면 실행 위치(작업 디렉터리)에 따라 앱마다 다른 파일이 만들어진다.
/// </summary>
public static class SqliteConnectionResolver
{
    /// <summary>%LocalAppData%/HD.AMR — 필요 시 생성.</summary>
    public static string AppDataDirectory
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HD.AMR");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>상대 Data Source 를 <see cref="AppDataDirectory"/> 아래 절대 경로로 재작성한
    /// 연결 문자열. 이미 절대 경로면 그대로 둔다.</summary>
    public static string Resolve(IConfiguration config)
    {
        var raw = config.GetConnectionString("DefaultConnection") ?? "Data Source=hd_amr.db";
        var builder = new SqliteConnectionStringBuilder(raw);
        var dataSource = builder.DataSource;

        if (!string.IsNullOrWhiteSpace(dataSource) && !Path.IsPathRooted(dataSource))
            builder.DataSource = Path.Combine(AppDataDirectory, dataSource);

        return builder.ToString();
    }

    /// <summary>해석된 DB 파일의 절대 경로 (레거시 가져오기/백업용).</summary>
    public static string ResolveDbPath(IConfiguration config)
        => new SqliteConnectionStringBuilder(Resolve(config)).DataSource;
}
