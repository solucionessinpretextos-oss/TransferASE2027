using System.Data;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using FileXfer.Server.Data;
using FileXfer.Server.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FileXfer.Server.Services;

public sealed class AlertOperationsService(
    IDbContextFactory<FileXferDbContext> dbFactory, IEnumerable<IAlertTransport> transports, IOptions<AlertOptions> options)
{
    public const string TestAction = "Monitoring.AlertChannelTest";
    public const string ReprocessAction = "Monitoring.AlertReprocess";
    private const string TestReason = "Prueba manual del canal de alertas";
    private const string TestDeduplicationPrefix = "admin-test:";
    private const string ReprocessDeduplicationPrefix = "admin-reprocess:";
    private const int AuditDetailsMaxLength = 2048;
    private static readonly TimeSpan TestInterval = TimeSpan.FromMinutes(1);
    private static readonly JsonSerializerOptions AuditJson = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    public sealed record Receipt(Guid DeliveryId, Guid EventId, string Status, string Message);

    public sealed record Result(int StatusCode, string Message, Receipt? Delivery = null,
        Guid? ExistingDeliveryId = null, int? RetryAfterSeconds = null);

    public sealed record DeliveryDetail(Guid Id, Guid EventId, string Transport, string Title, string Status,
        int FailedAttempts, string? FailureCode, string FailureExplanation, string Remediation,
        DateTimeOffset UpdatedUtc, DateTimeOffset? DeliveredUtc, DateTimeOffset? NextAttemptUtc);

    private sealed record AuditDetails(
        [property: JsonPropertyOrder(-2)] string Channel,
        [property: JsonPropertyOrder(-1)] Guid? SourceDeliveryId,
        Guid DeliveryId, Guid EventId, string Reason,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OutcomeStatus = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? OutcomeUtc = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OutcomeFailureCode = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? FailedAttempts = null);

    public Task<Result> TestAsync(string actor, string? ip, string channel, Guid requestId, CancellationToken ct)
    {
        if (channel is not ("email" or "webhook"))
            return Task.FromResult(new Result(StatusCodes.Status400BadRequest, "Seleccione correo SMTP o webhook."));
        return EnqueueAsync(actor, ip, requestId, channel, null, TestReason, ct);
    }

    public Task<Result> ReprocessAsync(string actor, string? ip, Guid sourceId, string reason, Guid requestId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length is < 5 or > 500)
            return Task.FromResult(new Result(StatusCodes.Status400BadRequest, "Indique un motivo de entre 5 y 500 caracteres."));
        return EnqueueAsync(actor, ip, requestId, null, sourceId, reason.Trim(), ct);
    }

    public async Task<DeliveryDetail?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.AlertOutboxDeliveries.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, ct);
        if (row is null) return null;
        var (explanation, remediation) = AlertFailureDescription.Describe(row.LastFailureCode);
        return new(row.Id, row.EventId, row.Transport, row.Title, row.Status, row.AttemptCount, row.LastFailureCode,
            explanation, remediation, row.UpdatedUtc, row.DeliveredUtc,
            row.Status == AlertOutboxStatuses.Pending ? row.NextAttemptUtc : null);
    }

    private async Task<Result> EnqueueAsync(string actor, string? ip, Guid requestId, string? testChannel,
        Guid? sourceId, string reason, CancellationToken ct)
    {
        if (requestId == Guid.Empty)
            return new(StatusCodes.Status400BadRequest, "Indique un identificador válido para la solicitud.");

        await using var strategyContext = await dbFactory.CreateDbContextAsync(ct);
        var strategy = strategyContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(token => PersistAsync(actor, ip, requestId, testChannel, sourceId, reason, token), ct);
    }

    private async Task<Result> PersistAsync(string actor, string? ip, Guid requestId, string? testChannel,
        Guid? sourceId, string reason, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // La comprobación del origen y el límite de frecuencia comparten la misma transacción que el alta.
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
            : null;

        var action = sourceId is null ? TestAction : ReprocessAction;
        var previous = await db.AuditLogs.AsNoTracking().SingleOrDefaultAsync(entry => entry.Id == requestId, ct);
        if (previous is not null)
        {
            if (previous.Action != action || previous.ActorRfc != actor || ReadDetails(previous.Details) is not { } details ||
                details.SourceDeliveryId != sourceId || details.Reason != reason ||
                (testChannel is not null && details.Channel != testChannel))
                return new(StatusCodes.Status409Conflict, "Ese identificador pertenece a otra solicitud.");

            var existing = await db.AlertOutboxDeliveries.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == details.DeliveryId, ct);
            return existing is null
                ? new(StatusCodes.Status410Gone, "La entrega de esa solicitud ya salió del período de retención.")
                : Accepted(existing, "La solicitud ya fue registrada.");
        }

        AlertOutboxDelivery? source = null;
        var channel = testChannel;
        if (sourceId is { } id)
        {
            source = await db.AlertOutboxDeliveries.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, ct);
            if (source is null)
                return new(StatusCodes.Status404NotFound, "No se encontró el aviso de origen.");
            if (source.Status != AlertOutboxStatuses.DeadLetter)
                return new(StatusCodes.Status409Conflict, "Solo se puede reprocesar un aviso con intentos agotados.");
            channel = source.Transport;
        }

        if (channel is null || !options.Value.Enabled ||
            !transports.Any(transport => transport.Name == channel && transport.IsConfigured))
            return new(StatusCodes.Status409Conflict, "El canal de esa entrega no está configurado o está deshabilitado.");

        var auditPrefix = JsonSerializer.Serialize(new { channel, sourceDeliveryId = sourceId }, AuditJson)[..^1] + ",";
        var sameTarget = db.AuditLogs.AsNoTracking()
            .Where(entry => entry.Action == action && entry.Details != null && entry.Details.StartsWith(auditPrefix));
        var now = DateTimeOffset.UtcNow;
        if (source is null)
        {
            var since = now - TestInterval;
            var lastTest = await sameTarget.Where(entry => entry.TimestampUtc > since)
                .OrderByDescending(entry => entry.TimestampUtc)
                .Select(entry => (DateTimeOffset?)entry.TimestampUtc).FirstOrDefaultAsync(ct);
            if (lastTest is { } last)
                return new(StatusCodes.Status429TooManyRequests, "Espere un minuto entre pruebas del mismo canal.",
                    RetryAfterSeconds: Math.Max(1, (int)Math.Ceiling((last + TestInterval - now).TotalSeconds)));
        }
        else
        {
            var previousDetails = await sameTarget.Select(entry => entry.Details).FirstOrDefaultAsync(ct);
            if (previousDetails is not null)
            {
                var copy = ReadDetails(previousDetails)?.DeliveryId;
                if (copy is { } deliveryId && !await db.AlertOutboxDeliveries.AsNoTracking().AnyAsync(item => item.Id == deliveryId, ct))
                    copy = null;
                return new(StatusCodes.Status409Conflict, "Este aviso ya tiene un reproceso registrado.", ExistingDeliveryId: copy);
            }
        }

        var row = new AlertOutboxDelivery
        {
            Id = requestId,
            // Mantiene la identidad lógica y la clave de idempotencia que recibe el webhook.
            EventId = source?.EventId ?? requestId,
            Transport = channel,
            DeduplicationKey = sourceId is { } origin ? $"{ReprocessDeduplicationPrefix}{origin:N}" : $"{TestDeduplicationPrefix}{requestId:N}",
            DeduplicationBucket = 0,
            Title = source?.Title ?? "Prueba de canal de alertas",
            Message = source?.Message ?? $"Prueba manual del canal configurado en Transfer. Identificador: {requestId:D}.",
            OccurredUtc = source?.OccurredUtc ?? now,
            CreatedUtc = now,
            UpdatedUtc = now,
            NextAttemptUtc = now
        };
        var auditDetails = new AuditDetails(channel, sourceId, row.Id, row.EventId, reason);
        // Reserva el peor tamaño de los campos terminales en la misma columna de bitácora.
        var auditWithOutcome = auditDetails with
        {
            OutcomeStatus = AlertOutboxStatuses.DeadLetter,
            OutcomeUtc = DateTimeOffset.MaxValue,
            OutcomeFailureCode = new string('\uffff', 128),
            FailedAttempts = int.MaxValue
        };
        if (JsonSerializer.Serialize(auditWithOutcome, AuditJson).Length > AuditDetailsMaxLength)
            return new(StatusCodes.Status400BadRequest, "El motivo contiene demasiados caracteres especiales. Simplifíquelo e intente de nuevo.");

        db.AlertOutboxDeliveries.Add(row);
        db.AuditLogs.Add(new AuditLog
        {
            Id = requestId, TimestampUtc = now, ActorRfc = actor, Ip = ip, Action = action,
            Details = JsonSerializer.Serialize(auditDetails, AuditJson)
        });
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return Accepted(row, "Se registró la entrega en la cola de alertas.");
    }

    private static Result Accepted(AlertOutboxDelivery row, string message) =>
        new(StatusCodes.Status202Accepted, message, new Receipt(row.Id, row.EventId, row.Status, message));

    public static async Task RecordOutcomeAsync(FileXferDbContext db, AlertOutboxDelivery delivery, CancellationToken ct)
    {
        if (delivery.Status is not (AlertOutboxStatuses.Delivered or AlertOutboxStatuses.DeadLetter) ||
            !(delivery.DeduplicationKey.StartsWith(TestDeduplicationPrefix, StringComparison.Ordinal) ||
              delivery.DeduplicationKey.StartsWith(ReprocessDeduplicationPrefix, StringComparison.Ordinal)))
            return;

        var audit = await db.AuditLogs.SingleOrDefaultAsync(entry => entry.Id == delivery.Id &&
            (entry.Action == TestAction || entry.Action == ReprocessAction), ct);
        if (audit is null || ReadDetails(audit.Details) is not { } details) return;

        audit.Details = JsonSerializer.Serialize(details with
        {
            OutcomeStatus = delivery.Status,
            OutcomeUtc = delivery.UpdatedUtc,
            OutcomeFailureCode = delivery.LastFailureCode,
            FailedAttempts = delivery.AttemptCount
        }, AuditJson);
    }

    private static AuditDetails? ReadDetails(string? json)
    {
        if (json is null) return null;
        try { return JsonSerializer.Deserialize<AuditDetails>(json, AuditJson); }
        catch (JsonException) { return null; }
    }
}
