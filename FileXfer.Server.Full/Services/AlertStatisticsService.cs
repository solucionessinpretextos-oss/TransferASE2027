using System.Data;
using System.Globalization;
using FileXfer.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace FileXfer.Server.Services;

public sealed class AlertStatisticsService(IDbContextFactory<FileXferDbContext> dbFactory)
{
    public const string ResetAction = "Monitoring.AlertStatisticsReset";

    public sealed record ResetEntry(Guid Id, DateTimeOffset TimestampUtc, string? ActorRfc, string Reason,
        DateTimeOffset? PreviousStartedUtc, int ExhaustedBefore);

    public sealed record HistoryPage(DateTimeOffset? StatisticsStartedUtc, int Page, int PageSize, int TotalCount,
        IReadOnlyList<ResetEntry> Items);

    public static Task<DateTimeOffset?> GetStartedUtcAsync(FileXferDbContext db, CancellationToken ct) =>
        Resets(db).OrderByDescending(entry => entry.TimestampUtc)
            .Select(entry => (DateTimeOffset?)entry.TimestampUtc).FirstOrDefaultAsync(ct);

    public async Task<HistoryPage> GetHistoryAsync(int page, int pageSize, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var query = Resets(db);
        var startedUtc = await GetStartedUtcAsync(db, ct);
        var count = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(entry => entry.TimestampUtc).ThenBy(entry => entry.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new(startedUtc, page, pageSize, count, rows.Select(Describe).ToArray());
    }

    public async Task<ResetEntry> ResetAsync(string actor, string? ip, string reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length is < 5 or > 500)
            throw new ArgumentException("Indique un motivo de entre 5 y 500 caracteres.");

        // Conserve la identidad de la operación si SQL vuelve a intentar una confirmación ambigua.
        var id = Guid.NewGuid();
        await using var strategyContext = await dbFactory.CreateDbContextAsync(ct);
        var strategy = strategyContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(() => PersistResetAsync(id, actor, ip, reason.Trim(), ct));
    }

    private async Task<ResetEntry> PersistResetAsync(Guid id, string actor, string? ip, string reason,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
            : null;

        var existing = await Resets(db).SingleOrDefaultAsync(entry => entry.Id == id, ct);
        if (existing is not null)
        {
            if (transaction is not null) await transaction.CommitAsync(ct);
            return Describe(existing);
        }

        var previous = await GetStartedUtcAsync(db, ct);
        var now = DateTimeOffset.UtcNow;
        var startedUtc = previous is { } earlier && now <= earlier ? earlier.AddTicks(1) : now;
        var exhausted = db.AlertOutboxDeliveries.AsNoTracking().Where(row =>
            row.Status == AlertOutboxStatuses.DeadLetter && row.UpdatedUtc < startedUtc);
        if (previous is { } previousStart) exhausted = exhausted.Where(row => row.UpdatedUtc >= previousStart);
        var exhaustedBefore = await exhausted.CountAsync(ct);

        db.AuditLogs.Add(new AuditLog
        {
            Id = id,
            TimestampUtc = startedUtc,
            ActorRfc = actor,
            Ip = ip,
            Action = ResetAction,
            // El motivo ocupa el resto del campo y puede contener saltos de línea.
            Details = string.Create(CultureInfo.InvariantCulture, $"{previous:O}\n{exhaustedBefore}\n{reason}")
        });
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return new(id, startedUtc, actor, reason, previous, exhaustedBefore);
    }

    private static IQueryable<AuditLog> Resets(FileXferDbContext db) =>
        db.AuditLogs.AsNoTracking().Where(entry => entry.Action == ResetAction);

    private static ResetEntry Describe(AuditLog entry)
    {
        var details = entry.Details?.Split('\n', 3);
        if (details is not { Length: 3 })
            return new(entry.Id, entry.TimestampUtc, entry.ActorRfc, entry.Details ?? "", null, 0);

        var previous = DateTimeOffset.TryParseExact(details[0], "O", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var parsed) ? (DateTimeOffset?)parsed : null;
        _ = int.TryParse(details[1], NumberStyles.None, CultureInfo.InvariantCulture, out var exhaustedBefore);
        return new(entry.Id, entry.TimestampUtc, entry.ActorRfc, details[2], previous, exhaustedBefore);
    }
}
