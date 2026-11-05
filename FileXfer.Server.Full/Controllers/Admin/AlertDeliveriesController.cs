using System.Text.RegularExpressions;
using FileXfer.Server.Data;
using FileXfer.Server.Options;
using FileXfer.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FileXfer.Server.Controllers.Admin;

[ApiController]
[Route("admin/monitoring/alerts")]
[Authorize(Policy = "PrivilegedEfirma")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AlertDeliveriesController(
    IDbContextFactory<FileXferDbContext> dbFactory, IOptions<AlertOptions> options,
    ILogger<AlertDeliveriesController> log, IBudiDevRepository? budi = null) : ControllerBase
{
    public sealed record DeliveryDto(Guid Id, Guid EventId, string Transport, string Title, string Status,
        int FailedAttempts, string? FailureCode, string FailureExplanation, string Remediation,
        DateTimeOffset OccurredUtc, DateTimeOffset UpdatedUtc, DateTimeOffset? NextAttemptUtc,
        string? Rfc, int? FileId, string? FileName, string? TraceId, string RelatedItem);
    public sealed record PageDto(int Page, int PageSize, int TotalCount, bool Enabled, bool EmailConfigured,
        bool WebhookConfigured, int MaxDeliveryAttempts, string[] ConfigurationIssues, IReadOnlyList<DeliveryDto> Items);

    [HttpGet]
    public async Task<ActionResult<PageDto>> Get(CancellationToken ct, [FromQuery] string status = "DeadLetter",
        [FromQuery] int page = 1, [FromQuery] int pageSize = 10)
    {
        if (page < 1 || page > 100_000 || pageSize is < 1 or > 100)
            return BadRequest(new { message = "Página o tamaño de página fuera del rango permitido." });
        if (status is not ("All" or "DeadLetter" or "Pending" or "Processing" or "Delivered"))
            return BadRequest(new { message = "Estado de entrega inválido." });
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var query = db.AlertOutboxDeliveries.AsNoTracking();
        if (status != "All") query = query.Where(row => row.Status == status);
        var count = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(row => row.UpdatedUtc).ThenBy(row => row.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var items = new List<DeliveryDto>();
        foreach (var row in rows)
        {
            var item = Describe(row);
            // Las alertas antiguas solo guardaban el ID del catálogo. La consulta conserva el contexto RFC.
            if (item.FileId is > 0 && item.FileName is null && item.Rfc is not null && budi is not null)
            {
                try
                {
                    var descriptor = await budi.GetArchivoLocalForDownloadAsync(item.FileId.Value, item.Rfc, ct);
                    if (descriptor is not null) item = item with { FileName = descriptor.FileName, RelatedItem = descriptor.FileName };
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception error) { log.LogWarning("Nombre de archivo no disponible para alerta {EventId}; ErrorType={ErrorType}", row.EventId, error.GetType().Name); }
            }
            items.Add(item);
        }
        var opt = options.Value;
        var emailConfigured = !string.IsNullOrWhiteSpace(opt.SmtpHost) && !string.IsNullOrWhiteSpace(opt.EmailFrom) && !string.IsNullOrWhiteSpace(opt.EmailTo);
        var webhookConfigured = !string.IsNullOrWhiteSpace(opt.WebhookUrl);
        var issues = new List<string>();
        if (opt.Enabled && !emailConfigured && !webhookConfigured) issues.Add("El alertamiento externo está habilitado pero no hay un canal completo configurado.");
        if (opt.Enabled && opt.SmtpHost?.Contains("tu-dominio", StringComparison.OrdinalIgnoreCase) == true)
            issues.Add("SMTP utiliza un servidor de ejemplo. Configure el host SMTP real, el puerto y las direcciones de envío; proporcione autenticación si el relay la requiere.");
        return Ok(new PageDto(page, pageSize, count, opt.Enabled, emailConfigured, webhookConfigured,
            opt.MaxDeliveryAttempts, issues.ToArray(), items));
    }

    public static DeliveryDto Describe(AlertOutboxDelivery row)
    {
        var rfc = Field(row.Message, "RFC", @"[A-Z&Ña-z]{3,4}[0-9]{6}[A-Za-z0-9]{3}")?.ToUpperInvariant();
        var fileId = int.TryParse(Field(row.Message, "ArchivoLocalId", @"\d{1,10}"), out var parsed) && parsed > 0 ? parsed : (int?)null;
        var trace = Field(row.Message, "TraceId", @"[A-Za-z0-9:_\-.]{1,128}");
        string? fileName = null;
        if (fileId is not null)
        {
            var encoded = Field(row.Message, "FileNameEncoded", @"[^;\r\n]{1,2048}");
            if (encoded is not null)
            {
                try { fileName = Uri.UnescapeDataString(encoded).Replace('\r', ' ').Replace('\n', ' '); }
                catch (UriFormatException) { /* Se conserva el identificador del archivo. */ }
            }
        }
        var related = fileName ?? (fileId is not null ? $"Archivo #{fileId} · nombre no disponible" :
            row.Title == "Login SAT fallido" ? "Autenticación con e.firma; sin archivo asociado." :
            row.Title.StartsWith("Reutilización de refresh token", StringComparison.Ordinal) ? "Sesión de usuario; sin archivo asociado." : "Evento del sistema; sin archivo identificado.");
        if (fileId is null && row.Title == "Login SAT fallido" && row.Message.Contains("fuera de vigencia", StringComparison.OrdinalIgnoreCase))
            related = "Autenticación rechazada por certificado fuera de vigencia; sin archivo asociado.";
        var (explanation, remediation) = AlertFailureDescription.Describe(row.LastFailureCode);
        return new(row.Id, row.EventId, row.Transport, row.Title, row.Status, row.AttemptCount,
            row.LastFailureCode, explanation, remediation, row.OccurredUtc, row.UpdatedUtc,
            row.Status == AlertOutboxStatuses.Pending ? row.NextAttemptUtc : null, rfc, fileId, fileName, trace, related);
    }

    private static string? Field(string text, string key, string pattern)
    {
        foreach (var part in text.Split(';'))
        {
            var field = part.TrimStart();
            if (!field.StartsWith(key + "=", StringComparison.Ordinal)) continue;
            var value = field[(key.Length + 1)..];
            if (Regex.IsMatch(value, @"\A(?:" + pattern + @")\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                return value;
        }
        return null;
    }
}
