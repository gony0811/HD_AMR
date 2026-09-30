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
    public const string SourceUi = "UI";
    public const string SourceAcs = "ACS";

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

    /// <summary>최신순 조회. source/onlyFailures 는 선택 필터.</summary>
    public async Task<List<OperationLog>> QueryAsync(
        int limit, string? source = null, bool onlyFailures = false, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HdAmrDbContext>();
        var q = db.OperationLogs.AsNoTracking().AsQueryable();
        if (source is not null) q = q.Where(l => l.Source == source);
        if (onlyFailures) q = q.Where(l => l.Success == false);
        return await q.OrderByDescending(l => l.Id).Take(limit).ToListAsync(ct);
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
