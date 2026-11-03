using FileXfer.Server.Uploading;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FileXfer.Server.Controllers.Admin;

[ApiController]
[Route("admin/monitoring/pending")]
[Authorize(Policy = "PrivilegedEfirma")]
public sealed class PendingTransfersController : ControllerBase
{
    private readonly PendingTransferMaintenanceService _maintenance;
    public PendingTransfersController(PendingTransferMaintenanceService maintenance) => _maintenance = maintenance;

    [HttpGet]
    public async Task<ActionResult<PendingTransferPage>> Get(
        CancellationToken ct, [FromQuery] int page = 1, [FromQuery] int pageSize = 25)
    {
        try { return Ok(await _maintenance.GetPendingAsync(page, pageSize, ct)); }
        catch (ArgumentException exception) { return BadRequest(new { message = exception.Message }); }
    }

    public sealed record RemoveRequest(Guid[] UploadIds, string Reason);

    [HttpPost("remove")]
    [Authorize(Policy = "AdminEfirma")]
    public async Task<ActionResult<PendingTransferCleanupResult>> Remove(RemoveRequest request, CancellationToken ct)
    {
        try
        {
            var actor = Actor();
            if (string.IsNullOrWhiteSpace(actor)) return Forbid();
            if (request.UploadIds is null) return BadRequest(new { message = "Seleccione las transferencias a eliminar." });
            return Ok(await _maintenance.RemoveAsync(request.UploadIds, request.Reason, actor, Ip(), ct));
        }
        catch (ArgumentException exception) { return BadRequest(new { message = exception.Message }); }
    }

    [HttpPost("cleanup-expired")]
    [Authorize(Policy = "AdminEfirma")]
    public async Task<ActionResult<PendingTransferCleanupResult>> CleanupExpired(
        [FromBody] CleanupRequest request, CancellationToken ct)
    {
        var actor = Actor();
        if (string.IsNullOrWhiteSpace(actor)) return Forbid();
        if (string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest(new { message = "Indique el motivo de limpieza." });
        try { return Ok(await _maintenance.CleanupExpiredAsync(actor, Ip(), ct, request.Reason)); }
        catch (ArgumentException exception) { return BadRequest(new { message = exception.Message }); }
    }

    public sealed record CleanupRequest(string Reason);

    private string? Actor() => User.FindFirst("rfc")?.Value;
    private string? Ip() => HttpContext.Connection.RemoteIpAddress?.ToString();
}
