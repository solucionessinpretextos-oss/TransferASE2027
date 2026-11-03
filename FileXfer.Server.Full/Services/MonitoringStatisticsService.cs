using FileXfer.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace FileXfer.Server.Services;

/// <summary>Una marca auditable inicia el período visible sin borrar evidencia histórica.</summary>
public sealed class MonitoringStatisticsService(IDbContextFactory<FileXferDbContext> dbFactory)
{
    public const string ResetAction = "Monitoring.StatisticsReset";

    public static Task<DateTimeOffset?> GetStartedUtcAsync(FileXferDbContext db, CancellationToken ct) =>
        db.AuditLogs.AsNoTracking()
            .Where(entry => entry.Action == ResetAction)
            .OrderByDescending(entry => entry.TimestampUtc)
            .Select(entry => (DateTimeOffset?)entry.TimestampUtc)
            .FirstOrDefaultAsync(ct);

    public async Task<DateTimeOffset> ResetAsync(string actor, string? ip, string reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length is < 5 or > 500)
            throw new ArgumentException("Indique un motivo de entre 5 y 500 caracteres.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow;
        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            TimestampUtc = now,
            ActorRfc = actor,
            Ip = ip,
            Action = ResetAction,
            Details = $"Nuevo período de estadísticas. Motivo: {reason.Trim()}. Se conserva el historial, archivos y recibos."
        });
        await db.SaveChangesAsync(ct);
        return now;
    }
}
