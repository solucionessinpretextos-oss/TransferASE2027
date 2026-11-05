using System.ComponentModel.DataAnnotations;
using FileXfer.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FileXfer.Server.Controllers.Admin;

[ApiController]
[Route("admin/monitoring/statistics")]
[Authorize(Policy = "PrivilegedEfirma")]
public sealed class MonitoringStatisticsController(MonitoringStatisticsService statistics) : ControllerBase
{
    // MVC valida records posicionales desde los parámetros del constructor.
    public sealed record ResetRequest([Required, StringLength(500, MinimumLength = 5)] string Reason);

    [HttpPost("reset")]
    [Authorize(Policy = "AdminEfirma")]
    public async Task<IActionResult> Reset([FromBody] ResetRequest request, CancellationToken ct)
    {
        var actor = User.FindFirst("rfc")?.Value;
        if (string.IsNullOrWhiteSpace(actor)) return Forbid();
        try
        {
            var startedUtc = await statistics.ResetAsync(
                actor, HttpContext.Connection.RemoteIpAddress?.ToString(), request.Reason, ct);
            return Ok(new
            {
                statisticsStartedUtc = startedUtc,
                message = "Estadísticas reiniciadas. Los indicadores de actividad empiezan un nuevo período; pendientes, salud real, archivos, recibos y auditoría se conservan."
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}
