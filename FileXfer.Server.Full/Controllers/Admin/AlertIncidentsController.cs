using FileXfer.Server.Data;
using FileXfer.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FileXfer.Server.Controllers.Admin;

[ApiController]
[Route("admin/monitoring/alerts/incidents")]
[Authorize(Policy = "PrivilegedEfirma")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AlertIncidentsController(IDbContextFactory<FileXferDbContext> dbFactory) : ControllerBase
{
    public sealed record IncidentDto(string Transport, string FailureCode, string Description, string Remediation,
        int AffectedCount, int ActiveCount, int ExhaustedCount, DateTimeOffset FirstObservedUtc,
        DateTimeOffset LastObservedUtc, DateTimeOffset? LastSuccessfulDeliveryUtc);

    public sealed record PageDto(DateTimeOffset? StatisticsStartedUtc, int Page, int PageSize,
        int TotalCount, IReadOnlyList<IncidentDto> Items);

    private sealed class IncidentGroup
    {
        public required string Transport { get; init; }
        public required string FailureCode { get; init; }
        public int AffectedCount { get; init; }
        public int ActiveCount { get; init; }
        public int ExhaustedCount { get; init; }
        public DateTimeOffset FirstObservedUtc { get; init; }
        public DateTimeOffset LastObservedUtc { get; init; }
    }

    [HttpGet]
    public async Task<ActionResult<PageDto>> Get(CancellationToken ct, [FromQuery] string period = "Current",
        [FromQuery] int page = 1, [FromQuery] int pageSize = 10)
    {
        if (page is < 1 or > 100_000 || pageSize is < 1 or > 100)
            return BadRequest(new { message = "Página o tamaño de página fuera del rango permitido." });
        if (period is not ("All" or "Current"))
            return BadRequest(new { message = "Período de incidencias inválido." });

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var startedUtc = await AlertStatisticsService.GetStartedUtcAsync(db, ct);
        var deliveries = db.AlertOutboxDeliveries.AsNoTracking()
            .Where(row => row.Transport == "email" || row.Transport == "webhook");
        var grouped = GroupFailures(deliveries, period == "Current" ? startedUtc : null);
        var count = await grouped.CountAsync(ct);
        var rows = await grouped.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        var successful = await deliveries.Where(row => row.Status == AlertOutboxStatuses.Delivered && row.DeliveredUtc != null)
            .GroupBy(row => row.Transport)
            .Select(group => new { Transport = group.Key, DeliveredUtc = group.Max(row => row.DeliveredUtc) })
            .ToDictionaryAsync(row => row.Transport, row => row.DeliveredUtc, ct);

        var items = rows.Select(row =>
        {
            var code = SafeFailureCode(row.FailureCode);
            var (description, remediation) = AlertFailureDescription.Describe(code);
            return new IncidentDto(row.Transport, code, description, remediation, row.AffectedCount, row.ActiveCount,
                row.ExhaustedCount, row.FirstObservedUtc, row.LastObservedUtc, successful.GetValueOrDefault(row.Transport));
        }).ToArray();
        return Ok(new PageDto(startedUtc, page, pageSize, count, items));
    }

    private static IQueryable<IncidentGroup> GroupFailures(IQueryable<AlertOutboxDelivery> deliveries, DateTimeOffset? startedUtc)
    {
        var failures = deliveries.Where(row => row.LastFailureCode != null && row.LastFailureCode != "" &&
            (row.Status == AlertOutboxStatuses.Pending || row.Status == AlertOutboxStatuses.Processing ||
             row.Status == AlertOutboxStatuses.DeadLetter));
        if (startedUtc is { } cutoff)
            failures = failures.Where(row => row.Status != AlertOutboxStatuses.DeadLetter || row.UpdatedUtc >= cutoff);

        return failures.GroupBy(row => new { row.Transport, row.LastFailureCode })
            .Select(group => new IncidentGroup
            {
                Transport = group.Key.Transport,
                FailureCode = group.Key.LastFailureCode!,
                AffectedCount = group.Count(),
                ActiveCount = group.Count(row => row.Status != AlertOutboxStatuses.DeadLetter),
                ExhaustedCount = group.Count(row => row.Status == AlertOutboxStatuses.DeadLetter),
                FirstObservedUtc = group.Min(row => row.UpdatedUtc),
                LastObservedUtc = group.Max(row => row.UpdatedUtc)
            })
            .OrderByDescending(row => row.LastObservedUtc).ThenBy(row => row.Transport).ThenBy(row => row.FailureCode);
    }

    private static string SafeFailureCode(string value) =>
        value.Length <= 128 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
            ? value : "unknown_failure";
}
