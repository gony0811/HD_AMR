using System.Threading.Channels;
using HD.AMR.App.Data;
using HD.AMR.App.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service;

/// <summary>
/// 운영 로그 수집기(싱글톤) — 수동(UI)/ACS 발 동작의 시작·성공·실패(원인 포함)를 DB(OperationLogs)에
/// 영속하고, UI 로그 페이지가 구독할 <see cref="Changed"/> 이벤트를 발생시킨다.
///
/// 호출측(시퀀스/order 실행기)은 <see cref="Log"/> 한 줄만 부른다 — DB 쓰기는 채널 큐로 직렬화된
/// 백그라운드 소비자가 수행하므로 실행 경로를 블로킹하지 않고, DB 장애가 임무를 죽이지 않는다
/// (쓰기 실패는 앱 로그로만 남긴다).
/// </summary>
public sealed class OperationLogService
{
    /// <summary>앱 화면 조작(수동).</summary>
    public const string SourceUi = "UI";
    /// <summary>ACS(VDA5050) order·action·instantAction.</summary>
    public const string SourceAcs = "ACS";
    /// <summary>AMR 장비 자체 — 상태 변화 관측(펜던트·조이스틱 수동 조작, 장비 측 모드 전환 등).</summary>
    public const string SourceAmr = "AMR";
    /// <summary>현장 물리 버튼(START/STOP/EMO).</summary>
    public const string SourceIo = "IO";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OperationLogService> _logger;
    private readonly Channel<OperationLog> _queue = Channel.CreateUnbounded<OperationLog>(
        new UnboundedChannelOptions { SingleReader = true });

    public OperationLogService(IServiceScopeFactory scopeFactory, ILogger<OperationLogService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _ = Task.Run(ConsumeAsync);
    }

    /// <summary>새 로그가 기록될 때 발생(임의 스레드). UI 는 디스패처로 마샬링해서 갱신할 것.</summary>
    public event Action? Changed;

    /// <summary>로그 1건 기록 — 비블로킹. success: true=성공, false=실패(detail 에 원인), null=정보성.</summary>
    public void Log(string source, string category, string name, bool? success, string detail,
        string? correlationId = null)
    {
        _queue.Writer.TryWrite(new OperationLog
        {
            TimestampUtc = DateTime.UtcNow,
            Source = source,
            Category = category,
            Name = name,
            Success = success,
            Detail = detail,
            CorrelationId = correlationId,
        });
    }

    /// <summary>최신순 조회. source/category/onlyFailures 는 선택 필터(null = 전체).</summary>
    public async Task<List<OperationLog>> QueryAsync(
        int limit, string? source = null, bool onlyFailures = false, CancellationToken ct = default,
        string? category = null)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HdAmrDbContext>();
        var q = db.OperationLogs.AsNoTracking().AsQueryable();
        if (source is not null) q = q.Where(l => l.Source == source);
        if (category is not null) q = q.Where(l => l.Category == category);
        if (onlyFailures) q = q.Where(l => l.Success == false);
        return await q.OrderByDescending(l => l.Id).Take(limit).ToListAsync(ct);
    }

    /// <summary>기록된 분류 목록(중복 제거, 이름순) — 필터 드롭다운용.</summary>
    public async Task<List<string>> QueryCategoriesAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HdAmrDbContext>();
        return await db.OperationLogs.AsNoTracking().Select(l => l.Category).Distinct().OrderBy(c => c).ToListAsync(ct);
    }

    private async Task ConsumeAsync()
    {
        await foreach (var entry in _queue.Reader.ReadAllAsync())
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<HdAmrDbContext>();
                db.OperationLogs.Add(entry);
                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "운영 로그 DB 기록 실패 — 항목 유실: [{Cat}] {Name}", entry.Category, entry.Name);
            }
            try { Changed?.Invoke(); }
            catch (Exception ex) { _logger.LogWarning(ex, "운영 로그 Changed 구독자 예외"); }
        }
    }
}

/// <summary>운영 로그 분류 코드 — 필터 기준이므로 호출측은 문자열 대신 이 상수를 쓴다.</summary>
public static class OpCategory
{
    /// <summary>VDA5050 order 수명주기(수신/거부/교체/완결/실패).</summary>
    public const string Order = "ORDER";
    /// <summary>order 의 노드 액션(startWeldInspection 등) 시작·구성·결과.</summary>
    public const string Action = "ACTION";
    /// <summary>instantActions(emergencyStop/initPosition 등).</summary>
    public const string Instant = "INSTANT";
    /// <summary>AMR 주행 명령·도착 판정.</summary>
    public const string Drive = "DRIVE";
    /// <summary>코봇 동작(주행 전 홈 복귀 등 시퀀스 밖 동작).</summary>
    public const string Cobot = "COBOT";
    /// <summary>시퀀스 실행 전체.</summary>
    public const string Sequence = "SEQUENCE";
    /// <summary>시퀀스 개별 스텝.</summary>
    public const string Step = "STEP";
    /// <summary>AMR 주행 모드(Drive/Cart) 전환.</summary>
    public const string AmrMode = "AMR_MODE";
    /// <summary>AMR 상태 변화(주행 시작/정지, 오류, 주행정지, 실행상태, 연결).</summary>
    public const string AmrState = "AMR_STATE";
    /// <summary>AMR 수동 조작(조그·명령 없는 주행).</summary>
    public const string AmrManual = "AMR_MANUAL";
    /// <summary>화면에서 보낸 AMR 레지스터 쓰기 명령.</summary>
    public const string AmrCommand = "AMR_CMD";
    /// <summary>비상정지.</summary>
    public const string EStop = "ESTOP";
    /// <summary>ACS 통신 링크(브로커 접속/두절, ACS 생존 신호).</summary>
    public const string Link = "LINK";

    /// <summary>필터 드롭다운 기본 목록(코드, 표시명).</summary>
    public static readonly IReadOnlyList<(string Code, string Label)> All = new[]
    {
        (Order, "오더"), (Action, "액션"), (Instant, "즉시액션"), (Drive, "주행"), (Cobot, "코봇"),
        (Sequence, "시퀀스"), (Step, "스텝"), (AmrMode, "AMR 모드"), (AmrState, "AMR 상태"),
        (AmrManual, "AMR 수동"), (AmrCommand, "AMR 명령"), (EStop, "비상정지"), (Link, "통신"),
    };

    /// <summary>표시명 — 목록에 없는 코드는 그대로.</summary>
    public static string LabelOf(string code) => All.FirstOrDefault(c => c.Code == code).Label ?? code;
}
