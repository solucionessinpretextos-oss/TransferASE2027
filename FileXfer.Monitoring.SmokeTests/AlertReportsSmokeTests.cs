using System.Reflection;
using System.Text.Json;
using FileXfer.Server.Controllers.Admin;
using FileXfer.Server.Data;
using FileXfer.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

internal static class AlertReportsSmokeTests
{
    private const string Actor = "CARJ721209SD2";

    public static async Task RunAsync()
    {
        var factory = new Factory(new DbContextOptionsBuilder<FileXferDbContext>()
            .UseInMemoryDatabase("alert-reports-" + Guid.NewGuid()).Options);
        var cutoff = new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
        var historical = Delivery("email", AlertOutboxStatuses.DeadLetter, "smtp_535", cutoff.AddMinutes(-10));
        var active = Delivery("email", AlertOutboxStatuses.Processing, "smtp_535", cutoff.AddMinutes(-5));
        var exhausted = Delivery("email", AlertOutboxStatuses.DeadLetter, "smtp_535", cutoff.AddMinutes(5));
        var webhook = Delivery("webhook", AlertOutboxStatuses.Pending, "http_403", cutoff.AddMinutes(6));
        var delivered = Delivery("email", AlertOutboxStatuses.Delivered, null, cutoff.AddMinutes(-1));
        var queued = Delivery("email", AlertOutboxStatuses.Pending, null, cutoff.AddMinutes(7));
        var purgedDelivery = Guid.NewGuid();
        await using (var db = factory.CreateDbContext())
        {
            db.AlertOutboxDeliveries.AddRange(historical, active, exhausted, webhook, delivered, queued);
            db.AuditLogs.AddRange(
                Log(AlertStatisticsService.ResetAction, cutoff, "\n1\nSMTP verificado"),
                Log(AlertOperationsService.TestAction, cutoff.AddMinutes(1), JsonSerializer.Serialize(new
                {
                    channel = "email", deliveryId = purgedDelivery, outcomeStatus = AlertOutboxStatuses.Delivered,
                    reason = "Prueba administrativa", payload = "UNRELATED-SECRET"
                })),
                Log(AlertOperationsService.ReprocessAction, cutoff.AddMinutes(2), JsonSerializer.Serialize(new
                {
                    channel = "email", deliveryId = exhausted.Id, sourceDeliveryId = historical.Id,
                    reason = "Recuperación autorizada"
                })),
                Log("login_ok", cutoff.AddMinutes(3), "UNRELATED-SECRET"));
            db.FileTypePolicyRevisions.Add(new FileTypePolicyRevision
            {
                Id = Guid.NewGuid(), Extension = ".pdf", Version = 2, ChangedBy = Actor, ChangedUtc = cutoff.AddMinutes(-1),
                ChangeReason = "Verificación de PDFs"
            });
            await db.SaveChangesAsync();
        }

        var audit = new OperationalAuditController(factory);
        var entries = Payload(await audit.Get(default, rfc: Actor.ToLowerInvariant()));
        Check(entries.TotalCount == 4 && entries.Items.Select(item => item.Action).SequenceEqual(new[]
        {
            AlertOperationsService.ReprocessAction, AlertOperationsService.TestAction, AlertStatisticsService.ResetAction, "FilePolicy.Revision"
        }), "La bitácora no combinó, ordenó o filtró correctamente las acciones administrativas.");
        Check(entries.Items[0].DeliveryId == exhausted.Id && entries.Items[0].DeliveryStatus == AlertOutboxStatuses.DeadLetter &&
              entries.Items[1].DeliveryId == purgedDelivery && entries.Items[1].DeliveryStatus == AlertOutboxStatuses.Delivered,
            "La bitácora perdió el resultado vigente o el resultado conservado tras retención.");
        Check(!JsonSerializer.Serialize(entries).Contains("UNRELATED-SECRET", StringComparison.Ordinal),
            "La bitácora devolvió datos no autorizados del registro original.");
        var second = Payload(await audit.Get(default, from: cutoff, to: cutoff.AddMinutes(1), page: 2, pageSize: 1));
        Check(second.TotalCount == 2 && second.Items.Single().Action == AlertStatisticsService.ResetAction,
            "Los límites de fecha o paginación de bitácora son incorrectos.");
        Check(Payload(await audit.Get(default, action: "FilePolicy.Revision")).Items.Single().Summary.Contains(".pdf", StringComparison.Ordinal),
            "El filtro de políticas no devolvió la revisión asociada.");

        var incidents = new AlertIncidentsController(factory);
        var current = Payload(await incidents.Get(default));
        var smtp = current.Items.Single(item => item.Transport == "email");
        Check(current.StatisticsStartedUtc == cutoff && current.TotalCount == 2 && smtp.AffectedCount == 2 &&
              smtp.ActiveCount == 1 && smtp.ExhaustedCount == 1 && smtp.FirstObservedUtc == active.UpdatedUtc &&
              smtp.LastObservedUtc == exhausted.UpdatedUtc && smtp.LastSuccessfulDeliveryUtc == delivered.DeliveredUtc,
            "El período actual perdió fallos activos o incluyó agotados históricos.");
        var all = Payload(await incidents.Get(default, period: "All", page: 2, pageSize: 1));
        smtp = all.Items.Single();
        Check(all.TotalCount == 2 && smtp.Transport == "email" && smtp.FailureCode == "smtp_535" &&
              smtp.AffectedCount == 3 && smtp.ExhaustedCount == 2 && smtp.FirstObservedUtc == historical.UpdatedUtc,
            "El historial no incluyó las entregas agotadas o no respetó la paginación.");
        Check(typeof(OperationalAuditController).GetCustomAttribute<AuthorizeAttribute>()?.Policy == "AdminEfirma" &&
              typeof(AlertIncidentsController).GetCustomAttribute<AuthorizeAttribute>()?.Policy == "PrivilegedEfirma",
            "Los permisos de los reportes no coinciden con el perfil de operación.");

        VerifySql(cutoff);
        Console.WriteLine("PASS alert-reports audited-actions=filtered delivery-outcome=retained incidents=current-and-historical sql2014=translated");
    }

