using System.Globalization;
using System.Text.Json;
using FileXfer.Server.Data;
using FileXfer.Server.Security;
using FileXfer.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FileXfer.Server.Controllers.Admin;

[ApiController]
[Route("admin/monitoring/audit")]
[Authorize(Policy = "AdminEfirma")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class OperationalAuditController(IDbContextFactory<FileXferDbContext> dbFactory) : ControllerBase
{
    private const string PolicyRevision = "FilePolicy.Revision";
    private const string RfcCreated = "rfc_provision_create";
    private const string RfcUpdated = "rfc_provision_update";
    private const string RfcDeactivated = "rfc_provision_deactivate";
    private const string RfcActivated = "rfc_provision_activate";
    private const string RfcSeeded = "rfc_provision_seed_admin";
    private const string TransferReconciled = "PendingTransferReconciled";
    private const string TransferExpired = "PendingTransferExpired";
    private const string TransferCancelled = "PendingTransferCancelled";

    public sealed record ActionDto(string Value, string Label);
    public sealed record EntryDto(Guid Id, DateTimeOffset TimestampUtc, string? ActorRfc, string Action, string Label,
        string Summary, Guid? DeliveryId = null, string? DeliveryStatus = null);
    public sealed record PageDto(int Page, int PageSize, int TotalCount, IReadOnlyList<ActionDto> Actions,
        IReadOnlyList<EntryDto> Items);

    private sealed class AuditRow
    {
        public Guid Id { get; init; }
        public DateTimeOffset TimestampUtc { get; init; }
        public string? ActorRfc { get; init; }
        public required string Action { get; init; }
        public string? Details { get; init; }
        public string? Extension { get; init; }
        public int? Version { get; init; }
    }

    private static readonly ActionDto[] Actions =
    [
        new(MonitoringStatisticsService.ResetAction, "Reinicio de estadísticas de transferencias"),
        new(AlertStatisticsService.ResetAction, "Reinicio de estadísticas de alertas"),
        new(AlertOperationsService.TestAction, "Prueba de canal de alertas"),
        new(AlertOperationsService.ReprocessAction, "Reproceso de alerta"),
        new(PolicyRevision, "Versión de política de archivos"),
        new(RfcCreated, "Alta de acceso por RFC"),
        new(RfcUpdated, "Cambio de acceso por RFC"),
        new(RfcDeactivated, "Desactivación de acceso por RFC"),
        new(RfcActivated, "Activación de acceso por RFC"),
        new(RfcSeeded, "Alta inicial de administrador"),
        new(TransferReconciled, "Conciliación de transferencia pendiente"),
        new(TransferExpired, "Limpieza de transferencia vencida"),
        new(TransferCancelled, "Retiro de transferencia pendiente")
    ];

    private static readonly Dictionary<string, string> Labels = Actions.ToDictionary(action => action.Value,
        action => action.Label, StringComparer.Ordinal);
    private static readonly string[] LogActions = Actions.Where(action => action.Value != PolicyRevision)
        .Select(action => action.Value).ToArray();

    [HttpGet]
    public async Task<ActionResult<PageDto>> Get(CancellationToken ct, [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null, [FromQuery] string? rfc = null, [FromQuery] string? action = null,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 10)
    {
        if (page is < 1 or > 100_000 || pageSize is < 1 or > 100)
            return BadRequest(new { message = "Página o tamaño de página fuera del rango permitido." });
        if (from > to)
            return BadRequest(new { message = "El rango de fechas es inválido." });
        if (!string.IsNullOrEmpty(action) && !Labels.ContainsKey(action))
            return BadRequest(new { message = "Acción administrativa inválida." });

        string? actor = null;
        if (!string.IsNullOrWhiteSpace(rfc) && !IdentityInputPolicy.TryNormalizeRfc(rfc, out actor))
            return BadRequest(new { message = "Indique un RFC de operador válido." });

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var query = AuditRows(db, from, to, actor, action);
        var count = await query.CountAsync(ct);
        var rows = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var items = rows.Select(Describe).ToArray();
        var ids = items.Where(item => item.DeliveryId.HasValue).Select(item => item.DeliveryId!.Value).Distinct().ToArray();
        if (ids.Length > 0)
        {
            var statuses = await db.AlertOutboxDeliveries.AsNoTracking().Where(row => EF.Constant(ids).Contains(row.Id))
                .Select(row => new { row.Id, row.Status }).ToDictionaryAsync(row => row.Id, row => row.Status, ct);
            for (var index = 0; index < items.Length; index++)
            {
                var item = items[index];
                if (item.DeliveryId is { } id && statuses.TryGetValue(id, out var status) &&
                    status is AlertOutboxStatuses.Pending or AlertOutboxStatuses.Processing or
                        AlertOutboxStatuses.Delivered or AlertOutboxStatuses.DeadLetter)
                    items[index] = item with { DeliveryStatus = status };
            }
        }
        return Ok(new PageDto(page, pageSize, count, Actions, items));
    }

    private static IQueryable<AuditRow> AuditRows(FileXferDbContext db, DateTimeOffset? from, DateTimeOffset? to,
        string? actor, string? action)
    {
        var logs = db.AuditLogs.AsNoTracking().Where(row => row.Action != null && EF.Constant(LogActions).Contains(row.Action));
        var revisions = db.FileTypePolicyRevisions.AsNoTracking();
        if (from is { } first)
        {
            logs = logs.Where(row => row.TimestampUtc >= first);
            revisions = revisions.Where(row => row.ChangedUtc >= first);
        }
        if (to is { } last)
        {
            logs = logs.Where(row => row.TimestampUtc <= last);
            revisions = revisions.Where(row => row.ChangedUtc <= last);
        }
        if (actor is not null)
        {
            logs = logs.Where(row => row.ActorRfc == actor);
            revisions = revisions.Where(row => row.ChangedBy == actor);
        }
        if (!string.IsNullOrEmpty(action)) logs = logs.Where(row => row.Action == action);

        var audit = logs.Select(row => new AuditRow
        {
            Id = row.Id, TimestampUtc = row.TimestampUtc, ActorRfc = row.ActorRfc, Action = row.Action!,
            Details = row.Details, Extension = null, Version = null
        });
        var policies = revisions.Select(row => new AuditRow
        {
            Id = row.Id, TimestampUtc = row.ChangedUtc, ActorRfc = row.ChangedBy, Action = PolicyRevision,
            Details = row.ChangeReason, Extension = row.Extension, Version = row.Version
        });
        var query = action == PolicyRevision ? policies : string.IsNullOrEmpty(action) ? audit.Concat(policies) : audit;
        return query.OrderByDescending(row => row.TimestampUtc).ThenBy(row => row.Id).ThenBy(row => row.Action);
    }

    private static EntryDto Describe(AuditRow row)
    {
        var details = row.Details ?? string.Empty;
        var actor = IdentityInputPolicy.TryNormalizeRfc(row.ActorRfc, out var rfc) ? rfc :
            row.ActorRfc is "system" or "system-seed" or "system-cleanup" ? row.ActorRfc : null;
        var entry = new EntryDto(row.Id, row.TimestampUtc, actor, row.Action, Labels.GetValueOrDefault(row.Action,
            "Operación administrativa"), "Sin detalle estructurado disponible.");
        switch (row.Action)
        {
            case PolicyRevision:
                var extension = row.Extension;
                if (extension is { Length: > 1 and <= 24 } && extension[0] == '.' &&
                    extension.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_'))
                    return entry with { Summary = WithReason($"{extension} · versión {row.Version}", details) };
                break;
            case MonitoringStatisticsService.ResetAction:
                const string prefix = "Nuevo período de estadísticas. Motivo: ";
                const string suffix = ". Se conserva el historial, archivos y recibos.";
                if (details.StartsWith(prefix, StringComparison.Ordinal) && details.EndsWith(suffix, StringComparison.Ordinal))
                    return entry with { Summary = WithReason("Nuevo período de transferencias", details[prefix.Length..^suffix.Length]) };
                break;
            case AlertStatisticsService.ResetAction:
                var parts = details.Split('\n', 3);
                if (parts.Length == 3 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var exhausted))
                    return entry with { Summary = WithReason($"{exhausted} entregas agotadas pasaron al historial", parts[2]) };
                break;
            case AlertOperationsService.TestAction:
            case AlertOperationsService.ReprocessAction:
                return DescribeDelivery(entry, details);
            case RfcCreated:
            case RfcUpdated:
            case RfcDeactivated:
            case RfcActivated:
            case RfcSeeded:
                if (IdentityInputPolicy.TryNormalizeRfc(Field(details, "RFC="), out var target))
                {
                    var summary = $"RFC administrado {target}";
                    if (IdentityInputPolicy.TryNormalizeRoles(Field(details, "Roles=")?.Split(','), out var roles))
                        summary += $" · Roles {string.Join(", ", roles)}";
                    return entry with { Summary = summary };
                }
                break;
            case TransferReconciled:
            case TransferExpired:
            case TransferCancelled:
                if (Guid.TryParse(Field(details, "UploadId="), out var uploadId))
                {
                    var summary = $"Transferencia {uploadId:D}";
                    summary += Field(details, "PhysicalAction=") switch
                    {
                        "Preserved" => " · Archivo conservado",
                        "Deleted" => " · Parcial retirado",
                        _ => string.Empty
                    };
                    const string marker = "; Reason=";
                    var markerIndex = details.IndexOf(marker, StringComparison.Ordinal);
                    return entry with { Summary = WithReason(summary, markerIndex < 0 ? null : details[(markerIndex + marker.Length)..]) };
                }
                break;
        }
        return entry;
    }

    private static EntryDto DescribeDelivery(EntryDto entry, string details)
    {
        try
        {
            using var json = JsonDocument.Parse(details);
            if (json.RootElement.ValueKind != JsonValueKind.Object) return entry;
            var root = json.RootElement;
            var channel = TextProperty(root, "channel") switch
            {
                "email" => "Correo SMTP",
                "webhook" => "Webhook",
                _ => "Canal sin identificar"
            };
            if (Guid.TryParse(TextProperty(root, "sourceDeliveryId"), out var source))
                channel += $" · Entrega original {source:D}";
            var id = Guid.TryParse(TextProperty(root, "deliveryId"), out var deliveryId) ? deliveryId : (Guid?)null;
            var outcome = TextProperty(root, "outcomeStatus");
            if (outcome is not (AlertOutboxStatuses.Delivered or AlertOutboxStatuses.DeadLetter)) outcome = null;
            var failureCode = TextProperty(root, "outcomeFailureCode");
            if (outcome == AlertOutboxStatuses.DeadLetter && failureCode is { Length: > 0 and <= 120 } &&
                failureCode.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-'))
                channel += $" · Código de entrega {failureCode}";
            return entry with
            {
                Summary = WithReason(channel, TextProperty(root, "reason")),
                DeliveryId = id,
                DeliveryStatus = outcome
            };
        }
        catch (JsonException) { return entry; }
    }

    private static string? TextProperty(JsonElement root, string key) =>
        root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? Field(string text, string prefix) => text.Split(';').Select(part => part.Trim())
        .FirstOrDefault(part => part.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];

    private static string WithReason(string summary, string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return summary;
        var clean = new string(reason.Trim().Take(512).Select(character => char.IsControl(character) ? ' ' : character).ToArray());
        return $"{summary} · Motivo: {clean}";
    }
}
