using System.ComponentModel.DataAnnotations;
using FileXfer.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FileXfer.Server.Controllers.Admin;

[ApiController]
[Route("admin/monitoring/alerts/statistics")]
[Authorize(Policy = "AdminEfirma")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AlertStatisticsController(AlertStatisticsService statistics) : ControllerBase
{
    public sealed record ResetRequest([Required, StringLength(500, MinimumLength = 5)] string Reason);

    [HttpGet]
    public async Task<ActionResult<AlertStatisticsService.HistoryPage>> Get(CancellationToken ct,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 10)
    {
        if (page < 1 || page > 100_000 || pageSize is < 1 or > 100)
            return BadRequest(new { message = "Página o tamaño de página fuera del rango permitido." });
        return Ok(await statistics.GetHistoryAsync(page, pageSize, ct));
    }

    [HttpPost("reset")]
    public async Task<IActionResult> Reset([FromBody] ResetRequest request, CancellationToken ct)
    {
        var actor = User.FindFirst("rfc")?.Value;
        if (string.IsNullOrWhiteSpace(actor)) return Forbid();

        try
        {
            var reset = await statistics.ResetAsync(actor, HttpContext.Connection.RemoteIpAddress?.ToString(),
                request.Reason, ct);
            return Ok(new
            {
                statisticsStartedUtc = reset.TimestampUtc,
                exhaustedBefore = reset.ExhaustedBefore,
                message = "Se inició un nuevo período de alertas. La bitácora conserva el motivo y el historial de entregas permanece disponible."
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}