    private static void VerifySql(DateTimeOffset cutoff)
    {
        using var db = new FileXferDbContext(new DbContextOptionsBuilder<FileXferDbContext>()
            .UseSqlServer("Server=sql2014.invalid;Database=translation_only;Integrated Security=True;Encrypt=True",
                sql => sql.UseCompatibilityLevel(120)).Options);
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        var audit = (IQueryable)typeof(OperationalAuditController).GetMethod("AuditRows", flags)!
            .Invoke(null, [db, cutoff.AddDays(-1), cutoff, Actor, null])!;
        var incidents = (IQueryable)typeof(AlertIncidentsController).GetMethod("GroupFailures", flags)!
            .Invoke(null, [db.AlertOutboxDeliveries.AsNoTracking(), cutoff])!;
        var auditSql = audit.ToQueryString();
        var incidentSql = incidents.ToQueryString();
        Check(auditSql.Contains("UNION ALL", StringComparison.OrdinalIgnoreCase) &&
              incidentSql.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase) &&
              !auditSql.Contains("OPENJSON", StringComparison.OrdinalIgnoreCase) &&
              !incidentSql.Contains("OPENJSON", StringComparison.OrdinalIgnoreCase),
            "Una consulta de reportes no se agregó en SQL o requiere OPENJSON.");
    }

    private static AlertOutboxDelivery Delivery(string transport, string status, string? code, DateTimeOffset updated) => new()
    {
        Id = Guid.NewGuid(), EventId = Guid.NewGuid(), DeduplicationKey = Guid.NewGuid().ToString("N"),
        Transport = transport, Status = status, LastFailureCode = code, OccurredUtc = updated, CreatedUtc = updated,
        UpdatedUtc = updated, DeliveredUtc = status == AlertOutboxStatuses.Delivered ? updated : null,
        Message = "UNRELATED-SECRET"
    };

    private static AuditLog Log(string action, DateTimeOffset timestamp, string details) => new()
    {
        Id = Guid.NewGuid(), ActorRfc = Actor, Action = action, TimestampUtc = timestamp, Details = details
    };

    private static T Payload<T>(ActionResult<T> response) =>
        response.Result is OkObjectResult { Value: T value } ? value : throw new InvalidOperationException("Se esperaba HTTP 200.");

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Factory(DbContextOptions<FileXferDbContext> options) : IDbContextFactory<FileXferDbContext>
    {
        public FileXferDbContext CreateDbContext() => new(options);
    }
}
